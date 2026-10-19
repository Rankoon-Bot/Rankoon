import { Component, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

export interface ReportFilterValue {
  from: string;
  to: string;
  search: string;
}

@Component({
  selector: 'app-report-filter-bar',
  standalone: true,
  imports: [FormsModule, TranslocoPipe],
  template: `
    <form class="filter-bar" (ngSubmit)="submit()">
      <label class="search-field">
        <span>{{ 'reports.search' | transloco }}</span>
        <input type="search" name="search" [ngModel]="draftSearch()" (ngModelChange)="draftSearch.set($event)" [placeholder]="searchPlaceholder()" />
      </label>
      <label>
        <span>{{ 'reports.from' | transloco }}</span>
        <input type="datetime-local" name="from" [ngModel]="draftFrom()" (ngModelChange)="draftFrom.set($event)" />
      </label>
      <label>
        <span>{{ 'reports.to' | transloco }}</span>
        <input type="datetime-local" name="to" [ngModel]="draftTo()" (ngModelChange)="draftTo.set($event)" />
      </label>
      <button class="rk-button rk-button--primary" type="submit">{{ 'reports.apply' | transloco }}</button>
      <button class="rk-button" type="button" (click)="reset.emit()">{{ 'reports.reset' | transloco }}</button>
    </form>
  `,
  styles: [`
    .filter-bar { display: grid; grid-template-columns: minmax(220px, 1fr) repeat(2, minmax(180px, auto)) auto auto; align-items: end; gap: var(--rk-space-3); padding: var(--rk-space-4); background: var(--rk-surface-1); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-lg); box-shadow: var(--rk-shadow-panel); }
    label { display: grid; gap: var(--rk-space-1); color: var(--rk-text-muted); font-size: .75rem; font-weight: 700; text-transform: uppercase; letter-spacing: .06em; }
    input { width: 100%; min-height: 44px; text-transform: none; letter-spacing: normal; }

    @media (max-width: 900px) { .filter-bar { grid-template-columns: 1fr 1fr; } .search-field { grid-column: 1 / -1; } }
    @media (max-width: 560px) { .filter-bar { grid-template-columns: 1fr; } .search-field { grid-column: auto; } }
  `]
})
export class ReportFilterBarComponent {
  readonly from = input('');
  readonly to = input('');
  readonly search = input('');
  readonly searchPlaceholder = input('');
  readonly apply = output<ReportFilterValue>();
  readonly reset = output<void>();
  readonly draftFrom = signal('');
  readonly draftTo = signal('');
  readonly draftSearch = signal('');

  constructor() {
    effect(() => {
      this.draftFrom.set(this.from());
      this.draftTo.set(this.to());
      this.draftSearch.set(this.search());
    });
  }

  submit(): void {
    this.apply.emit({ from: this.draftFrom(), to: this.draftTo(), search: this.draftSearch().trim() });
  }
}
