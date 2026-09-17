namespace Dispatch.SharedKernel;

/// <summary>
/// Something with a lifetime and an identity, where identity is what makes it the same thing.
/// </summary>
/// <remarks>
/// The distinction that matters: two work orders with identical fields are two different work
/// orders. Two addresses with identical fields are the same address. The first is an entity —
/// compared by id — and the second is a value object, compared by its values. Getting this
/// backwards is the most common early mistake in a domain model, and it shows up later as
/// aggregates that cannot be told apart in a collection.
/// </remarks>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    protected Entity(TId id) => Id = id;

    /// <summary>
    /// Required by EF Core's materialiser, which needs a way in that does not run the
    /// constructor's invariants.
    /// </summary>
    /// <remarks>
    /// <c>protected</c>, not <c>public</c>: this is a door for the persistence layer only.
    /// Application code that reaches for it is constructing an entity in an invalid state, which
    /// is precisely what the real constructor exists to prevent.
    /// </remarks>
    protected Entity() => Id = default!;

    public TId Id { get; protected init; }

    public bool Equals(Entity<TId>? other) =>
        other is not null && other.GetType() == GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => obj is Entity<TId> entity && Equals(entity);

    public override int GetHashCode() => EqualityComparer<TId>.Default.GetHashCode(Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}

/// <summary>
/// The entry point to an aggregate: the only object outside code is allowed to hold a reference
/// to, and the boundary a transaction is drawn around.
/// </summary>
/// <remarks>
/// <para>
/// An aggregate is a <b>consistency boundary</b>, not a convenience grouping. Everything inside
/// it is saved in one transaction and its invariants hold at every commit. Everything outside is
/// referenced by id and reached eventually, through an event.
/// </para>
/// <para>
/// That is the whole design decision, and it is why aggregates are usually smaller than people
/// first draw them: every extra entity pulled inside is another row locked in the same
/// transaction, and another source of write contention that has nothing to do with the rule you
/// were trying to protect.
/// </para>
/// </remarks>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id) { }
    protected AggregateRoot() { }

    /// <summary>What happened, waiting to be dispatched once the change is committed.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Records that something happened. Does <b>not</b> publish it.
    /// </summary>
    /// <remarks>
    /// The aggregate collects; the unit of work dispatches after <c>SaveChanges</c> succeeds.
    /// Publishing from inside the aggregate would announce a decision that the transaction may
    /// still roll back — and a handler that has already emailed the customer cannot un-email
    /// them. Collecting keeps the domain ignorant of messaging entirely, which is also what lets
    /// it be tested without a broker.
    /// </remarks>
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
