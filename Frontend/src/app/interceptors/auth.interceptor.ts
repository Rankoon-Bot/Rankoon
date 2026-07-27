import { HttpContextToken, HttpEvent, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';

const REFRESH_RETRY_ATTEMPTED = new HttpContextToken(() => false);
const CSRF_REQUEST = new HttpContextToken(() => false);
const CSRF_RETRY_ATTEMPTED = new HttpContextToken(() => false);

function requiresCsrf(method: string): boolean {
  return !['GET', 'HEAD', 'OPTIONS'].includes(method);
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const isApiRequest = req.url.includes('/api/');
  const isAuthRequest = req.url.includes('/api/auth/');
  const isCsrfRequest = req.url.includes('/api/auth/csrf');

  if (!isApiRequest) {
    return next(req);
  }

  const authService = inject(AuthService);
  const credentialedRequest = req.clone({ withCredentials: true });

  if (isCsrfRequest || credentialedRequest.context.get(CSRF_REQUEST)) {
    return next(credentialedRequest);
  }

  const send = (request: HttpRequest<unknown>): Observable<HttpEvent<unknown>> => next(request).pipe(
    catchError(error => {
      if (error.status === 403 && requiresCsrf(request.method) && !request.context.get(CSRF_RETRY_ATTEMPTED)) {
        return authService.renewCsrfToken().pipe(
          switchMap(token => send(request.clone({
            headers: request.headers.set('X-CSRF-Token', token),
            context: request.context.set(CSRF_RETRY_ATTEMPTED, true)
          })))
        );
      }

      if (isAuthRequest || error.status !== 401 || request.context.get(REFRESH_RETRY_ATTEMPTED)) {
        return throwError(() => error);
      }

      return authService.refreshToken().pipe(
        switchMap(refreshed => refreshed
          ? send(request.clone({ context: request.context.set(REFRESH_RETRY_ATTEMPTED, true) }))
          : throwError(() => error)
        )
      );
    })
  );

  if (!requiresCsrf(credentialedRequest.method)) return send(credentialedRequest);

  return authService.getCsrfToken().pipe(
    switchMap(token => send(credentialedRequest.clone({
      headers: credentialedRequest.headers.set('X-CSRF-Token', token),
      context: credentialedRequest.context.set(CSRF_REQUEST, true)
    })))
  );
};
