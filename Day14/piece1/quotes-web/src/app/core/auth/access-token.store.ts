import { Injectable, computed, signal } from '@angular/core';

/**
 * Holds the bearer token for the current session.
 *
 * In memory only, on purpose. `POST /api/auth/login` also returns a long-lived refresh
 * token, and persisting either to localStorage would leave a credential readable by any
 * script on the origin. A reload signing the user out is the correct trade here; a real
 * deployment would use an httpOnly cookie instead.
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
