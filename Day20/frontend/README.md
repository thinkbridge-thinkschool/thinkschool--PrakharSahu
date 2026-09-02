# Day 16 / Piece 2 — State management, signals first

The quotes feature's state modelled with **signals and one `@Injectable` service** — no store library —
including an **optimistic delete** against the real Week-1 API, with the concurrency cases handled
explicitly. Builds on Day 16 / piece 1.

**Stack:** Angular 21.2 (zoneless, standalone) · signals + `computed` · Vitest

Brief, agent output and verification log: [`exercise.txt`](./exercise.txt).
The adoption rule: [`docs/when-to-adopt-a-store.md`](./docs/when-to-adopt-a-store.md).

---

## The state, in full

Four writable signals. Everything the template reads is derived.

```ts
private readonly serverQuotes = signal<readonly Quote[]>([]);        // what GET last returned
private readonly status       = signal<QuotesViewStatus>('idle');
private readonly loadError    = signal<ApiError | null>(null);
private readonly removing     = signal<ReadonlySet<number>>(new Set());  // the optimistic layer
private readonly failures     = signal<readonly RemovalFailure[]>([]);
private loadToken = 0;                                                // out-of-order load guard

readonly quotes = computed(() => {
  const hidden = this.removing();
  return this.serverQuotes().filter(q => !hidden.has(q.id));
});
```

**The rendered list is derived, never mutated.** That single decision is what makes the concurrent cases
boring: a refresh landing mid-delete replaces `serverQuotes` without touching `removing`, so the row the
user just dismissed cannot flicker back. There is no code path where two copies of the truth disagree,
because there is only one and it is computed.

---

## Why `DELETE` and not `POST`

`DELETE /api/quotes/{id:int}` (`QuoteEndpointExtensions.cs:64`) is guarded by `RequireAuthorization()`
with **no policy**, plus an imperative owner check (`IsOwnerHandler`: `quote.UserId == sub`). Unlike
`POST` and `PUT`, it does **not** require the `quotes.write` scope that login never mints — so it is the
one write on this API reachable with a real login. Confirmed by curl before building on it:

| Request | Result |
|---|---|
| `DELETE /api/quotes/6` (owned) | `204`, row gone from `GET /api/quotes` |
| `DELETE /api/quotes/3` (owned by another user) | `403` |
| `DELETE /api/quotes/9999` | `404` |
| `DELETE /api/quotes/1` (no token) | `401` |

The seeded data has quotes owned by two different users, so the **403 rollback is a real path**, not a
stub.

---

## Concurrency, handled explicitly

| Case | Guard |
|---|---|
| Two refreshes answering out of order | `loadToken` — only the newest ticket may write |
| Two deletes at once, one refused | `removing` is a `Set` keyed by id; failures are per-row |
| Refresh landing mid-delete | `quotes` is derived, so the pending row stays hidden |
| Double-click on the same delete | early return if the id is already in `removing` |
| Stale complaints after a good refresh | a successful load clears `failures` |

---

## Screenshots

| | |
|---|---|
| ![Signed-in quotes list with delete controls](./ScreenShots/01-signed-in-list-with-delete.jpg) | ![Optimistic rollback after a real 403](./ScreenShots/02-optimistic-rollback-403.jpg) |
| Signed in — delete available per row | Two deletes at once: `#2` → 204 gone, `#3` → 403 restored |
| ![Signed out, delete controls hidden](./ScreenShots/03-signed-out-delete-hidden.jpg) | |
| Signed out — controls hidden, reason given | |

Live network for the concurrent pair:

```
DELETE /api/quotes/2  →  204
DELETE /api/quotes/3  →  403
rows "#1 #2 #3 #4 #5"  →  "#1 #3 #4 #5"
banner: "Quote #3 belongs to someone else, so it was not deleted."
```

---

## Running it

```cmd
Day16\piece2\start-dev.cmd          :: API on :5267 + app on :4207, one window each
```

```bash
cd Day16/piece2/quotes-web && npm install
npm test                              # 144 tests, 12 files
```

### Accounts

There is no fixture account you have to be told about. `POST /api/auth/register` is anonymous
and creates one, and the app exposes it at **`/register`** ("Create account" in the nav).
Registration returns `201` with a live session, so a new account lands signed in rather than
being bounced to the sign-in form.

```bash
curl -X POST http://localhost:5267/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"you@example.com","password":"at-least-8-chars"}'
```

Rules: valid email, password of 8 characters or more, one account per email
(`409` if taken). Emails are normalised to trimmed lowercase server-side, so
`You@Example.com` and `you@example.com` are the same account.

### If sign-in fails, check this first

**The API is not running.** `curl http://localhost:5267/api/quotes` returning `000` rather than
`200` is the tell. Logging in mints a brand-new token regardless of any lifetime, so "it stopped
working after N minutes" cannot be an expiry problem.

It is no longer possible to reach the *wrong database*, which used to look identical to a wrong
password. The connection string is resolved against the API's own content root
(`InfrastructureExtensions.ResolveConnectionString`), not the process working directory, so every
launch — this script, a bare `dotnet run`, an IDE, or a script that has `cd`-ed elsewhere — opens
`Day5/piece6/QuotesApi/quotes.db`. The start scripts deliberately set **no** connection string;
adding one back would reintroduce the split-brain.

Account credentials are never stored in this repository.

---

## Known gaps

- The load token is per-store, so any load supersedes any other. Fine with one list; wrong the moment a
  server-side filtered query exists alongside a plain refresh.
- Optimistic delete has **no undo and no timeout** — a DELETE that never answers hides the row forever.
- Removal failures accumulate until dismissed or cleared by a refresh; nothing caps the list.
- The whole design assumes the list fits in memory, because `GET /api/quotes` returns the entire table.
  That assumption is threshold 5 in the adoption rule, and the one most likely to break.
