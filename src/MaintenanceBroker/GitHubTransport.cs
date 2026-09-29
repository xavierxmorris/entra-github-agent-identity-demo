using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaintenanceBroker;

public sealed record GitHubReply(JsonElement Body, string? RequestId, bool NotFound = false,
    DateTimeOffset? BlockedUntil = null);

public sealed class GitHubBudget(TimeProvider clock)
{
    public DateTimeOffset Now => clock.GetUtcNow();
    private readonly object sync = new();
    private DateTimeOffset blockedUntil;

    public void DemandAvailable()
    {
        lock (sync)
        {
            if (clock.GetUtcNow() < blockedUntil)
                throw new BrokerException("github_rate_limited", StatusCodes.Status503ServiceUnavailable);
        }
    }

    public bool Observe(HttpResponseMessage response)
    {
        var now = clock.GetUtcNow();
        var until = DelayUntil(response, now);
        if (until is null) return false;
        lock (sync)
        {
            if (until > blockedUntil) blockedUntil = until.Value;
        }
        return IsLimited(response);
    }

    public static bool IsLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden &&
            (response.Headers.RetryAfter is not null ||
                (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.FirstOrDefault() == "0")));

    public static DateTimeOffset? DelayUntil(HttpResponseMessage response, DateTimeOffset now)
    {
        var exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.FirstOrDefault() == "0";
        if (!exhausted && !IsLimited(response)) return null;

        var delay = TimeSpan.FromMinutes(1);
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > delay) delay = delta;
        if (response.Headers.RetryAfter?.Date is { } date && date - now > delay) delay = date - now;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) &&
            epoch > now.ToUnixTimeSeconds())
            delay = TimeSpan.FromSeconds(Math.Max(delay.TotalSeconds,
                Math.Min(DateTimeOffset.MaxValue.ToUnixTimeSeconds(), epoch) - now.ToUnixTimeSeconds()));
        return delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
    }
}

public sealed class GitHubTransport(HttpClient client, GitHubBudget budget, ActionGate gate, AuditWriter audit)
{
    public const string ApiVersion = "2026-03-10";
    public static readonly Uri ApiBase = new("https://api.github.com/");

    public async Task<GitHubReply> SendAsync(HttpMethod method, string path, string credential,
        object? payload, AuditContext context, string operation, IExecutionGuard guard, CancellationToken cancellationToken,
        bool allowNotFound = false, bool cleanup = false)
    {
        if (cleanup && (method != HttpMethod.Delete || path != "installation/token" || payload is not null))
            throw new BrokerException("invalid_cleanup_operation");
        if (client.BaseAddress != ApiBase || path.StartsWith('/') ||
            !Uri.TryCreate(path, UriKind.Relative, out var relative) ||
            new Uri(ApiBase, relative).Host != ApiBase.Host)
            throw new BrokerException("invalid_github_destination");
        if (!cleanup)
        {
            budget.DemandAvailable();
            if (method != HttpMethod.Get)
            {
                if (guard.ReadOnly && path.StartsWith("repos/", StringComparison.Ordinal))
                    throw new BrokerException("reconciliation_mutation_forbidden");
                gate.DemandEnabled();
                await guard.DemandAsync(cancellationToken);
            }
        }
        using var request = new HttpRequestMessage(method, relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd("entra-github-maintenance-demo/1.0");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var requestId = SafeRequestId(response);
            var limited = budget.Observe(response);
            var blockedUntil = GitHubBudget.DelayUntil(response, budget.Now);
            var tokenResponse = operation == "create_installation_token" && response.IsSuccessStatusCode;
            if (!cleanup && !tokenResponse && blockedUntil is not null)
                await guard.ObserveAsync(blockedUntil.Value, cancellationToken);
            audit.Write(context, operation, response.IsSuccessStatusCode ? "upstream_ok" : "upstream_rejected",
                requestId, upstreamStatus: (int)response.StatusCode);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return new(default, requestId, true);
            if (!response.IsSuccessStatusCode)
                throw new BrokerException(limited ? "github_rate_limited" : "github_upstream_failed",
                    limited ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway);
            var expectedStatus = method == HttpMethod.Get ? response.StatusCode == HttpStatusCode.OK :
                method == HttpMethod.Post ? response.StatusCode == HttpStatusCode.Created :
                method == HttpMethod.Put ? response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created :
                cleanup && method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NoContent;
            if (!expectedStatus) throw new BrokerException("github_unexpected_status");
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new(default, requestId);
            // The bounded response is already buffered; capture an issued token even if cancellation arrives now.
            var parseToken = tokenResponse ? CancellationToken.None : cancellationToken;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(parseToken),
                new JsonDocumentOptions { MaxDepth = 64 }, parseToken);
            return new(json.RootElement.Clone(), requestId, BlockedUntil: blockedUntil);
        }
        catch (HttpRequestException)
        {
            throw new BrokerException("github_transport_failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerException("github_timeout", StatusCodes.Status504GatewayTimeout);
        }
        catch (JsonException)
        {
            throw new BrokerException("github_invalid_response");
        }
    }

    private static string? SafeRequestId(HttpResponseMessage response)
    {
        var value = response.Headers.TryGetValues("X-GitHub-Request-Id", out var values)
            ? values.FirstOrDefault() : null;
        return value is { Length: > 0 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '.')
            ? value : null;
    }
}

internal static class GitHubJson
{
    public static JsonElement Field(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var field))
            throw new BrokerException("github_invalid_response");
        return field;
    }

    public static string Text(this JsonElement element, string name, int maximumLength = 256, bool allowEmpty = false)
    {
        var field = element.Field(name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } value ||
            (!allowEmpty && value.Length == 0) || value.Length > maximumLength)
            throw new BrokerException("github_invalid_response");
        return value;
    }

    public static long Number(this JsonElement element, string name)
    {
        var field = element.Field(name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var value))
            throw new BrokerException("github_invalid_response");
        return value;
    }

    public static string Sha(this JsonElement element, string name)
    {
        var value = element.Text(name, 40);
        if (value.Length != 40 || !value.All(char.IsAsciiHexDigit))
            throw new BrokerException("github_invalid_response");
        return value;
    }
}
