import {
  DestroyRef,
  Injectable,
  InjectionToken,
  Provider,
  computed,
  inject,
  signal,
} from '@angular/core';

import { ApiError } from '../../../core/http/api-error';
import { TokenStore } from '../../../core/auth/token-store';
import { JobsApiClient } from '../data-access/jobs-api.client';
import { CreateJobRequest, Job, isTerminal } from '../domain/job';

/** The mutually exclusive things the job list can be showing. */
export type JobsViewStatus = 'idle' | 'loading' | 'ready' | 'empty' | 'failed';

/** Where the enqueue form got to. Mirrors `CreateState` in the quotes feature. */
export type SubmitState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'submitting' }
  | { readonly kind: 'accepted'; readonly job: Job }
  | { readonly kind: 'failed'; readonly message: string };

/** A cancellation the server refused, kept per job so two failures do not overwrite each other. */
export interface CancelFailure {
  readonly id: string;
  readonly message: string;
}

/**
 * How long to wait between polls.
 *
 * An injection token rather than a constant for the same reason `RETRY_POLICY` is one: the
 * tests need it at zero, and reaching into the store to patch a private field would be
 * testing a different object than the one that ships.
 *
 * 1500ms is a compromise, not a magic number. `SimulatedWorkHandler` reports progress in
 * tenths, so a three-second job changes roughly twice as fast as this polls — fast enough
 * that the row visibly moves, slow enough that watching one job for a minute is forty
 * requests rather than six hundred.
 */
export const JOBS_POLL_INTERVAL_MS = new InjectionToken<number>('JOBS_POLL_INTERVAL_MS', {
  providedIn: 'root',
  factory: () => 1500,
});

export function provideJobsPollInterval(milliseconds: number): Provider {
  return { provide: JOBS_POLL_INTERVAL_MS, useValue: milliseconds };
}

/**
 * Polling stops after this many failures in a row, and waits to be told to start again.
 *
 * The case this exists for is the API being down. Without it the page sits there issuing a
 * doomed request every 1.5 seconds for as long as the tab is open — and because
 * `retryIdempotentInterceptor` replays each one twice, that is three requests a tick against
 * a server that is already in trouble.
 */
const MAX_CONSECUTIVE_POLL_FAILURES = 3;

/** Recent history is enough; the server retains 500 and clamps this to 1–200 anyway. */
const LIST_LIMIT = 25;

/**
 * State for the jobs feature: the polling loop, the enqueue form's outcome, and cancellation.
 *
 * PROVIDED BY THE PAGE, NOT IN ROOT — and unlike `CreateQuoteStore`, which is page-scoped
 * because half-typed text is view state, this one is page-scoped because it owns a TIMER. A
 * root-scoped poller would keep polling after the user navigated to the quote list and for
 * the rest of the session. Page scope means the timer is torn down by the same `DestroyRef`
 * that disposes the component.
 *
 * WHY POLL AT ALL. `POST /api/jobs` answers 202 with a job id and nothing else; the work
 * happens on `JobProcessor`'s loop, off the request thread, and the server has no way to push
 * the outcome back. Polling `GET /api/jobs` is the client half of that contract. It is also
 * the part that is easy to get wrong, so the loop is built around four rules:
 *
 *   1. ONE REQUEST AT A TIME. The next poll is scheduled after the previous one settles
 *      (`setTimeout` chained, never `setInterval`). On a slow connection `setInterval` stacks
 *      requests faster than they complete and the queue of pending polls grows without bound.
 *   2. STOP WHEN THERE IS NOTHING TO WATCH. If every job is terminal the loop stops rather
 *      than idling. Nothing on screen can change until the user acts, and a timer that fires
 *      forever to learn nothing is just a battery drain with a job list attached.
 *   3. STOP WHEN THE TAB IS HIDDEN. Browsers throttle background timers but do not stop the
 *      requests, so a backgrounded tab keeps its session alive against the API for hours.
 *      Becoming visible again polls immediately, which also catches up whatever was missed.
 *   4. GIVE UP AFTER REPEATED FAILURES, and say so, rather than hammering a server that is
 *      already failing.
 */
@Injectable()
export class JobsStore {
  private readonly api = inject(JobsApiClient);
  private readonly tokens = inject(TokenStore);
  private readonly intervalMs = inject(JOBS_POLL_INTERVAL_MS);
  private readonly destroyRef = inject(DestroyRef);

