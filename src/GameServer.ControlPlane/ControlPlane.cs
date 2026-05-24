namespace GameServer.ControlPlane;

/// <summary>
/// Placeholder for the control plane. It will own tenant registry/provisioning,
/// tenant→database mapping, feature flags, the game catalog, protocol version
/// policy, quotas, and audit records.
/// </summary>
/// <remarks>
/// Hard rule for future work: the control plane must never sit in the hot
/// simulation path. It configures and provisions; it does not handle realtime
/// traffic. Nothing here yet — the first slice deliberately runs without it.
/// </remarks>
public static class ControlPlanePlaceholder
{
    public const string Status = "not-implemented: see ARCHITECTURE.md planned milestones";
}
