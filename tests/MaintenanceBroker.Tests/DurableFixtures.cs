using Azure;
using Azure.Data.Tables;
using MaintenanceBroker;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaintenanceBroker.Tests;

internal sealed class MemoryPolicyStore : IPolicyStore
{
    public Dictionary<(string, string), TableEntity> Rows { get; } = [];
    public List<(string, string)> Reads { get; } = [];
    public bool Unavailable { get; set; }

    public MemoryPolicyStore()
    {
        Rows[("control", "global")] = new("control", "global") { ["Enabled"] = true };
        foreach (var capability in Fixtures.Capabilities()) Rows[("capability", capability.Id)] = CapabilityRow(capability);
        Grant(Fixtures.Caller(), Fixtures.Capabilities()[0]);
        Grant(Fixtures.Caller("Delegated"), Fixtures.Capabilities()[0]);
    }

    public void Grant(CallerIdentity caller, Capability capability, long version = 1)
    {
        var row = CapabilityRow(capability);
        row.PartitionKey = PolicyRegistry.PrincipalPartition(caller);
        row["Mode"] = caller.Mode;
        row["TenantId"] = caller.TenantId.ToString("D");
        row["ObjectId"] = caller.ObjectId.ToString("D");
        row["ClientId"] = caller.ClientId.ToString("D");
        row["CapabilityVersion"] = capability.Version;
        row["Version"] = version;
        Rows[(row.PartitionKey, row.RowKey)] = row;
    }

    public static TableEntity CapabilityRow(Capability capability) => new("capability", capability.Id)
    {
        ["Enabled"] = true, ["Version"] = capability.Version,
        ["RepositoryId"] = capability.RepositoryId, ["InstallationId"] = capability.InstallationId,
        ["Owner"] = capability.Owner, ["Name"] = capability.Name, ["BaseBranch"] = capability.BaseBranch
    };

    public Task<TableEntity?> ReadAsync(string partition, string row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Rows)
        {
            Reads.Add((partition, row));
            if (Unavailable) throw new BrokerException("storage_unavailable", 503);
            return Task.FromResult(Rows.TryGetValue((partition, row), out var entity)
                ? new TableEntity(entity.ToDictionary(x => x.Key, x => x.Value)) : null);
        }
    }
}

internal sealed class MemoryOperationStore : IOperationStore
{
    private readonly object sync = new();
    private readonly Dictionary<string, DurableOperation> operations = [];
    private readonly Dictionary<string, OutboxItem> outbox = [];
    private int version;
    public int Count { get { lock (sync) return operations.Count; } }
    public int OutboxCount { get { lock (sync) return outbox.Count; } }
    public int Admits;
    public int Reads;
    public int Probes;
    public bool FailBeforeCommit { get; set; }
    public bool LoseCommitResponse { get; set; }
    public bool FailDeleteOutbox { get; set; }
    public bool FailReplace { get; set; }
    public bool Unavailable { get; set; }
    public Func<CancellationToken, Task>? BeforeAdmit { get; set; }
    public Func<CancellationToken, Task>? BeforeProbe { get; set; }

    public Task<DurableOperation?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Reads);
        lock (sync)
        {
            if (Unavailable) throw new BrokerException("storage_unavailable", 503);
            return Task.FromResult(operations.GetValueOrDefault(id));
        }
    }

    public async Task<DurableOperation> AdmitAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Admits);
        if (BeforeAdmit is not null) await BeforeAdmit(cancellationToken);
        operation.Validate();
        lock (sync)
        {
            if (Unavailable || FailBeforeCommit) throw new BrokerException("storage_unavailable", 503);
            if (operations.TryGetValue(operation.Id, out var existing)) return existing;
            var saved = operation with { ETag = (++version).ToString() };
            operations.Add(saved.Id, saved);
            outbox.Add(saved.Id, new(saved.Id, saved.ETag));
            if (LoseCommitResponse) throw new BrokerException("storage_unavailable", 503);
            return saved;
        }
    }

    public Task<bool> ReplaceAsync(DurableOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        operation.Validate();
        lock (sync)
        {
            if (Unavailable || FailReplace) throw new BrokerException("storage_unavailable", 503);
            if (!operations.TryGetValue(operation.Id, out var previous) || previous.ETag != operation.ETag)
                return Task.FromResult(false);
            operations[operation.Id] = operation with { ETag = (++version).ToString() };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<OutboxItem>> ReadOutboxAsync(int shard, CancellationToken cancellationToken)
    {
        lock (sync)
            return Task.FromResult<IReadOnlyList<OutboxItem>>(outbox.Values
                .Where(item => OperationIds.Partition(item.OperationId) == OperationIds.Shard(shard))
                .OrderBy(item => item.OperationId, StringComparer.Ordinal).Take(32).ToArray());
    }

    public Task DeleteOutboxAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (FailDeleteOutbox) throw new BrokerException("storage_unavailable", 503);
            if (outbox.TryGetValue(item.OperationId, out var previous) && previous.ETag == item.ETag)
                outbox.Remove(item.OperationId);
            return Task.CompletedTask;
        }
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Probes);
        if (Unavailable) throw new BrokerException("storage_unavailable", 503);
        if (BeforeProbe is not null) await BeforeProbe(cancellationToken);
    }
}

