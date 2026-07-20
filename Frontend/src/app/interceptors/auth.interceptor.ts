import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { AuthStore } from '../store/auth.store';

const REFRESH_RETRY_ATTEMPTED = new HttpContextToken(() => false);

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const isApiRequest = req.url.includes('/api/');
  const isAuthRequest = req.url.includes('/api/auth/');
  const isRefreshRequest = req.url.includes('/api/auth/refresh');

  if (!isApiRequest || isRefreshRequest) {
    return next(req);
  }

  const authStore = inject(AuthStore);
  const authService = inject(AuthService);
  const token = authStore.token();

  if (!token) {
    return next(req);
  }

  if (isAuthRequest) {
    return next(req.clone({ headers: req.headers.set('Authorization', `Bearer ${token}`) }));
  }

  return authService.ensureValidAccessToken().pipe(
    switchMap(accessToken => {
      if (!accessToken) {
        return throwError(() => new Error('Unable to refresh access token'));
      }

      const authReq = req.clone({ headers: req.headers.set('Authorization', `Bearer ${accessToken}`) });
      return next(authReq).pipe(
        catchError(error => {
          if (error.status !== 401 || req.context.get(REFRESH_RETRY_ATTEMPTED)) {
            return throwError(() => error);
          }

          // A server-side invalidation or clock skew can still cause a 401 despite a local expiry check.
          return authService.refreshToken().pipe(
            switchMap(refreshed => {
              const refreshedToken = authStore.token();
              if (!refreshed || !refreshedToken) {
                return throwError(() => error);
              }

              return next(req.clone({
                context: req.context.set(REFRESH_RETRY_ATTEMPTED, true),
                headers: req.headers.set('Authorization', `Bearer ${refreshedToken}`)
              }));
            })
          );
        })
      );
    })
  );
};
