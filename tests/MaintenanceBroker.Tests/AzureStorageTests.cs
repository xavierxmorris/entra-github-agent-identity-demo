using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class AzureStorageTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("none")]
    public async Task Real_adapter_uses_one_same_partition_insert_transaction_and_recovers_uncertain_commits(string failure)
    {
        var clients = new RecordingStorageClients();
        var store = new AzureOperationStore(clients);
        var operation = await new WorkerHarness().AdmitAsync();
        clients.Table.TransactionFailure = failure;
        if (failure != "none")
        {
            Assert.Equal("storage_unavailable", (await Assert.ThrowsAsync<BrokerException>(() =>
                store.AdmitAsync(operation, CancellationToken.None))).Code);
            Assert.Equal(failure == "after" ? 2 : 0, clients.Table.Entities.Count);
        }
        clients.Table.TransactionFailure = "none";
        var saved = await store.AdmitAsync(operation, CancellationToken.None);
        Assert.Equal(operation.Id, saved.Id);
        var actions = clients.Table.Transactions[0];
        Assert.Equal(2, actions.Length);
        Assert.All(actions, action => Assert.Equal(TableTransactionActionType.Add, action.ActionType));
        Assert.Single(actions.Select(action => action.Entity.PartitionKey).Distinct());
        Assert.Contains(actions, action => action.Entity.RowKey == "op:" + operation.Id);
        Assert.Contains(actions, action => action.Entity.RowKey == "out:" + operation.Id);
        var replay = await store.AdmitAsync(operation with { AcceptedAt = operation.AcceptedAt.AddSeconds(1) },
            CancellationToken.None);
        Assert.Equal(saved.AcceptedAt, replay.AcceptedAt);
        Assert.Equal(2, clients.Table.Entities.Count);
    }

    [Fact]
    public async Task Real_adapter_uses_conditional_replacement_and_deletes_only_outbox()
    {
        var clients = new RecordingStorageClients();
        var store = new AzureOperationStore(clients);
        var saved = await store.AdmitAsync(await new WorkerHarness().AdmitAsync(), CancellationToken.None);
        Assert.True(await store.ReplaceAsync(saved with { Status = OperationStatus.Expired, Error = "decision_expired" }, CancellationToken.None));
        Assert.False(await store.ReplaceAsync(saved with { Status = OperationStatus.Failed, Error = "test_failure" }, CancellationToken.None));
        var shard = Convert.ToInt32(OperationIds.Partition(saved.Id)[1..], 16);
        var outbox = await store.ReadOutboxAsync(shard, CancellationToken.None);
        var query = Assert.Single(clients.Table.Queries);
        Assert.Equal(32, query.Limit);
        Assert.Equal($"PartitionKey eq '{OperationIds.Shard(shard)}' and RowKey ge 'out:' and RowKey lt 'out;'", query.Filter);
        Assert.Single(outbox);
        await store.DeleteOutboxAsync(outbox[0], CancellationToken.None);
        Assert.Single(clients.Table.Entities);
        Assert.Equal(OperationStatus.Expired, (await store.ReadAsync(saved.Id, CancellationToken.None))!.Status);
        Assert.All(clients.Table.Conditions, etag => Assert.NotEqual("*", etag.ToString()));
    }

    [Fact]
    public async Task Real_budget_adapter_persists_global_concurrency_start_limit_and_rate_headers_across_instances()
    {
        var clients = new RecordingStorageClients();
        var first = new AzureInstallationBudget(clients);
        var restarted = new AzureInstallationBudget(clients);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await first.AcquireAsync(202, "owner-1", now, now.AddMinutes(2), CancellationToken.None));
        Assert.False(await restarted.AcquireAsync(202, "owner-2", now, now.AddMinutes(2), CancellationToken.None));
        await first.BlockAsync(202, now.AddMinutes(10), CancellationToken.None);
        await first.ReleaseAsync(202, "owner-1", CancellationToken.None);
        Assert.False(await restarted.AcquireAsync(202, "owner-2", now.AddMinutes(3), now.AddMinutes(5), CancellationToken.None));
        for (var i = 1; i < AzureInstallationBudget.OperationsPerHour; i++)
        {
            Assert.True(await restarted.AcquireAsync(202, "owner-2", now.AddMinutes(11), now.AddMinutes(13), CancellationToken.None));
            await restarted.ReleaseAsync(202, "owner-2", CancellationToken.None);
        }
        Assert.False(await first.AcquireAsync(202, "owner-3", now.AddMinutes(15), now.AddMinutes(17), CancellationToken.None));
        Assert.True(await first.AcquireAsync(202, "owner-3", now.AddHours(1), now.AddHours(1).AddMinutes(2), CancellationToken.None));
        Assert.Single(clients.Table.Entities);
        Assert.All(clients.Table.Conditions, etag => Assert.NotEqual("*", etag.ToString()));
    }

    [Fact]
    public async Task Real_queue_adapter_uses_only_reference_infinite_ttl_one_delivery_and_bounded_visibility()
    {
        var clients = new RecordingStorageClients();
        var queue = new AzureWorkQueue(clients);
        var operation = await new WorkerHarness().AdmitAsync();
        await queue.SendAsync(operation.Id, CancellationToken.None);
        var sent = Assert.Single(clients.Work.Sends);
        Assert.Equal(operation.Id, sent.Text);
        Assert.Equal(TimeSpan.FromSeconds(-1), sent.Ttl);
        await queue.ReceiveAsync(CancellationToken.None);
        Assert.Equal(1, clients.Work.MaximumReceived);
        Assert.Equal(TimeSpan.FromSeconds(150), clients.Work.Visibility);
        var delivery = new QueueDelivery("id", "receipt", operation.Id, 5);
        await queue.AcknowledgeAsync(delivery, CancellationToken.None);
        Assert.Equal(("id", "receipt"), Assert.Single(clients.Work.Deletes));
        await queue.PoisonAsync(delivery with { OperationId = "untrusted-body-fixture" }, "invalid_queue_reference",
            CancellationToken.None);
        var poison = Assert.Single(clients.Dead.Sends);
        Assert.DoesNotContain("untrusted-body-fixture", poison.Text);
        Assert.Equal(TimeSpan.FromSeconds(-1), poison.Ttl);
    }

    [Fact]
    public async Task Readiness_adapter_queries_only_a_bounded_probe_and_never_creates_resources()
    {
        var clients = new RecordingStorageClients();
        await new AzureOperationStore(clients).ProbeAsync(CancellationToken.None);
        var query = Assert.Single(clients.Table.Queries);
        Assert.Equal(1, query.Limit);
        Assert.Equal("PartitionKey eq 'health' and RowKey eq 'probe'", query.Filter);
        Assert.Empty(clients.Table.Transactions);
    }

    [Fact]
    public async Task Worker_queue_readiness_reads_both_queue_properties_without_dequeuing_or_writing()
    {
        var clients = new RecordingStorageClients();
        await new AzureWorkQueue(clients).ProbeAsync(CancellationToken.None);
        foreach (var queue in new[] { clients.Work, clients.Dead })
        {
            Assert.Equal(1, queue.PropertyReads);
            Assert.Null(queue.MaximumReceived);
            Assert.Empty(queue.Sends);
            Assert.Empty(queue.Deletes);
        }
    }
}