internal sealed class MemoryQueue : IWorkQueue
{
    public List<QueueDelivery> Sent { get; } = [];
    public List<string> Acknowledged { get; } = [];
    public List<(QueueDelivery Delivery, string Error)> Poisoned { get; } = [];
    public bool FailBeforeSend { get; set; }
    public bool LoseSendResponse { get; set; }
    public bool FailAck { get; set; }
    public bool FailPoison { get; set; }
    public int Probes { get; private set; }
    public Task SendAsync(string operationId, CancellationToken cancellationToken)
    {
        if (FailBeforeSend) throw new BrokerException("storage_unavailable", 503);
        Sent.Add(new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), operationId, 1));
        if (LoseSendResponse) throw new BrokerException("storage_unavailable", 503);
        return Task.CompletedTask;
    }
    public Task<QueueDelivery?> ReceiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Sent.FirstOrDefault(item => !Acknowledged.Contains(item.MessageId)));
    public Task AcknowledgeAsync(QueueDelivery delivery, CancellationToken cancellationToken)
    {
        if (FailAck) throw new BrokerException("storage_unavailable", 503);
        Acknowledged.Add(delivery.MessageId);
        return Task.CompletedTask;
    }
    public Task PoisonAsync(QueueDelivery delivery, string error, CancellationToken cancellationToken)
    {
        if (FailPoison) throw new BrokerException("storage_unavailable", 503);
        Poisoned.Add((delivery, error));
        return Task.CompletedTask;
    }
    public Task ProbeAsync(CancellationToken cancellationToken)
    {
        Probes++;
        if (FailBeforeSend) throw new BrokerException("storage_unavailable", 503);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryBudget : IInstallationBudget
{
    private readonly object sync = new();
    private readonly Dictionary<long, (string Owner, DateTimeOffset Until, DateTimeOffset Window, int Count,
        DateTimeOffset Blocked)> entries = [];
    public bool Unavailable { get; set; }
    public Task<bool> AcquireAsync(long installation, string owner, DateTimeOffset now, DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (Unavailable) throw new BrokerException("storage_unavailable", 503);
            var entry = entries.GetValueOrDefault(installation);
            if (entry.Until > now || entry.Blocked > now) return Task.FromResult(false);
            if (now >= entry.Window.AddHours(1)) entry = entry with { Window = now, Count = 0 };
            if (entry.Count >= AzureInstallationBudget.OperationsPerHour) return Task.FromResult(false);
            entries[installation] = (owner, until, entry.Window, entry.Count + 1, entry.Blocked);
            return Task.FromResult(true);
        }
    }
    public Task DemandAsync(long installation, string owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            var entry = entries.GetValueOrDefault(installation);
            if (Unavailable) throw new BrokerException("storage_unavailable", 503);
            if (entry.Owner != owner || entry.Until <= now) throw new BrokerException("installation_budget_lost", 503);
            if (entry.Blocked > now) throw new BrokerException("github_rate_limited", 503);
            return Task.CompletedTask;
        }
    }
    public Task BlockAsync(long installation, DateTimeOffset until, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (Unavailable) throw new BrokerException("storage_unavailable", 503);
            var entry = entries[installation];
            if (until > entry.Blocked) entries[installation] = entry with { Blocked = until };
            return Task.CompletedTask;
        }
    }
    public Task ReleaseAsync(long installation, string owner, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            var entry = entries[installation];
            if (entry.Owner == owner) entries[installation] = entry with { Owner = "", Until = default };
            return Task.CompletedTask;
        }
    }
}

internal sealed class FixtureGuard : IExecutionGuard
{
    public bool ReadOnly { get; init; }
    public Func<CancellationToken, Task>? OnDemand { get; set; }
    public Func<DateTimeOffset, CancellationToken, Task>? OnObserve { get; set; }
    public Task DemandAsync(CancellationToken cancellationToken) => OnDemand?.Invoke(cancellationToken) ?? Task.CompletedTask;
    public Task ObserveAsync(DateTimeOffset blockedUntil, CancellationToken cancellationToken) =>
        OnObserve?.Invoke(blockedUntil, cancellationToken) ?? Task.CompletedTask;
}

internal sealed class WorkerHarness
{
    public ManualClock Clock { get; } = new();
    public MemoryPolicyStore Policy { get; } = new();
    public MemoryOperationStore Store { get; } = new();
    public MemoryQueue Queue { get; } = new();
    public MemoryBudget Budget { get; } = new();
    public OperationSpy Operation { get; } = new();
    public Microsoft.Extensions.Configuration.IConfigurationRoot Configuration { get; } = Fixtures.Configuration();
    public PolicyRegistry Registry => new(Policy, new ActionGate(Configuration));
    public AuditWriter Audit { get; } = new(NullLogger<AuditWriter>.Instance);
    public WorkerProcessor Processor => new(Fixtures.Settings(), Store, Queue, Registry, Budget, Operation, Clock, Audit);
    public OutboxDispatcher Dispatcher => new(Store, Queue, Registry, Clock, Audit);
    public async Task<DurableOperation> AdmitAsync(string mode = "Workload", Guid? requestId = null)
    {
        var caller = Fixtures.Caller(mode);
        var capability = Fixtures.Capabilities()[0];
        var request = requestId ?? Guid.Parse(Fixtures.RequestId);
        return await Store.AdmitAsync(new(OperationIds.Create(caller, capability.Id, request), caller,
            capability, 1, request, Clock.GetUtcNow(), Clock.GetUtcNow().AddMinutes(5)), CancellationToken.None);
    }
    public async Task<QueueDelivery> DispatchAsync(DurableOperation operation)
    {
        await Dispatcher.DispatchAsync(Convert.ToInt32(OperationIds.Partition(operation.Id)[1..], 16), CancellationToken.None);
        return Queue.Sent[^1];
    }
}
