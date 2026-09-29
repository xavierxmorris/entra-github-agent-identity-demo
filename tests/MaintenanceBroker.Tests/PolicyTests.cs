using System.Security.Claims;
using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData("Workload")]
    [InlineData("Delegated")]
    public async Task Configured_identity_can_only_use_its_capability(string mode)
    {
        var policy = Fixtures.Policy();
        var principal = Fixtures.Principal(Fixtures.Claims(mode));
        var allowed = await policy.EvaluateAsync(principal, "sandbox", CancellationToken.None);
        Assert.True(allowed.Allowed);
        Assert.Equal(mode, allowed.Caller!.Mode);
        Assert.Equal(101, allowed.Capability!.RepositoryId);
        Assert.False((await policy.EvaluateAsync(principal, "other", CancellationToken.None)).Allowed);
        Assert.False((await policy.EvaluateAsync(principal, "unknown", CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData("scp", "Agent.Invoke")]
    [InlineData("scp", "")]
    [InlineData("scp", "Maintenance.Request")]
    [InlineData("idtyp", "user")]
    public async Task Delegated_or_conflicting_claims_never_satisfy_workload_policy(string type, string value)
    {
        var claims = Fixtures.Claims();
        claims.Add(new Claim(type, value));
        Assert.False((await Fixtures.Policy().EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task Roles_do_not_replace_delegated_scope()
    {
        var claims = Fixtures.Claims("Delegated");
        claims.RemoveAll(c => c.Type == "scp");
        claims.Add(new Claim("roles", "Maintenance.Request"));
        Assert.False((await Fixtures.Policy().EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData("ver", "1.0")]
    [InlineData("tid", "99999999-9999-4999-8999-999999999999")]
    [InlineData("oid", "99999999-9999-4999-8999-999999999999")]
    [InlineData("azp", "99999999-9999-4999-8999-999999999999")]
    [InlineData("roles", "Maintenance.Request.Extra")]
    public async Task Wrong_claims_are_denied(string type, string value)
    {
        var claims = Fixtures.Claims();
        claims.RemoveAll(c => c.Type == type);
        claims.Add(new Claim(type, value));
        Assert.False((await Fixtures.Policy().EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData("tid")]
    [InlineData("oid")]
    [InlineData("azp")]
    [InlineData("ver")]
    public async Task Duplicate_and_missing_identity_claims_are_denied(string type)
    {
        var claims = Fixtures.Claims();
        claims.Add(claims.Single(c => c.Type == type));
        var policy = Fixtures.Policy();
        Assert.False((await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
        claims.RemoveAll(c => c.Type == type);
        Assert.False((await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task Delegated_scope_is_exact_and_conflicting_app_identity_is_denied()
    {
        var policy = Fixtures.Policy();
        var claims = Fixtures.Claims("Delegated");
        claims.Add(new Claim("idtyp", "app"));
        Assert.False((await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
        claims.RemoveAll(c => c.Type is "idtyp" or "scp");
        claims.Add(new Claim("scp", "Agent.Invoke.Extra"));
        Assert.False((await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
        claims.RemoveAll(c => c.Type == "scp");
        claims.Add(new Claim("scp", "unrelated Agent.Invoke"));
        Assert.True((await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
        claims.Add(new Claim("scp", "Agent.Invoke"));
        Assert.False((await policy.EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData("Workload", "app", true)]
    [InlineData("Workload", "unknown", false)]
    [InlineData("Delegated", "user", true)]
    [InlineData("Delegated", "unknown", false)]
    public async Task Optional_identity_type_must_agree_with_the_selected_mode(string mode, string type, bool allowed)
    {
        var claims = Fixtures.Claims(mode);
        claims.Add(new Claim("idtyp", type));
        Assert.Equal(allowed, (await Fixtures.Policy()
            .EvaluateAsync(Fixtures.Principal(claims), "sandbox", CancellationToken.None)).Allowed);
    }

    [Fact]
    public void Kill_switch_is_fail_closed_and_observes_configuration_changes()
    {
        var configuration = Fixtures.Configuration();
        var gate = new ActionGate(configuration);
        gate.DemandEnabled();
        foreach (var value in new string?[] { "true", null, "invalid" })
        {
            configuration["Broker:KillSwitch"] = value;
            Assert.Equal("actions_disabled", Assert.Throws<BrokerException>(gate.DemandEnabled).Code);
        }
    }
}
