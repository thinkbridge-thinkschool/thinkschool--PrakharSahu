import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthApiClient } from './core/auth/auth-api.client';
import { TokenStore } from './core/auth/token-store';

/** Application shell: navigation and the outlet. No feature state lives here. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly tokens = inject(TokenStore);
  private readonly auth = inject(AuthApiClient);

  /** Revokes the refresh token server-side and clears the cookie, then drops local state. */
  protected async signOut(): Promise<void> {
    await this.auth.signOut();
    this.tokens.clear();
  }
}
