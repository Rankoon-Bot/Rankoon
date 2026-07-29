import { Component, inject, Input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '../../../i18n/locale.service';
import { SeasonPreview } from '../../../services/guild.service';

@Component({
  selector: 'rk-season-timeline-preview',
  standalone: true,
  imports: [TranslocoPipe],
  templateUrl: './season-timeline-preview.component.html',
  styleUrl: './season-timeline-preview.component.scss',
})
export class SeasonTimelinePreviewComponent {
  private readonly locale = inject(LocaleService);
  @Input() items: SeasonPreview[] = [];
  @Input() gapDays = 0;
  @Input() loading = false;
  @Input() unavailable = false;
  format(value: string): string { return this.locale.date(value, { day: '2-digit', month: 'short', year: 'numeric' }); }
}
