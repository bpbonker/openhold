using System.Net;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Identity;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class AccountTests(VaultFixture vault)
{
    private static string NewUsername() => $"user{Guid.NewGuid():N}"[..14];

    private OpenholdClient Admin => vault.Client(VaultFixture.Administrator);

    /// <summary>An Openhold account that has changed its temporary password; returns its password.</summary>
    private async Task<string> CreateReadyAccountAsync(string username, bool twoFactor = false)
    {
        var created = await Admin.CreateAccountAsync(new CreateAccountRequest(username, "Test Person", RequireTwoFactor: twoFactor));
        var client = vault.Client("nobody");
        var first = await client.SignInAsync(username, created.TemporaryPassword);
        client.UseAccessToken(first.AccessToken, username);
        const string password = "correct horse battery staple";
        await client.ChangePasswordAsync(created.TemporaryPassword, password);
        return password;
    }

    [Fact]
    public async Task A_new_account_must_change_its_temporary_password_before_doing_anything_else()
    {
        var username = NewUsername();
        var created = await Admin.CreateAccountAsync(new CreateAccountRequest(username, "Priya Shah", RequireTwoFactor: false));
        var client = vault.Client("nobody");

        var first = await client.SignInAsync(username, created.TemporaryPassword);
        client.UseAccessToken(first.AccessToken, username);
        var blocked = await Assert.ThrowsAsync<OpenholdApiException>(() => client.ListFolderAsync("$"));
        var weak = await Assert.ThrowsAsync<OpenholdApiException>(() => client.ChangePasswordAsync(created.TemporaryPassword, "short"));
        await client.ChangePasswordAsync(created.TemporaryPassword, "a long and memorable phrase");
        var second = await client.SignInAsync(username, "a long and memorable phrase");
        client.UseAccessToken(second.AccessToken, username);

        Assert.True(first.MustChangePassword);
        Assert.Equal("password_change_required", blocked.Code);
        Assert.Equal("weak_password", weak.Code);
        Assert.False(second.MustChangePassword);
        await client.ListFolderAsync("$");
        Assert.Equal(username, (await client.WhoAmIAsync()).Username);
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_account_until_it_times_out_or_is_unlocked()
    {
        var username = NewUsername();
        var password = await CreateReadyAccountAsync(username);
        var client = vault.Client("nobody");

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<OpenholdApiException>(() => client.SignInAsync(username, "wrong password guess"));
        var locked = await Assert.ThrowsAsync<OpenholdApiException>(() => client.SignInAsync(username, password));
        var unknown = await Assert.ThrowsAsync<OpenholdApiException>(() => client.SignInAsync("nobody-like-this", password));
        await Admin.UnlockAccountAsync(username);

        Assert.Equal("account_locked", locked.Code);
        Assert.Equal("sign_in_failed", unknown.Code); // the same answer as a wrong password: usernames can't be probed
        await client.SignInAsync(username, password);
    }

    [Fact]
    public async Task Two_factor_sign_in_needs_the_authenticator_code_once_set_up()
    {
        var username = NewUsername();
        var created = await Admin.CreateAccountAsync(new CreateAccountRequest(username, "Two Factor", RequireTwoFactor: true));
        var client = vault.Client("nobody");
        var first = await client.SignInAsync(username, created.TemporaryPassword);
        client.UseAccessToken(first.AccessToken, username);
        await client.ChangePasswordAsync(created.TemporaryPassword, "another long passphrase");
        var second = await client.SignInAsync(username, "another long passphrase");
        client.UseAccessToken(second.AccessToken, username);

        Assert.True(second.MustSetUpTwoFactor);
        var setup = await client.BeginTwoFactorAsync();
        var secret = Base32Decode(setup.Secret);
        await Assert.ThrowsAsync<OpenholdApiException>(() => client.ConfirmTwoFactorAsync("000000" == Totp.Code(secret, DateTimeOffset.UtcNow) ? "111111" : "000000"));
        await client.ConfirmTwoFactorAsync(Totp.Code(secret, DateTimeOffset.UtcNow));

        var needsCode = await Assert.ThrowsAsync<OpenholdApiException>(() => client.SignInAsync(username, "another long passphrase"));
        var signedIn = await client.SignInAsync(username, "another long passphrase", Totp.Code(secret, DateTimeOffset.UtcNow));

        Assert.Equal("two_factor_code_needed", needsCode.Code);
        Assert.False(signedIn.MustSetUpTwoFactor);
        Assert.Contains(await Admin.ListAccountsAsync(), a => a.Username == username && a.TwoFactorEnabled);
        Assert.StartsWith("otpauth://totp/", setup.SetupUri);
    }

    [Fact]
    public async Task A_disabled_account_is_refused_whichever_way_it_signs_in()
    {
        var windowsUser = NewUsername();
        var person = vault.Client(windowsUser);
        await person.ListFolderAsync("$"); // first sign-in records the account

        await Admin.UpdateAccountAsync(windowsUser, new UpdateAccountRequest(Disabled: true));
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => person.ListFolderAsync("$"));
        var self = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.UpdateAccountAsync(VaultFixture.Administrator, new UpdateAccountRequest(Disabled: true)));
        await Admin.UpdateAccountAsync(windowsUser, new UpdateAccountRequest(Disabled: false));

        Assert.Equal((HttpStatusCode.Forbidden, "account_disabled"), (refused.StatusCode, refused.Code));
        Assert.Equal("cant_disable_self", self.Code);
        await person.ListFolderAsync("$");
    }

    [Fact]
    public async Task A_session_token_works_once_and_reusing_it_ends_every_session()
    {
        var username = NewUsername();
        var password = await CreateReadyAccountAsync(username);
        var client = vault.Client("nobody");
        var signedIn = await client.SignInAsync(username, password);

        var refreshed = await client.RefreshSessionAsync(signedIn.SessionToken);
        var reused = await Assert.ThrowsAsync<OpenholdApiException>(() => client.RefreshSessionAsync(signedIn.SessionToken));
        var afterReuse = await Assert.ThrowsAsync<OpenholdApiException>(() => client.RefreshSessionAsync(refreshed.SessionToken));

        Assert.NotEqual(signedIn.SessionToken, refreshed.SessionToken);
        Assert.Equal("session_ended", reused.Code);
        Assert.Equal("session_ended", afterReuse.Code); // a copied token ends the real one too
    }

    [Fact]
    public async Task Only_people_with_the_manage_users_privilege_manage_accounts()
    {
        var helpdesk = NewUsername();
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client(helpdesk).ListAccountsAsync());

        var current = await Admin.GetConfigurationAsync();
        var groupId = $"helpdesk-{Guid.NewGuid():N}";
        await Admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            Groups = [.. current.Configuration.Groups, new GroupDefinition(groupId, "Helpdesk", [helpdesk], [Privileges.ManageUsers])],
        }));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.NotEmpty(await vault.Client(helpdesk).ListAccountsAsync());
        Assert.Contains(Privileges.ManageUsers, (await vault.Client(helpdesk).WhoAmIAsync()).Privileges);
    }

    [Fact]
    public async Task Auditors_read_the_audit_log_and_access_review_but_nobody_else_does()
    {
        var auditor = NewUsername();
        var username = NewUsername();
        await CreateReadyAccountAsync(username);
        await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client("nobody").SignInAsync(username, "not the password"));
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client(auditor).ListAuditAsync());

        var current = await Admin.GetConfigurationAsync();
        await Admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            Groups = [.. current.Configuration.Groups, new GroupDefinition($"auditors-{Guid.NewGuid():N}", "Auditors", [auditor], [Privileges.ViewAudit])],
        }));
        var events = await vault.Client(auditor).ListAuditAsync(username: username);
        var review = await vault.Client(auditor).GetAccessReviewAsync();

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(events, e => e.Action == "account_created");
        Assert.Contains(events, e => e.Action == "sign_in_failed");
        Assert.Contains(events, e => e.Action == "password_changed");
        Assert.Contains(review, r => r.Username == auditor && r.Groups.Contains("Auditors") && r.Privileges.Contains(Privileges.ViewAudit));
        Assert.Contains(review, r => r.Username == username && r.Kind == "local");
    }

    [Fact]
    public async Task A_forged_or_foreign_token_is_refused()
    {
        var client = vault.Client("nobody");
        client.UseAccessToken("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiIsImlzcyI6Im9wZW5ob2xkIiwiYXVkIjoib3BlbmhvbGQifQ.c2lnbmF0dXJl", "admin"); // gitleaks:allow (a forged token, refused by design)

        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => client.ListAccountsAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
