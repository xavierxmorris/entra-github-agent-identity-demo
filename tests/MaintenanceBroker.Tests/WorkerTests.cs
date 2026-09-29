using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class WorkerTests
{
    [Theory]
    [InlineData("persist-before-send")]
    [InlineData("send-before-mark")]
    [InlineData("lost-send-response")]
    public async Task Outbox_crash_windows_recover_and_duplicates_execute_once_under_a_current_lease(string window)
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        harness.Queue.FailBeforeSend = window == "persist-before-send";
        harness.Queue.LoseSendResponse = window == "lost-send-response";
        harness.Store.FailDeleteOutbox = window == "send-before-mark";
        await Assert.ThrowsAsync<BrokerException>(() => harness.DispatchAsync(operation));
        Assert.Equal(1, harness.Store.OutboxCount);
        harness.Queue.FailBeforeSend = false;
        harness.Queue.LoseSendResponse = false;
        harness.Store.FailDeleteOutbox = false;
        await harness.DispatchAsync(operation);
        Assert.Equal(0, harness.Store.OutboxCount);
        foreach (var message in harness.Queue.Sent)
            await harness.Processor.ProcessAsync(message, CancellationToken.None);
        Assert.Equal(1, harness.Operation.Calls);
        Assert.Equal(OperationStatus.Completed, (await harness.Store.ReadAsync(operation.Id, CancellationToken.None))!.Status);
        Assert.Equal(harness.Queue.Sent.Count, harness.Queue.Acknowledged.Count);
    }

    [Fact]
    public async Task Competing_workers_and_duplicate_messages_do_not_take_an_active_operation()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Operation.Handler = async (plan, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Fixtures.Result(plan);
        };
        var processing = harness.Processor.ProcessAsync(message, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Processor.ProcessAsync(message with { MessageId = "competing-worker", DequeueCount = 5 },
                CancellationToken.None);
            Assert.Equal(1, harness.Operation.Calls);
            Assert.Empty(harness.Queue.Acknowledged);
        }
        finally { release.SetResult(); }
        await processing;
        Assert.Single(harness.Queue.Acknowledged);
    }

    [Fact]
    public async Task Etag_compare_exchange_has_only_one_winner()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var candidates = Enumerable.Range(0, 12).Select(_ => operation with
        {
            Owner = Guid.NewGuid().ToString("D"), Status = OperationStatus.Working,
            LeaseUntil = harness.Clock.GetUtcNow() + DurableOperation.LeaseDuration
        });
        var outcomes = await Task.WhenAll(candidates.Select(candidate =>
            harness.Store.ReplaceAsync(candidate, CancellationToken.None)));
        Assert.Single(outcomes, won => won);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("capability")]
    [InlineData("grant-version")]
    [InlineData("capability-version")]
    [InlineData("binding")]
    public async Task Live_revocation_or_remapping_of_queued_work_prevents_signing_and_mutations(string change)
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        var grant = harness.Policy.Rows[(PolicyRegistry.PrincipalPartition(operation.Caller), "sandbox")];
        var capability = harness.Policy.Rows[("capability", "sandbox")];
        switch (change)
        {
            case "grant": grant["Enabled"] = false; break;
            case "capability": capability["Enabled"] = false; break;
            case "grant-version": grant["Version"] = 2L; break;
            case "capability-version":
                grant["CapabilityVersion"] = 2L;
                capability["Version"] = 2L;
                break;
            case "binding":
                capability["RepositoryId"] = 999L;
                grant["RepositoryId"] = 999L;
                break;
        }
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        var result = await harness.Store.ReadAsync(operation.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Denied, result!.Status);
        Assert.Equal(0, harness.Operation.Calls);
        Assert.Single(harness.Queue.Acknowledged);
    }

    [Theory]
    [InlineData("Delegated")]
    [InlineData("Workload")]
    public async Task Decision_expiry_is_not_renewed_by_queue_delivery(string mode)
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync(mode);
        var message = await harness.DispatchAsync(operation);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        var current = await harness.Store.ReadAsync(operation.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Expired, current!.Status);
        Assert.Equal("decision_expired", current.Error);
        Assert.Equal(0, harness.Operation.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_working_uses_read_only_reconciliation_never_resumes_mutation(bool found)
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        Assert.True(await harness.Store.ReplaceAsync(operation with
        {
            Status = OperationStatus.Working, Owner = Guid.NewGuid().ToString("D"),
            LeaseUntil = harness.Clock.GetUtcNow().AddSeconds(120)
        }, CancellationToken.None));
        harness.Clock.Advance(TimeSpan.FromSeconds(121));
        harness.Operation.GuardedHandler = (plan, guard, _) =>
        {
            Assert.True(guard.ReadOnly);
            return found ? Task.FromResult(Fixtures.Result(plan) with { Status = "existing" }) :
                throw new BrokerException("reconciliation_required", 409);
        };
        await harness.Processor.ProcessAsync(message with { DequeueCount = 2 }, CancellationToken.None);
        var outcome = await harness.Store.ReadAsync(operation.Id, CancellationToken.None);
        Assert.Equal(found ? OperationStatus.Completed : OperationStatus.ReconciliationRequired, outcome!.Status);
        Assert.Equal(new[] { true }, harness.Operation.ReadOnlyCalls);
    }

    [Fact]
    public async Task Expired_stale_worker_never_uses_new_user_authority_to_sign()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync("Delegated");
        var message = await harness.DispatchAsync(operation);
        Assert.True(await harness.Store.ReplaceAsync(operation with
        {
            Status = OperationStatus.Working, Owner = Guid.NewGuid().ToString("D"),
            LeaseUntil = harness.Clock.GetUtcNow().AddSeconds(120)
        }, CancellationToken.None));
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        Assert.Equal(OperationStatus.ReconciliationRequired,
            (await harness.Store.ReadAsync(operation.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, harness.Operation.Calls);
    }

    [Fact]
    public async Task Outcome_persists_before_ack_and_redelivery_after_ack_failure_does_not_execute()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        harness.Queue.FailAck = true;
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        Assert.Equal(OperationStatus.Completed, (await harness.Store.ReadAsync(operation.Id, CancellationToken.None))!.Status);
        Assert.Empty(harness.Queue.Acknowledged);
        harness.Queue.FailAck = false;
        await harness.Processor.ProcessAsync(message with { DequeueCount = 2 }, CancellationToken.None);
        Assert.Single(harness.Queue.Acknowledged);
        Assert.Equal(1, harness.Operation.Calls);
    }

    [Fact]
    public async Task Failed_outcome_persistence_leaves_stale_working_for_read_only_reconciliation()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        harness.Operation.Handler = (plan, _) =>
        {
            harness.Store.FailReplace = true;
            return Task.FromResult(Fixtures.Result(plan));
        };
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        Assert.Empty(harness.Queue.Acknowledged);
        Assert.Equal(OperationStatus.Working, (await harness.Store.ReadAsync(operation.Id, CancellationToken.None))!.Status);
        harness.Store.FailReplace = false;
        harness.Operation.Handler = null;
        harness.Clock.Advance(TimeSpan.FromSeconds(121));
        await harness.Processor.ProcessAsync(message with { DequeueCount = 2 }, CancellationToken.None);
        Assert.Equal(new[] { false, true }, harness.Operation.ReadOnlyCalls);
    }

    [Fact]
    public async Task Dependency_failure_is_fail_closed_and_poison_is_visible_before_ack()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        harness.Policy.Unavailable = true;
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        Assert.Equal(0, harness.Operation.Calls);
        Assert.Empty(harness.Queue.Acknowledged);
        harness.Queue.FailPoison = true;
        await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Processor.ProcessAsync(message with { DequeueCount = 5 }, CancellationToken.None));
        var current = await harness.Store.ReadAsync(operation.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Poisoned, current!.Status);
        Assert.Equal("storage_unavailable", current.View.Error);
        Assert.Empty(harness.Queue.Acknowledged);
        harness.Queue.FailPoison = false;
        await harness.Processor.ProcessAsync(message with { DequeueCount = 6 }, CancellationToken.None);
        Assert.Single(harness.Queue.Poisoned);
        Assert.Single(harness.Queue.Acknowledged);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("global")]
    [InlineData("grant")]
    [InlineData("lease")]
    [InlineData("expiry")]
    public async Task Guard_rechecks_authority_control_and_ownership_immediately_before_external_actions(string change)
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        var message = await harness.DispatchAsync(operation);
        harness.Operation.GuardedHandler = async (plan, guard, cancellationToken) =>
        {
            await guard.DemandAsync(cancellationToken);
            switch (change)
            {
                case "local": harness.Configuration["Broker:KillSwitch"] = "true"; break;
                case "global": harness.Policy.Rows[("control", "global")]["Enabled"] = false; break;
                case "grant":
                    harness.Policy.Rows[(PolicyRegistry.PrincipalPartition(operation.Caller), "sandbox")]["Enabled"] = false;
                    break;
                case "lease": harness.Clock.Advance(TimeSpan.FromSeconds(120)); break;
                case "expiry": harness.Clock.Advance(TimeSpan.FromMinutes(5)); break;
            }
            await guard.DemandAsync(cancellationToken);
            Assert.Fail("A revoked or expired decision must not authorize a mutation.");
            return Fixtures.Result(plan);
        };
        await harness.Processor.ProcessAsync(message, CancellationToken.None);
        var outcome = await harness.Store.ReadAsync(operation.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.ReconciliationRequired, outcome!.Status);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task Shared_installation_budget_enforces_concurrency_hourly_starts_and_rate_block_across_workers()
    {
        var budget = new MemoryBudget();
        var now = DateTimeOffset.UtcNow;
        Assert.True(await budget.AcquireAsync(202, "first", now, now.AddMinutes(2), CancellationToken.None));
        Assert.False(await budget.AcquireAsync(202, "second", now, now.AddMinutes(2), CancellationToken.None));
        await budget.BlockAsync(202, now.AddMinutes(10), CancellationToken.None);
        await budget.ReleaseAsync(202, "first", CancellationToken.None);
        Assert.False(await budget.AcquireAsync(202, "second", now.AddMinutes(3), now.AddMinutes(5), CancellationToken.None));
        for (var i = 1; i < AzureInstallationBudget.OperationsPerHour; i++)
        {
            Assert.True(await budget.AcquireAsync(202, "next", now.AddMinutes(11), now.AddMinutes(13), CancellationToken.None));
            await budget.ReleaseAsync(202, "next", CancellationToken.None);
        }
        Assert.False(await budget.AcquireAsync(202, "last", now.AddMinutes(15), now.AddMinutes(17), CancellationToken.None));
        Assert.True(await budget.AcquireAsync(203, "other-installation", now, now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task Outbox_rechecks_revocation_and_expiry_without_dispatching_expired_decisions()
    {
        var harness = new WorkerHarness();
        var operation = await harness.AdmitAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Dispatcher.DispatchAsync(Convert.ToInt32(OperationIds.Partition(operation.Id)[1..], 16),
            CancellationToken.None);
        Assert.Empty(harness.Queue.Sent);
        Assert.Equal(0, harness.Store.OutboxCount);
        Assert.Equal(OperationStatus.Expired, (await harness.Store.ReadAsync(operation.Id, CancellationToken.None))!.Status);
    }
}
