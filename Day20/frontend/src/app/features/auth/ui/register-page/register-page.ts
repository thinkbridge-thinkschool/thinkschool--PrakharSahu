import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { AuthApiClient } from '../../../../core/auth/auth-api.client';
import { TokenStore } from '../../../../core/auth/token-store';

/** Mirrors `MinimumPasswordLength` in AuthEndpoints.cs. */
export const MINIMUM_PASSWORD_LENGTH = 8;

/**
 * Create an account, lazy-loaded.
 *
 * Registration signs the new user in as part of the same request, so this navigates straight
 * to `returnUrl` on success exactly as sign-in does — there is no state in which the account
 * exists but the person is still looking at a form.
 */
@Component({
  selector: 'app-register-page',
  imports: [RouterLink],
  templateUrl: './register-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RegisterPage {
  private readonly auth = inject(AuthApiClient);
  private readonly tokens = inject(TokenStore);
  private readonly router = inject(Router);

  /**
   * Bound from `?returnUrl=` by `withComponentInputBinding()`.
   *
   * The transform is load-bearing, and is here because its absence has already caused this
   * bug twice in this codebase: when the query parameter is absent the router binds
   * `undefined`, which OVERRIDES the declared default, and the guard clause below then throws
   * on `undefined.startsWith`.
   */
  readonly returnUrl = input('/quotes', {
    transform: (value: string | undefined) => value ?? '/quotes',
  });

  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly busy = signal(false);
  protected readonly failure = signal<string | null>(null);

  protected readonly minimumPasswordLength = MINIMUM_PASSWORD_LENGTH;

  /**
   * Checked here as well as on the server, and the server's answer is still the one that
   * counts — this only exists so the person is told before a round trip, not instead of one.
   */
  protected readonly passwordTooShort = computed(
    () => this.password().length > 0 && this.password().length < MINIMUM_PASSWORD_LENGTH,
  );

  protected readonly canSubmit = computed(
    () =>
      !this.busy() &&
      this.email().trim().length > 0 &&
      this.password().length >= MINIMUM_PASSWORD_LENGTH,
  );

  protected onEmail(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
  }

  protected onPassword(event: Event): void {
    this.password.set((event.target as HTMLInputElement).value);
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.canSubmit()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    const outcome = await this.auth.register(this.email(), this.password());
    // Never keep the password around longer than the request needs it.
    this.password.set('');
    this.busy.set(false);

    if (outcome.status === 'rejected') {
      this.failure.set(outcome.message);
      return;
    }

    this.tokens.setAccessToken(outcome.tokens.accessToken, outcome.tokens.expiresIn);
    // `returnUrl` is a same-origin path produced by our own guard, but it arrives through the
    // URL bar, so refuse anything that could send the user off-site.
    const target = this.returnUrl();
    await this.router.navigateByUrl(
      target.startsWith('/') && !target.startsWith('//') ? target : '/quotes',
    );
  }
}
