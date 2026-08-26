import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

import { ApiError, toApiError } from './api-error';

/**
 * Turns every `HttpErrorResponse` into a typed `ApiError` before it leaves the HTTP layer.
 *
 * After this point nothing in the app sees a status code or a raw response body — callers
 * switch on `error.kind` and render `error.friendlyMessage`. That is what stops the
 * `text/plain` .NET stack trace this API returns on a malformed request from ever reaching
 * a screen.
 *
 * Registered BEFORE the retry interceptor, so that in the response direction retry runs
 * first (closest to the backend, still seeing raw failures) and this maps only whatever
 * survives the retries.
 */
export const errorMappingInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof ApiError) {
        return throwError(() => error);
      }
      if (error instanceof HttpErrorResponse) {
        return throwError(() => toApiError(error));
      }
      // Something that is not an HTTP failure at all — a bug in an earlier interceptor, or
      // a serialisation error. Wrap it rather than letting an untyped throwable escape.
      return throwError(
        () =>
          new ApiError({
            kind: 'unknown',
            status: 0,
            friendlyMessage: 'Something went wrong while talking to the Quotes API.',
            cause: error,
          }),
      );
    }),
  );
