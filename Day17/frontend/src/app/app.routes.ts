import { Routes } from '@angular/router';

/**
 * Root route table. Everything is lazy; nothing but the shell is in the initial bundle.
 *
 * The quotes feature is mounted with `loadChildren` so it owns its own sub-routes (see
 * features/quotes/quotes.routes.ts) instead of the root file knowing about every page.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'quotes' },

  {
    path: 'quotes',
    loadChildren: () => import('./features/quotes/quotes.routes').then((m) => m.quotesRoutes),
  },

  {
    path: 'sign-in',
    title: 'Sign in',
    loadComponent: () =>
      import('./features/auth/ui/sign-in-page/sign-in-page').then((m) => m.SignInPage),
  },

  {
    path: 'register',
    title: 'Create an account',
    loadComponent: () =>
      import('./features/auth/ui/register-page/register-page').then((m) => m.RegisterPage),
  },

  {
    path: 'not-found',
    title: 'Not found',
    loadComponent: () =>
      import('./features/shell/not-found-page/not-found-page').then((m) => m.NotFoundPage),
  },

  // `redirectTo` would rewrite the address bar and lose what the user actually typed, so
  // the wildcard renders the not-found page in place and keeps the bad url visible.
  {
    path: '**',
    title: 'Not found',
    loadComponent: () =>
      import('./features/shell/not-found-page/not-found-page').then((m) => m.NotFoundPage),
  },
];
