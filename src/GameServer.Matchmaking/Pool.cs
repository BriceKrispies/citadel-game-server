namespace GameServer.Matchmaking;

/// <summary>
/// A named, scope-bound, predicate-filtered view over the ticket set. A pool's identity is its
/// <see cref="Key"/> = (name, scope): two pools with the same name in different scopes are DIFFERENT
/// pools, so a match function reading one pool can only ever see one scope's tickets.
/// </summary>
/// <remarks>
/// The predicate is an additional WITHIN-scope filter (e.g. "skill &lt; 100", "mode == ranked"). It is
/// never how isolation is enforced — that is the scope in the key. A ticket whose scope differs from
/// the pool's scope is rejected by <see cref="Admits"/> regardless of the predicate, so a misconfigured
/// predicate can never leak a cross-tenant/cross-version ticket in.
/// </remarks>
public sealed class Pool
{
    private readonly Func<MatchTicket, bool> _predicate;

    public Pool(string name, MatchScope scope, Func<MatchTicket, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A pool name is required.", nameof(name));
        }

        Key = new PoolKey(name, scope);
        _predicate = predicate ?? (static _ => true);
    }

    public PoolKey Key { get; }

    public MatchScope Scope => Key.Scope;

    /// <summary>
    /// True if <paramref name="ticket"/> belongs in this pool. Scope is checked FIRST and is
    /// non-negotiable: a ticket from another scope is never admitted, even if the predicate would
    /// accept it. Within-scope, the predicate decides.
    /// </summary>
    public bool Admits(MatchTicket ticket) =>
        ticket.Scope == Scope && _predicate(ticket);

    /// <summary>The tickets from <paramref name="tickets"/> this pool admits, scope-checked then filtered.</summary>
    public IEnumerable<MatchTicket> Filter(IEnumerable<MatchTicket> tickets) =>
        tickets.Where(Admits);
}

/// <summary>
/// A pool's identity: its name within a single <see cref="MatchScope"/>. The scope is part of the key
/// (not metadata) so the type system itself keeps tenants/games/versions in separate pools.
/// </summary>
public readonly record struct PoolKey(string Name, MatchScope Scope)
{
    public override string ToString() => $"{Name}@{Scope}";
}