  private readonly serverJobs = signal<readonly Job[]>([]);
  private readonly depth = signal(0);
  private readonly status = signal<JobsViewStatus>('idle');
  private readonly loadError = signal<ApiError | null>(null);
  private readonly submitState = signal<SubmitState>({ kind: 'idle' });
  private readonly cancelRequested = signal<ReadonlySet<string>>(new Set());
  private readonly cancelFailures = signal<readonly CancelFailure[]>([]);

  private readonly polling = signal(false);
  private readonly consecutiveFailures = signal(0);
  /** True once the loop has given up. Only `resume()` clears it. */
  private readonly suspended = signal(false);
  private readonly hidden = signal(false);

  private timer: ReturnType<typeof setTimeout> | null = null;
  private pending: Promise<void> | null = null;
  private started = false;
  private disposed = false;

  /**
   * Guards against out-of-order responses, exactly as `QuotesStore.loadToken` does — but it
   * earns its keep harder here, because polling means there is nearly always one in flight.
   * `submit()` bumps it too: a poll issued before the new job existed must not be allowed to
   * overwrite the list and drop the row the user just created.
   */
  private loadToken = 0;

  constructor() {
    this.hidden.set(this.documentHidden());

    const onVisibilityChange = () => this.onVisibilityChanged();
    document.addEventListener('visibilitychange', onVisibilityChange);

    this.destroyRef.onDestroy(() => {
      document.removeEventListener('visibilitychange', onVisibilityChange);
      this.dispose();
    });
  }

  // ---- what the UI reads -------------------------------------------------------------

  readonly jobs = computed(() => this.serverJobs());
  readonly queueDepth = computed(() => this.depth());
  readonly viewStatus = computed(() => this.status());
  readonly error = computed(() => this.loadError());
  readonly createState = this.submitState.asReadonly();
  readonly isSubmitting = computed(() => this.submitState().kind === 'submitting');
  readonly removalFailures = computed(() => this.cancelFailures());
  readonly isPolling = computed(() => this.polling());
  readonly hasGivenUp = computed(() => this.suspended());
  readonly isPaused = computed(() => this.hidden() && this.activeCount() > 0);

  /** Exposed so the page can state the cadence rather than leaving the user to guess it. */
  readonly pollIntervalMs = this.intervalMs;

  /** Jobs the worker has not finished with. Also the answer to "should the loop keep going?". */
  readonly activeCount = computed(
    () => this.serverJobs().filter((job) => !isTerminal(job.status)).length,
  );

  /**
   * `POST` and `DELETE` both carry `.RequireAuthorization()`; `GET /api/jobs` does not. So
   * the list is readable signed out and only the controls are gated — the same split the
   * quotes page makes, and the reason there is no `authGuard` on this route.
   */
  readonly canManage = this.tokens.isSignedIn;

  /**
   * True from the moment cancellation is requested until the job actually reaches a terminal
   * state — NOT until the DELETE returns.
   *
   * The server answers 202, meaning "asked", and the job keeps running until its handler next
   * observes the token. Clearing this on the response would flip the button back to "Cancel"
   * on a job that is already stopping, and invite a second pointless DELETE.
   */
  isCancelling(id: string): boolean {
    return this.cancelRequested().has(id);
  }

  // ---- commands ----------------------------------------------------------------------

  /**
   * Fetches once and arms the loop if anything is still running.
   *
   * The single entry point: the page's `ngOnInit`, the Refresh button and the visibility
   * handler all call this, so there is one place where "fetch, then decide whether to keep
   * fetching" is expressed.
   */
  async load(): Promise<void> {
    this.started = true;
    await this.refresh();
    this.schedule();
  }

  /** Clears the give-up state and starts over. Bound to the button the failure banner shows. */
  async resume(): Promise<void> {
    this.suspended.set(false);
    this.consecutiveFailures.set(0);
    await this.load();
  }

