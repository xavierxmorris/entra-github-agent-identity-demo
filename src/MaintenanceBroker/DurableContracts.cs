using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MaintenanceBroker;

public static class OperationIds
{
    public const int Shards = 16;
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Create(CallerIdentity caller, string capability, Guid request) =>
        capability + "." + Hash($"maintenance-v2|{caller.Mode}|{caller.TenantId:D}|{caller.ObjectId:D}|{caller.ClientId:D}|{capability}|{request:D}");
    public static string? CapabilityId(string id)
    {
        var parts = id.Split('.');
        return parts.Length == 2 && SettingsState.ValidCapabilityId(parts[0]) &&
            parts[1].Length == 64 && parts[1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? parts[0] : null;
    }
    public static string Partition(string id)
    {
        if (CapabilityId(id) is null) throw new BrokerException("invalid_operation_id", 400);
        return Shard(Convert.ToInt32(id[(id.IndexOf('.') + 1)..(id.IndexOf('.') + 3)], 16) % Shards);
    }
    public static string Shard(int shard) => $"s{shard:x2}";
}

public static class OperationStatus
{
    public const string Pending = "pending";
    public const string Working = "working";
    public const string Reconciling = "reconciling";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string ReconciliationRequired = "reconciliation_required";
    public const string Poisoned = "poisoned";
    public static bool Terminal(string status) =>
        status is Completed or Failed or Denied or Expired or ReconciliationRequired or Poisoned;
}

public sealed record DurableOperation(
    string Id, CallerIdentity Caller, Capability Capability, long GrantVersion, Guid RequestId,
    DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt, string Status = OperationStatus.Pending,
    string? Owner = null, DateTimeOffset? LeaseUntil = null, MaintenanceResult? Result = null,
    string? Error = null, [property: JsonIgnore] string ETag = "")
{
    public static readonly TimeSpan DecisionWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(90);
    [JsonIgnore] public bool IsTerminal => OperationStatus.Terminal(Status);
    [JsonIgnore] public OperationPlan Plan => OperationPlan.Create(Caller, Capability, RequestId);
    [JsonIgnore] public OperationView View => new(Id, Status, Capability.Id, RequestId, AcceptedAt, ExpiresAt, Result, Error);

    public void Validate()
    {
        if (Caller is null || Capability is null || !SettingsState.ValidCapability(Capability) ||
            Caller.Mode is not ("Workload" or "Delegated") || Caller.TenantId == Guid.Empty ||
            Caller.ObjectId == Guid.Empty || Caller.ClientId == Guid.Empty || RequestId == Guid.Empty ||
            Id != OperationIds.Create(Caller, Capability.Id, RequestId) || GrantVersion <= 0 ||
            AcceptedAt < DateTimeOffset.UnixEpoch || ExpiresAt <= AcceptedAt || ExpiresAt - AcceptedAt > DecisionWindow ||
            (!IsTerminal && Status is not (OperationStatus.Pending or OperationStatus.Working or OperationStatus.Reconciling)) ||
            (Status == OperationStatus.Pending && (Owner is not null || LeaseUntil is not null)) ||
            (Status == OperationStatus.Completed && (Result is null || Error is not null)) ||
            (Status != OperationStatus.Completed && Result is not null) ||
            (IsTerminal && Status != OperationStatus.Completed && Error is null) ||
            (Error is not null && (Error.Length is < 1 or > 80 || !Error.All(c => char.IsAsciiLetterLower(c) || c == '_'))) ||
            (Status is OperationStatus.Working or OperationStatus.Reconciling &&
                (!SettingsState.ValidGuid(Owner) || LeaseUntil is null || LeaseUntil <= AcceptedAt || LeaseUntil > ExpiresAt)))
            throw new BrokerException("operation_invalid", StatusCodes.Status503ServiceUnavailable);
        if (Result is not null && (Result.Status is not ("created" or "existing") ||
            Result.CapabilityId != Capability.Id || Result.RepositoryId != Capability.RepositoryId ||
            Result.RequestId != RequestId || Result.Branch != Plan.Branch || Result.PullRequestNumber <= 0 ||
            Result.PullRequestState is not ("open" or "closed") ||
            Result.PullRequestUrl != $"https://github.com/{Capability.Owner}/{Capability.Name}/pull/{Result.PullRequestNumber}"))
            throw new BrokerException("operation_invalid", StatusCodes.Status503ServiceUnavailable);
    }
}

public sealed record OperationView(string Id, string Status, string CapabilityId, Guid RequestId,
    DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt, MaintenanceResult? Result, string? Error);
public sealed record OutboxItem(string OperationId, string ETag);
public sealed record QueueDelivery(string MessageId, string Receipt, string OperationId, long DequeueCount);

public interface IOperationStore
{
    Task<DurableOperation?> ReadAsync(string id, CancellationToken cancellationToken);
    Task<DurableOperation> AdmitAsync(DurableOperation operation, CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(DurableOperation operation, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutboxItem>> ReadOutboxAsync(int shard, CancellationToken cancellationToken);
    Task DeleteOutboxAsync(OutboxItem item, CancellationToken cancellationToken);
    Task ProbeAsync(CancellationToken cancellationToken);
}

public interface IWorkQueue
{
    Task SendAsync(string operationId, CancellationToken cancellationToken);
    Task<QueueDelivery?> ReceiveAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(QueueDelivery delivery, CancellationToken cancellationToken);
    Task PoisonAsync(QueueDelivery delivery, string error, CancellationToken cancellationToken);
    Task ProbeAsync(CancellationToken cancellationToken);
}

public interface IInstallationBudget
{
    Task<bool> AcquireAsync(long installation, string owner, DateTimeOffset now, DateTimeOffset until,
        CancellationToken cancellationToken);
    Task DemandAsync(long installation, string owner, DateTimeOffset now, CancellationToken cancellationToken);
    Task BlockAsync(long installation, DateTimeOffset until, CancellationToken cancellationToken);
    Task ReleaseAsync(long installation, string owner, CancellationToken cancellationToken);
}

public interface IExecutionGuard
{
    bool ReadOnly { get; }
    Task DemandAsync(CancellationToken cancellationToken);
    Task ObserveAsync(DateTimeOffset blockedUntil, CancellationToken cancellationToken);
}
