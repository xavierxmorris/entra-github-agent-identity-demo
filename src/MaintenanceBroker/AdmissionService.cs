namespace MaintenanceBroker;

public sealed class AdmissionService(IOperationStore store, PolicyRegistry registry, TimeProvider clock)
{
    public async Task<DurableOperation> AdmitAsync(PolicyDecision decision, Guid requestId,
        CancellationToken cancellationToken)
    {
        if (!decision.Allowed || decision.Caller is null || decision.Capability is null)
            throw new BrokerException("capability_denied", 403);
        var proposed = new DurableOperation(OperationIds.Create(decision.Caller, decision.Capability.Id, requestId),
            decision.Caller, decision.Capability, decision.GrantVersion, requestId, clock.GetUtcNow(), decision.ExpiresAt);
        if (proposed.ExpiresAt <= proposed.AcceptedAt) throw new BrokerException("decision_expired", 403);
        await registry.DemandCurrentAsync(proposed, cancellationToken);
        var stored = await store.AdmitAsync(proposed, cancellationToken);
        await DemandDisclosureAsync(stored, decision, cancellationToken);
        return stored;
    }

    public async Task<DurableOperation?> ReadAsync(string id, PolicyDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.Allowed) throw new BrokerException("capability_denied", 403);
        var stored = await store.ReadAsync(id, cancellationToken);
        if (stored is not null) await DemandDisclosureAsync(stored, decision, cancellationToken);
        return stored;
    }

    private async Task DemandDisclosureAsync(DurableOperation stored, PolicyDecision decision,
        CancellationToken cancellationToken)
    {
        if (stored.Caller != decision.Caller || stored.Capability != decision.Capability ||
            stored.GrantVersion != decision.GrantVersion)
            throw new BrokerException("operation_access_denied", 403);
        await registry.DemandCurrentAsync(stored, cancellationToken);
    }
}
