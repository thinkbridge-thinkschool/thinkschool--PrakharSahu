import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';

import { TokenStore } from '../../../../core/auth/token-store';
import { AuthApiClient } from '../../../../core/auth/auth-api.client';

/**
 * Sign-in, lazy-loaded.
 *
 * `returnUrl` comes from the query string that `authGuard` attached when it redirected, so
 * a user who deep-linked to a protected page lands back on it rather than on a generic
 * home page.
 */
@Component({
  selector: 'app-sign-in-page',
  templateUrl: './sign-in-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInPage {
  private readonly auth = inject(AuthApiClient);
  private readonly tokens = inject(TokenStore);
  private readonly router = inject(Router);

  /**
   * Bound from `?returnUrl=` by `withComponentInputBinding()`.
   *
   * The transform is load-bearing. When the query parameter is absent — i.e. anyone who
   * reaches /sign-in from the nav rather than by being redirected — the router binds
   * `undefined`, which OVERRIDES the declared default. Without this, a successful sign-in
   * left the user sitting on the sign-in page.
   */
  readonly returnUrl = input('/quotes', {
    transform: (value: string | undefined) => value ?? '/quotes',
  });

  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly busy = signal(false);
  protected readonly failure = signal<string | null>(null);

  protected onEmail(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
  }

  protected onPassword(event: Event): void {
    this.password.set((event.target as HTMLInputElement).value);
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    const outcome = await this.auth.signIn(this.email(), this.password());
    // Never keep the password around longer than the request needs it.
    this.password.set('');
    this.busy.set(false);

    if (outcome.status === 'rejected') {
      this.failure.set(outcome.message);
      return;
    }

    this.tokens.setAccessToken(outcome.tokens.accessToken, outcome.tokens.expiresIn);
    // `returnUrl` is a same-origin path produced by our own guard, but it arrives through
    // the URL bar, so refuse anything that could send the user off-site.
    const target = this.returnUrl();
    await this.router.navigateByUrl(target.startsWith('/') && !target.startsWith('//') ? target : '/quotes');
  }
}
