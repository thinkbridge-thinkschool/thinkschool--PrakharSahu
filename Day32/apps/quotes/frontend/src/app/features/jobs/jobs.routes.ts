import { Routes } from '@angular/router';

/**
 * The jobs feature, mounted by the root config at `/jobs`.
 *
 * One route today, and still its own file rather than a `loadComponent` in the root table:
 * the feature owns its routing the same way quotes does, so adding `/jobs/:id` later is a
 * change here rather than a change to the app shell.
 *
 * NO `authGuard`. `GET /api/jobs` carries no `.RequireAuthorization()` on the server, so
 * guarding the route would lock users out of data the API hands to anyone. Enqueue and
 * cancel are gated inside the page instead, which is where the token is actually required.
 */
export const jobsRoutes: Routes = [
  {
    path: '',
    title: 'Background jobs',
    loadComponent: () => import('./ui/jobs-page/jobs-page').then((m) => m.JobsPage),
  },
];
