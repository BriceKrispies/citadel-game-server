# 010 — Wave 7: admin/control-plane hardening

**Status:** open
**Area:** ControlPlane / Admin / Host
**Wave:** 7 (roadmap Phase 7)
**Depends on:** Wave 3 (durable DB for tenants/games/versions/audit)
**Hot-file owner:** control-plane owner of `Program.cs` `/api/v1` group + `ControlPlane/*` + `Admin/*`.

## Context
Tenants and games are hardcoded in the composition root; there's no tenant/game/version CRUD, no
admin room-terminate, no limits-config endpoints, only a single `platform-admin` role, and `Admin.cs`
is a placeholder. Audit is in-memory. See gap analysis area 12.

## Scope
- **CRUD** (platform-admin authz, audited): tenants, games, **game versions** — backed by the Wave 3
  durable registries (so "add a tenant" is no longer a code change).
- Admin **room-terminate** endpoint (routes through the authoritative pathway, not a side mutation).
- **Capacity/limits config** endpoints (read + update `AdmissionPolicy`-style ceilings).
- **Granular admin roles** beyond `platform-admin` (e.g. read-only operator vs. game-admin) on
  `CallerPrincipal`.
- **Metrics/dashboard** endpoints; **durable audit** (Wave 3 store).

## Tests required
- `ControlPlaneCrudAuthz` (integration): every new endpoint authenticates, tenant-authorizes, audits.
- `NoCrossTenantEnumeration`: a tenant caller cannot list/read/terminate another tenant's resources.
- `RoomTerminateGoesThroughAuthoritativePath`: terminate is observable and audited; no orphan state.

## Adversarial focus
Authz on **every** new endpoint (not just the happy path); no cross-tenant enumeration via list/get;
audit completeness (every mutation + denial recorded); role escalation attempts.

## Acceptance criteria
Standard DoD + named tests green + a tenant/game/version can be provisioned and a room terminated
through audited, authorized APIs + no code change required to add a tenant.

## References
- Plan/spec: Appendix A §12
- `Program.cs` (`/api/v1`, `/admin`), `ControlPlane/*` (registries, `CallerPrincipal`, `AuditLog`), `Admin/Admin.cs`
