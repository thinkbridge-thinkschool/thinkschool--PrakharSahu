/**
 * A quote exactly as `GET /api/quotes` puts it on the wire.
 *
 * Field names mirror the Week-1 `QuotesApi.Models.Quote` entity after ASP.NET's default
 * camelCase serialisation. Verified against the running API on 2026-08-24:
 *
 *   {"id":1,"text":"…","author":"…","createdAt":"2026-08-10T10:00:00+00:00",
 *    "isDeleted":false,"userId":1}
 *
 * `isDeleted` and `userId` are part of the payload because the endpoint returns the
 * entity rather than a read DTO. They are modelled here so the contract is honest, even
 * though the repository already filters soft-deleted rows out server-side.
 */
export interface Quote {
  readonly id: number;
  readonly text: string;
  readonly author: string;
  /** ISO-8601 with an explicit offset (`+00:00`), not a `Z` suffix — parse, never string-compare. */
  readonly createdAt: string;
  readonly isDeleted: boolean;
  readonly userId: number;
}

/**
 * Runtime check that a parsed JSON value really is a `Quote`.
 *
 * `httpResource<Quote>` is an unchecked cast — the interface above is erased at compile
 * time, so anything the API sends is *claimed* to be a Quote. A `null` body, a paging
 * envelope, or a renamed field would otherwise flow into the view typed as something it
 * is not. This is the one place that turns "the server said so" into "we checked".
 */
export function isQuote(value: unknown): value is Quote {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate['id'] === 'number' &&
    typeof candidate['text'] === 'string' &&
    typeof candidate['author'] === 'string' &&
    typeof candidate['createdAt'] === 'string' &&
    typeof candidate['isDeleted'] === 'boolean' &&
    typeof candidate['userId'] === 'number'
  );
}

/** Runtime check for the bare array `GET /api/quotes` returns. */
export function isQuoteArray(value: unknown): value is Quote[] {
  return Array.isArray(value) && value.every(isQuote);
}

/** Raised when the API answers successfully with something that is not a quote. */
export class MalformedQuoteError extends Error {
  constructor(detail: string) {
    super(`The Quotes API returned a response that is not ${detail}.`);
    this.name = 'MalformedQuoteError';
  }
}

/** How the visible list is ordered. */
export type SortOrder = 'newest' | 'oldest';

/** One row of the "who is quoted most" strip. */
export interface AuthorTally {
  readonly author: string;
  readonly count: number;
}
