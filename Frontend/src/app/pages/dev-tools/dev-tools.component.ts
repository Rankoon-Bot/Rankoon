import { CommonModule } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize, Observable } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { DevToolsService, DevelopmentLeaderboardStatus } from '../../services/dev-tools.service';
import { AppStore } from '../../store/app.store';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-dev-tools',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, TranslocoPipe],
  templateUrl: './dev-tools.component.html',
  styleUrls: ['./dev-tools.component.scss'],
})
export class DevToolsComponent implements OnInit {
  private readonly api = inject(DevToolsService);
  private readonly errors = inject(ApiErrorService);
  private readonly i18n = inject(TranslocoService);
  private readonly toast = inject(ToastService);
  readonly app = inject(AppStore);
  readonly status = signal<DevelopmentLeaderboardStatus | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal('');
  userCount = 100;
  eventCount = 10;
  minimumXp = 5;
  maximumXp = 100;

  ngOnInit(): void { this.refresh(); }
  refresh(): void {
    this.loading.set(true);
    this.error.set('');
    this.api.status(this.guildId()).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: status => this.status.set(status),
      error: error => this.error.set(this.errors.resolve(error, 'devTools.errors.load').message),
    });
  }
  generate(): void { this.run(this.api.generate(this.guildId(), this.userCount), 'devTools.messages.generated'); }
  triggerEvents(): void {
    this.busy.set(true);
    this.api.triggerEvents(this.guildId(), this.eventCount, this.minimumXp, this.maximumXp).pipe(finalize(() => this.busy.set(false))).subscribe({
      next: result => { this.status.set(result.status); this.toast.success(this.i18n.translate('devTools.messages.events', { count: result.granted })); },
      error: error => this.toast.error(this.errors.resolve(error, 'devTools.errors.events').message),
    });
  }
  remove(): void {
    if (!confirm(this.i18n.translate('devTools.removeConfirm'))) return;
    this.run(this.api.remove(this.guildId()), 'devTools.messages.removed');
  }
  private run(request: Observable<DevelopmentLeaderboardStatus>, messageKey: string): void {
    this.busy.set(true);
    request.pipe(finalize(() => this.busy.set(false))).subscribe({
      next: status => { this.status.set(status); this.toast.success(this.i18n.translate(messageKey)); },
      error: error => this.toast.error(this.errors.resolve(error, 'devTools.errors.operation').message),
    });
  }
  private guildId(): string { return this.app.selectedGuild()?.id ?? ''; }
}
