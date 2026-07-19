import { CommonModule } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { GuildService, LeaderboardSettings, LeaderboardVisibility } from '../../services/guild.service';
import { AppStore } from '../../store/app.store';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiErrorService } from '../../services/api-error.service';

@Component({ selector: 'app-leaderboard-settings', standalone: true, imports: [CommonModule, FormsModule, RouterLink, TranslocoPipe], templateUrl: './leaderboard-settings.component.html', styleUrls: ['./leaderboard-settings.component.scss'] })
export class LeaderboardSettingsComponent implements OnInit {
  private readonly app = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);
  readonly settings = signal<LeaderboardSettings | null>(null);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly message = signal('');
  readonly error = signal('');
  alias = '';
  visibility: LeaderboardVisibility = 'MembersOnly';

  ngOnInit(): void { this.load(); }
  load(): void {
    const guildId = this.app.selectedGuild()?.id;
    if (!guildId) { this.loading.set(false); this.error.set(this.i18n.translate('errors.noServer')); return; }
    this.loading.set(true); this.error.set('');
    this.api.leaderboardSettings(guildId).pipe(finalize(() => this.loading.set(false))).subscribe({ next: settings => this.apply(settings), error: error => this.error.set(this.apiErrors.resolve(error, 'errors.settingsLoad').message) });
  }
  save(): void {
    const guildId = this.app.selectedGuild()?.id;
    if (!guildId || !this.alias.trim() || this.saving()) return;
    this.saving.set(true); this.error.set(''); this.message.set('');
    this.api.saveLeaderboardSettings(guildId, { alias: this.alias, visibility: this.visibility }).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: settings => { this.apply(settings); this.message.set(this.i18n.translate('leaderboardSettings.saved')); },
      error: response => this.error.set(response.status === 409 ? this.i18n.translate('errors.aliasTaken') : this.apiErrors.resolve(response, 'errors.save').message),
    });
  }
  private apply(settings: LeaderboardSettings): void { this.settings.set(settings); this.alias = settings.alias; this.visibility = settings.visibility; }
}
