import { HttpRequest, provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { HTTP_INTERCEPTOR_CHAIN } from '../../../app.config';
import { TokenStore } from '../../../core/auth/token-store';
import { provideQuotesApiBaseUrl } from '../../../core/config/quotes-api.config';
import { provideRetryPolicy } from '../../../core/http/retry-idempotent.interceptor';
import { Job, JobStatus, SIMULATE_JOB } from '../domain/job';
import { JobsStore, provideJobsPollInterval } from './jobs-store';

import { clearBrowserState } from '../../../../testing/browser-state';

const LIST_URL = '/api/jobs?limit=25';

function job(id: string, status: JobStatus, extra: Partial<Job> = {}): Job {
  return {
    id,
    type: SIMULATE_JOB,
    status,
    createdAt: '2026-09-13T09:00:00+00:00',
    startedAt: null,
    completedAt: null,
    queueLatencyMs: null,
    durationMs: null,
    progress: null,
    result: null,
    error: null,
    ...extra,
  };
}

function list(jobs: readonly Job[], queueDepth = 0) {
  return { queueDepth, jobs };
}

const isPoll = (request: HttpRequest<unknown>) =>
  request.method === 'GET' && request.url === LIST_URL;

describe('JobsStore', () => {
  let store: JobsStore;
  let httpTesting: HttpTestingController;
  let tokens: TokenStore;

  /**
   * Lets the microtask queue and one round of timers drain.
   *
   * The loop is a chain of `setTimeout`s rather than an interval, so advancing it means
   * handing control back to the event loop rather than ticking a clock.
   */
  const turn = () => new Promise((resolve) => setTimeout(resolve, 0));

  /**
   * Waits for the next poll and hands it back.
   *
   * Doubles as the assertion that polls never overlap: finding two in flight at once means
   * the chain forked, which is the failure `setInterval` would produce and this design is
   * built to avoid.
   */
  async function nextPoll(): Promise<TestRequest> {
    for (let attempt = 0; attempt < 12; attempt++) {
      const [first, ...rest] = httpTesting.match(isPoll);
      if (rest.length > 0) {
        throw new Error(`${rest.length + 1} polls were in flight at once`);
      }
      if (first) {
        return first;
      }
      await turn();
    }
    throw new Error('no poll was issued');
  }

  /** Gives the loop several turns to misbehave, then asserts it issued nothing. */
  async function expectQuiet(): Promise<void> {
    for (let attempt = 0; attempt < 6; attempt++) {
      await turn();
    }
    httpTesting.expectNone(() => true);
  }

  beforeEach(() => {
    // TokenStore restores from sessionStorage at construction, so a session written by an
    // earlier test would leak into this one and make 'signed out' cases pass wrongly.
    clearBrowserState();
    TestBed.configureTestingModule({
      providers: [
        // The real chain, so failures arrive as ApiError exactly as they do in the app.
        provideHttpClient(withInterceptors([...HTTP_INTERCEPTOR_CHAIN])),
        provideHttpClientTesting(),
        provideQuotesApiBaseUrl('/api'),
        // Retries off: a 5xx GET is replayed twice in production, which is Day 15's behaviour
        // and Day 15's tests. Leaving it on here would make every failure case flush three
        // times and assert nothing extra about this store.
        provideRetryPolicy({ maxRetries: 0, baseDelayMs: 0, maxDelayMs: 0 }),
        // Zero interval, so the chain advances on event-loop turns instead of wall-clock
        // time. Each tick still blocks on its response, so the test stays in control.
        provideJobsPollInterval(0),
        JobsStore,
      ],
    });
    store = TestBed.inject(JobsStore);
    httpTesting = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStore);
    tokens.setAccessToken('a-token', 3600);
  });

  afterEach(() => {
    // Stop the loop before verifying, or a tick scheduled during the last assertion shows up
    // as an unexpected request.
    store.dispose();
    httpTesting.verify({ ignoreCancelled: true });
  });

  describe('loading', () => {
    it('starts idle without calling the API', () => {
      expect(store.viewStatus()).toBe('idle');
      expect(store.isPolling()).toBe(false);
      httpTesting.expectNone(() => true);
    });

    it('asks for a bounded slice of history, not everything', async () => {
      const pending = store.load();
      const request = httpTesting.expectOne(isPoll);

      expect(request.request.method).toBe('GET');
      request.flush(list([]));
      await pending;
    });

    it('reports the payload and the queue depth', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Succeeded')], 4));
      await pending;

      expect(store.viewStatus()).toBe('ready');
      expect(store.jobs().map((j) => j.id)).toEqual(['a']);
      expect(store.queueDepth()).toBe(4);
    });

    it('is empty, not ready-with-nothing, when the server has no history', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([]));
      await pending;

      expect(store.viewStatus()).toBe('empty');
    });

    it('rejects a body whose status is not one the server can produce', async () => {
      const pending = store.load();
      httpTesting
        .expectOne(isPoll)
        .flush({ queueDepth: 0, jobs: [{ ...job('a', 'Queued'), status: 'Pending' }] });
      await pending;

      // Unrecognised is not "probably fine": status drives both the template and the decision
      // to keep polling, so it fails loudly instead of rendering a blank row forever.
      expect(store.viewStatus()).toBe('failed');
      expect(store.error()?.kind).toBe('unknown');
    });
  });

  describe('the polling loop', () => {
    it('keeps polling while a job is unfinished, one request at a time', async () => {
      const pending = store.load();
      httpTesting
        .expectOne(isPoll)
        .flush(list([job('a', 'Running', { progress: 'Step 1 of 10.' })]));
      await pending;

      expect(store.isPolling()).toBe(true);

      // nextPoll throws if it ever sees two in flight together.
      const second = await nextPoll();
      second.flush(list([job('a', 'Running', { progress: 'Step 4 of 10.' })]));

      const third = await nextPoll();
      third.flush(list([job('a', 'Running', { progress: 'Step 9 of 10.' })]));
      await turn();

      expect(store.jobs().at(0)?.progress).toBe('Step 9 of 10.');
    });

    it('stops once every job is terminal', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      const second = await nextPoll();
      second.flush(
        list([job('a', 'Succeeded', { result: '3 quotes across 2 authors.', durationMs: 1200 })]),
      );

      // Nothing on screen can change until the user acts, so the timer must not keep firing.
      await expectQuiet();
      expect(store.isPolling()).toBe(false);
      expect(store.activeCount()).toBe(0);
      expect(store.jobs().at(0)?.result).toBe('3 quotes across 2 authors.');
    });

    it('never starts when the first response is already all terminal', async () => {
      const pending = store.load();
      httpTesting
        .expectOne(isPoll)
        .flush(list([job('a', 'Failed', { error: 'Simulated failure.' })]));
      await pending;

      await expectQuiet();
      expect(store.isPolling()).toBe(false);
    });

    it('does not fork the loop when Refresh is pressed mid-poll', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      const inFlight = await nextPoll();
      // A manual refresh while a poll is out must join it, not race it.
      const manual = store.load();
      httpTesting.expectNone(isPoll);

      inFlight.flush(list([job('a', 'Running')]));
      await manual;

      const next = await nextPoll();
      next.flush(list([job('a', 'Cancelled')]));
      await expectQuiet();
    });
  });

  describe('when polling fails', () => {
    it('keeps the rows it already has and flags them stale', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      (await nextPoll()).flush('', { status: 500, statusText: 'Server Error' });
      await turn();

      // Rows that were correct a moment ago are still the best information available.
      expect(store.viewStatus()).toBe('ready');
      expect(store.jobs()).toHaveLength(1);
      expect(store.error()?.kind).toBe('server');
    });

    it('fails outright only when there is nothing on screen to keep', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush('', { status: 500, statusText: 'Server Error' });
      await pending;

      expect(store.viewStatus()).toBe('failed');
    });

    it('gives up after three failures in a row and stops issuing requests', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      for (let attempt = 0; attempt < 3; attempt++) {
        (await nextPoll()).flush('', { status: 503, statusText: 'Unavailable' });
        await turn();
      }

      expect(store.hasGivenUp()).toBe(true);
      expect(store.isPolling()).toBe(false);
      // The point of giving up: stop hammering a server that is already in trouble.
      await expectQuiet();
    });

    it('counts consecutively, so a success in between resets it', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      (await nextPoll()).flush('', { status: 503, statusText: 'Unavailable' });
      await turn();
      (await nextPoll()).flush('', { status: 503, statusText: 'Unavailable' });
      await turn();
      (await nextPoll()).flush(list([job('a', 'Running')]));
      await turn();
      (await nextPoll()).flush('', { status: 503, statusText: 'Unavailable' });
      await turn();

      // Four failures total, but never three in a row.
      expect(store.hasGivenUp()).toBe(false);
    });

    it('resumes when asked, and clears the banner on success', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      for (let attempt = 0; attempt < 3; attempt++) {
        (await nextPoll()).flush('', { status: 503, statusText: 'Unavailable' });
        await turn();
      }
      expect(store.hasGivenUp()).toBe(true);

      const resumed = store.resume();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await resumed;

      expect(store.hasGivenUp()).toBe(false);
      expect(store.error()).toBeNull();

      (await nextPoll()).flush(list([job('a', 'Succeeded')]));
      await expectQuiet();
    });
  });

  describe('queueing a job', () => {
    it('shows the accepted job immediately and starts watching it', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([]));
      await pending;
      expect(store.isPolling()).toBe(false);

      const submitted = store.submit({ type: SIMULATE_JOB, payload: '{"durationMs":3000}' });
      const post = httpTesting.expectOne('/api/jobs');
      expect(post.request.method).toBe('POST');
      // 202 Accepted, and the body is the job in its Queued state.
      post.flush(job('new', 'Queued'), { status: 202, statusText: 'Accepted' });
      await submitted;

      expect(store.jobs().map((j) => j.id)).toEqual(['new']);
      expect(store.createState()).toEqual({ kind: 'accepted', job: job('new', 'Queued') });

      // There is something to watch now, so the loop must wake up.
      (await nextPoll()).flush(list([job('new', 'Running')]));
      await turn();
      expect(store.jobs().at(0)?.status).toBe('Running');
    });

    it('does not let a poll issued before the submit drop the new job', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;

      // This poll was answered from a list that predates the job below.
      const stalePoll = await nextPoll();

      const submitted = store.submit({ type: SIMULATE_JOB });
      httpTesting
        .expectOne('/api/jobs')
        .flush(job('new', 'Queued'), { status: 202, statusText: 'Accepted' });
      await submitted;

      stalePoll.flush(list([job('a', 'Running')]));
      await turn();

      // Applying the stale response would have dropped 'new' until the next tick brought it
      // back — a flicker that reads as a lost job.
      expect(store.jobs().map((j) => j.id)).toContain('new');
    });

    it('refuses a second submit while one is in flight', async () => {
      const first = store.submit({ type: SIMULATE_JOB });
      const second = await store.submit({ type: SIMULATE_JOB });

      expect(second).toBeNull();
      httpTesting
        .expectOne('/api/jobs')
        .flush(job('new', 'Queued'), { status: 202, statusText: 'Accepted' });
      await first;
    });

    it('names the shutdown case rather than reporting a generic server error', async () => {
      const submitted = store.submit({ type: SIMULATE_JOB });
      httpTesting
        .expectOne('/api/jobs')
        .flush(
          { message: 'The service is shutting down. Retry shortly.' },
          { status: 503, statusText: 'Unavailable' },
        );

      expect(await submitted).toBeNull();
      expect(store.createState()).toEqual({
        kind: 'failed',
        message: 'The API is shutting down and is not accepting new jobs. Try again in a moment.',
      });
    });

    it('passes an unknown-type 400 through verbatim, because it lists the real handlers', async () => {
      const submitted = store.submit({ type: 'nope' });
      httpTesting
        .expectOne('/api/jobs')
        .flush(
          { message: "Unknown job type 'nope'. Known types: quote-report, simulate" },
          { status: 400, statusText: 'Bad Request' },
        );
      await submitted;

      const state = store.createState();
      expect(state.kind === 'failed' && state.message).toContain('quote-report, simulate');
    });
  });

  describe('cancelling', () => {
    async function loadRunning(): Promise<void> {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;
    }

    it('stays marked until the job is actually terminal, not until the DELETE returns', async () => {
      await loadRunning();
      const inFlightPoll = await nextPoll();

      const cancelled = store.cancel('a');
      const del = httpTesting.expectOne('/api/jobs/a');
      expect(del.request.method).toBe('DELETE');
      // 202: asked, not stopped. The job is still Running in this very response.
      del.flush(job('a', 'Running'), { status: 202, statusText: 'Accepted' });
      await cancelled;

      expect(store.isCancelling('a')).toBe(true);

      inFlightPoll.flush(list([job('a', 'Running')]));
      await turn();
      // Still running, so still marked — flipping back to "Cancel" here would invite a second
      // pointless DELETE.
      expect(store.isCancelling('a')).toBe(true);

      (await nextPoll()).flush(list([job('a', 'Cancelled', { error: 'The job was cancelled.' })]));
      await turn();
      expect(store.isCancelling('a')).toBe(false);
    });

    it('clears the mark for a job that succeeded before the token was observed', async () => {
      await loadRunning();
      const inFlightPoll = await nextPoll();

      const cancelled = store.cancel('a');
      httpTesting
        .expectOne('/api/jobs/a')
        .flush(job('a', 'Running'), { status: 202, statusText: 'Accepted' });
      await cancelled;

      inFlightPoll.flush(
        list([job('a', 'Succeeded', { result: 'Completed 3000ms of simulated work.' })]),
      );
      await turn();

      // Every terminal state clears it, not just Cancelled — otherwise a job that finished in
      // the gap would show "Cancelling…" forever.
      expect(store.isCancelling('a')).toBe(false);
    });

    it('surfaces the 409 the server writes, and unmarks the row', async () => {
      await loadRunning();

      const cancelled = store.cancel('a');
      httpTesting
        .expectOne('/api/jobs/a')
        .flush(
          {
            message:
              'The job has not started yet, so it cannot be cancelled. Retry once it is Running.',
          },
          { status: 409, statusText: 'Conflict' },
        );
      await cancelled;

      expect(store.isCancelling('a')).toBe(false);
      expect(store.removalFailures()).toEqual([
        {
          id: 'a',
          message:
            'The job has not started yet, so it cannot be cancelled. Retry once it is Running.',
        },
      ]);

      store.dismissFailure('a');
      expect(store.removalFailures()).toEqual([]);
    });

    it('ignores a second click while the first cancellation is pending', async () => {
      await loadRunning();

      const first = store.cancel('a');
      await store.cancel('a');

      httpTesting
        .expectOne('/api/jobs/a')
        .flush(job('a', 'Running'), { status: 202, statusText: 'Accepted' });
      await first;
    });
  });

  describe('a backgrounded tab', () => {
    let visibility = 'visible';

    beforeEach(() => {
      visibility = 'visible';
      Object.defineProperty(document, 'visibilityState', {
        configurable: true,
        get: () => visibility,
      });
    });

    afterEach(() => {
      // Restore the real accessor so the next spec file is not left with this stub.
      delete (document as unknown as Record<string, unknown>)['visibilityState'];
    });

    it('pauses while hidden and catches up on return', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;
      expect(store.isPolling()).toBe(true);

      visibility = 'hidden';
      document.dispatchEvent(new Event('visibilitychange'));

      // Browsers throttle background timers but do not stop the requests, so this has to be
      // handled rather than relied upon.
      await expectQuiet();
      expect(store.isPolling()).toBe(false);
      expect(store.isPaused()).toBe(true);

      visibility = 'visible';
      document.dispatchEvent(new Event('visibilitychange'));

      // Polls immediately rather than waiting out an interval, so the page is current by the
      // time the user has finished looking at it.
      (await nextPoll()).flush(list([job('a', 'Succeeded')]));
      await turn();
      expect(store.isPaused()).toBe(false);
    });
  });

  describe('disposal', () => {
    it('stops the loop, so a destroyed page cannot keep polling', async () => {
      const pending = store.load();
      httpTesting.expectOne(isPoll).flush(list([job('a', 'Running')]));
      await pending;
      expect(store.isPolling()).toBe(true);

      store.dispose();

      await expectQuiet();
      expect(store.isPolling()).toBe(false);
    });
  });
});
