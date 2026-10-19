import { Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

export type SkeletonLayout = 'form' | 'cards' | 'table' | 'chart' | 'dashboard';

@Component({
  selector: 'rk-loading-skeleton',
  standalone: true,
  imports: [TranslocoPipe],
  template: `
    <section class="loading-skeleton" role="status" aria-busy="true">
      <span class="rk-sr-only">{{ 'common.loading' | transloco }}</span>
      <div aria-hidden="true" [class]="'skeleton-content skeleton-content--' + layout()">
        @if (layout() === 'cards' || layout() === 'dashboard') {
          <div class="skeleton-cards">
            @for (card of cards; track card) {
              <div class="skeleton-panel"><div class="rk-skeleton skeleton-label"></div><div class="rk-skeleton skeleton-value"></div><div class="rk-skeleton skeleton-line"></div></div>
            }
          </div>
        }
        @if (layout() === 'table') {
          <div class="skeleton-panel skeleton-table">
            @for (row of rows; track row) {
              <div class="skeleton-row"><div class="rk-skeleton skeleton-avatar"></div><div class="rk-skeleton skeleton-line"></div><div class="rk-skeleton skeleton-value"></div></div>
            }
          </div>
        }
        @if (layout() === 'form') {
          @for (panel of panels; track panel) {
            <div class="skeleton-panel"><div class="rk-skeleton skeleton-label"></div><div class="skeleton-fields">@for (field of fields; track field) { <div><div class="rk-skeleton skeleton-label"></div><div class="rk-skeleton skeleton-control"></div></div> }</div></div>
          }
        }
        @if (layout() === 'chart' || layout() === 'dashboard') {
          <div class="skeleton-panel"><div class="rk-skeleton skeleton-label"></div><div class="rk-skeleton skeleton-chart"></div></div>
        }
      </div>
    </section>
  `,
  styleUrl: './loading-skeleton.component.scss',
})
export class LoadingSkeletonComponent {
  readonly layout = input<SkeletonLayout>('form');
  readonly cards = [1, 2, 3, 4];
  readonly rows = [1, 2, 3, 4, 5];
  readonly panels = [1, 2];
  readonly fields = [1, 2];
}
