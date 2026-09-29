using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MaintenanceBroker;

public sealed class BrokerException(string code, int statusCode = StatusCodes.Status502BadGateway)
    : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record MaintenanceRequest(string CapabilityId, Guid RequestId)
{
    public const int MaximumBytes = 1024;

    public static async Task<MaintenanceRequest> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json",
                StringComparison.OrdinalIgnoreCase))
            throw new BrokerException("json_required", StatusCodes.Status415UnsupportedMediaType);
        var buffer = new byte[MaximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumBytes) throw new BrokerException("request_too_large", StatusCodes.Status413PayloadTooLarge);
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var fields = json.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 2 || fields.Count(f => f.Name == "capabilityId") != 1 ||
                fields.Count(f => f.Name == "requestId") != 1 ||
                fields.Any(f => f.Value.ValueKind != JsonValueKind.String)) throw Invalid();
            var capability = json.RootElement.GetProperty("capabilityId").GetString();
            var rawId = json.RootElement.GetProperty("requestId").GetString();
            if (!SettingsState.ValidCapabilityId(capability) ||
                !Guid.TryParseExact(rawId, "D", out var id) || id == Guid.Empty ||
                id.ToString("D") != rawId) throw Invalid();
            return new(capability!, id);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static BrokerException Invalid() => new("invalid_request", StatusCodes.Status400BadRequest);
}

public sealed record OperationKey(CallerIdentity Caller, string CapabilityId, Guid RequestId);

public sealed record OperationPlan(OperationKey Key, Capability Capability, string Branch, string Content)
{
    public const string FilePath = "demo/maintenance.json";
    public const string Title = "demo: synthetic maintenance metadata";
    public const string PullRequestBody =
        "Synthetic maintenance metadata only. No package update is performed. " +
        "Created by the broker's GitHub App, not by impersonating the requesting Entra user. " +
        "Review only; this demo never merges pull requests.";

    public static OperationPlan Create(CallerIdentity caller, Capability capability, Guid requestId)
    {
        var key = new OperationKey(caller, capability.Id, requestId);
        var scope = string.Join('\n', "maintenance-v1", caller.TenantId.ToString("D"),
            caller.ObjectId.ToString("D"), caller.ClientId.ToString("D"), caller.Mode, capability.Id,
            capability.RepositoryId.ToString(CultureInfo.InvariantCulture),
            capability.InstallationId.ToString(CultureInfo.InvariantCulture), capability.Owner,
            capability.Name, capability.BaseBranch, requestId.ToString("D"));
        var operationId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        var content = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "synthetic-maintenance-metadata",
            operationId,
            requestId = requestId.ToString("D"),
            capabilityId = capability.Id,
            packageChanges = false
        }) + "\n";
        return new(key, capability, $"demo/{operationId}", content);
    }
}

public sealed record MaintenanceResult(
    string Status,
    string CapabilityId,
    Guid RequestId,
    long RepositoryId,
    string Branch,
    int PullRequestNumber,
    string PullRequestUrl,
    string PullRequestState,
    string? GitHubRequestId,
    bool Replayed = false);
