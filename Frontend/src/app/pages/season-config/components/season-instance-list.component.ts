import { Component, inject, Input } from '@angular/core';
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
  @Input() mode: 'planned' | 'past' = 'planned';
  @Input() limit = 24;
  visible(): Season[] {
    const filtered = this.mode === 'planned' ? this.seasons.filter(item => item.status === 'Scheduled') : this.seasons.filter(item => item.status === 'Closed' || item.status === 'Cancelled');
    return this.mode === 'past' ? filtered.slice(0, this.limit) : filtered;
  }
  format(value: string): string { return this.locale.date(value, { dateStyle: 'medium' }); }
}
