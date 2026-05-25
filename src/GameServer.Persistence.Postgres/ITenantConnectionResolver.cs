namespace GameServer.Persistence.Postgres;

/// <summary>
/// Maps a tenant id to the Postgres connection string for THAT tenant's database. This is the
/// backbone of the database-per-tenant isolation guarantee: a tenant's data lives in its own
/// database, reached only through its own connection string. There is no shared table holding
/// multiple tenants' rows, so isolation does not depend on a (leak-prone) <c>WHERE tenant_id = …</c>
/// predicate that a bug could omit — it is enforced at the connection/database boundary.
/// </summary>
/// <remarks>
/// A production resolver reads the per-tenant mapping from the control-plane tenant registry
/// (provisioning assigns each tenant a database). Tests use a resolver that points each tenant at a
/// distinct database on a throwaway Postgres container, proving cross-tenant reads are impossible.
/// </remarks>
public interface ITenantConnectionResolver
{
    /// <summary>
    /// The connection string for <paramref name="tenantId"/>'s database. Implementations MUST return a
    /// connection that can reach ONLY that tenant's data (a distinct database, or a role scoped to it).
    /// </summary>
    string ConnectionStringFor(string tenantId);
}

/// <summary>
/// A resolver backed by an explicit tenant→connection-string map (e.g. bound from configuration:
/// the control plane provisions each tenant a database and records its connection string). A request
/// for an unmapped tenant FAILS rather than falling back to a shared/default database — a missing
/// mapping must never silently route one tenant's data into another's (or a common) database.
/// </summary>
public sealed class DictionaryTenantConnectionResolver : ITenantConnectionResolver
{
    private readonly IReadOnlyDictionary<string, string> _byTenant;

    public DictionaryTenantConnectionResolver(IReadOnlyDictionary<string, string> connectionStringsByTenant) =>
        _byTenant = connectionStringsByTenant;

    public string ConnectionStringFor(string tenantId) =>
        _byTenant.TryGetValue(tenantId, out var connectionString)
            ? connectionString
            : throw new InvalidOperationException(
                $"No database is mapped for tenant '{tenantId}'. Refusing to fall back to a shared " +
                "database — per-tenant isolation requires an explicit mapping.");
}
