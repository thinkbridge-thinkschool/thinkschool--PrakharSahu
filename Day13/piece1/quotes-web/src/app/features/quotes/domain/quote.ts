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

/** How the visible list is ordered. */
export type SortOrder = 'newest' | 'oldest';

/** One row of the "who is quoted most" strip. */
export interface AuthorTally {
  readonly author: string;
  readonly count: number;
}
