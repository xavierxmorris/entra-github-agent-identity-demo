using System.Threading.RateLimiting;
using MaintenanceBroker;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.None);
builder.Logging.AddFilter("Microsoft.IdentityModel", LogLevel.None);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaintenanceRequest.MaximumBytes);

// Resolve after host configuration, allowing test hosting to supply complete non-secret fixtures.
builder.Services.AddSingleton(provider => SettingsState.Load(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ActionGate>();
builder.Services.AddSingleton<IdentityPolicy>();
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddSingleton<IStorageClients, StorageClients>();
builder.Services.AddSingleton<IPolicyStore, AzurePolicyStore>();
builder.Services.AddSingleton<PolicyRegistry>();
builder.Services.AddSingleton<IOperationStore, AzureOperationStore>();
builder.Services.AddSingleton<IWorkQueue, AzureWorkQueue>();
builder.Services.AddSingleton<AdmissionService>();
if (builder.Configuration["Broker:Mode"] == "Worker")
{
    builder.Services.AddSingleton<IInstallationBudget, AzureInstallationBudget>();
    builder.Services.AddSingleton<GitHubBudget>();
    builder.Services.AddSingleton<IAppJwtFactory, KeyVaultAppJwtFactory>();
    builder.Services.AddSingleton<IMaintenanceOperation, MaintenanceOperation>();
    builder.Services.AddSingleton<OutboxDispatcher>();
    builder.Services.AddSingleton<WorkerProcessor>();
    builder.Services.AddHostedService<WorkerLoop>();
    builder.Services.AddHostedService<OutboxLoop>();
    builder.Services.AddHttpClient<GitHubTransport>(client =>
    {
        client.BaseAddress = GitHubTransport.ApiBase;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }).RemoveAllLoggers();
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SettingsState>((options, settings) =>
    {
        options.MapInboundClaims = false;
        options.SaveToken = false;
        options.IncludeErrorDetails = false;
        options.RequireHttpsMetadata = true;
        options.BackchannelTimeout = TimeSpan.FromSeconds(10);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidTypes = ["JWT"],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "oid",
            RoleClaimType = "roles"
        };
        if (settings.IsReady)
        {
            var issuer = $"https://login.microsoftonline.com/{Guid.Parse(settings.Options.TenantId):D}/v2.0";
            options.Authority = issuer;
            options.TokenValidationParameters.ValidIssuer = issuer;
            options.TokenValidationParameters.ValidAudience = Guid.Parse(settings.Options.Audience).ToString("D");
        }
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.Request.Path.StartsWithSegments("/v1")
                ? RateLimitPartition.GetFixedWindowLimiter("broker", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter("health")),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.Request.Path.StartsWithSegments("/v1")
                ? RateLimitPartition.GetConcurrencyLimiter("broker", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 2, QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter("health")));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "broker_rate_limited",
            correlationId = context.HttpContext.TraceIdentifier
        }, cancellationToken);
    };
});

var app = builder.Build();
var state = app.Services.GetRequiredService<SettingsState>();
if (!state.IsReady)
    app.Logger.LogError("BrokerConfigurationInvalid Fields={Fields}", string.Join(',', state.Errors));

