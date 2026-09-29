using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;

namespace MaintenanceBroker;

public interface IStorageClients
{
    TableClient Operations { get; }
    TableClient Policy { get; }
    QueueClient Queue { get; }
    QueueClient Poison { get; }
}

public sealed class StorageClients : IStorageClients
{
    public TableClient Operations { get; }
    public TableClient Policy { get; }
    public QueueClient Queue { get; }
    public QueueClient Poison { get; }

    public StorageClients(SettingsState settings)
    {
        if (!settings.IsReady) throw new BrokerException("broker_not_configured", 503);
        var options = settings.Options;
        var credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
        var tableOptions = new TableClientOptions();
        Configure(tableOptions);
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
        Configure(queueOptions);
        var tableUri = new Uri($"https://{options.StorageAccountName}.table.core.windows.net");
        Operations = new(tableUri, options.OperationsTableName, credential, tableOptions);
        Policy = new(tableUri, options.PolicyTableName, credential, tableOptions);
        Queue = new(new Uri($"https://{options.StorageAccountName}.queue.core.windows.net/{options.QueueName}"),
            credential, queueOptions);
        Poison = new(new Uri($"https://{options.StorageAccountName}.queue.core.windows.net/{options.PoisonQueueName}"),
            credential, queueOptions);
    }

    private static void Configure(ClientOptions options)
    {
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        options.Diagnostics.IsLoggingContentEnabled = false;
    }
}

internal static class StorageCall
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (RequestFailedException) { throw new BrokerException("storage_unavailable", 503); }
        catch (AuthenticationFailedException) { throw new BrokerException("managed_identity_failed", 503); }
    }

    public static async Task RunAsync(Func<Task> action) =>
        await RunAsync(async () => { await action(); return true; });
}

public sealed class AzurePolicyStore(IStorageClients clients) : IPolicyStore
{
    public Task<TableEntity?> ReadAsync(string partition, string row, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            var response = await clients.Policy.GetEntityIfExistsAsync<TableEntity>(partition, row,
                cancellationToken: cancellationToken);
            return response.HasValue ? response.Value : null;
        });
}