internal sealed class RecordingStorageClients : IStorageClients
{
    public RecordingTable Table { get; } = new();
    public RecordingQueue Work { get; } = new();
    public RecordingQueue Dead { get; } = new();
    public TableClient Operations => Table;
    public TableClient Policy => Table;
    public QueueClient Queue => Work;
    public QueueClient Poison => Dead;
}

internal sealed class RecordingTable : TableClient
{
    public Dictionary<(string, string), TableEntity> Entities { get; } = [];
    public List<TableTransactionAction[]> Transactions { get; } = [];
    public List<(string? Filter, int? Limit)> Queries { get; } = [];
    public List<ETag> Conditions { get; } = [];
    public string TransactionFailure { get; set; } = "none";
    private int version;

    public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey,
        IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var entity = Entities.GetValueOrDefault((partitionKey, rowKey));
        var value = entity is null ? default : Clone(entity) is T typed ? typed : throw new NotSupportedException();
        return Task.FromResult<NullableResponse<T>>(new OptionalResponse<T>(value));
    }

    public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
        IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default)
    {
        var actions = transactionActions.ToArray();
        Transactions.Add(actions);
        if (TransactionFailure == "before") throw new RequestFailedException(503, "test");
        if (actions.Any(action => Entities.ContainsKey((action.Entity.PartitionKey, action.Entity.RowKey))))
            throw new RequestFailedException(409, "test");
        foreach (var action in actions)
        {
            var entity = action.Entity as TableEntity ?? throw new NotSupportedException();
            Save(entity);
        }
        if (TransactionFailure == "after") throw new RequestFailedException(503, "test");
        return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>(
            actions.Select(_ => new StorageResponse()).ToArray(), new StorageResponse()));
    }

    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
    {
        if (Entities.ContainsKey((entity.PartitionKey, entity.RowKey))) throw new RequestFailedException(409, "test");
        Save(entity as TableEntity ?? throw new NotSupportedException());
        return Task.FromResult<Response>(new StorageResponse());
    }

    public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge,
        CancellationToken cancellationToken = default)
    {
        Conditions.Add(ifMatch);
        Assert.Equal(TableUpdateMode.Replace, mode);
        if (!Entities.TryGetValue((entity.PartitionKey, entity.RowKey), out var current))
            throw new RequestFailedException(404, "test");
        if (current.ETag != ifMatch) throw new RequestFailedException(412, "test");
        Save(entity as TableEntity ?? throw new NotSupportedException());
        return Task.FromResult<Response>(new StorageResponse());
    }

    public override Task<Response> DeleteEntityAsync(string partitionKey, string rowKey, ETag ifMatch = default,
        CancellationToken cancellationToken = default)
    {
        Conditions.Add(ifMatch);
        if (!Entities.TryGetValue((partitionKey, rowKey), out var current)) throw new RequestFailedException(404, "test");
        if (current.ETag != ifMatch) throw new RequestFailedException(412, "test");
        Entities.Remove((partitionKey, rowKey));
        return Task.FromResult<Response>(new StorageResponse());
    }

    public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null,
        IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        Queries.Add((filter, maxPerPage));
        var values = Entities.Values.Where(entity => entity.RowKey.StartsWith("out:", StringComparison.Ordinal))
            .Take(maxPerPage ?? 1000).Select(entity => Clone(entity) is T value ? value : throw new NotSupportedException()).ToArray();
        return AsyncPageable<T>.FromPages([Page<T>.FromValues(values, null, new StorageResponse())]);
    }

    private void Save(TableEntity entity)
    {
        var copy = Clone(entity);
        copy.ETag = new ETag((++version).ToString());
        Entities[(copy.PartitionKey, copy.RowKey)] = copy;
    }
    private static TableEntity Clone(TableEntity entity) => new(entity.ToDictionary(pair => pair.Key, pair => pair.Value))
        { ETag = entity.ETag };
}

