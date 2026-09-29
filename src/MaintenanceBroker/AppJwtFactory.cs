using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace MaintenanceBroker;

public interface IAppJwtFactory
{
    Task<string> CreateAsync(IExecutionGuard guard, CancellationToken cancellationToken);
}

public sealed class KeyVaultAppJwtFactory(SettingsState settings, ActionGate gate, TimeProvider clock)
    : IAppJwtFactory
{
    private readonly Lazy<CryptographyClient> client = new(() =>
    {
        var credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(settings.Options.ManagedIdentityClientId));
        var options = new CryptographyClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
        options.Diagnostics.IsLoggingContentEnabled = false;
        return new CryptographyClient(new Uri(settings.Options.KeyVaultKeyUri), credential, options);
    });

    public async Task<string> CreateAsync(IExecutionGuard guard, CancellationToken cancellationToken)
    {
        gate.DemandEnabled();
        if (!settings.IsReady || settings.Options.Mode != "Worker")
            throw new BrokerException("signing_not_permitted", StatusCodes.Status503ServiceUnavailable);
        await guard.DemandAsync(cancellationToken);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new
        {
            iss = settings.Options.GitHubAppId.ToString(CultureInfo.InvariantCulture),
            iat = now - 60,
            exp = now + 480
        }));
        var signingInput = $"{header}.{payload}";
        try
        {
            var signature = await client.Value.SignDataAsync(SignatureAlgorithm.RS256,
                Encoding.ASCII.GetBytes(signingInput), cancellationToken);
            return $"{signingInput}.{Base64UrlEncoder.Encode(signature.Signature)}";
        }
        catch (RequestFailedException)
        {
            throw new BrokerException("key_vault_signing_failed");
        }
        catch (AuthenticationFailedException)
        {
            throw new BrokerException("managed_identity_failed");
        }
    }
}
