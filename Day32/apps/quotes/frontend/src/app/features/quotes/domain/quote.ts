/**
 * A quote as `GET /api/quotes` actually returns it.
 *
 * Six fields, not three: the endpoint hands back the EF entity rather than a read model,
 * so `isDeleted` and `userId` are on the wire too. Recorded in
 * `contract/week1-api.recorded.ts` and pinned by the characterization spec.
 */
export interface Quote {
  readonly id: number;
  readonly text: string;
  readonly author: string;
  /** ISO-8601 with an explicit offset (`+00:00`), not a `Z` suffix. */
  readonly createdAt: string;
  readonly isDeleted: boolean;
  readonly userId: number;
}

/**
 * Runtime check that a parsed JSON value really is a `Quote`.
 *
 * `http.get<Quote[]>()` is an unchecked cast — the interface is erased and nothing verifies
 * the body. This is the one place that turns "the server said so" into "we checked".
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

/** The endpoint returns a bare array — no `{ items, total }` envelope. */
export function isQuoteArray(value: unknown): value is Quote[] {
  return Array.isArray(value) && value.every(isQuote);
}
