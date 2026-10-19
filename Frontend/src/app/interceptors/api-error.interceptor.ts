import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ApiErrorService } from '../services/api-error.service';
import { SKIP_API_ERROR_TOAST, SUPPRESSED_API_ERROR_KEYS } from './api-error.context';

/** Runs outside authInterceptor so recovered CSRF/session attempts never produce a toast. */
export const apiErrorInterceptor: HttpInterceptorFn = (request, next) => {
  const path = new URL(request.url, 'http://rankoon.local').pathname;
  if (!path.startsWith('/api/') || request.context.get(SKIP_API_ERROR_TOAST)) return next(request);
  const errors = inject(ApiErrorService);
  return next(request).pipe(catchError((error: unknown) => {
    const internalAuthRequest = /^\/api\/auth\/(refresh|csrf)\/?$/.test(path);
    const expectedSessionCheck = /^\/api\/auth\/(validate|me)\/?$/.test(path) && error instanceof HttpErrorResponse && [401, 403].includes(error.status);
    const suppressed = request.context.get(SUPPRESSED_API_ERROR_KEYS);
    const key = error instanceof HttpErrorResponse && typeof error.error?.errorKey === 'string' ? error.error.errorKey : '';
    if (!internalAuthRequest && !expectedSessionCheck && !suppressed.includes(key)) errors.notify(error);
    return throwError(() => error); // Preserve field errors, conflict handling and the caller's state.
  }));
};
