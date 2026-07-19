import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { AppLocale, LocaleService } from '../../i18n/locale.service';

@Component({
  selector: 'app-language-switcher',
  standalone: true,
  imports: [TranslocoPipe],
  template: `
    <label class="language">
      <span class="sr-only">{{ 'language.label' | transloco }}</span>
      <select [value]="locale.locale()" (change)="change($event)" [attr.aria-label]="'language.label' | transloco">
        <option value="en">EN</option>
        <option value="de">DE</option>
      </select>
    </label>
  `,
  styles: [`
    .language { display: block; }
    select { min-width: 64px; min-height: 44px; padding: 0 var(--rk-space-2); color: var(--rk-text-strong); background: var(--rk-surface-2); border: 1px solid var(--rk-border-strong); border-radius: var(--rk-radius-md); font-weight: 700; cursor: pointer; }
    select:hover { background: var(--rk-surface-3); border-color: var(--rk-info); }
    select:focus-visible { outline: none; box-shadow: var(--rk-focus-ring); }
    .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; }
  `]
})
export class LanguageSwitcherComponent {
  readonly locale = inject(LocaleService);

  change(event: Event): void {
    this.locale.setLocale((event.target as HTMLSelectElement).value as AppLocale);
  }
}