public sealed class AzureOperationStore(IStorageClients clients) : IOperationStore
{
    public Task<DurableOperation?> ReadAsync(string id, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            var response = await clients.Operations.GetEntityIfExistsAsync<TableEntity>(
                OperationIds.Partition(id), "op:" + id, cancellationToken: cancellationToken);
            return response.HasValue && response.Value is { } entity ? Decode(entity) : null;
        });

    public Task<DurableOperation> AdmitAsync(DurableOperation operation, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            operation.Validate();
            var entity = Encode(operation);
            var outbox = new TableEntity(entity.PartitionKey, "out:" + operation.Id);
            try
            {
                await clients.Operations.SubmitTransactionAsync(
                [
                    new(TableTransactionActionType.Add, entity),
                    new(TableTransactionActionType.Add, outbox)
                ], cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                // An uncertain earlier commit or a concurrent admission returns the original decision.
            }
            return await ReadAsync(operation.Id, cancellationToken) ??
                throw new BrokerException("operation_persistence_failed", 503);
        });

    public Task<bool> ReplaceAsync(DurableOperation operation, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            operation.Validate();
            if (string.IsNullOrEmpty(operation.ETag) || operation.ETag == "*")
                throw new BrokerException("operation_etag_required", 503);
            try
            {
                await clients.Operations.UpdateEntityAsync(Encode(operation), new ETag(operation.ETag),
                    TableUpdateMode.Replace, cancellationToken);
                return true;
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 412) { return false; }
        });

    public Task<IReadOnlyList<OutboxItem>> ReadOutboxAsync(int shard, CancellationToken cancellationToken) =>
        StorageCall.RunAsync<IReadOnlyList<OutboxItem>>(async () =>
        {
            if (shard is < 0 or >= OperationIds.Shards) throw new ArgumentOutOfRangeException(nameof(shard));
            var partition = OperationIds.Shard(shard);
            var query = clients.Operations.QueryAsync<TableEntity>(
                TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {"out:"} and RowKey lt {"out;"}"),
                maxPerPage: 32, select: ["PartitionKey", "RowKey"], cancellationToken: cancellationToken);
            await foreach (var page in query.AsPages(pageSizeHint: 32))
                return page.Values.Select(row => new OutboxItem(row.RowKey[4..], row.ETag.ToString())).ToArray();
            return [];
        });

    public Task DeleteOutboxAsync(OutboxItem item, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            if (string.IsNullOrEmpty(item.ETag) || item.ETag == "*")
                throw new BrokerException("outbox_etag_required", 503);
            try
            {
                await clients.Operations.DeleteEntityAsync(OperationIds.Partition(item.OperationId),
                    "out:" + item.OperationId, new ETag(item.ETag), cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 412)
            {
                // Another dispatcher removed this immutable reference after an acknowledged send.
            }
        });

    public Task ProbeAsync(CancellationToken cancellationToken) => StorageCall.RunAsync(async () =>
    {
        await foreach (var page in clients.Operations.QueryAsync<TableEntity>(
            "PartitionKey eq 'health' and RowKey eq 'probe'", maxPerPage: 1,
            select: ["RowKey"], cancellationToken: cancellationToken).AsPages(pageSizeHint: 1))
            break;
    });

    private static TableEntity Encode(DurableOperation operation) =>
        new(OperationIds.Partition(operation.Id), "op:" + operation.Id)
        {
            ["Data"] = JsonSerializer.Serialize(operation),
            ["Status"] = operation.Status,
            ["AcceptedAt"] = operation.AcceptedAt,
            ["ExpiresAt"] = operation.ExpiresAt
        };

    private static DurableOperation Decode(TableEntity entity)
    {
        if (!entity.TryGetValue("Data", out var data) || data is not string json || json.Length > 24000)
            throw new BrokerException("operation_invalid", 503);
        try
        {
            var operation = JsonSerializer.Deserialize<DurableOperation>(json) ??
                throw new BrokerException("operation_invalid", 503);
            operation.Validate();
            if (entity.RowKey != "op:" + operation.Id || entity.PartitionKey != OperationIds.Partition(operation.Id))
                throw new BrokerException("operation_invalid", 503);
            return operation with { ETag = entity.ETag.ToString() };
        }
        catch (JsonException) { throw new BrokerException("operation_invalid", 503); }
    }
}

public sealed class AzureWorkQueue(IStorageClients clients) : IWorkQueue
{
    public static readonly TimeSpan Visibility = TimeSpan.FromSeconds(150);
    public Task SendAsync(string operationId, CancellationToken cancellationToken) => StorageCall.RunAsync(async () =>
    {
        if (OperationIds.CapabilityId(operationId) is null) throw new BrokerException("invalid_operation_id", 400);
        await clients.Queue.SendMessageAsync(operationId, timeToLive: TimeSpan.FromSeconds(-1),
            cancellationToken: cancellationToken);
    });

    public Task<QueueDelivery?> ReceiveAsync(CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            var response = await clients.Queue.ReceiveMessagesAsync(1, Visibility, cancellationToken);
            var message = response.Value.SingleOrDefault();
            return message is null ? null : new QueueDelivery(message.MessageId, message.PopReceipt,
                message.Body.ToString(), message.DequeueCount);
        });

    public Task AcknowledgeAsync(QueueDelivery delivery, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            await clients.Queue.DeleteMessageAsync(delivery.MessageId, delivery.Receipt, cancellationToken);
        });

    public Task PoisonAsync(QueueDelivery delivery, string error, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            // Never copy an untrusted queue body to poison storage or logs.
            await clients.Poison.SendMessageAsync(JsonSerializer.Serialize(new
            {
                operationId = OperationIds.CapabilityId(delivery.OperationId) is null ? null : delivery.OperationId,
                messageId = delivery.MessageId, error
            }), timeToLive: TimeSpan.FromSeconds(-1), cancellationToken: cancellationToken);
        });

    public Task ProbeAsync(CancellationToken cancellationToken) => StorageCall.RunAsync(async () =>
    {
        await clients.Queue.GetPropertiesAsync(cancellationToken);
        await clients.Poison.GetPropertiesAsync(cancellationToken);
    });
}

