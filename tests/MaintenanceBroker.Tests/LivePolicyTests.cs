using System.Security.Claims;
using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class LivePolicyTests
{
    [Theory]
    [InlineData("Delegated", 30)]
    [InlineData("Delegated", 900)]
    [InlineData("Workload", 30)]
    [InlineData("Workload", 900)]
    public async Task Decision_window_is_bounded_by_original_jwt_expiry_and_five_minutes(string mode, int lifetime)
    {
        var harness = new WorkerHarness();
        var claims = Fixtures.Claims(mode);
        claims.Add(new Claim("exp", harness.Clock.GetUtcNow().AddSeconds(lifetime).ToUnixTimeSeconds().ToString()));
        var policy = new IdentityPolicy(Fixtures.Settings(), harness.Registry, harness.Clock);
        var decision = await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None);
        Assert.True(decision.Allowed);
        Assert.Equal(harness.Clock.GetUtcNow().AddSeconds(Math.Min(lifetime, 300)), decision.ExpiresAt);
        Assert.Equal(new[]
        {
            ("control", "global"), ("capability", "sandbox"),
            (PolicyRegistry.PrincipalPartition(Fixtures.Caller(mode)), "sandbox")
        }, harness.Policy.Reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("expired")]
    [InlineData("invalid")]
    public async Task Invalid_expiry_denies_before_policy_storage(string kind)
    {
        var harness = new WorkerHarness();
        var claims = Fixtures.Claims();
        if (kind != "missing")
            claims.Add(new Claim("exp", kind == "invalid" ? "NaN" :
                harness.Clock.GetUtcNow().AddSeconds(kind == "expired" ? 0 : 60).ToUnixTimeSeconds().ToString()));
        if (kind == "duplicate") claims.Add(claims[^1]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var decision = await new IdentityPolicy(Fixtures.Settings(), harness.Registry, harness.Clock)
            .EvaluateAsync(principal, "sandbox", CancellationToken.None);
        Assert.False(decision.Allowed);
        Assert.Empty(harness.Policy.Reads);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Version")]
    [InlineData("RepositoryId")]
    [InlineData("InstallationId")]
    [InlineData("Owner")]
    [InlineData("Name")]
    [InlineData("BaseBranch")]
    public async Task Missing_stored_capability_fields_fail_closed(string field)
    {
        var harness = new WorkerHarness();
        harness.Policy.Rows[("capability", "sandbox")].Remove(field);
        var exception = await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None));
        Assert.Equal("policy_invalid", exception.Code);
    }

    [Theory]
    [InlineData("Mode")]
    [InlineData("TenantId")]
    [InlineData("ObjectId")]
    [InlineData("ClientId")]
    [InlineData("CapabilityVersion")]
    [InlineData("Version")]
    public async Task Missing_stored_grant_binding_fields_fail_closed(string field)
    {
        var harness = new WorkerHarness();
        harness.Policy.Rows[(PolicyRegistry.PrincipalPartition(Fixtures.Caller()), "sandbox")].Remove(field);
        Assert.Equal("policy_invalid", (await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Stored_types_values_and_full_repository_binding_are_validated()
    {
        var harness = new WorkerHarness();
        var capability = harness.Policy.Rows[("capability", "sandbox")];
        foreach (var invalid in new object[] { "101", 101, -1L })
        {
            capability["RepositoryId"] = invalid;
            Assert.Equal("policy_invalid", (await Assert.ThrowsAsync<BrokerException>(() =>
                harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None))).Code);
        }
        capability["RepositoryId"] = 101L;
        capability["Owner"] = "https://untrusted/path";
        Assert.Equal("policy_invalid", (await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None))).Code);
        capability["Owner"] = "example";
        harness.Policy.Rows[(PolicyRegistry.PrincipalPartition(Fixtures.Caller()), "sandbox")]["InstallationId"] = 999L;
        Assert.Equal("policy_invalid", (await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Policy_reads_are_live_and_disabled_or_missing_control_is_fail_closed()
    {
        var harness = new WorkerHarness();
        Assert.True((await harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None)).Allowed);
        harness.Policy.Rows[("control", "global")]["Enabled"] = false;
        Assert.Equal("actions_disabled", (await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None))).Code);
        harness.Policy.Rows.Remove(("control", "global"));
        Assert.Equal("actions_disabled", (await Assert.ThrowsAsync<BrokerException>(() =>
            harness.Registry.DemandEnabledAsync(CancellationToken.None))).Code);
    }
}
