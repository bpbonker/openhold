using Openhold.Client.Sdk;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class MultiVaultTests(VaultFixture vault)
{
    [Fact]
    public async Task Server_lists_its_vaults()
    {
        var names = await vault.Client("ivy").ListVaultsAsync();

        Assert.Equal(["Production", "Sandbox"], names);
    }

    [Fact]
    public async Task Vaults_are_completely_separate()
    {
        var production = vault.Client("ivy");
        var sandbox = vault.Client("ivy", vault: "Sandbox");
        var tag = Guid.NewGuid().ToString("N")[..10];
        var name = $"SANDBOX-{tag}.dwg";

        await VaultFixture.AddFilesAsync(sandbox, VaultFixture.UniqueFolder("mv"), name);
        // CAD names are unique per vault, not across vaults: the same name can exist in both.
        await VaultFixture.AddFilesAsync(production, VaultFixture.UniqueFolder("mv"), name);

        var inSandbox = await sandbox.SearchAsync(tag);
        var inProduction = await production.SearchAsync(tag);

        Assert.Single(inSandbox.Hits);
        Assert.Single(inProduction.Hits);
        Assert.NotEqual(inSandbox.Hits[0].File.Id, inProduction.Hits[0].File.Id);
    }

    [Fact]
    public async Task Unknown_vault_is_reported_plainly()
    {
        var error = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client("ivy", vault: "Nope").ListFolderAsync("$"));

        Assert.Equal("not_found", error.Code);
        Assert.Contains("no vault called 'Nope'", error.Message);
    }

    [Fact]
    public async Task Health_works_for_a_named_vault()
    {
        var health = await vault.Client("ivy", vault: "Sandbox").GetHealthAsync();

        Assert.Equal("ok", health.Status);
    }
}
