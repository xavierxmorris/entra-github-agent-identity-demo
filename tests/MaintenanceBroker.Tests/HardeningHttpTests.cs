using System.Net;
using System.Net.Http.Json;
using MaintenanceBroker;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenanceBroker.Tests;

public sealed class HardeningHttpTests
{
    [Fact]
    public async Task Admission_does_not_register_signer_github_client_or_worker()
    {
        using var factory = new BrokerFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Services.GetService<IAppJwtFactory>());
        Assert.Null(factory.Services.GetService<GitHubTransport>());
        Assert.Null(factory.Services.GetService<IMaintenanceOperation>());
        Assert.Null(factory.Services.GetService<WorkerProcessor>());
        Assert.Null(factory.Services.GetService<IInstallationBudget>());
    }

    [Fact]
    public async Task Worker_maps_health_only_even_with_a_valid_access_token()
    {
        using var factory = new BrokerFactory(mode: "Worker");
        using var client = factory.CreateClient();
        Assert.NotNull(factory.Services.GetService<IAppJwtFactory>());
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var post = await PostAsync(client);
        using var get = await client.GetAsync("/v1/operations/" +
            OperationIds.Create(Fixtures.Caller(), "sandbox", Guid.Parse(Fixtures.RequestId)));
        using var health = await client.GetAsync("/healthz");
        using var ready = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(0, factory.Store.Admits);
    }

    [Fact]
    public async Task Shared_store_preserves_idempotency_across_restarted_hosts_and_completed_duplicates_return_result()
    {
        var store = new MemoryOperationStore();
        OperationView original;
        using (var firstHost = new BrokerFactory(sharedStore: store))
        using (var client = Authorized(firstHost))
        {
            using var response = await PostAsync(client);
            original = (await response.Content.ReadFromJsonAsync<OperationView>())!;
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        var operation = (await store.ReadAsync(original.Id, CancellationToken.None))!;
        Assert.True(await store.ReplaceAsync(operation with
        {
            Status = OperationStatus.Completed, Result = Fixtures.Result(operation.Plan)
        }, CancellationToken.None));
        using var restarted = new BrokerFactory(sharedStore: store);
        using var second = Authorized(restarted);
        using var duplicate = await PostAsync(second);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var completed = (await duplicate.Content.ReadFromJsonAsync<OperationView>())!;
        Assert.Equal(original.Id, completed.Id);
        Assert.Equal(original.AcceptedAt, completed.AcceptedAt);
        Assert.Equal(OperationStatus.Completed, completed.Status);
        Assert.Equal(17, completed.Result!.PullRequestNumber);
        Assert.Equal(1, store.Count);
        using var get = await second.GetAsync(duplicate.Headers.Location);
        Assert.Equal(completed, await get.Content.ReadFromJsonAsync<OperationView>());
    }

    [Fact]
    public async Task Get_requires_new_authentication_same_caller_and_current_grant_before_disclosure()
    {
        using var factory = new BrokerFactory();
        using var client = Authorized(factory);
        using var admitted = await PostAsync(client);
        var location = admitted.Headers.Location!;
        using var own = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(Fixtures.Claims("Delegated")));
        using var other = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.DoesNotContain(Fixtures.RequestId, await other.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        factory.Policy.Rows[(PolicyRegistry.PrincipalPartition(Fixtures.Caller()), "sandbox")]["Enabled"] = false;
        var readsBefore = factory.Store.Reads;
        using var revoked = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(readsBefore, factory.Store.Reads);
    }

    [Theory]
    [InlineData("Admission", "policy")]
    [InlineData("Admission", "operations")]
    [InlineData("Worker", "policy")]
    [InlineData("Worker", "operations")]
    [InlineData("Worker", "queue")]
    public async Task Readiness_actually_checks_dependencies_and_never_writes_or_signs(string mode, string dependency)
    {
        using var factory = new BrokerFactory(mode: mode);
        using var client = factory.CreateClient();
        if (dependency == "policy") factory.Policy.Unavailable = true;
        if (dependency == "operations") factory.Store.Unavailable = true;
        if (dependency == "queue") factory.Queue.FailBeforeSend = true;
        using var ready = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, factory.Store.Admits);
        Assert.Empty(factory.Queue.Sent);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Fact]
    public async Task Admission_readiness_needs_only_table_reads_not_queue_metadata_or_poison_permissions()
    {
        using var factory = new BrokerFactory();
        using var client = factory.CreateClient();
        factory.Queue.FailBeforeSend = true;
        using var ready = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(new[] { ("control", "global") }, factory.Policy.Reads);
        Assert.Equal(1, factory.Store.Probes);
        Assert.Equal(0, factory.Queue.Probes);
        Assert.Empty(factory.Queue.Sent);
        Assert.Empty(factory.Queue.Acknowledged);
        Assert.Empty(factory.Queue.Poisoned);
    }

    [Fact]
    public async Task Readiness_is_bounded_even_if_an_injected_dependency_ignores_cancellation()
    {
        using var factory = new BrokerFactory();
        using var client = factory.CreateClient();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Store.BeforeProbe = _ => stalled.Task;
        try
        {
            using var response = await client.GetAsync("/readyz").WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { stalled.SetResult(); }
    }

    [Fact]
    public async Task Durable_disabled_switch_and_policy_failure_cannot_admit_new_operations()
    {
        using var factory = new BrokerFactory();
        using var client = Authorized(factory);
        factory.Policy.Rows[("control", "global")]["Enabled"] = false;
        using var disabled = await PostAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        factory.Policy.Unavailable = true;
        using var unavailable = await PostAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(0, factory.Store.Count);
    }

    private static HttpClient Authorized(BrokerFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        return client;
    }
    private static Task<HttpResponseMessage> PostAsync(HttpClient client) =>
        client.PostAsJsonAsync("/v1/maintenance-pr", new { capabilityId = "sandbox", requestId = Fixtures.RequestId });
}
