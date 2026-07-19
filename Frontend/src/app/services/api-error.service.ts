import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { ApiErrorBody, ApiValidationItem, ResolvedApiError } from '../models/api-error.model';

@Injectable({ providedIn: 'root' })
export class ApiErrorService {
  private readonly transloco = inject(TranslocoService);

  resolve(error: unknown, fallbackKey = 'errors.generic'): ResolvedApiError {
    const body = error instanceof HttpErrorResponse ? error.error as ApiErrorBody | null : (error as { error?: ApiErrorBody })?.error;
    const validation = Object.entries(body?.errors ?? {}).flatMap(([field, items]) =>
      items.map(item => ({ ...item, field, message: this.itemMessage({ ...item, field }, fallbackKey) }))
    );
    const context = this.message(body?.errorKey, body?.message, body?.parameters, fallbackKey);
    const details = [...new Set(validation.map(item => item.message).filter(message => message !== context))];
    return { message: [context, ...details].join(' '), validation };
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
