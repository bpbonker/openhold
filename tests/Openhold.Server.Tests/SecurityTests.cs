using System.Net;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Openhold.Server.Identity;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class SecurityTests(VaultFixture vault)
{
    [Fact]
    public async Task Api_rejects_unauthenticated_requests()
    {
        using var anonymous = vault.AnonymousHttpClient();

        var response = await anonymous.GetAsync("api/folders?path=$");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_public_and_reveals_nothing_sensitive()
    {
        using var anonymous = vault.AnonymousHttpClient();

        var body = await anonymous.GetStringAsync("health");

        Assert.Contains("\"status\":\"ok\"", body);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readiness_checks_the_databases()
    {
        using var anonymous = vault.AnonymousHttpClient();
        var site = vault.CutOffSiteClient(VaultFixture.Administrator);

        var ready = await anonymous.GetStringAsync("health/ready");
        var cutOff = await site.GetReadinessAsync();

        Assert.Contains("\"status\":\"ok\"", ready);
        Assert.Equal("authority unreachable: Production, Sandbox", cutOff.Status);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var anonymous = vault.AnonymousHttpClient();

        var response = await anonymous.GetAsync("health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Audit_trail_cannot_be_altered_or_deleted()
    {
        await VaultFixture.AddFilesAsync(vault.Client("auditor"), VaultFixture.UniqueFolder("audit"), $"AUD-{Guid.NewGuid():N}.dwg");
        await using var connection = new NpgsqlConnection(vault.ConnectionString);
        await connection.OpenAsync();

        foreach (var sql in new[] { "UPDATE audit_events SET action = 'forged'", "DELETE FROM audit_events", "TRUNCATE audit_events" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public void Dev_header_auth_is_refused_outside_development()
    {
        var options = new OpenholdOptions { AuthMode = "Dev" };

        Assert.Throws<InvalidOperationException>(() => SecurityGuards.Validate(options, new FakeEnvironment(Environments.Production)));
        SecurityGuards.Validate(options, new FakeEnvironment(Environments.Development));
        SecurityGuards.Validate(new OpenholdOptions { AuthMode = "Windows" }, new FakeEnvironment(Environments.Production));
        Assert.Throws<InvalidOperationException>(() => SecurityGuards.Validate(new OpenholdOptions { AuthMode = "None" }, new FakeEnvironment(Environments.Production)));
    }

    [Theory]
    [InlineData(@"EXAMPLE\JSmith", "jsmith")]
    [InlineData("jsmith@example.local", "jsmith")]
    [InlineData("jsmith", "jsmith")]
    [InlineData("  ", null)]
    public void Usernames_normalise_to_one_account_per_person(string input, string? expected) =>
        Assert.Equal(expected, ActorResolver.NormalizeUsername(input));

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Openhold.Server";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
