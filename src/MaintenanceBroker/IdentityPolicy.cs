using System.Globalization;
using System.Security.Claims;
using Azure.Data.Tables;

namespace MaintenanceBroker;

public sealed record CallerIdentity(Guid TenantId, Guid ObjectId, Guid ClientId, string Mode);
public sealed record PolicyDecision(bool Allowed, string Reason, CallerIdentity? Caller = null,
    Capability? Capability = null, long GrantVersion = 0, DateTimeOffset ExpiresAt = default);

public interface IPolicyStore
{
    Task<TableEntity?> ReadAsync(string partition, string row, CancellationToken cancellationToken);
}

public sealed class PolicyRegistry(IPolicyStore store, ActionGate gate)
{
    public static string PrincipalPartition(CallerIdentity caller) => "principal:" +
        OperationIds.Hash($"{caller.Mode}|{caller.TenantId:D}|{caller.ObjectId:D}|{caller.ClientId:D}");

    public async Task DemandEnabledAsync(CancellationToken cancellationToken)
    {
        gate.DemandEnabled();
        var control = await store.ReadAsync("control", "global", cancellationToken);
        if (control is null || !Enabled(control))
            throw new BrokerException("actions_disabled", StatusCodes.Status503ServiceUnavailable);
    }

    public async Task<PolicyDecision> ReadGrantAsync(CallerIdentity caller, string capabilityId,
        CancellationToken cancellationToken)
    {
        await DemandEnabledAsync(cancellationToken);
        var capabilityRow = await store.ReadAsync("capability", capabilityId, cancellationToken);
        var grant = await store.ReadAsync(PrincipalPartition(caller), capabilityId, cancellationToken);
        if (capabilityRow is null || grant is null || !Enabled(capabilityRow) || !Enabled(grant))
            return new(false, "capability_denied", caller);
        var capability = ReadCapability(capabilityRow, "Version");
        var binding = ReadCapability(grant, "CapabilityVersion");
        var version = Number(grant, "Version");
        if (version <= 0 || Text(grant, "Mode") != caller.Mode ||
            Text(grant, "TenantId") != caller.TenantId.ToString("D") ||
            Text(grant, "ObjectId") != caller.ObjectId.ToString("D") ||
            Text(grant, "ClientId") != caller.ClientId.ToString("D") || binding != capability)
            throw Invalid();
        return new(true, "allowed", caller, capability, version);
    }

    public async Task DemandCurrentAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        var decision = await ReadGrantAsync(operation.Caller, operation.Capability.Id, cancellationToken);
        if (!decision.Allowed || decision.GrantVersion != operation.GrantVersion ||
            decision.Capability != operation.Capability)
            throw new BrokerException("grant_revoked_or_changed", StatusCodes.Status403Forbidden);
    }

    public static Capability ReadCapability(TableEntity row, string versionField)
    {
        var capability = new Capability
        {
            Id = row.RowKey, Version = Number(row, versionField),
            RepositoryId = Number(row, "RepositoryId"), InstallationId = Number(row, "InstallationId"),
            Owner = Text(row, "Owner"), Name = Text(row, "Name"), BaseBranch = Text(row, "BaseBranch")
        };
        if (!SettingsState.ValidCapability(capability)) throw Invalid();
        return capability;
    }

    private static bool Enabled(TableEntity row) =>
        row.TryGetValue("Enabled", out var value) && value is bool enabled ? enabled : throw Invalid();
    private static string Text(TableEntity row, string name) =>
        row.TryGetValue(name, out var value) && value is string text ? text : throw Invalid();
    private static long Number(TableEntity row, string name) =>
        row.TryGetValue(name, out var value) && value is long number ? number : throw Invalid();
    private static BrokerException Invalid() => new("policy_invalid", StatusCodes.Status503ServiceUnavailable);
}

public sealed class IdentityPolicy(SettingsState settings, PolicyRegistry registry, TimeProvider clock)
{
    public async Task<PolicyDecision> EvaluateAsync(ClaimsPrincipal principal, string capabilityId,
        CancellationToken cancellationToken)
    {
        var caller = ReadCaller(principal);
        if (caller is null) return new(false, "invalid_identity_claims");
        if (caller.TenantId != Guid.Parse(settings.Options.TenantId))
            return new(false, "tenant_denied", caller);
        var scopes = principal.FindAll("scp").ToArray();
        var idTypes = principal.FindAll("idtyp").ToArray();
        if (idTypes.Length > 1 ||
            (caller.Mode == "Workload" && idTypes.Length == 1 && idTypes[0].Value != "app") ||
            (caller.Mode == "Delegated" && idTypes.Length == 1 && idTypes[0].Value != "user"))
            return new(false, "identity_mode_conflict", caller);
        if (caller.Mode == "Workload")
        {
            if (!principal.HasClaim("roles", "Maintenance.Request"))
                return new(false, "application_role_required", caller);
        }
        else if (scopes.Length != 1 || !scopes[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("Agent.Invoke", StringComparer.Ordinal))
            return new(false, "delegated_scope_required", caller);
        if (!long.TryParse(Single(principal, "exp"), NumberStyles.None, CultureInfo.InvariantCulture, out var exp) ||
            exp <= clock.GetUtcNow().ToUnixTimeSeconds() || exp > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return new(false, "decision_expired", caller);
        var expires = DateTimeOffset.FromUnixTimeSeconds(exp);
        var maximum = clock.GetUtcNow() + DurableOperation.DecisionWindow;
        var decision = await registry.ReadGrantAsync(caller, capabilityId, cancellationToken);
        return decision with { ExpiresAt = expires < maximum ? expires : maximum };
    }

    public static CallerIdentity? ReadCaller(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || Single(principal, "ver") != "2.0" ||
            !TryGuid(principal, "tid", out var tenant) || !TryGuid(principal, "oid", out var oid) ||
            !TryGuid(principal, "azp", out var client))
            return null;
        return new(tenant, oid, client, principal.HasClaim(c => c.Type == "scp") ? "Delegated" : "Workload");
    }

    private static bool TryGuid(ClaimsPrincipal principal, string type, out Guid value) =>
        Guid.TryParseExact(Single(principal, type), "D", out value) && value != Guid.Empty;

    private static string? Single(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
