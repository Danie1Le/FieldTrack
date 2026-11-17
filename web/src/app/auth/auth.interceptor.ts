import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth';

/**
 * Sends the access token with every API call except the auth endpoints. A 401 means the token died early (revoked,
 * or the browser slept through the silent refresh): refresh once and retry; if the session is gone, sign out.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/') || request.url.startsWith('/api/auth/')) {
    return next(request);
  }

  const auth = inject(AuthService);
  const send = () => from(auth.accessToken()).pipe(switchMap((token) => next(withToken(request, token))));

  return send().pipe(
    catchError((error) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) {
        return throwError(() => error);
      }

      return from(auth.refresh()).pipe(
        switchMap((refreshed) => {
          if (refreshed) {
            return send();
          }

          auth.signOut();
          return throwError(() => error);
        }),
      );
    }),
  );
};

function withToken<T>(request: HttpRequest<T>, token: string | null): HttpRequest<T> {
  return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
}
