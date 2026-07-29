import { Component, EventEmitter, inject, Input, Output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../../i18n/locale.service';
import { Season, SeasonSettings } from '../../../services/guild.service';

@Component({
  selector: 'rk-season-status-summary',
  standalone: true,
  imports: [TranslocoPipe],
  templateUrl: './season-status-summary.component.html',
  styleUrl: './season-status-summary.component.scss',
})
export class SeasonStatusSummaryComponent {
  private readonly locale = inject(LocaleService);
  private readonly i18n = inject(TranslocoService);
  @Input({ required: true }) settings!: SeasonSettings;
  @Input() current: Season | null = null;
  @Input() next: Season | null = null;
  @Input() complete = false;
  @Output() readonly setup = new EventEmitter<void>();

  status(): 'disabled' | 'active' | 'ready' | 'attention' {
    if (!this.settings.enabled) return 'disabled';
    if (this.current) return 'active';
    if (!this.complete) return 'attention';
    return 'ready';
  }
  progress(): number {
    if (!this.current) return 0;
    const start = new Date(this.current.startsAtUtc).getTime();
    const end = new Date(this.current.endsAtUtc).getTime();
    return Math.max(0, Math.min(100, Math.round((Date.now() - start) / (end - start) * 100)));
  }
  remaining(): string {
    if (!this.current) return '';
    const days = Math.max(0, Math.ceil((new Date(this.current.endsAtUtc).getTime() - Date.now()) / 86_400_000));
    return this.i18n.translate(days === 1 ? 'seasons.statusSummary.dayRemaining' : 'seasons.statusSummary.daysRemaining', { count: this.locale.number(days) });
  }
  formatDate(value: string): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }); }
}
