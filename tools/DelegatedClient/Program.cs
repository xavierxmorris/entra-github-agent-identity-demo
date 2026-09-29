using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Client;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: DelegatedClient <ignored-local-state.json> [canonical-request-guid]");
    return 2;
}

try
{
    using var state = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    var root = state.RootElement;
    var tenantId = Guid.Parse(root.GetProperty("tenantId").GetString()!).ToString("D");
    var clientId = Guid.Parse(root.GetProperty("entra").GetProperty("delegated").GetProperty("clientId").GetString()!).ToString("D");
    var apiId = Guid.Parse(root.GetProperty("entra").GetProperty("api").GetProperty("clientId").GetString()!).ToString("D");
    var origin = new Uri(root.GetProperty("azure").GetProperty("admissionUrl").GetString()!);
    if (origin.Scheme != "https" || origin.Port != 443 || origin.UserInfo.Length != 0 ||
        origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
        !origin.Host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Expected the HTTPS App Service origin.");
    var requestId = args.Length == 2 ? Guid.ParseExact(args[1], "D") : Guid.NewGuid();
    if (requestId == Guid.Empty || (args.Length == 2 && args[1] != requestId.ToString("D")))
        throw new InvalidDataException("Request ID must be a canonical lowercase nonempty GUID.");

    Console.WriteLine("Workforce delegated test, not bank-customer identity provisioning. Sign-in, consent and Conditional Access remain enforced.");
    var application = PublicClientApplicationBuilder.Create(clientId)
        .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
        .WithRedirectUri("http://localhost")
        .Build();
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    var authentication = await application.AcquireTokenInteractive([$"api://{apiId}/Agent.Invoke"])
        .WithUseEmbeddedWebView(false).ExecuteAsync(deadline.Token);
    // No persistent MSAL cache, refresh-token storage or token output.
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = origin,
        Timeout = TimeSpan.FromSeconds(30)
    };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.AccessToken);
    Console.WriteLine($"Request ID: {requestId:D}");
    using var submitted = await http.PostAsJsonAsync("v1/maintenance-pr",
        new { capabilityId = "sandbox-maintenance", requestId = requestId.ToString("D") }, deadline.Token);
    if (submitted.StatusCode is not (System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.OK))
    {
        Console.Error.WriteLine($"Admission rejected the operation: HTTP {(int)submitted.StatusCode}.");
        return 1;
    }
    using var accepted = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(deadline.Token));
    var operation = accepted.RootElement.Clone();
    var id = operation.GetProperty("id").GetString();
    if (id is null || id.Length != 64 || id.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
        throw new InvalidDataException("Invalid operation identifier.");
    using var pollDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    while (operation.GetProperty("status").GetString() is "pending" or "working" or "reconciling")
    {
        await Task.Delay(TimeSpan.FromSeconds(5), pollDeadline.Token);
        using var response = await http.GetAsync($"v1/operations/{id}", pollDeadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            Console.Error.WriteLine($"Status query failed: HTTP {(int)response.StatusCode}. Request ID is retained for investigation.");
            return 1;
        }
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(pollDeadline.Token));
        operation = status.RootElement.Clone();
    }
    var outcome = operation.GetProperty("status").GetString();
    Console.WriteLine($"Operation {id}: {outcome}");
    if (outcome != "completed")
    {
        Console.Error.WriteLine($"Operation ended without completion: {operation.GetProperty("error")}");
        return 1;
    }
    var pr = new Uri(operation.GetProperty("result").GetProperty("pullRequestUrl").GetString()!);
    if (pr.Scheme != "https" || pr.Host != "github.com" || pr.UserInfo.Length != 0)
        throw new InvalidDataException("Unexpected PR URL.");
    Console.WriteLine($"Synthetic pull request: {pr}");
    return 0;
}
catch (MsalException exception)
{
    Console.Error.WriteLine($"Entra sign-in failed: {exception.ErrorCode}. No credential fallback was attempted.");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Sign-in or operation deadline elapsed. Inspect existing operation state rather than resubmitting blindly.");
    return 1;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"HTTP request failed ({exception.StatusCode?.ToString() ?? "transport error"}). Credentials and response bodies are not logged.");
    return 1;
}
catch (Exception exception) when (exception is IOException or JsonException or FormatException or
    KeyNotFoundException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"Invalid local configuration or broker response ({exception.GetType().Name}).");
    return 2;
}
