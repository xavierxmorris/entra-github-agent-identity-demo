using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MaintenanceBroker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenanceBroker.Tests;

public sealed class HttpTests
{
    [Theory]
    [InlineData("Workload")]
    [InlineData("Delegated")]
    public async Task Valid_local_signed_access_tokens_use_real_bearer_validation_and_replay(string mode)
    {
        using var factory = new BrokerFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(Fixtures.Claims(mode)));
        using var first = await PostAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var result = await first.Content.ReadFromJsonAsync<OperationView>();
        Assert.NotNull(result);
        Assert.Equal("pending", result.Status);
        Assert.Equal("sandbox", result.CapabilityId);
        using var second = await PostAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var replay = await second.Content.ReadFromJsonAsync<OperationView>();
        Assert.NotNull(replay);
        Assert.Equal(result, replay);
        Assert.Equal(0, factory.Operation.Calls);
        Assert.Equal(1, factory.Store.Count);
        Assert.Equal(1, factory.Store.OutboxCount);
        Assert.Equal("/v1/operations/" + result.Id, first.Headers.Location!.ToString());
        Assert.Equal(TimeSpan.FromSeconds(5), first.Headers.RetryAfter!.Delta);
        Assert.True(first.Headers.Contains("X-Correlation-Id"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task Missing_or_cryptographically_invalid_tokens_return_401(string failure)
    {
        using var factory = new BrokerFactory();
        using var client = factory.CreateClient();
        using var otherKey = RSA.Create(2048);
        var token = failure switch
        {
            "missing" => null,
            "malformed" => "not-a-jwt",
            "issuer" => factory.Token(issuer: "https://login.microsoftonline.com/99999999-9999-4999-8999-999999999999/v2.0"),
            "audience" => factory.Token(audience: Fixtures.WorkloadClient),
            "expired" => factory.Token(expires: DateTime.UtcNow.AddMinutes(-5)),
            "signature" => factory.Token(alternateKey: otherKey),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await PostAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal(0, factory.Operation.Calls);
        Assert.Equal(0, factory.Store.Admits);
        Assert.Empty(factory.Policy.Reads);
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("missing-role")]
    [InlineData("delegated-role-confusion")]
    [InlineData("workload-scope-confusion")]
    [InlineData("wrong-client")]
    [InlineData("wrong-oid")]
    [InlineData("wrong-tenant")]
    [InlineData("v1")]
    public async Task Valid_tokens_with_wrong_policy_claims_return_403(string failure)
    {
        using var factory = new BrokerFactory();
        using var client = factory.CreateClient();
        var claims = Fixtures.Claims(failure == "delegated-role-confusion" ? "Delegated" : "Workload");
        switch (failure)
        {
            case "missing-role": claims.RemoveAll(c => c.Type == "roles"); break;
            case "delegated-role-confusion":
                claims.RemoveAll(c => c.Type == "scp");
                claims.Add(new Claim("roles", "Maintenance.Request"));
                break;
            case "workload-scope-confusion": claims.Add(new Claim("scp", "Agent.Invoke")); break;
            case "wrong-client": Replace("azp", Fixtures.DelegatedClient); break;
            case "wrong-oid": Replace("oid", Fixtures.UserObject); break;
            case "wrong-tenant": Replace("tid", Fixtures.UserObject); break;
            case "v1": Replace("ver", "1.0"); break;
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(claims));
        using var response = await PostAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
        Assert.Equal(0, factory.Store.Admits);
        void Replace(string type, string value)
        {
            claims.RemoveAll(c => c.Type == type);
            claims.Add(new Claim(type, value));
        }
    }

    [Theory]
    [InlineData("other")]
    [InlineData("unknown")]
    public async Task Cross_capability_requests_are_forbidden(string capabilityId)
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        using var response = await client.PostAsJsonAsync("/v1/maintenance-pr",
            new { capabilityId, requestId = Fixtures.RequestId });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Theory]
    [InlineData("""{"capabilityId":"sandbox","requestId":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","userId":"trusted"}""")]
    [InlineData("""{"capabilityId":"sandbox","requestId":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","path":".github/workflows/x.yml"}""")]
    [InlineData("""{"capabilityId":"sandbox","capabilityId":"other","requestId":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"}""")]
    [InlineData("""{"capabilityId":"sandbox","requestId":"AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"}""")]
    [InlineData("""{"capabilityId":"sandbox","requestId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"capabilityId":"sandbox","requestId":"aaaaaaaabbbb4ccc8dddeeeeeeeeeeee"}""")]
    [InlineData("""{"capabilityId":"sandbox"}""")]
    [InlineData("""[]""")]
    [InlineData("""{""")]
    public async Task Body_is_small_strict_json_not_a_source_of_identity_or_arbitrary_operations(string body)
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/maintenance-pr", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Fact]
    public async Task Wrong_media_type_and_oversized_body_are_rejected()
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        using var text = new StringContent("not-json");
        using var wrongType = await client.PostAsync("/v1/maintenance-pr", text);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using var large = new StringContent(new string(' ', 1025), Encoding.UTF8, "application/json");
        using var tooLarge = await client.PostAsync("/v1/maintenance-pr", large);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Fact]
    public async Task Kill_switch_denies_before_operation()
    {
        using var factory = new BrokerFactory(disabled: true);
        using var client = AuthorizedClient(factory);
        using var response = await PostAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Fact]
    public async Task Kill_switch_is_checked_again_before_cached_replay()
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        using var initial = await PostAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, initial.StatusCode);
        factory.Services.GetRequiredService<IConfiguration>()["Broker:KillSwitch"] = "true";
        using var disabled = await PostAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task Health_is_anonymous_and_readiness_checks_storage(bool configured, HttpStatusCode readyStatus)
    {
        using var factory = new BrokerFactory(configured);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await health.Content.ReadAsStringAsync());
        using var ready = await client.GetAsync("/readyz");
        Assert.Equal(readyStatus, ready.StatusCode);
        Assert.Contains("storage", await ready.Content.ReadAsStringAsync());
        Assert.Equal(configured ? 1 : 0, factory.Store.Probes);
        Assert.Equal(0, factory.Operation.Calls);
        if (!configured)
        {
            using var operation = await PostAsync(client);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, operation.StatusCode);
        }
    }

    [Fact]
    public async Task Failure_is_not_cached_as_success_and_error_details_are_sanitized()
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        factory.Store.FailBeforeCommit = true;
        using var failed = await PostAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("fixture-upstream-secret", await failed.Content.ReadAsStringAsync());
        factory.Store.FailBeforeCommit = false;
        using var retried = await PostAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
        Assert.Equal(1, factory.Store.Count);
    }

    [Fact]
    public async Task Duplicate_in_flight_admission_returns_same_durable_operation()
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Store.BeforeAdmit = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var pending = PostAsync(client);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            factory.Store.BeforeAdmit = null;
            using var duplicate = await PostAsync(client);
            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        }
        finally { release.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, factory.Store.Count);
    }

    [Fact]
    public async Task Global_rate_limit_bounds_requests_and_does_not_block_liveness()
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        for (var i = 0; i < 30; i++)
        {
            using var accepted = await PostAsync(client);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        using var rejected = await PostAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
    }

    [Fact]
    public async Task At_most_two_operations_run_concurrently_without_an_unbounded_queue()
    {
        using var factory = new BrokerFactory();
        using var client = AuthorizedClient(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Store.BeforeAdmit = async cancellationToken =>
        {
            if (factory.Store.Admits == 2) entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var first = PostAsync(client);
        var second = PostAsync(client, Guid.NewGuid().ToString("D"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var third = await PostAsync(client, Guid.NewGuid().ToString("D"));
            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        }
        finally { release.TrySetResult(); }
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        Assert.Equal(0, factory.Operation.Calls);
        Assert.Equal(2, factory.Store.Count);
    }

    private static HttpClient AuthorizedClient(BrokerFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? requestId = null) =>
        client.PostAsJsonAsync("/v1/maintenance-pr", new { capabilityId = "sandbox", requestId = requestId ?? Fixtures.RequestId });
}