  /**
   * `POST /api/jobs`.
   *
   * Resolves with the accepted job, or null if the server refused it. The returned job is
   * `Queued` — it has not run and may never run, which is the whole meaning of a 202.
   */
  async submit(request: CreateJobRequest): Promise<Job | null> {
    if (this.isSubmitting()) {
      return null; // a second click must not enqueue a second job
    }
    this.submitState.set({ kind: 'submitting' });

    try {
      const job = await this.api.enqueue(request);

      // Invalidate anything in flight before writing. A poll issued a moment ago was answered
      // from a list that predates this job; letting it land would drop the new row until the
      // next tick brought it back, which reads as a flicker and looks like a lost job.
      this.loadToken += 1;
      this.serverJobs.update((jobs) => [job, ...jobs]);
      this.status.set('ready');
      this.submitState.set({ kind: 'accepted', job });

      // There is now something to watch, so the loop may need waking.
      this.schedule();
      return job;
    } catch (failure) {
      this.submitState.set({ kind: 'failed', message: this.explainSubmit(failure) });
      return null;
    }
  }

  /**
   * `DELETE /api/jobs/{id}` — a request to stop, not an instruction.
   *
   * Not applied optimistically, and that is the difference from `QuotesStore.remove`. A
   * delete either happens or is refused; a cancellation is *asked for*, and a handler that
   * finishes first simply succeeds. Showing "Cancelled" immediately would be a guess that the
   * next poll frequently contradicts, so the row is marked as stopping and the server's own
   * answer is what changes the status.
   */
  async cancel(id: string): Promise<void> {
    if (this.cancelRequested().has(id)) {
      return;
    }

    this.markCancelRequested(id);
    this.dismissFailure(id);

    try {
      await this.api.cancel(id);
      // Deliberately discarding the 202 body. It is a snapshot taken before the handler had a
      // chance to observe the token, so it almost always still says Running — patching it in
      // could overwrite a poll that already landed with something fresher.
      this.schedule();
    } catch (failure) {
      this.unmarkCancelRequested(id);
      const error = this.asApiError(failure);
      this.cancelFailures.update((all) => [...all, { id, message: this.explainCancel(error) }]);
    }
  }

  dismissFailure(id: string): void {
    this.cancelFailures.update((all) => all.filter((failure) => failure.id !== id));
  }

  dismissSubmitOutcome(): void {
    this.submitState.set({ kind: 'idle' });
  }

  /** Stops the loop and abandons anything in flight. Called on destroy. */
  dispose(): void {
    this.disposed = true;
    this.clearTimer();
    this.polling.set(false);
    this.loadToken += 1;
  }

  // ---- the loop ----------------------------------------------------------------------

