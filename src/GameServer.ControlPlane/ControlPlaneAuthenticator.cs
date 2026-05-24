namespace GameServer.ControlPlane;

/// <summary>
/// Authenticates a control-plane caller from the raw <c>Authorization</c> header,
/// yielding a <see cref="CallerPrincipal"/>. A seam: the API-key implementation here
/// can be swapped for one that validates an IdP-issued bearer JWT without changing the
/// endpoints. Kept free of ASP.NET types so it is unit-testable in isolation.
/// </summary>
public interface IControlPlaneAuthenticator
{
    bool TryAuthenticate(string? authorizationHeader, out CallerPrincipal caller);
}

/// <summary>
/// Authenticates callers against a fixed set of API keys presented as
/// <c>Authorization: Bearer &lt;key&gt;</c>. Each key maps to the principal (tenant
/// scope + roles) it grants. Unknown/missing/wrong-scheme headers fail closed.
/// </summary>
public sealed class ApiKeyControlPlaneAuthenticator : IControlPlaneAuthenticator
{
    private const string BearerScheme = "Bearer ";

    private readonly IReadOnlyDictionary<string, CallerPrincipal> _keys;

    public ApiKeyControlPlaneAuthenticator(IReadOnlyDictionary<string, CallerPrincipal> apiKeys) => _keys = apiKeys;

    public bool TryAuthenticate(string? authorizationHeader, out CallerPrincipal caller)
    {
        caller = null!;
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var key = authorizationHeader[BearerScheme.Length..].Trim();
        return key.Length > 0 && _keys.TryGetValue(key, out caller!);
    }
}
