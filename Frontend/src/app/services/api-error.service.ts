import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { ApiValidationItem, ResolvedApiError } from '../models/api-error.model';
import { readApiError, statusErrorKey, validationItems } from './api-error-response';
import { ToastService } from './toast.service';

@Injectable({ providedIn: 'root' })
export class ApiErrorService {
  private readonly transloco = inject(TranslocoService);
  private readonly toast = inject(ToastService);
  private readonly notified = new WeakSet<object>();

  resolve(error: unknown, fallbackKey = 'errors.generic'): ResolvedApiError {
    const payload: unknown = error instanceof HttpErrorResponse ? error.error : (error as { error?: unknown } | null)?.error;
    const body = readApiError(payload);
    const validation = validationItems(body).map(item => ({ ...item, message: this.itemMessage(item, fallbackKey) }));
    const key = body?.errorKey || (!body?.message && error instanceof HttpErrorResponse ? statusErrorKey(error.status) : undefined);
    const context = this.message(key, body?.message, body?.parameters, fallbackKey);
    const details = [...new Set(validation.map(item => item.message).filter(message => message !== context))];
    return { message: [context, ...details].join(' '), validation };
  }

  /** One notification per failed response, even when an interceptor and the page both handle it. */
  notify(error: unknown, fallbackKey = 'errors.generic'): ResolvedApiError {
    const resolved = this.resolve(error, fallbackKey);
    if (typeof error === 'object' && error !== null) {
      if (this.notified.has(error)) return resolved;
      this.notified.add(error);
    }
    this.toast.error(resolved.message);
    return resolved;
  }

  private itemMessage(item: ApiValidationItem, fallbackKey: string): string {
    return this.message(item.errorKey, item.message, { ...item.parameters, field: item.field ?? '' }, fallbackKey);
  }

  private message(errorKey: string | undefined, backendMessage: string | undefined, params: Record<string, unknown> | undefined, fallbackKey: string): string {
    if (errorKey) {
      const key = `apiErrors.${errorKey}`;
      const translated = this.transloco.translate(key, params);
      if (translated !== key) return translated;
    }
    return backendMessage?.trim() || this.transloco.translate(fallbackKey);
  }
}
