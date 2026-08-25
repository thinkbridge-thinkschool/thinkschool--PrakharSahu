import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';

import { SessionStore } from '../../session-store';

/**
 * Minimal sign-in, present only because `POST /api/quotes` is guarded.
 *
 * The credentials are read from the two inputs, handed straight to `POST /api/auth/login`,
 * and never stored, echoed or logged. The resulting access token is held in memory for the
 * tab's lifetime — see `AccessTokenStore` for why it is not persisted.
 */
@Component({
  selector: 'app-sign-in-panel',
  templateUrl: './sign-in-panel.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInPanel {
  protected readonly session = inject(SessionStore);

  protected readonly email = signal('');
  protected readonly password = signal('');

  protected readonly statusMessage = computed(() => {
    const state = this.session.sessionState();
    switch (state.kind) {
      case 'rejected':
        return 'That email and password combination was not accepted.';
      case 'failed':
        return state.message;
      case 'signed-in':
        return 'Signed in.';
      default:
        return '';
    }
  });

  protected readonly isFailure = computed(() => {
    const kind = this.session.sessionState().kind;
    return kind === 'rejected' || kind === 'failed';
  });

  protected onEmailInput(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
  }

  protected onPasswordInput(event: Event): void {
    this.password.set((event.target as HTMLInputElement).value);
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    await this.session.signIn(this.email(), this.password());
    // Never keep the password in a signal any longer than the request needs it.
    this.password.set('');
  }
}
