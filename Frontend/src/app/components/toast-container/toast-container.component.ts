import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-toast-container',
  standalone: true,
  imports: [TranslocoPipe],
  template: `
    <section class="toast-container" [attr.aria-label]="'common.notifications' | transloco">
      @for (toast of toasts.toasts(); track toast.id) {
        <article class="toast toast--{{ toast.type }}" [attr.role]="toast.type === 'error' ? 'alert' : 'status'" [attr.aria-live]="toast.type === 'error' ? 'assertive' : 'polite'" aria-atomic="true">
          <div class="toast__content"><span class="toast__icon" aria-hidden="true">{{ icon(toast.type) }}</span><p>{{ toast.message }}</p></div>
          <button class="toast__close" type="button" (click)="toasts.dismiss(toast.id)" [attr.aria-label]="'common.dismissNotification' | transloco">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18"/></svg>
          </button>
          <span class="toast__progress" aria-hidden="true"></span>
        </article>
      }
    </section>
  `,
  styleUrl: './toast-container.component.scss',
})
export class ToastContainerComponent {
  readonly toasts = inject(ToastService);

  icon(type: 'success' | 'error' | 'warning' | 'info'): string {
    return type === 'success' ? 'OK' : type === 'error' ? '!' : type === 'warning' ? '!' : 'i';
  }
}
