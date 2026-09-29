using System.Net;
using System.Text;
using System.Text.Json;
using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class GitHubOperationTests
{
    [Theory]
    [InlineData("Workload")]
    [InlineData("Delegated")]
    public async Task Successful_operation_scopes_token_writes_only_metadata_opens_pr_and_revokes(string mode)
    {
        var plan = Fixtures.Plan(mode);
        using var harness = new GitHubHarness(GitHubFixtures.Success(plan));
        var result = await harness.ExecuteAsync(plan);
        Assert.Equal("created", result.Status);
        Assert.Equal(17, result.PullRequestNumber);
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.All(harness.Handler.Responses, response => Assert.True(response.WasDisposed));
        var requests = harness.Handler.Requests;
        using var tokenBody = JsonDocument.Parse(requests[0].Body!);
        Assert.Equal(2, tokenBody.RootElement.EnumerateObject().Count());
        Assert.Equal(101, Assert.Single(tokenBody.RootElement.GetProperty("repository_ids").EnumerateArray()).GetInt64());
        var permissions = tokenBody.RootElement.GetProperty("permissions");
        Assert.Equal(2, permissions.EnumerateObject().Count());
        Assert.Equal("write", permissions.GetProperty("contents").GetString());
        Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
        Assert.Equal("Bearer fixture-app-jwt-not-a-real-credential", requests[0].Authorization);
        Assert.All(requests.Skip(1), request => Assert.Equal("Bearer " + GitHubFixtures.InstallationToken, request.Authorization));
        using var writeBody = JsonDocument.Parse(Assert.Single(requests, r => r.Method == "PUT").Body!);
        Assert.Equal(plan.Branch, writeBody.RootElement.GetProperty("branch").GetString());
        Assert.Equal(plan.Content, Encoding.UTF8.GetString(Convert.FromBase64String(
            writeBody.RootElement.GetProperty("content").GetString()!)));
        Assert.False(writeBody.RootElement.TryGetProperty("author", out _));
        var pr = Assert.Single(requests, r => r.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/pulls"));
        using var prBody = JsonDocument.Parse(pr.Body!);
        Assert.Equal(OperationPlan.Title, prBody.RootElement.GetProperty("title").GetString());
        Assert.Equal(OperationPlan.PullRequestBody, prBody.RootElement.GetProperty("body").GetString());
        Assert.Equal(plan.Branch, prBody.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", prBody.RootElement.GetProperty("base").GetString());
        Assert.False(prBody.RootElement.GetProperty("maintainer_can_modify").GetBoolean());
        Assert.Equal("DELETE", requests[^1].Method);
        Assert.DoesNotContain(GitHubFixtures.InstallationToken, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("fixture-app-jwt", JsonSerializer.Serialize(result));

        Assert.NotEmpty(harness.Audit.Events);
        foreach (var entry in harness.Audit.Events)
        {
            var fields = entry.ToDictionary(pair => pair.Key, pair => pair.Value);
            Assert.Equal(plan.Key.Caller.TenantId, fields["TenantId"]);
            Assert.Equal(plan.Key.Caller.ObjectId, fields["ObjectId"]);
            Assert.Equal(plan.Key.Caller.ClientId, fields["ClientId"]);
            Assert.Equal(mode, fields["IdentityMode"]);
            Assert.Equal(plan.Capability.Id, fields["CapabilityId"]);
            Assert.Equal(101L, fields["RepositoryId"]);
            Assert.DoesNotContain(GitHubFixtures.InstallationToken, JsonSerializer.Serialize(entry));
            Assert.DoesNotContain("fixture-app-jwt", JsonSerializer.Serialize(entry));
        }
    }

    [Fact]
    public async Task Repository_rename_or_mapping_mismatch_prevents_all_repository_writes()
    {
        using var harness = new GitHubHarness([GitHubFixtures.Token(), GitHubFixtures.Repo(999), GitHubFixtures.Revoke()]);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(Fixtures.Plan()));
        Assert.Equal("repository_identity_mismatch", error.Code);
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.DoesNotContain(harness.Handler.Requests,
            request => request.Method is "PUT" or "PATCH" || request.Uri.AbsolutePath.EndsWith("/pulls"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(13)]
    public async Task Upstream_failures_are_explicit_never_retried_and_revoke_any_known_token(int failedStep)
    {
        var plan = Fixtures.Plan();
        var success = GitHubFixtures.Success(plan);
        var steps = success.Take(failedStep).Append(success[failedStep] with
        {
            Status = HttpStatusCode.InternalServerError,
            Body = new { message = "raw-upstream-secret-fixture" }
        }).ToList();
        if (failedStep > 0) steps.Add(GitHubFixtures.Revoke());
        using var harness = new GitHubHarness(steps);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(plan));
        Assert.Equal("github_upstream_failed", error.Code);
        Assert.DoesNotContain("raw-upstream-secret-fixture", error.ToString());
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.Equal(steps.Count, harness.Handler.Requests.Count);
        Assert.All(harness.Handler.Responses, response => Assert.True(response.WasDisposed));
    }

    [Fact]
    public async Task Successful_pr_with_failed_revocation_is_not_reported_as_success()
    {
        var plan = Fixtures.Plan();
        var steps = GitHubFixtures.Success(plan);
        steps[^1] = GitHubFixtures.Revoke() with { Status = HttpStatusCode.ServiceUnavailable };
        using var harness = new GitHubHarness(steps);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(plan));
        Assert.Equal("token_revocation_failed", error.Code);
        Assert.Contains(harness.Audit.Events, entry => entry.Any(field =>
            field.Key == "Operation" && Equals(field.Value, "pull_request_result")));
    }

    [Fact]
    public async Task Rerun_after_response_loss_finds_and_validates_existing_pr_without_repository_mutations()
    {
        var plan = Fixtures.Plan();
        using var harness = new GitHubHarness(GitHubFixtures.Existing(plan));
        var result = await harness.ExecuteAsync(plan);
        Assert.Equal("existing", result.Status);
        Assert.Equal(plan.Branch, result.Branch);
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.DoesNotContain(harness.Handler.Requests,
            r => r.Uri.AbsolutePath.StartsWith("/repos/") && r.Method != "GET");
    }

    [Fact]
    public async Task Rerun_after_metadata_write_resumes_predictable_branch_without_rewriting_file()
    {
        var plan = Fixtures.Plan();
        var success = GitHubFixtures.Success(plan);
        var steps = new List<GitHubStep>
        {
            GitHubFixtures.Token(), GitHubFixtures.Repo(), success[2],
            success[10], success[11], success[12], success[10], success[11], success[12], success[13], success[14]
        };
        using var harness = new GitHubHarness(steps);
        var result = await harness.ExecuteAsync(plan);
        Assert.Equal("created", result.Status);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.Method == "PUT" || r.Uri.AbsolutePath.EndsWith("/git/refs"));
        Assert.Equal(0, harness.Handler.Remaining);
    }

    [Fact]
    public async Task Unexpected_workflow_diff_is_rejected_before_metadata_write_or_pr()
    {
        var plan = Fixtures.Plan();
        var success = GitHubFixtures.Success(plan);
        var steps = success.Take(8).Append(success[8] with
        {
            Body = new { total_commits = 1,
                files = new[] { new { filename = ".github/workflows/unsafe.yml", status = "added" } } }
        }).Append(GitHubFixtures.Revoke());
        using var harness = new GitHubHarness(steps);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(plan));
        Assert.Equal("demo_branch_conflict", error.Code);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.Method == "PUT" ||
            (r.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/pulls")));
    }

    [Fact]
    public async Task Rate_exhaustion_stops_next_operation_but_still_attempts_revocation()
    {
        using var harness = new GitHubHarness([
            GitHubFixtures.Token() with { ExhaustRateLimit = true },
            GitHubFixtures.Revoke()
        ]);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(Fixtures.Plan()));
        Assert.Equal("github_rate_limited", error.Code);
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(0, harness.Handler.Remaining);
    }

    [Fact]
    public async Task Redirect_response_is_failure_not_a_credential_forwarding_instruction()
    {
        using var harness = new GitHubHarness([
            GitHubFixtures.Token(),
            GitHubFixtures.Repo() with { Status = HttpStatusCode.Redirect, Body = null },
            GitHubFixtures.Revoke()
        ]);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(Fixtures.Plan()));
        Assert.Equal("github_upstream_failed", error.Code);
        Assert.Equal(0, harness.Handler.Remaining);
    }

    [Fact]
    public async Task Kill_switch_blocks_signing_and_all_http_calls()
    {
        using var harness = new GitHubHarness([]);
        harness.Configuration["Broker:KillSwitch"] = "true";
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(Fixtures.Plan()));
        Assert.Equal("actions_disabled", error.Code);
        Assert.Equal(0, harness.Signer.Calls);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Kill_switch_enabled_mid_operation_blocks_next_write_but_not_cleanup()
    {
        var plan = Fixtures.Plan();
        using var harness = new GitHubHarness(GitHubFixtures.Success(plan).Take(9).Append(GitHubFixtures.Revoke()));
        harness.Handler.OnRequest = request =>
        {
            if (request.Method == "POST" && request.Uri.AbsolutePath.EndsWith("/git/refs"))
                harness.Configuration["Broker:KillSwitch"] = "true";
        };
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(plan));
        Assert.Equal("actions_disabled", error.Code);
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.DoesNotContain(harness.Handler.Requests, request => request.Method == "PUT");
        Assert.Equal("DELETE", harness.Handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Request_cancellation_still_uses_an_independent_revocation_deadline()
    {
        var plan = Fixtures.Plan();
        using var cancelled = new CancellationTokenSource();
        using var harness = new GitHubHarness([GitHubFixtures.Token(), GitHubFixtures.Repo(), GitHubFixtures.Revoke()]);
        harness.Handler.OnRequest = request =>
        {
            if (request.Uri.AbsolutePath == GitHubFixtures.Prefix) cancelled.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Operation.ExecuteAsync(plan, Fixtures.Audit(plan), harness.Guard, cancelled.Token));
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.Equal("DELETE", harness.Handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Malformed_dependency_response_is_an_error_and_does_not_skip_revocation()
    {
        using var harness = new GitHubHarness([
            GitHubFixtures.Token(),
            GitHubFixtures.Repo() with { Body = new { id = "not-a-number", full_name = "example/sandbox" } },
            GitHubFixtures.Revoke()
        ]);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(Fixtures.Plan()));
        Assert.Equal("github_invalid_response", error.Code);
        Assert.Equal(0, harness.Handler.Remaining);
    }

    [Fact]
    public async Task Production_signer_checks_kill_switch_before_constructing_azure_clients()
    {
        var signer = new KeyVaultAppJwtFactory(Fixtures.Settings(), new ActionGate(Fixtures.Configuration(true)),
            new ManualClock());
        var error = await Assert.ThrowsAsync<BrokerException>(() => signer.CreateAsync(new FixtureGuard(), CancellationToken.None));
        Assert.Equal("actions_disabled", error.Code);
    }

    [Fact]
    public async Task Read_only_reconciliation_reuses_all_pr_content_and_diff_checks_and_scopes_token_read_only()
    {
        var plan = Fixtures.Plan();
        using var harness = new GitHubHarness(GitHubFixtures.Existing(plan));
        var result = await harness.Operation.ExecuteAsync(plan, Fixtures.Audit(plan),
            new FixtureGuard { ReadOnly = true }, CancellationToken.None);
        Assert.Equal("existing", result.Status);
        using var tokenBody = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        var permissions = tokenBody.RootElement.GetProperty("permissions");
        Assert.Equal("read", permissions.GetProperty("contents").GetString());
        Assert.Equal("read", permissions.GetProperty("pull_requests").GetString());
        Assert.DoesNotContain(harness.Handler.Requests,
            request => request.Uri.AbsolutePath.StartsWith("/repos/") && request.Method != "GET");
        Assert.Equal(0, harness.Handler.Remaining);
    }

    [Fact]
    public async Task Read_only_reconciliation_with_no_pr_never_creates_or_resumes_a_branch()
    {
        var plan = Fixtures.Plan();
        using var harness = new GitHubHarness([
            GitHubFixtures.Token(), GitHubFixtures.Repo(),
            new("GET", GitHubFixtures.Prefix + "/pulls", HttpStatusCode.OK, Array.Empty<object>()),
            GitHubFixtures.Revoke()
        ]);
        var error = await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Operation.ExecuteAsync(plan, Fixtures.Audit(plan), new FixtureGuard { ReadOnly = true },
                CancellationToken.None));
        Assert.Equal("reconciliation_required", error.Code);
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.DoesNotContain(harness.Handler.Requests,
            request => request.Uri.AbsolutePath.StartsWith("/repos/") && request.Method != "GET");
    }

    [Fact]
    public async Task Transport_itself_refuses_mutations_in_read_only_mode_and_limits_cleanup_bypass()
    {
        using var harness = new GitHubHarness([]);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.Transport.SendAsync(HttpMethod.Post,
            "repos/example/sandbox/pulls", "test", new { title = "disallowed" }, Fixtures.Audit(Fixtures.Plan()),
            "create_pull_request", new FixtureGuard { ReadOnly = true }, CancellationToken.None));
        Assert.Equal("reconciliation_mutation_forbidden", error.Code);
        error = await Assert.ThrowsAsync<BrokerException>(() => harness.Transport.SendAsync(HttpMethod.Delete,
            "repos/example/sandbox", "test", null, Fixtures.Audit(Fixtures.Plan()), "delete_repo",
            new FixtureGuard(), CancellationToken.None, cleanup: true));
        Assert.Equal("invalid_cleanup_operation", error.Code);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Issued_token_is_revoked_even_when_persisting_rate_headers_fails()
    {
        using var harness = new GitHubHarness([
            GitHubFixtures.Token() with { ExhaustRateLimit = true }, GitHubFixtures.Revoke()
        ]);
        harness.Guard.OnObserve = (_, _) => throw new BrokerException("storage_unavailable", 503);
        var error = await Assert.ThrowsAsync<BrokerException>(() => harness.ExecuteAsync(Fixtures.Plan()));
        Assert.Equal("storage_unavailable", error.Code);
        Assert.Equal(0, harness.Handler.Remaining);
        Assert.Equal("DELETE", harness.Handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Signer_rechecks_execution_guard_and_rejects_admission_without_an_azure_call()
    {
        var settings = new SettingsState(Fixtures.Options("Admission"), []);
        var signer = new KeyVaultAppJwtFactory(settings, new ActionGate(Fixtures.Configuration()), new ManualClock());
        Assert.Equal("signing_not_permitted", (await Assert.ThrowsAsync<BrokerException>(() =>
            signer.CreateAsync(new FixtureGuard(), CancellationToken.None))).Code);
        signer = new KeyVaultAppJwtFactory(Fixtures.Settings(), new ActionGate(Fixtures.Configuration()), new ManualClock());
        Assert.Equal("grant_revoked_or_changed", (await Assert.ThrowsAsync<BrokerException>(() =>
            signer.CreateAsync(new FixtureGuard
            {
                OnDemand = _ => throw new BrokerException("grant_revoked_or_changed", 403)
            }, CancellationToken.None))).Code);
    }
}
