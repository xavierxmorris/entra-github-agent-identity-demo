using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class LedgerTests
{
    [Fact]
    public async Task Duplicate_admission_is_atomic_and_retained_across_time_boundaries()
    {
        var harness = new WorkerHarness();
        var first = await harness.AdmitAsync();
        var duplicate = await harness.AdmitAsync();
        Assert.Equal(first, duplicate);
        Assert.Equal(1, harness.Store.Count);
        Assert.Equal(1, harness.Store.OutboxCount);
        harness.Clock.Advance(TimeSpan.FromDays(365));
        Assert.Equal(first, await harness.AdmitAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Atomic_admission_crash_before_or_after_commit_is_recoverable(bool committed)
    {
        var harness = new WorkerHarness();
        harness.Store.FailBeforeCommit = !committed;
        harness.Store.LoseCommitResponse = committed;
        await Assert.ThrowsAsync<BrokerException>(() => harness.AdmitAsync());
        Assert.Equal(committed ? 1 : 0, harness.Store.Count);
        Assert.Equal(harness.Store.Count, harness.Store.OutboxCount);
        harness.Store.FailBeforeCommit = false;
        harness.Store.LoseCommitResponse = false;
        var retried = await harness.AdmitAsync();
        Assert.Equal(1, harness.Store.Count);
        await harness.DispatchAsync(retried);
        Assert.Single(harness.Queue.Sent);
    }

    [Fact]
    public async Task Operation_and_outbox_share_a_bounded_stable_partition_and_outbox_query_is_bounded()
    {
        var harness = new WorkerHarness();
        var partitions = new HashSet<string>();
        for (var index = 0; index < 600; index++)
        {
            var operation = await harness.AdmitAsync(requestId: Guid.NewGuid());
            partitions.Add(OperationIds.Partition(operation.Id));
            Assert.Equal(operation.Id, OperationIds.Create(operation.Caller, operation.Capability.Id, operation.RequestId));
        }
        Assert.Equal(OperationIds.Shards, partitions.Count);
        for (var shard = 0; shard < OperationIds.Shards; shard++)
            Assert.InRange((await harness.Store.ReadOutboxAsync(shard, CancellationToken.None)).Count, 0, 32);
    }

    [Fact]
    public void Id_separates_callers_clients_modes_and_capabilities_but_not_live_mapping_changes()
    {
        var caller = Fixtures.Caller();
        var request = Guid.Parse(Fixtures.RequestId);
        var original = OperationIds.Create(caller, "sandbox", request);
        Assert.NotEqual(original, OperationIds.Create(caller, "other", request));
        Assert.NotEqual(original, OperationIds.Create(caller with { ObjectId = Guid.NewGuid() }, "sandbox", request));
        Assert.NotEqual(original, OperationIds.Create(caller with { ClientId = Guid.NewGuid() }, "sandbox", request));
        Assert.NotEqual(original, OperationIds.Create(caller with { Mode = "Delegated" }, "sandbox", request));
        Assert.Equal("sandbox", OperationIds.CapabilityId(original));
        Assert.Null(OperationIds.CapabilityId(original.ToUpperInvariant()));
    }
}
