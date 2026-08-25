import { Injectable, computed, inject, signal } from '@angular/core';

import { AccessTokenStore } from './access-token.store';
import { AuthApiClient } from './auth-api.client';

export type SessionState =
  | { readonly kind: 'signed-out' }
  | { readonly kind: 'signing-in' }
  | { readonly kind: 'signed-in' }
  | { readonly kind: 'rejected' }
  | { readonly kind: 'failed'; readonly message: string };

/**
 * Sign-in exists here only because `POST /api/quotes` is guarded; the quote form is the
 * subject of this piece, not the session. Kept to the minimum that lets the write path be
 * exercised for real.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly auth = inject(AuthApiClient);
  private readonly tokens = inject(AccessTokenStore);

  private readonly state = signal<SessionState>({ kind: 'signed-out' });

  readonly sessionState = this.state.asReadonly();
  readonly isSignedIn = this.tokens.isSignedIn;
  readonly isSigningIn = computed(() => this.state().kind === 'signing-in');

  async signIn(email: string, password: string): Promise<void> {
    if (this.isSigningIn()) {
      return;
    }
    this.state.set({ kind: 'signing-in' });

    const outcome = await this.auth.signIn(email, password);
    switch (outcome.status) {
      case 'signed-in':
        this.tokens.set(outcome.accessToken);
        this.state.set({ kind: 'signed-in' });
        break;
      case 'rejected':
        this.state.set({ kind: 'rejected' });
        break;
      case 'failed':
        this.state.set({ kind: 'failed', message: outcome.message });
        break;
    }
  }

  signOut(): void {
    this.tokens.clear();
    this.state.set({ kind: 'signed-out' });
  }
}
