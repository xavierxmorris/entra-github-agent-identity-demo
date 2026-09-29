using System.Net;
using System.Text;
using System.Text.Json;
using MaintenanceBroker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MaintenanceBroker.Tests;

internal sealed record GitHubStep(string Method, string Path, HttpStatusCode Status, object? Body = null,
    bool ExhaustRateLimit = false);

internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string? Body);

internal sealed class RecordingHandler(IEnumerable<GitHubStep> steps) : HttpMessageHandler
{
    private readonly Queue<GitHubStep> remaining = new(steps);
    public List<RecordedRequest> Requests { get; } = [];
    public List<TrackedContent> Responses { get; } = [];
    public int Remaining => remaining.Count;
    public Action<RecordedRequest>? OnRequest { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.NotNull(request.RequestUri);
        Assert.Equal("https", request.RequestUri.Scheme);
        Assert.Equal("api.github.com", request.RequestUri.Host);
        Assert.Equal(GitHubTransport.ApiVersion, Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Requests.Add(new(request.Method.Method, request.RequestUri, request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        Assert.NotEmpty(remaining);
        var step = remaining.Dequeue();
        Assert.Equal(step.Method, request.Method.Method);
        Assert.Equal(step.Path, request.RequestUri.AbsolutePath);
        OnRequest?.Invoke(Requests[^1]);
        var response = new HttpResponseMessage(step.Status);
        response.Headers.Add("X-GitHub-Request-Id", "FIXTURE:123");
        if (step.ExhaustRateLimit)
        {
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("Retry-After", "120");
        }
        if (step.Body is not null)
        {
            var content = new TrackedContent(JsonSerializer.Serialize(step.Body));
            Responses.Add(content);
            response.Content = content;
        }
        return response;
    }
}

internal sealed class TrackedContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
{
    public bool WasDisposed { get; private set; }
    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class FixtureJwtFactory : IAppJwtFactory
{
    public int Calls { get; private set; }
    public Task<string> CreateAsync(IExecutionGuard guard, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult("fixture-app-jwt-not-a-real-credential");
    }
}

internal sealed class AuditCapture : ILogger<AuditWriter>
{
    public List<KeyValuePair<string, object?>[]> Events { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> fields) Events.Add(fields.ToArray());
    }
}

internal sealed class GitHubHarness : IDisposable
{
    public RecordingHandler Handler { get; }
    public FixtureJwtFactory Signer { get; } = new();
    public IConfigurationRoot Configuration { get; } = Fixtures.Configuration();
    public AuditCapture Audit { get; } = new();
    public GitHubTransport Transport { get; }
    public MaintenanceOperation Operation { get; }
    public FixtureGuard Guard { get; } = new();
    private readonly HttpClient client;

    public GitHubHarness(IEnumerable<GitHubStep> steps)
    {
        Handler = new RecordingHandler(steps);
        client = new HttpClient(Handler) { BaseAddress = GitHubTransport.ApiBase, Timeout = TimeSpan.FromSeconds(15) };
        var gate = new ActionGate(Configuration);
        var writer = new AuditWriter(Audit);
        Transport = new(client, new GitHubBudget(new ManualClock()), gate, writer);
        Operation = new(Signer, Transport, gate, writer);
    }

    public Task<MaintenanceResult> ExecuteAsync(OperationPlan plan) =>
        Operation.ExecuteAsync(plan, Fixtures.Audit(plan), Guard, CancellationToken.None);

    public void Dispose()
    {
        client.Dispose();
        (Configuration as IDisposable)?.Dispose();
    }
}

internal static class GitHubFixtures
{
    public const string BaseSha = "1111111111111111111111111111111111111111";
    public const string HeadSha = "2222222222222222222222222222222222222222";
    public const string BlobSha = "3333333333333333333333333333333333333333";
    public const string InstallationToken = "fixture-installation-token-not-a-real-credential";
    public const string Prefix = "/repos/example/sandbox";

    public static GitHubStep Token() => new("POST", "/app/installations/202/access_tokens", HttpStatusCode.Created,
        new { token = InstallationToken, expires_at = "2026-09-29T01:00:00Z",
            permissions = new { contents = "write", pull_requests = "write", metadata = "read" },
            repositories = new[] { new { id = 101, full_name = "example/sandbox" } } });
    public static GitHubStep Repo(long id = 101) => new("GET", Prefix, HttpStatusCode.OK,
        new { id, full_name = "example/sandbox" });
    public static GitHubStep Revoke() => new("DELETE", "/installation/token", HttpStatusCode.NoContent);
    public static object Ref(string sha) => new { @object = new { sha, type = "commit" } };
    public static object File(OperationPlan plan) => new
    {
        type = "file", path = OperationPlan.FilePath, encoding = "base64", sha = BlobSha,
        size = Encoding.UTF8.GetByteCount(plan.Content),
        content = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Content))
    };
    public static object Diff(bool changed) => changed
        ? new { total_commits = 1, files = new[] { new { filename = OperationPlan.FilePath, status = "added" } } }
        : new { total_commits = 0, files = Array.Empty<object>() };
    public static object Pull(OperationPlan plan) => new
    {
        number = 17, state = "open", html_url = "https://github.com/example/sandbox/pull/17",
        head = new { @ref = plan.Branch, sha = HeadSha, repo = new { id = 101 } },
        @base = new { @ref = "main", sha = BaseSha, repo = new { id = 101 } }
    };

    public static List<GitHubStep> Success(OperationPlan plan) =>
    [
        Token(),
        Repo(),
        new("GET", Prefix + "/pulls", HttpStatusCode.OK, Array.Empty<object>()),
        new("GET", Prefix + "/git/ref/heads/" + plan.Branch, HttpStatusCode.NotFound),
        new("GET", Prefix + "/git/ref/heads/main", HttpStatusCode.OK, Ref(BaseSha)),
        new("POST", Prefix + "/git/refs", HttpStatusCode.Created, new { @ref = "refs/heads/" + plan.Branch }),
        new("GET", Prefix + "/git/ref/heads/" + plan.Branch, HttpStatusCode.OK, Ref(BaseSha)),
        new("GET", Prefix + "/contents/" + OperationPlan.FilePath, HttpStatusCode.NotFound),
        new("GET", Prefix + "/compare/main..." + BaseSha, HttpStatusCode.OK, Diff(false)),
        new("PUT", Prefix + "/contents/" + OperationPlan.FilePath, HttpStatusCode.Created, new { commit = new { sha = HeadSha } }),
        new("GET", Prefix + "/git/ref/heads/" + plan.Branch, HttpStatusCode.OK, Ref(HeadSha)),
        new("GET", Prefix + "/contents/" + OperationPlan.FilePath, HttpStatusCode.OK, File(plan)),
        new("GET", Prefix + "/compare/main..." + HeadSha, HttpStatusCode.OK, Diff(true)),
        new("POST", Prefix + "/pulls", HttpStatusCode.Created, Pull(plan)),
        Revoke()
    ];

    public static List<GitHubStep> Existing(OperationPlan plan) =>
    [
        Token(), Repo(),
        new("GET", Prefix + "/pulls", HttpStatusCode.OK, new[] { Pull(plan) }),
        new("GET", Prefix + "/contents/" + OperationPlan.FilePath, HttpStatusCode.OK, File(plan)),
        new("GET", Prefix + "/compare/" + BaseSha + "..." + HeadSha, HttpStatusCode.OK, Diff(true)),
        Revoke()
    ];
}
