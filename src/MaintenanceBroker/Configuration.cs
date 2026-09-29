using System.Text.RegularExpressions;

namespace MaintenanceBroker;

public sealed class BrokerOptions
{
    public string Mode { get; set; } = "";
    public bool KillSwitch { get; set; } = true;
    public string TenantId { get; set; } = "";
    public string Audience { get; set; } = "";
    public long GitHubAppId { get; set; }
    public string KeyVaultKeyUri { get; set; } = "";
    public string ManagedIdentityClientId { get; set; } = "";
    public string StorageAccountName { get; set; } = "";
    public string OperationsTableName { get; set; } = "operations";
    public string PolicyTableName { get; set; } = "policies";
    public string QueueName { get; set; } = "maintenance";
    public string PoisonQueueName { get; set; } = "maintenance-poison";
}

public sealed record Capability
{
    public long Version { get; set; }
    public string Id { get; set; } = "";
    public long RepositoryId { get; set; }
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public long InstallationId { get; set; }
    public string BaseBranch { get; set; } = "";
}

public sealed record SettingsState(BrokerOptions Options, IReadOnlyList<string> Errors)
{
    public bool IsReady => Errors.Count == 0;

    public static SettingsState Load(IConfiguration configuration)
    {
        var options = new BrokerOptions();
        try
        {
            configuration.GetSection("Broker").Bind(options);
        }
        catch (InvalidOperationException)
        {
            // Binding exceptions may contain configuration values; report only the category.
            return new(options, ["Broker:binding"]);
        }
        var errors = Validate(options).ToList();
        if (!bool.TryParse(configuration["Broker:KillSwitch"], out _)) errors.Add("Broker:KillSwitch");
        if (configuration.GetSection("Broker:Capabilities").Exists() ||
            configuration.GetSection("Broker:Principals").Exists())
            errors.Add("Broker:static_policy_not_supported");
        return new(options, errors);
    }

    public static IReadOnlyList<string> Validate(BrokerOptions options)
    {
        var errors = new List<string>();
        if (options.Mode is not ("Admission" or "Worker")) errors.Add("Broker:Mode");
        if (!ValidGuid(options.TenantId)) errors.Add("Broker:TenantId");
        if (!ValidGuid(options.Audience)) errors.Add("Broker:Audience");
        if (!ValidGuid(options.ManagedIdentityClientId)) errors.Add("Broker:ManagedIdentityClientId");
        if (!Matches(options.StorageAccountName, @"[a-z0-9]{3,24}")) errors.Add("Broker:StorageAccountName");
        if (!Matches(options.OperationsTableName, @"[A-Za-z][A-Za-z0-9]{2,62}"))
            errors.Add("Broker:OperationsTableName");
        if (!Matches(options.PolicyTableName, @"[A-Za-z][A-Za-z0-9]{2,62}") ||
            string.Equals(options.PolicyTableName, options.OperationsTableName, StringComparison.OrdinalIgnoreCase))
            errors.Add("Broker:PolicyTableName");
        if (!ValidQueue(options.QueueName)) errors.Add("Broker:QueueName");
        if (!ValidQueue(options.PoisonQueueName) || options.PoisonQueueName == options.QueueName)
            errors.Add("Broker:PoisonQueueName");
        if (options.Mode == "Admission" && (options.GitHubAppId != 0 || options.KeyVaultKeyUri.Length != 0))
            errors.Add("Broker:worker_settings_on_admission");
        if (options.Mode == "Worker")
        {
            if (options.GitHubAppId <= 0) errors.Add("Broker:GitHubAppId");
            if (!Uri.TryCreate(options.KeyVaultKeyUri, UriKind.Absolute, out var key) ||
                key.Scheme != Uri.UriSchemeHttps || !key.IsDefaultPort ||
                key.UserInfo.Length != 0 || key.Query.Length != 0 || key.Fragment.Length != 0 ||
                !Matches(key.Host, @"[a-z][a-z0-9-]{1,22}[a-z0-9]\.vault\.azure\.net") ||
                !Matches(key.AbsolutePath, @"/keys/[A-Za-z0-9-]{1,127}/[a-fA-F0-9]{32}"))
                errors.Add("Broker:KeyVaultKeyUri");
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool ValidCapability(Capability capability) =>
        ValidCapabilityId(capability.Id) && capability.Version > 0 &&
        capability.RepositoryId > 0 && capability.InstallationId > 0 &&
        Matches(capability.Owner, @"[A-Za-z0-9][A-Za-z0-9-]{0,38}") &&
        Matches(capability.Name, @"[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}") &&
        ValidBaseBranch(capability.BaseBranch);

    private static bool ValidQueue(string value) =>
        Matches(value, @"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]") && !value.Contains("--", StringComparison.Ordinal);

    public static bool ValidGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;

    public static bool ValidCapabilityId(string? value) => Matches(value, @"[a-z][a-z0-9-]{0,63}");

    private static bool ValidBaseBranch(string value) =>
        Matches(value, @"[A-Za-z0-9_][A-Za-z0-9_./-]{0,99}") &&
        !value.StartsWith("demo/", StringComparison.Ordinal) &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("//", StringComparison.Ordinal) &&
        value.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') &&
            !part.EndsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string? value, string pattern) =>
        value is not null && Regex.IsMatch(value, @"\A(?:" + pattern + @")\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

public sealed class ActionGate(IConfiguration configuration)
{
    public bool IsEnabled =>
        bool.TryParse(configuration["Broker:KillSwitch"], out var disabled) && !disabled;

    public void DemandEnabled()
    {
        if (!IsEnabled) throw new BrokerException("actions_disabled", StatusCodes.Status503ServiceUnavailable);
    }
}
