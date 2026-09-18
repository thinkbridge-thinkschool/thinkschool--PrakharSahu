import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiError } from '../../../core/http/api-error';
import { QUOTES_API_BASE_URL } from '../../../core/config/quotes-api.config';
import { CreateJobRequest, Job, JobList, isJob, isJobList } from '../domain/job';

/**
 * Transport for `/api/jobs`.
 *
 * Same division of labour as `QuotesApiClient`: this owns the URL and the response-shape
 * check and nothing else. Auth headers, retries and error mapping are interceptor concerns,
 * so every failure that escapes here is already an `ApiError`.
 *
 * ONE NOTE ON RETRIES. `retryIdempotentInterceptor` replays GETs, which means a poll that
 * hits a 503 is retried with backoff inside the single call below. That is why the store
 * does not add a backoff of its own — it would be the second layer, multiplying the first.
 */
@Injectable({ providedIn: 'root' })
export class JobsApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(QUOTES_API_BASE_URL);

  /**
   * `POST /api/jobs` — **202 Accepted**, not 201, `JobEndpoints.cs:58`.
   *
   * The status code is the contract: 202 says the work was accepted, not that it happened.
   * The body is the job in its `Queued` state and the `Location` header points at where the
   * result will eventually appear, which is exactly what the poller needs.
   *
   * Needs a token (`.RequireAuthorization()`), because an anonymous caller who can enqueue
   * expensive work has a denial-of-service primitive.
   *
   * Two failures are worth knowing apart, and both arrive as an `ApiError` carrying the
   * server's own sentence: 400 for an unknown job type (the message lists the registered
   * handlers) and 503 while the host is shutting down and the queue is closed.
   */
  async enqueue(request: CreateJobRequest): Promise<Job> {
    const body = await firstValueFrom(this.http.post<unknown>(`${this.baseUrl}/jobs`, request));

    if (!isJob(body)) {
      throw new ApiError({
        kind: 'unknown',
        status: 202,
        friendlyMessage: 'The job was accepted but the response was unreadable.',
        cause: body,
      });
    }
    return body;
  }

  /**
   * `GET /api/jobs` — the recent jobs and the live queue depth, `JobEndpoints.cs:121`.
   *
   * The poller uses this rather than `GET /api/jobs/{id}` per active job: one request per
   * tick regardless of how many jobs are in flight, and the queue depth comes along for free.
   * Anonymous, like the quote reads.
   *
   * `limit` is clamped server-side to 1–200, so an out-of-range value is corrected rather
   * than refused.
   */
  async listJobs(limit?: number): Promise<JobList> {
    const url =
      limit === undefined ? `${this.baseUrl}/jobs` : `${this.baseUrl}/jobs?limit=${limit}`;
    const body = await firstValueFrom(this.http.get<unknown>(url));

    if (!isJobList(body)) {
      throw new ApiError({
        kind: 'unknown',
        status: 200,
        friendlyMessage: 'The Quotes API returned a job list this app does not understand.',
        cause: body,
      });
    }
    return body;
  }

  /**
   * `GET /api/jobs/{id}` — where the 202's `Location` header points.
   *
   * A 404 here does not only mean "no such id". Job history is in-process and capped at 500
   * entries, so a restart or enough newer jobs will lose one that really did run; the server
   * says as much in the message, and the mapper surfaces it.
   */
  async getJob(id: string): Promise<Job> {
    const body = await firstValueFrom(this.http.get<unknown>(`${this.baseUrl}/jobs/${id}`));

    if (!isJob(body)) {
      throw new ApiError({
        kind: 'unknown',
        status: 200,
        friendlyMessage: 'The Quotes API returned something this app does not understand.',
        cause: body,
      });
    }
    return body;
  }

  /**
   * `DELETE /api/jobs/{id}` — **202 Accepted**, `JobEndpoints.cs:133`.
   *
   * 202 and not 204 because cancellation is a request, not an instruction: the job stops the
   * next time its handler observes the token. The returned job is therefore usually still
   * `Running` — the caller must keep polling rather than treat the response as the outcome.
   *
   * 409 is the interesting failure and comes in two flavours the server distinguishes: the
   * job already finished, or it has not started yet and so has no token to signal.
   */
  async cancel(id: string): Promise<Job> {
    const body = await firstValueFrom(this.http.delete<unknown>(`${this.baseUrl}/jobs/${id}`));

    if (!isJob(body)) {
      throw new ApiError({
        kind: 'unknown',
        status: 202,
        friendlyMessage: 'The cancellation was accepted but the response was unreadable.',
        cause: body,
      });
    }
    return body;
  }
}
