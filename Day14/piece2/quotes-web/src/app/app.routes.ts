import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    // Lazily loaded so the feature — store, transport and all three components — stays a
    // separate chunk from the shell.
    loadComponent: () =>
      import('./features/quotes/ui/quotes-page/quotes-page').then((m) => m.QuotesPage),
    title: 'Quotes',
  },
  { path: '**', redirectTo: '' },
];
