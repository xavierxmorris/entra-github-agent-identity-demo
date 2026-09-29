using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using MaintenanceBroker;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MaintenanceBroker.Tests;

internal sealed class BrokerFactory(bool configured = true, bool disabled = false, string mode = "Admission",
    MemoryOperationStore? sharedStore = null, MemoryPolicyStore? sharedPolicy = null) : WebApplicationFactory<Program>
{
    private readonly RSA signingKey = RSA.Create(2048);
    public OperationSpy Operation { get; } = new();
    public MemoryOperationStore Store { get; } = sharedStore ?? new();
    public MemoryPolicyStore Policy { get; } = sharedPolicy ?? new();
    public MemoryQueue Queue { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Broker:Mode"] = configured ? mode : "" }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            var options = Fixtures.Options(mode);
            var values = new Dictionary<string, string?>
            {
                ["Broker:KillSwitch"] = disabled.ToString()
            };
            if (configured)
            {
                values["Broker:TenantId"] = options.TenantId;
                values["Broker:Mode"] = mode;
                values["Broker:Audience"] = options.Audience;
                values["Broker:GitHubAppId"] = options.GitHubAppId.ToString();
                values["Broker:KeyVaultKeyUri"] = options.KeyVaultKeyUri;
                values["Broker:ManagedIdentityClientId"] = options.ManagedIdentityClientId;
                values["Broker:StorageAccountName"] = options.StorageAccountName;
            }
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPolicyStore>();
            services.RemoveAll<IOperationStore>();
            services.RemoveAll<IWorkQueue>();
            services.AddSingleton<IPolicyStore>(Policy);
            services.AddSingleton<IOperationStore>(Store);
            services.AddSingleton<IWorkQueue>(Queue);
            foreach (var registration in services.Where(service =>
                service.ImplementationType == typeof(WorkerLoop) || service.ImplementationType == typeof(OutboxLoop)).ToArray())
                services.Remove(registration);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var publicKey = new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = "local-test" };
                var metadata = new OpenIdConnectConfiguration { Issuer = Fixtures.Issuer };
                metadata.SigningKeys.Add(publicKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                options.Authority = null;
                options.MetadataAddress = string.Empty;
                // Only discovery/key material is replaced. Production validation and authorization remain intact.
            });
        });
    }

    public string Token(IEnumerable<Claim>? claims = null, string? issuer = null, string? audience = null,
        DateTime? expires = null, RSA? alternateKey = null)
    {
        var key = new RsaSecurityKey(alternateKey ?? signingKey) { KeyId = "local-test" };
        var token = new JwtSecurityToken(issuer ?? Fixtures.Issuer, audience ?? Fixtures.Audience,
            claims ?? Fixtures.Claims(), DateTime.UtcNow.AddHours(-1), expires ?? DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) signingKey.Dispose();
    }
}

internal sealed class OperationSpy : IMaintenanceOperation
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public Func<OperationPlan, CancellationToken, Task<MaintenanceResult>>? Handler { get; set; }
    public Func<OperationPlan, IExecutionGuard, CancellationToken, Task<MaintenanceResult>>? GuardedHandler { get; set; }
    public List<bool> ReadOnlyCalls { get; } = [];
    public Task<MaintenanceResult> ExecuteAsync(OperationPlan plan, AuditContext context, IExecutionGuard guard,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        lock (ReadOnlyCalls) ReadOnlyCalls.Add(guard.ReadOnly);
        if (GuardedHandler is not null) return GuardedHandler(plan, guard, cancellationToken);
        return Handler is null ? Task.FromResult(Fixtures.Result(plan)) : Handler(plan, cancellationToken);
    }
}
