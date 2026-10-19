import { Component, input } from '@angular/core';

export type IconName = 'refresh' | 'close' | 'trash' | 'arrow-up' | 'arrow-down' | 'more' | 'search' | 'check';

/** Decorative outline icons. The containing control supplies its accessible name. */
@Component({
  selector: 'rk-icon',
  standalone: true,
  host: { 'aria-hidden': 'true' },
  template: `
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" focusable="false">
      <path [attr.d]="paths[name()]" />
    </svg>
  `,
  styles: [`
    :host { display: inline-flex; flex: 0 0 var(--rk-icon-size); width: var(--rk-icon-size); height: var(--rk-icon-size); vertical-align: middle; }
    svg { display: block; width: 100%; height: 100%; }
  `],
})
export class IconComponent {
  readonly name = input.required<IconName>();
  readonly paths: Record<IconName, string> = {
    refresh: 'M20 7v5h-5M4 17v-5h5M6.1 6.1A8 8 0 0 1 19.7 10M4.3 14A8 8 0 0 0 17.9 17.9',
    close: 'm6 6 12 12M6 18 18 6',
    trash: 'M3 6h18M9 6V4h6v2M5 6l1 14h12l1-14M10 10v6M14 10v6',
    'arrow-up': 'M12 19V5m-6 6 6-6 6 6',
    'arrow-down': 'M12 5v14m-6-6 6 6 6-6',
    more: 'M5 12h.01M12 12h.01M19 12h.01',
    search: 'M21 21l-4.5-4.5M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0',
    check: 'm5 12 4 4L19 6',
  };
}