internal sealed class RecordingQueue : QueueClient
{
    public List<(string Text, TimeSpan? Ttl)> Sends { get; } = [];
    public List<(string, string)> Deletes { get; } = [];
    public int? MaximumReceived { get; private set; }
    public TimeSpan? Visibility { get; private set; }
    public int PropertyReads { get; private set; }
    public override Task<Response<QueueProperties>> GetPropertiesAsync(CancellationToken cancellationToken = default)
    {
        PropertyReads++;
        return Task.FromResult(Response.FromValue(
            QueuesModelFactory.QueueProperties(new Dictionary<string, string>(), 0), new StorageResponse()));
    }
    public override Task<Response<SendReceipt>> SendMessageAsync(string messageText, TimeSpan? visibilityTimeout = null,
        TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        Sends.Add((messageText, timeToLive));
        return Task.FromResult(Response.FromValue(QueuesModelFactory.SendReceipt("id", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7), "receipt", DateTimeOffset.UtcNow), new StorageResponse()));
    }
    public override Task<Response<QueueMessage[]>> ReceiveMessagesAsync(int? maxMessages = null,
        TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
    {
        MaximumReceived = maxMessages;
        Visibility = visibilityTimeout;
        return Task.FromResult(Response.FromValue(Array.Empty<QueueMessage>(), new StorageResponse()));
    }
    public override Task<Response> DeleteMessageAsync(string messageId, string popReceipt,
        CancellationToken cancellationToken = default)
    {
        Deletes.Add((messageId, popReceipt));
        return Task.FromResult<Response>(new StorageResponse());
    }
}

internal sealed class OptionalResponse<T>(T? value) : NullableResponse<T>
{
    public override bool HasValue => value is not null;
    public override T Value => value ?? throw new InvalidOperationException();
    public override Response GetRawResponse() => new StorageResponse();
}

internal sealed class StorageResponse : Response
{
    public override int Status => 200;
    public override string ReasonPhrase => "OK";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = "";
    public override void Dispose() { }
    protected override bool ContainsHeader(string name) => false;
    protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value) { value = null; return false; }
    protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values) { values = null; return false; }
}
