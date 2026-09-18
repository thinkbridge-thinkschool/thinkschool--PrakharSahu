/**
 * A background job as the API actually serves it.
 *
 * This is `JobResponse` (`Endpoints/JobEndpoints.cs:13`), NOT the `Job` entity. The server
 * projects deliberately so that an internal field cannot widen the public API by accident,
 * and this interface mirrors that projection rather than the entity behind it.
 *
 * Every optional field arrives as an explicit `null`, not as a missing key: they are
 * nullable C# properties, and System.Text.Json writes nulls by default. So `'progress' in
 * job` is always true and only the value tells you anything.
 */
export interface Job {
  /** A GUID string. Unlike a quote id this is not a number, so never coerce it. */
  readonly id: string;
  readonly type: string;
  readonly status: JobStatus;
  /** ISO-8601 with an explicit offset (`+00:00`), matching the quotes endpoints. */
  readonly createdAt: string;
  readonly startedAt: string | null;
  readonly completedAt: string | null;
  /** Time spent waiting in the queue. Null until the worker picks the job up. */
  readonly queueLatencyMs: number | null;
  /** Time spent running. Null until the job reaches a terminal state. */
  readonly durationMs: number | null;
  /** Free text the handler writes as it goes, so a slow job is not a black box. */
  readonly progress: string | null;
  /** Set on `Succeeded`, and only then. */
  readonly result: string | null;
  /** Set on `Failed` or `Cancelled`. A message, never a stack trace — the server strips it. */
  readonly error: string | null;
}

/**
 * The five states in `JobStatus` (`Models/Job.cs:12`), serialized as their enum NAMES.
 *
 * `job.Status.ToString()` in the projection is what makes these PascalCase strings rather
 * than the integers a raw enum would serialize to. Lowercasing them here would look tidier
 * and would silently stop matching the wire.
 */
export const JOB_STATUSES = ['Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled'] as const;

export type JobStatus = (typeof JOB_STATUSES)[number];

/**
 * The three states a job never leaves.
 *
 * The server keeps them distinct rather than collapsing them into one "Finished" flag, and
 * the distinction is the reason polling can stop: anything else means the worker still has
 * work to do and the row on screen will change again.
 */
const TERMINAL: ReadonlySet<JobStatus> = new Set<JobStatus>(['Succeeded', 'Failed', 'Cancelled']);

export function isTerminal(status: JobStatus): boolean {
  return TERMINAL.has(status);
}

/** What `GET /api/jobs` returns: the recent jobs plus the live queue depth. */
export interface JobList {
  /**
   * Jobs still waiting to be picked up.
   *
   * Worth surfacing because it is invisible in per-job durations: those stay flat while the
   * backlog grows, right up until the bounded channel is full and enqueues start blocking.
   */
  readonly queueDepth: number;
  /** Most recently created first — `InMemoryJobStore.List` orders by `CreatedAt` descending. */
  readonly jobs: readonly Job[];
}

/** The body `POST /api/jobs` accepts. `payload` is opaque to everything but the handler. */
export interface CreateJobRequest {
  readonly type: string;
  readonly payload?: string | null;
}

/**
 * The handler types registered on the server.
 *
 * Duplicated knowledge, and knowingly so: there is no endpoint that lists handler types, so
 * the alternative is a free-text box. The duplication is bounded — POST rejects an unknown
 * type with a 400 whose message names every registered handler, and that message is rendered
 * verbatim, so the server stays the authority even when this list falls behind.
 */
export const QUOTE_REPORT_JOB = 'quote-report';
export const SIMULATE_JOB = 'simulate';

/** Options `SimulatedWorkHandler` reads out of the payload. Both are optional there. */
export interface SimulateOptions {
  /** Clamped server-side to 0–120,000ms, so a silly number is corrected rather than refused. */
  readonly durationMs: number;
  readonly shouldFail: boolean;
}

export function simulatePayload(options: SimulateOptions): string {
  return JSON.stringify({ durationMs: options.durationMs, shouldFail: options.shouldFail });
}

/**
 * Runtime check that a parsed body really is a `Job`.
 *
 * `http.get<Job>()` is an unchecked cast — the interface is erased at runtime and nothing
 * verifies the body. Same reasoning as `isQuote`, with one addition: `status` drives a
 * `@switch` in the template and the decision to keep polling, so an unrecognised value would
 * quietly render nothing and poll forever.
 */
export function isJob(value: unknown): value is Job {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate['id'] === 'string' &&
    typeof candidate['type'] === 'string' &&
    isJobStatus(candidate['status']) &&
    typeof candidate['createdAt'] === 'string' &&
    isNullable(candidate['startedAt'], 'string') &&
    isNullable(candidate['completedAt'], 'string') &&
    isNullable(candidate['queueLatencyMs'], 'number') &&
    isNullable(candidate['durationMs'], 'number') &&
    isNullable(candidate['progress'], 'string') &&
    isNullable(candidate['result'], 'string') &&
    isNullable(candidate['error'], 'string')
  );
}

export function isJobStatus(value: unknown): value is JobStatus {
  return typeof value === 'string' && (JOB_STATUSES as readonly string[]).includes(value);
}

/** `GET /api/jobs` returns an envelope, unlike `GET /api/quotes` which returns a bare array. */
export function isJobList(value: unknown): value is JobList {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate['queueDepth'] === 'number' &&
    Array.isArray(candidate['jobs']) &&
    candidate['jobs'].every(isJob)
  );
}

/** `undefined` is rejected too: a missing key means the shape changed, and that is worth knowing. */
function isNullable(value: unknown, type: 'string' | 'number'): boolean {
  return value === null || typeof value === type;
}