public sealed class AzureInstallationBudget(IStorageClients clients) : IInstallationBudget
{
    public const int OperationsPerHour = 6;
    public Task<bool> AcquireAsync(long installation, string owner, DateTimeOffset now, DateTimeOffset until,
        CancellationToken cancellationToken) => StorageCall.RunAsync(async () =>
    {
        var entity = await ReadAsync(installation, cancellationToken);
        var exists = entity is not null;
        entity ??= new TableEntity($"installation:{installation}", "budget")
        {
            ["Owner"] = "", ["LeaseUntil"] = DateTimeOffset.UnixEpoch,
            ["BlockedUntil"] = DateTimeOffset.UnixEpoch, ["WindowStart"] = now, ["Count"] = 0
        };
        Validate(entity);
        if (Date(entity, "BlockedUntil") > now || Date(entity, "LeaseUntil") > now) return false;
        if (now >= Date(entity, "WindowStart") + TimeSpan.FromHours(1))
        {
            entity["WindowStart"] = now;
            entity["Count"] = 0;
        }
        var count = (int)entity["Count"];
        if (count >= OperationsPerHour) return false;
        entity["Count"] = count + 1;
        entity["Owner"] = owner;
        entity["LeaseUntil"] = until;
        try
        {
            if (exists)
                await clients.Operations.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);
            else await clients.Operations.AddEntityAsync(entity, cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412) { return false; }
    });

    public Task DemandAsync(long installation, string owner, DateTimeOffset now, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            var entity = await ReadAsync(installation, cancellationToken) ??
                throw new BrokerException("installation_budget_lost", 503);
            Validate(entity);
            if ((string)entity["Owner"] != owner || Date(entity, "LeaseUntil") <= now)
                throw new BrokerException("installation_budget_lost", 503);
            if (Date(entity, "BlockedUntil") > now) throw new BrokerException("github_rate_limited", 503);
        });

    public Task BlockAsync(long installation, DateTimeOffset until, CancellationToken cancellationToken) =>
        ChangeAsync(installation, entity =>
        {
            if (Date(entity, "BlockedUntil") >= until) return false;
            entity["BlockedUntil"] = until;
            return true;
        }, cancellationToken);

    public Task ReleaseAsync(long installation, string owner, CancellationToken cancellationToken) =>
        ChangeAsync(installation, entity =>
        {
            if ((string)entity["Owner"] != owner) return false;
            entity["Owner"] = "";
            entity["LeaseUntil"] = DateTimeOffset.UnixEpoch;
            return true;
        }, cancellationToken);

    private Task ChangeAsync(long installation, Func<TableEntity, bool> change, CancellationToken cancellationToken) =>
        StorageCall.RunAsync(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var entity = await ReadAsync(installation, cancellationToken) ??
                    throw new BrokerException("installation_budget_lost", 503);
                Validate(entity);
                if (!change(entity)) return;
                try
                {
                    await clients.Operations.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);
                    return;
                }
                catch (RequestFailedException exception) when (exception.Status == 412) { }
            }
            throw new BrokerException("installation_budget_conflict", 503);
        });

    private async Task<TableEntity?> ReadAsync(long installation, CancellationToken cancellationToken)
    {
        var response = await clients.Operations.GetEntityIfExistsAsync<TableEntity>($"installation:{installation}",
            "budget", cancellationToken: cancellationToken);
        return response.HasValue ? response.Value : null;
    }

    private static DateTimeOffset Date(TableEntity entity, string field) => (DateTimeOffset)entity[field];
    private static void Validate(TableEntity entity)
    {
        if (!entity.TryGetValue("Owner", out var owner) || owner is not string ||
            !entity.TryGetValue("Count", out var count) || count is not int value || value is < 0 or > OperationsPerHour ||
            new[] { "LeaseUntil", "BlockedUntil", "WindowStart" }.Any(field =>
                !entity.TryGetValue(field, out var date) || date is not DateTimeOffset))
            throw new BrokerException("installation_budget_invalid", 503);
    }
}
