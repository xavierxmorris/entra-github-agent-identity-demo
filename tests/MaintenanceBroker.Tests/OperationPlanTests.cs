using System.Text.Json;
using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class OperationPlanTests
{
    [Fact]
    public void Operation_has_only_deterministic_synthetic_metadata_and_fixed_git_shape()
    {
        var plan = Fixtures.Plan();
        Assert.Equal(plan.Key, Fixtures.Plan().Key);
        Assert.Equal(plan.Branch, Fixtures.Plan().Branch);
        Assert.Equal(plan.Content, Fixtures.Plan().Content);
        Assert.Matches("^demo/[a-f0-9]{64}$", plan.Branch);
        Assert.Equal("demo/maintenance.json", OperationPlan.FilePath);
        using var content = JsonDocument.Parse(plan.Content);
        Assert.Equal(6, content.RootElement.EnumerateObject().Count());
        Assert.Equal("synthetic-maintenance-metadata", content.RootElement.GetProperty("kind").GetString());
        Assert.False(content.RootElement.GetProperty("packageChanges").GetBoolean());
        Assert.Equal(Fixtures.RequestId, content.RootElement.GetProperty("requestId").GetString());
        Assert.DoesNotContain(Fixtures.WorkloadObject, plan.Content);
        Assert.DoesNotContain(Fixtures.UserObject, plan.Content);
    }

    [Fact]
    public void Branch_is_bound_to_caller_mode_client_capability_and_repository_mapping()
    {
        var plan = Fixtures.Plan();
        var caller = plan.Key.Caller;
        foreach (var changed in new[]
        {
            caller with { ObjectId = Guid.NewGuid() },
            caller with { ClientId = Guid.NewGuid() },
            caller with { TenantId = Guid.NewGuid() },
            caller with { Mode = "Delegated" }
        })
            Assert.NotEqual(plan.Branch, OperationPlan.Create(changed, plan.Capability, plan.Key.RequestId).Branch);
        Assert.NotEqual(plan.Branch, OperationPlan.Create(caller, Fixtures.Capabilities()[1], plan.Key.RequestId).Branch);
        var remapped = Fixtures.Capabilities()[0];
        remapped.RepositoryId++;
        Assert.NotEqual(plan.Branch, OperationPlan.Create(caller, remapped, plan.Key.RequestId).Branch);
        Assert.NotEqual(plan.Branch, OperationPlan.Create(caller, plan.Capability, Guid.NewGuid()).Branch);
    }
}
