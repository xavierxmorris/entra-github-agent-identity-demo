using MaintenanceBroker;
using Microsoft.Extensions.Configuration;

namespace MaintenanceBroker.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Complete_fixture_is_ready_and_empty_settings_are_not()
    {
        Assert.Empty(SettingsState.Validate(Fixtures.Options()));
        Assert.False(SettingsState.Load(new ConfigurationBuilder().Build()).IsReady);
    }

    [Theory]
    [InlineData("http://example-vault.vault.azure.net/keys/app/0123456789abcdef0123456789abcdef")]
    [InlineData("https://evil.example/keys/app/0123456789abcdef0123456789abcdef")]
    [InlineData("https://example-vault.vault.azure.net.evil.example/keys/app/0123456789abcdef0123456789abcdef")]
    [InlineData("https://example-vault.vault.azure.net/keys/app")]
    [InlineData("https://example-vault.vault.azure.net/secrets/app/0123456789abcdef0123456789abcdef")]
    [InlineData("https://user@example-vault.vault.azure.net/keys/app/0123456789abcdef0123456789abcdef")]
    [InlineData("https://example-vault.vault.azure.net/keys/app/0123456789abcdef0123456789abcdef?secret=x")]
    public void Key_uri_must_be_versioned_https_key_in_public_azure(string uri)
    {
        var options = Fixtures.Options();
        options.KeyVaultKeyUri = uri;
        Assert.Contains("Broker:KeyVaultKeyUri", SettingsState.Validate(options));
    }

    [Theory]
    [InlineData("demo/another-request")]
    [InlineData("../main")]
    [InlineData("main.lock")]
    [InlineData("main//feature")]
    [InlineData("main?ref=other")]
    public void Unsafe_base_branches_are_rejected(string branch)
    {
        var capability = Fixtures.Capabilities()[0] with { BaseBranch = branch };
        Assert.False(SettingsState.ValidCapability(capability));
    }

    [Fact]
    public async Task Principal_must_reference_existing_capability_in_the_single_tenant()
    {
        var store = new MemoryPolicyStore();
        var row = store.Rows[(PolicyRegistry.PrincipalPartition(Fixtures.Caller()), "sandbox")];
        row["TenantId"] = "99999999-9999-4999-8999-999999999999";
        var registry = new PolicyRegistry(store, new ActionGate(Fixtures.Configuration()));
        Assert.Equal("policy_invalid", (await Assert.ThrowsAsync<BrokerException>(() =>
            registry.ReadGrantAsync(Fixtures.Caller(), "sandbox", CancellationToken.None))).Code);
        var options = Fixtures.Options();
        options.Audience = "api://" + Fixtures.Audience;
        Assert.Contains("Broker:Audience", SettingsState.Validate(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("worker")]
    [InlineData("Both")]
    public void Mode_is_explicit_and_case_sensitive(string mode)
    {
        var options = Fixtures.Options();
        options.Mode = mode;
        Assert.Contains("Broker:Mode", SettingsState.Validate(options));
    }

    [Fact]
    public void Admission_has_no_signing_configuration_and_storage_names_are_validated()
    {
        Assert.Empty(SettingsState.Validate(Fixtures.Options("Admission")));
        var options = Fixtures.Options("Admission");
        options.GitHubAppId = 1;
        Assert.Contains("Broker:worker_settings_on_admission", SettingsState.Validate(options));
        options = Fixtures.Options();
        options.StorageAccountName = "https://untrusted";
        options.QueueName = "bad--queue";
        options.PoisonQueueName = "bad--queue";
        options.PolicyTableName = options.OperationsTableName;
        var errors = SettingsState.Validate(options);
        Assert.Contains("Broker:StorageAccountName", errors);
        Assert.Contains("Broker:PolicyTableName", errors);
        Assert.Contains("Broker:QueueName", errors);
        Assert.Contains("Broker:PoisonQueueName", errors);
    }

    [Fact]
    public void Invalid_binding_is_reported_without_returning_its_value()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Broker:GitHubAppId"] = "invalid-sensitive-fixture" }).Build();
        Assert.Equal(new[] { "Broker:binding" }, SettingsState.Load(configuration).Errors);
    }
}
