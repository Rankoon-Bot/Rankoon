import { Component, Input } from '@angular/core';

@Component({
  selector: 'app-user-avatar',
  standalone: true,
  template: `<span class="avatar" aria-hidden="true">@if (iconUrl && !imageFailed) { <img [src]="iconUrl" alt="" width="40" height="40" loading="lazy" decoding="async" (error)="imageFailed = true"> } @else { {{ displayName.charAt(0).toUpperCase() }} }</span>`,
  styles: `:host { display: contents; } .avatar { display: grid; flex: 0 0 40px; place-items: center; width: 40px; height: 40px; overflow: hidden; color: var(--rk-text-strong); background: var(--rk-surface-3); border: 1px solid var(--rk-border-strong); border-radius: 50%; font-weight: 700; } .avatar img { width: 100%; height: 100%; object-fit: cover; } @media (max-width: 520px) { :host { display: none; } }`,
})
export class UserAvatarComponent {
  @Input({ required: true }) displayName = '';
  imageFailed = false;
  private value: string | null | undefined;

  @Input()
  set iconUrl(value: string | null | undefined) {
    this.value = value;
    this.imageFailed = false;
  }

  get iconUrl(): string | null | undefined { return this.value; }
}
