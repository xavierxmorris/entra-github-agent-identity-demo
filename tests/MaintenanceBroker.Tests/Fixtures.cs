using System.Security.Claims;
using MaintenanceBroker;
using Microsoft.Extensions.Configuration;

namespace MaintenanceBroker.Tests;

internal static class Fixtures
{
    public const string Tenant = "11111111-1111-4111-8111-111111111111";
    public const string Audience = "22222222-2222-4222-8222-222222222222";
    public const string WorkloadClient = "33333333-3333-4333-8333-333333333333";
    public const string WorkloadObject = "44444444-4444-4444-8444-444444444444";
    public const string DelegatedClient = "55555555-5555-4555-8555-555555555555";
    public const string UserObject = "66666666-6666-4666-8666-666666666666";
    public const string RequestId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    public static string Issuer => $"https://login.microsoftonline.com/{Tenant}/v2.0";

    public static BrokerOptions Options(string mode = "Worker") => new()
    {
        Mode = mode,
        KillSwitch = false,
        TenantId = Tenant,
        Audience = Audience,
        GitHubAppId = mode == "Worker" ? 12345 : 0,
        ManagedIdentityClientId = "77777777-7777-4777-8777-777777777777",
        KeyVaultKeyUri = mode == "Worker" ? "https://example-vault.vault.azure.net/keys/github-app/0123456789abcdef0123456789abcdef" : "",
        StorageAccountName = "fixturestorage"
    };

    public static Capability[] Capabilities() =>
        [
            new() { Id = "sandbox", Version = 1, RepositoryId = 101, Owner = "example", Name = "sandbox",
                InstallationId = 202, BaseBranch = "main" },
            new() { Id = "other", Version = 1, RepositoryId = 102, Owner = "example", Name = "other",
                InstallationId = 203, BaseBranch = "main" }
        ];

    public static List<Claim> Claims(string mode = "Workload") =>
    [
        new("ver", "2.0"), new("tid", Tenant), new("sub", "test-subject"),
        new("oid", mode == "Workload" ? WorkloadObject : UserObject),
        new("azp", mode == "Workload" ? WorkloadClient : DelegatedClient),
        mode == "Workload" ? new("roles", "Maintenance.Request") : new("scp", "Agent.Invoke")
    ];

    public static ClaimsPrincipal Principal(IEnumerable<Claim> claims)
    {
        var values = claims.ToList();
        if (!values.Any(c => c.Type == "exp"))
            values.Add(new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString()));
        return new(new ClaimsIdentity(values, "test"));
    }
    public static IdentityPolicy Policy() => new(Settings(),
        new PolicyRegistry(new MemoryPolicyStore(), new ActionGate(Configuration())), TimeProvider.System);
    public static SettingsState Settings() => new(Options(), []);
    public static CallerIdentity Caller(string mode = "Workload") => IdentityPolicy.ReadCaller(Principal(Claims(mode)))!;
    public static OperationPlan Plan(string mode = "Workload") =>
        OperationPlan.Create(Caller(mode), Capabilities()[0], Guid.Parse(RequestId));
    public static AuditContext Audit(OperationPlan plan) => new("fixture-correlation")
    {
        Caller = plan.Key.Caller,
        CapabilityId = plan.Capability.Id,
        RequestId = plan.Key.RequestId,
        RepositoryId = plan.Capability.RepositoryId,
        Branch = plan.Branch,
        Decision = "allowed"
    };

    public static IConfigurationRoot Configuration(bool disabled = false) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Broker:KillSwitch"] = disabled.ToString() })
        .Build();

    public static MaintenanceResult Result(OperationPlan plan) => new("created", plan.Capability.Id,
        plan.Key.RequestId, plan.Capability.RepositoryId, plan.Branch, 17,
        "https://github.com/example/sandbox/pull/17", "open", "FIXTURE:123");
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}
