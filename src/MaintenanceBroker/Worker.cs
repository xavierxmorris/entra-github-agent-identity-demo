namespace MaintenanceBroker;

public sealed class ExecutionGuard(DurableOperation owned, bool readOnly, IOperationStore store,
    PolicyRegistry registry, IInstallationBudget budget, TimeProvider clock) : IExecutionGuard
{
    public bool ReadOnly => readOnly;
    public async Task DemandAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now >= owned.ExpiresAt) throw new BrokerException("decision_expired", 403);
        await registry.DemandCurrentAsync(owned, cancellationToken);
        await budget.DemandAsync(owned.Capability.InstallationId, owned.Owner!, now, cancellationToken);
        var current = await store.ReadAsync(owned.Id, cancellationToken);
        if (current is null || current.Owner != owned.Owner || current.ETag != owned.ETag ||
            current.LeaseUntil <= clock.GetUtcNow() ||
            current.Status != (readOnly ? OperationStatus.Reconciling : OperationStatus.Working))
            throw new BrokerException("operation_lease_lost", 409);
    }

    public Task ObserveAsync(DateTimeOffset blockedUntil, CancellationToken cancellationToken) =>
        budget.BlockAsync(owned.Capability.InstallationId, blockedUntil, cancellationToken);
}

public sealed class OutboxDispatcher(IOperationStore store, IWorkQueue queue, PolicyRegistry registry,
    TimeProvider clock, AuditWriter audit)
{
    public async Task DispatchAsync(int shard, CancellationToken cancellationToken)
    {
        await registry.DemandEnabledAsync(cancellationToken);
        foreach (var item in await store.ReadOutboxAsync(shard, cancellationToken))
        {
            var operation = await store.ReadAsync(item.OperationId, cancellationToken) ??
                throw new BrokerException("outbox_operation_missing", 503);
            var context = WorkerProcessor.Context(operation);
            if (!operation.IsTerminal)
            {
                try
                {
                    await registry.DemandCurrentAsync(operation, cancellationToken);
                    if (clock.GetUtcNow() >= operation.ExpiresAt)
                        throw new BrokerException("decision_expired", 403);
                }
                catch (BrokerException exception) when (exception.StatusCode == 403 &&
                    operation.Status == OperationStatus.Pending)
                {
                    var denied = operation with
                    {
                        Status = exception.Code == "decision_expired" ? OperationStatus.Expired : OperationStatus.Denied,
                        Error = exception.Code
                    };
                    if (!await store.ReplaceAsync(denied, cancellationToken)) continue;
                    audit.Write(context, "dispatch", denied.Status);
                    await store.DeleteOutboxAsync(item, cancellationToken);
                    continue;
                }
                await queue.SendAsync(operation.Id, cancellationToken);
            }
            // Sending and deleting are deliberately not atomic; duplicate references are safe.
            await store.DeleteOutboxAsync(item, cancellationToken);
            audit.Write(context, "outbox_dispatch", operation.IsTerminal ? "terminal" : "sent");
        }
    }
}

