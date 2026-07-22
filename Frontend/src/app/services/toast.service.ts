import { Injectable, signal } from '@angular/core';

export type ToastType = 'success' | 'error' | 'warning' | 'info';

export interface Toast {
  id: number;
  message: string;
  type: ToastType;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly duration = 6000;
  private readonly limit = 6;
  private nextId = 0;
  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();
  private readonly _toasts = signal<Toast[]>([]);

  readonly toasts = this._toasts.asReadonly();

  success(message: string): void { this.show(message, 'success'); }
  error(message: string): void { this.show(message, 'error'); }
  warning(message: string): void { this.show(message, 'warning'); }
  info(message: string): void { this.show(message, 'info'); }

  dismiss(id: number): void {
    const timer = this.timers.get(id);
    if (timer) clearTimeout(timer);
    this.timers.delete(id);
    this._toasts.update(toasts => toasts.filter(toast => toast.id !== id));
  }

  private show(message: string, type: ToastType): void {
    const toast = { id: ++this.nextId, message, type };
    const oldest = this._toasts()[0];
    if (oldest && this._toasts().length >= this.limit) this.dismiss(oldest.id);
    this._toasts.update(toasts => [...toasts, toast]);
    this.timers.set(toast.id, setTimeout(() => this.dismiss(toast.id), this.duration));
  }
}
