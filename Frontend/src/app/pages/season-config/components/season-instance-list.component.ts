import { Component, EventEmitter, inject, Input, Output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '../../../i18n/locale.service';
import { Season } from '../../../services/guild.service';

@Component({
  selector: 'rk-season-instance-list',
  standalone: true,
  imports: [TranslocoPipe],
  templateUrl: './season-instance-list.component.html',
  styleUrl: './season-instance-list.component.scss',
})
export class SeasonInstanceListComponent {
  private readonly locale = inject(LocaleService);
  @Input() seasons: Season[] = [];
  @Input() limit = 24;
  @Input() busy = false;
  @Input() dirty = false;
  @Input() enabled = true;
  @Input() timeZone: string | undefined;
  @Output() readonly edit = new EventEmitter<Season>();
  @Output() readonly action = new EventEmitter<{ action: 'start' | 'close' | 'cancel' | 'resume' | 'delete' | 'pause'; season: Season }>();
  visible(): Season[] {
    const live = this.seasons.filter(item => item.status === 'Active' || item.status === 'Closing');
    const scheduled = this.seasons.filter(item => (item.status === 'Scheduled' || item.status === 'Paused')).sort((a, b) => new Date(a.startsAtUtc).getTime() - new Date(b.startsAtUtc).getTime());
    const past = this.seasons.filter(item => item.status === 'Closed' || item.status === 'Cancelled').slice(0, this.limit);
    return [...live, ...scheduled, ...past];
  }
  actions(season: Season): Array<'start' | 'close' | 'cancel' | 'resume' | 'delete' | 'pause'> {
    if (season.status === 'Scheduled') return [...(this.canStart(season) ? ['start' as const] : []), 'pause', 'delete'];
    if (season.status === 'Paused') return [...(new Date(season.endsAtUtc).getTime() > Date.now() ? ['resume' as const] : []), 'delete'];
    if (season.status === 'Active') return ['close', 'cancel'];
    if (season.status === 'Cancelled') return [...(this.isResumable(season) ? ['resume' as const] : []), ...(!this.isReferenced(season) ? ['delete' as const] : [])];
    return [];
  }
  dangerous(action: string): boolean { return action === 'close' || action === 'cancel' || action === 'delete'; }
  private isReferenced(season: Season): boolean { return !!season.id && this.seasons.some(item => item.previousSeasonId === season.id); }
  private isResumable(season: Season): boolean {
    const now = Date.now();
    return this.enabled && !season.finalized && now < new Date(season.endsAtUtc).getTime()
      && !this.seasons.some(item => item.id !== season.id && item.status !== 'Cancelled'
        && new Date(item.startsAtUtc) < new Date(season.endsAtUtc) && new Date(season.startsAtUtc) < new Date(item.endsAtUtc))
      && (new Date(season.startsAtUtc).getTime() > now || !this.seasons.some(item => item.status === 'Active' || item.status === 'Closing'));
  }
  private canStart(season: Season): boolean {
    return this.enabled && !this.seasons.some(item => item.status === 'Active' || item.status === 'Closing')
      && new Date(season.startsAtUtc).getTime() <= Date.now() && Date.now() < new Date(season.endsAtUtc).getTime();
  }
  format(value: string): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short', timeZone: this.timeZone }); }
}