public sealed class WorkerProcessor(SettingsState settings, IOperationStore store, IWorkQueue queue,
    PolicyRegistry registry, IInstallationBudget budget, IMaintenanceOperation maintenance,
    TimeProvider clock, AuditWriter audit)
{
    public const int MaximumDeliveries = 5;

    public async Task ProcessAsync(QueueDelivery delivery, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        try { await ProcessCoreAsync(delivery, deadline.Token); }
        catch (Exception exception) when (exception is BrokerException or OperationCanceledException)
        {
            var code = exception is BrokerException broker ? broker.Code : "worker_deadline_exceeded";
            audit.Write(new AuditContext(Guid.NewGuid().ToString("D")), "worker_delivery", code);
            if (delivery.DequeueCount < MaximumDeliveries || OperationIds.CapabilityId(delivery.OperationId) is null)
                return;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var current = await store.ReadAsync(delivery.OperationId, cleanup.Token);
            if (current is not null && !current.IsTerminal &&
                (current.Status == OperationStatus.Pending || current.LeaseUntil <= clock.GetUtcNow()))
            {
                var poisoned = current with
                {
                    Status = current.Status == OperationStatus.Pending
                        ? OperationStatus.Poisoned : OperationStatus.ReconciliationRequired,
                    Error = code
                };
                if (await store.ReplaceAsync(poisoned, cleanup.Token))
                {
                    audit.Write(Context(poisoned), "worker_delivery_exhausted", poisoned.Status);
                    if (poisoned.Status == OperationStatus.Poisoned)
                        await queue.PoisonAsync(delivery, code, cleanup.Token);
                    await queue.AcknowledgeAsync(delivery, cleanup.Token);
                }
            }
        }
    }

    private async Task ProcessCoreAsync(QueueDelivery delivery, CancellationToken cancellationToken)
    {
        if (!settings.IsReady || settings.Options.Mode != "Worker")
            throw new BrokerException("worker_not_configured", 503);
        if (OperationIds.CapabilityId(delivery.OperationId) is null)
        {
            audit.Write(new AuditContext(Guid.NewGuid().ToString("D")), "worker_delivery", "invalid_queue_reference");
            await queue.PoisonAsync(delivery, "invalid_queue_reference", cancellationToken);
            await queue.AcknowledgeAsync(delivery, cancellationToken);
            return;
        }
        var operation = await store.ReadAsync(delivery.OperationId, cancellationToken);
        if (operation is null)
        {
            if (delivery.DequeueCount < MaximumDeliveries) throw new BrokerException("queued_operation_missing", 503);
            await queue.PoisonAsync(delivery, "queued_operation_missing", cancellationToken);
            await queue.AcknowledgeAsync(delivery, cancellationToken);
            return;
        }
        operation.Validate();
        var context = Context(operation);
        if (operation.IsTerminal)
        {
            if (operation.Status == OperationStatus.Poisoned)
                await queue.PoisonAsync(delivery, operation.Error ?? "poisoned", cancellationToken);
            await queue.AcknowledgeAsync(delivery, cancellationToken);
            return;
        }
        var now = clock.GetUtcNow();
        var readOnly = operation.Status is OperationStatus.Working or OperationStatus.Reconciling;
        if (readOnly && operation.LeaseUntil > now)
        {
            audit.Write(context, "worker_delivery", "already_owned");
            return;
        }
        try
        {
            if (operation.Caller.TenantId != Guid.Parse(settings.Options.TenantId))
                throw new BrokerException("tenant_denied", 403);
            if (operation.ExpiresAt <= now) throw new BrokerException("decision_expired", 403);
            await registry.DemandCurrentAsync(operation, cancellationToken);
        }
        catch (BrokerException exception) when (exception.StatusCode == 403)
        {
            var rejected = operation with
            {
                Status = readOnly ? OperationStatus.ReconciliationRequired :
                    exception.Code == "decision_expired" ? OperationStatus.Expired : OperationStatus.Denied,
                Error = exception.Code
            };
            if (await store.ReplaceAsync(rejected, cancellationToken))
            {
                audit.Write(context, "worker_decision", rejected.Status);
                await queue.AcknowledgeAsync(delivery, cancellationToken);
            }
            return;
        }
        var owner = Guid.NewGuid().ToString("D");
        var until = now + DurableOperation.LeaseDuration;
        if (until > operation.ExpiresAt) until = operation.ExpiresAt;
        if (!await budget.AcquireAsync(operation.Capability.InstallationId, owner, now, until, cancellationToken))
            throw new BrokerException("installation_budget_exhausted", 503);
        try
        {
            var claimed = operation with
            {
                Owner = owner, LeaseUntil = until,
                Status = readOnly ? OperationStatus.Reconciling : OperationStatus.Working
            };
            if (!await store.ReplaceAsync(claimed, cancellationToken)) return;
            var owned = await store.ReadAsync(operation.Id, cancellationToken) ??
                throw new BrokerException("operation_persistence_failed", 503);
            if (owned.Owner != owner) throw new BrokerException("operation_lease_lost", 409);
            var guard = new ExecutionGuard(owned, readOnly, store, registry, budget, clock);
            DurableOperation outcome;
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            execution.CancelAfter(TimeSpan.FromTicks(Math.Min(DurableOperation.ExecutionDeadline.Ticks,
                Math.Max(1, (until - clock.GetUtcNow()).Ticks))));
            try
            {
                await guard.DemandAsync(execution.Token);
                var result = await maintenance.ExecuteAsync(owned.Plan, context, guard, execution.Token);
                outcome = owned with { Status = OperationStatus.Completed, Result = result, Error = null };
            }
            catch (Exception exception) when (exception is BrokerException or OperationCanceledException)
            {
                outcome = owned with
                {
                    Status = OperationStatus.ReconciliationRequired,
                    Error = exception is BrokerException broker ? broker.Code : "execution_deadline_exceeded"
                };
            }
            // Even cancellation cannot acknowledge a message before its durable outcome is recorded.
            using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!await store.ReplaceAsync(outcome, persist.Token))
                throw new BrokerException("operation_lease_lost", 409);
            audit.Write(context, readOnly ? "worker_reconciliation" : "worker_execution", outcome.Status,
                outcome.Result?.GitHubRequestId, outcome.Result?.PullRequestNumber);
            await queue.AcknowledgeAsync(delivery, persist.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await budget.ReleaseAsync(operation.Capability.InstallationId, owner, cleanup.Token);
        }
    }

    public static AuditContext Context(DurableOperation operation) => new(operation.Id)
    {
        Caller = operation.Caller, CapabilityId = operation.Capability.Id, RequestId = operation.RequestId,
        RepositoryId = operation.Capability.RepositoryId, Branch = operation.Plan.Branch, Decision = "admitted"
    };
}

public sealed class WorkerLoop(WorkerProcessor processor, IWorkQueue queue,
    AuditWriter audit) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                receive.CancelAfter(TimeSpan.FromSeconds(10));
                var message = await queue.ReceiveAsync(receive.Token);
                if (message is not null) await processor.ProcessAsync(message, stoppingToken);
            }

            catch (Exception exception) { Log(exception, "queue_poll"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private void Log(Exception exception, string operation) =>
        audit.Write(new AuditContext(Guid.NewGuid().ToString("D")), operation,
            exception is BrokerException broker ? broker.Code : exception.GetType().Name);
}

public sealed class OutboxLoop(OutboxDispatcher dispatcher, AuditWriter audit) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var shard = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            poll.CancelAfter(TimeSpan.FromSeconds(15));
            try { await dispatcher.DispatchAsync(shard, poll.Token); }
            catch (Exception exception)
            {
                audit.Write(new AuditContext(Guid.NewGuid().ToString("D")), "outbox_poll",
                    exception is BrokerException broker ? broker.Code : exception.GetType().Name);
            }
            shard = (shard + 1) % OperationIds.Shards;
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
