import { Component, Input } from '@angular/core';

@Component({
  selector: 'app-user-avatar',
  standalone: true,
  template: `<span class="avatar" [class.avatar--large]="size === 'large'" aria-hidden="true">@if (iconUrl && !imageFailed) { <img [src]="iconUrl" alt="" [attr.width]="size === 'large' ? 64 : 40" [attr.height]="size === 'large' ? 64 : 40" loading="lazy" decoding="async" (error)="imageFailed = true"> } @else { {{ displayName.charAt(0).toUpperCase() }} }</span>`,
  styles: `:host { display: contents; } .avatar { display: grid; flex: 0 0 40px; place-items: center; width: 40px; height: 40px; overflow: hidden; color: var(--rk-text-strong); background: var(--rk-surface-3); border: 1px solid var(--rk-border-strong); border-radius: 50%; font-weight: 700; } .avatar--large { flex-basis: 64px; width: 64px; height: 64px; } .avatar img { width: 100%; height: 100%; object-fit: cover; }`,
})
export class UserAvatarComponent {
  @Input({ required: true }) displayName = '';
  @Input() size: 'default' | 'large' = 'default';
  imageFailed = false;
  private value: string | null | undefined;

  @Input()
  set iconUrl(value: string | null | undefined) {
    this.value = value;
    this.imageFailed = false;
  }

  get iconUrl(): string | null | undefined { return this.value; }
}