app.Use(async (context, next) =>
{
    context.TraceIdentifier = Guid.NewGuid().ToString("D");
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    context.Response.Headers.CacheControl = "no-store";
    var auditContext = new AuditContext(context.TraceIdentifier);
    context.Items[typeof(AuditContext)] = auditContext;
    try
    {
        if (context.Request.Path.StartsWithSegments("/v1") && !state.IsReady)
            throw new BrokerException("broker_not_configured", StatusCodes.Status503ServiceUnavailable);
        await next(context);
    }
    catch (BrokerException exception)
    {
        app.Services.GetRequiredService<AuditWriter>().Write(auditContext, "request_failed", exception.Code);
        await ErrorAsync(context, exception.Code, exception.StatusCode);
    }
    catch (BadHttpRequestException exception)
    {
        await ErrorAsync(context, "invalid_http_request", exception.StatusCode);
    }
    catch (OperationCanceledException)
    {
        await ErrorAsync(context, "operation_cancelled_or_timed_out", StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception exception)
    {
        // Do not log exception messages/objects from credential-bearing dependencies.
        app.Logger.LogError("BrokerUnhandledFailure CorrelationId={CorrelationId} ExceptionType={ExceptionType}",
            context.TraceIdentifier, exception.GetType().Name);
        await ErrorAsync(context, "internal_error", StatusCodes.Status500InternalServerError);
    }
    finally
    {
        if (context.Request.Path.StartsWithSegments("/v1"))
        {
            auditContext.Caller ??= IdentityPolicy.ReadCaller(context.User);
            app.Services.GetRequiredService<AuditWriter>().Write(auditContext, "http_request",
                context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/readyz", async (HttpContext http) =>
{
    if (!state.IsReady) return Results.Json(new { status = "not-ready", checks = "storage" }, statusCode: 503);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        var services = http.RequestServices;
        var control = await services.GetRequiredService<IPolicyStore>().ReadAsync("control", "global", deadline.Token)
            .WaitAsync(deadline.Token);
        if (control is null || !control.TryGetValue("Enabled", out var enabled) || enabled is not bool)
            throw new BrokerException("policy_invalid", 503);
        await services.GetRequiredService<IOperationStore>().ProbeAsync(deadline.Token).WaitAsync(deadline.Token);
        if (state.Options.Mode == "Worker")
            await services.GetRequiredService<IWorkQueue>().ProbeAsync(deadline.Token).WaitAsync(deadline.Token);
        return Results.Ok(new { status = "ready", checks = "storage" });
    }
    catch (Exception exception) when (exception is BrokerException or OperationCanceledException)
    {
        app.Services.GetRequiredService<AuditWriter>().Write((AuditContext)http.Items[typeof(AuditContext)]!,
            "readiness", exception is BrokerException broker ? broker.Code : "storage_probe_timeout");
        return Results.Json(new { status = "not-ready", checks = "storage" }, statusCode: 503);
    }
}).AllowAnonymous();
if (state.Options.Mode == "Admission")
{
    app.MapPost("/v1/maintenance-pr", HandleMaintenanceAsync).RequireAuthorization();
    app.MapGet("/v1/operations/{id}", HandleStatusAsync).RequireAuthorization();
}
app.Run();

static async Task<IResult> HandleMaintenanceAsync(HttpContext http, IdentityPolicy policy,
    AdmissionService admission, AuditWriter audit)
{
    var context = (AuditContext)http.Items[typeof(AuditContext)]!;
    context.Caller = IdentityPolicy.ReadCaller(http.User);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(15));
    var request = await MaintenanceRequest.ReadAsync(http.Request, deadline.Token);
    context.CapabilityId = request.CapabilityId;
    context.RequestId = request.RequestId;
    var decision = await policy.EvaluateAsync(http.User, request.CapabilityId, deadline.Token);
    context.Caller = decision.Caller;
    context.Decision = decision.Reason;
    context.RepositoryId = decision.Capability?.RepositoryId;
    audit.Write(context, "authorize_maintenance", decision.Reason);
    if (!decision.Allowed) return Results.Forbid();
    var operation = await admission.AdmitAsync(decision, request.RequestId, deadline.Token);
    audit.Write(context, "admission", operation.Status);
    http.Response.Headers.Location = "/v1/operations/" + operation.Id;
    if (operation.IsTerminal) return Results.Ok(operation.View);
    http.Response.Headers.RetryAfter = "5";
    return Results.Json(operation.View, statusCode: StatusCodes.Status202Accepted);
}

static async Task<IResult> HandleStatusAsync(string id, HttpContext http, IdentityPolicy policy,
    AdmissionService admission, AuditWriter audit)
{
    var capabilityId = OperationIds.CapabilityId(id);
    if (capabilityId is null) throw new BrokerException("invalid_operation_id", 400);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(15));
    var decision = await policy.EvaluateAsync(http.User, capabilityId, deadline.Token);
    var context = (AuditContext)http.Items[typeof(AuditContext)]!;
    context.Caller = decision.Caller;
    context.CapabilityId = capabilityId;
    context.Decision = decision.Reason;
    audit.Write(context, "authorize_status", decision.Reason);
    if (!decision.Allowed) return Results.Forbid();
    var operation = await admission.ReadAsync(id, decision, deadline.Token);
    if (operation is null) return Results.NotFound(new { error = "operation_not_found" });
    if (!operation.IsTerminal) http.Response.Headers.RetryAfter = "5";
    return Results.Ok(operation.View);
}

static Task ErrorAsync(HttpContext context, string error, int status)
{
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { error, correlationId = context.TraceIdentifier });
}

public partial class Program { }
