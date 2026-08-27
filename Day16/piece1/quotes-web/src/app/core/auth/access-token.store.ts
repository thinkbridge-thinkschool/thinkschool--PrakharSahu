import { Injectable, computed, signal } from '@angular/core';

/**
 * Holds the bearer token for the current session, in memory only.
 *
 * `POST /api/auth/login` also returns a long-lived refresh token; persisting either to
 * localStorage would leave a credential readable by any script on the origin. Losing the
 * session on reload is the right trade here.
 */
@Injectable({ providedIn: 'root' })
export class AccessTokenStore {
  private readonly token = signal<string | null>(null);

  readonly accessToken = this.token.asReadonly();
  readonly isSignedIn = computed(() => this.token() !== null);

  set(accessToken: string): void {
    this.token.set(accessToken);
  }

  clear(): void {
    this.token.set(null);
  }
}
