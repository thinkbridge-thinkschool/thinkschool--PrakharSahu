# Postmortem — 32 days

Two systems shipped today. One had been on Azure since Day 17, but only in the form it had on
Day 17. The other had 103 passing tests and had never run anywhere but a laptop. Deploying both in
one afternoon was the most informative thing I did all month, and most of what follows is a
consequence of having left it until the end.

---

## What I would do differently

**Deploy on day one of an arc, not the last day.** Shipping found ten defects and not one was
findable by a test. A client calling an API version that did not exist. Migrations generated
against the wrong database engine. A SAS key for a namespace that rejects SAS keys. A transaction
that cannot be opened by hand under a retrying execution strategy. A signing key that silently
rotated on every release. An orphaned credential, because Bicep does not delete what it stops
declaring. Every one of them was a binding between two parts that were each individually correct,
which is precisely the category a test suite cannot reach: unit tests check a part, integration
tests check parts you thought to put together, and a deployment checks the ones you did not.

**Pick the database before the code, not after.** Dispatch ran on `ConcurrentDictionary` until
Day 29, on the reasoning that you should not choose a database before the aggregate boundaries have
met a real requirement. Defensible, and still a mistake: an in-memory store cannot fail to persist,
so it made a missing `SaveChanges` invisible. The bug did not exist until there was a database, and
then it had been there all along. An afternoon spent choosing SQL Server on day one would have
deleted a whole class of "works against a fake" defect.

**Verify the binding, not the value.** My own verification script asserted that SQL had password
authentication disabled — by reading a property that `az sql server show` does not expand. It
returned empty, the check read empty as "not disabled", and it reported the server as accepting
passwords when it does not. A green check that is structurally incapable of failing correctly is
worse than no check, because it spends the credibility of every other check beside it. I have now
written that bug three times: here, in Day 31's CI skip-gate, and in the performance run that
reported a 29% regression which was a 22% improvement.

---

## What the hardest bug taught me

A managed identity has three GUIDs. The **resource id** is what a container app references. The
**principal id** is what a role assignment names. The **client id** is what SQL derives an external
user's SID from.

I created the database user from the principal id. Every layer then reported success: the identity
existed, the user existed, the app authenticated, the token was valid and freshly issued. The login
was still refused — `Login failed for user '<token-identified principal>'`, a message that names
what it could not find with a placeholder, because it never resolved it.

Nothing was broken. Two correct objects did not refer to each other, and no layer was capable of
noticing, because all three values are GUIDs and every one of them was real.

The lesson is narrower and more useful than "be careful". When a system hands you several
identifiers of the same shape, the type system cannot help and the error messages will not either.
The only defence left is naming, applied at every boundary. There is now no parameter called `id`
anywhere in this deployment code: `identity.bicep` emits all three with a comment explaining which
is which, `SqlGrant` takes `identity-client-id` as a named argument and refuses to start if it is
not a GUID, and the Bicep parameter carrying it says *"not interchangeable with the principal id"*.
That is not documentation — it is the fix, because the bug was never in the code. It was in
believing two GUIDs were the same kind of thing.

The programme's most expensive bugs all share that shape: the outbox that silently never drained
because SQLite cannot compare `DateTimeOffset` and a `catch` swallowed it; the CI gate that
confidently reported a green build having run 73 of 103 tests; a Service Bus credential stored
flawlessly in a vault for a namespace that refuses credentials; this one. **None of them failed.
Each succeeded at something slightly beside the point.** I now distrust a green result I have not
tried to make red on purpose.

---

## The one thing I am proudest of

The double-booking race.

Two technicians could be booked into the same window. The aggregate checked for a clash and then
wrote, and between those two statements another request could do the same. Every existing test
passed straight through it, because the tests ran one request at a time.

Finding it meant writing a test whose only purpose was to break my own code: fire concurrent
bookings at one slot, assert that exactly one wins. Fixing it meant understanding what
`Serializable` buys and what it costs — the transaction takes range locks, so the check and the
write became one atomic decision — and then not trusting that either. A filtered unique index sits
underneath as a backstop, so if the isolation level is ever weakened, or a future code path writes
a reservation without going through the aggregate, the *database* refuses it, and the application
turns that violation into the same clean 409.

I am proud of it because it is the one place I did not stop at "the tests pass". I went looking for
the invariant that could still be violated, found one, closed it at two layers, and proved it with
a test that fails if either layer is removed.

The zero-secret posture across this deployment is the more visible achievement — one secret exists
in the whole system, and two commands demonstrate it. But most of that came from taking Azure's
good defaults seriously. The race was hard.