  /**
   * Arms the next tick, if there is any reason to.
   *
   * Idempotent on purpose: `load`, `submit` and `cancel` all call it without knowing whether
   * a timer is already pending, and the `this.timer !== null` guard is what keeps that from
   * spawning a second loop that doubles the request rate.
   */
  private schedule(): void {
    if (this.timer !== null || this.disposed) {
      return;
    }

    // A request is already out. Whoever started it calls `schedule()` when it settles, so
    // arming a timer now would fire a tick that just joins the same in-flight promise and
    // schedules again — a tight loop of timers for as long as the response takes.
    if (this.pending !== null) {
      return;
    }

    if (this.suspended() || this.hidden() || this.activeCount() === 0) {
      this.polling.set(false);
      return;
    }

    this.polling.set(true);
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.tick();
    }, this.intervalMs);
  }

  /** Fetch, then decide whether to go round again — the chain that replaces `setInterval`. */
  private async tick(): Promise<void> {
    await this.refresh();
    this.schedule();
  }

  /**
   * `GET /api/jobs`, at most one at a time.
   *
   * Concurrent callers share the request in flight rather than issuing their own. Without
   * this a click on Refresh during a poll produces two requests whose responses race, and the
   * loser is decided by the network rather than by which one is newer.
   */
  async refresh(): Promise<void> {
    this.pending ??= this.runRefresh().finally(() => {
      this.pending = null;
    });
    return this.pending;
  }

  private async runRefresh(): Promise<void> {
    const ticket = ++this.loadToken;

    // Only the very first fetch is allowed to show a skeleton. A poll that blanked the list
    // every 1.5 seconds would be unreadable.
    if (this.status() === 'idle') {
      this.status.set('loading');
    }

    try {
      const list = await this.api.listJobs(LIST_LIMIT);
      if (ticket !== this.loadToken) {
        return; // superseded by a newer fetch, or by a submit
      }

      this.serverJobs.set(list.jobs);
      this.depth.set(list.queueDepth);
      this.status.set(list.jobs.length === 0 ? 'empty' : 'ready');
      this.loadError.set(null);
      this.consecutiveFailures.set(0);
      this.suspended.set(false);
      this.pruneCancelRequests(list.jobs);
    } catch (failure) {
      if (ticket !== this.loadToken) {
        return;
      }

      const failures = this.consecutiveFailures() + 1;
      this.consecutiveFailures.set(failures);
      this.loadError.set(this.asApiError(failure));

      // A failed poll must not throw away rows that are still perfectly good information.
      // 'failed' is only for the case where there is nothing on screen to keep.
      if (this.serverJobs().length === 0) {
        this.status.set('failed');
      }

      if (failures >= MAX_CONSECUTIVE_POLL_FAILURES) {
        this.suspended.set(true);
        this.clearTimer();
        this.polling.set(false);
      }
    }
  }

  private onVisibilityChanged(): void {
    const hidden = this.documentHidden();
    this.hidden.set(hidden);

    if (hidden) {
      this.clearTimer();
      this.polling.set(false);
      return;
    }

    // Coming back: poll straight away rather than waiting out an interval, so the page is
    // current by the time the user has finished looking at it.
    if (this.started && !this.disposed && !this.suspended()) {
      void this.load();
    }
  }

  // ---- internals ---------------------------------------------------------------------

  /**
   * Drops the "stopping" marker once the server agrees the job is finished.
   *
   * Every terminal state clears it, not just `Cancelled`: a job that succeeded in the gap
   * between the DELETE being sent and the token being observed is finished, and leaving it
   * marked would show a permanent "Cancelling…" on a row that completed.
   */
  private pruneCancelRequests(jobs: readonly Job[]): void {
    const requested = this.cancelRequested();
    if (requested.size === 0) {
      return;
    }

    const stillRunning = new Set(
      jobs.filter((job) => !isTerminal(job.status)).map((job) => job.id),
    );
    // A job that has dropped out of the list entirely is finished as far as this page can
    // tell, so it is pruned too rather than marked forever.
    const next = new Set([...requested].filter((id) => stillRunning.has(id)));

    if (next.size !== requested.size) {
      this.cancelRequested.set(next);
    }
  }

  private markCancelRequested(id: string): void {
    this.cancelRequested.update((ids) => new Set(ids).add(id));
  }

  private unmarkCancelRequested(id: string): void {
    this.cancelRequested.update((ids) => {
      const next = new Set(ids);
      next.delete(id);
      return next;
    });
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  /** `visibilityState` is absent in some test environments; absent means visible. */
  private documentHidden(): boolean {
    return typeof document !== 'undefined' && document.visibilityState === 'hidden';
  }

  private explainSubmit(failure: unknown): string {
    if (!(failure instanceof ApiError)) {
      return 'The job could not be queued.';
    }
    switch (failure.kind) {
      case 'unauthorized':
        return 'Your session expired before the job was queued. Sign in and try again.';
      case 'server':
        // 503 is the specific one worth naming: the host is shutting down and
        // `ChannelJobQueue.Complete()` has closed the queue, so this is not a bug and
        // retrying against the next instance will work.
        return failure.status === 503
          ? 'The API is shutting down and is not accepting new jobs. Try again in a moment.'
          : failure.friendlyMessage;
      default:
        // A 400 arrives as the server's own DomainError sentence, and for an unknown type it
        // lists every registered handler — more useful than anything written here.
        return failure.friendlyMessage;
    }
  }

  private explainCancel(error: ApiError): string {
    switch (error.kind) {
      case 'conflict':
        // Two different 409s, and the server distinguishes them: already finished, or queued
        // and therefore holding no token to signal. Its own wording says which.
        return error.friendlyMessage;
      case 'unauthorized':
        return 'Your session expired, so the job was not cancelled. Sign in and try again.';
      case 'not-found':
        return 'That job is no longer in the server’s history, so it could not be cancelled.';
      default:
        return `The job could not be cancelled. ${error.friendlyMessage}`;
    }
  }

  private asApiError(failure: unknown): ApiError {
    // The mapping interceptor guarantees this; the check exists so a future bug surfaces
    // loudly instead of rendering "[object Object]".
    return failure instanceof ApiError
      ? failure
      : new ApiError({
          kind: 'unknown',
          status: 0,
          friendlyMessage: 'Something went wrong.',
          cause: failure,
        });
  }
}
