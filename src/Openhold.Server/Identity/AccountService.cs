using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;

namespace Openhold.Server.Identity;

/// <summary>
/// Accounts and sign-in, for the whole server (accounts live in the identity vault, the first vault).
/// - Openhold accounts sign in with a password and, when set up, a two-factor code; they get a short-lived access
///   token and a rotating session token. Wrong passwords or codes lock the account for a while.
/// - Windows and organisation accounts are recorded the first time they're seen, so they too can be disabled.
/// - A disabled account is refused on every request, whichever way it signed in.
/// Every sign-in, failure and change is written to the audit log.
/// </summary>
public sealed class AccountService(VaultRegistry vaults, SigningKeys keys, IOptions<OpenholdOptions> options, TimeProvider clock)
{
    public const string Issuer = "openhold";
    public const string Audience = "openhold";

    /// <summary>Claim on access tokens that still need a password change or two-factor set-up first.</summary>
    public const string RestrictionClaim = "openhold_restriction";

    private static readonly TimeSpan StatusCacheTime = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (bool Disabled, DateTimeOffset Checked)> _status = new(StringComparer.OrdinalIgnoreCase);

    private AuthenticationOptions Settings => options.Value.Authentication;

    private NpgsqlDataSource Db => vaults.Default.Db.Primary;

    private sealed record AccountRow(
        string Username, string DisplayName, string? Email, string Kind, string? PasswordHash, bool MustChangePassword,
        bool MfaRequired, byte[]? MfaSecret, bool MfaEnabled, bool Disabled, int FailedAttempts, DateTime? LockedUntil,
        DateTime CreatedAt, DateTime? LastSignInAt, int Sessions);

    private const string AccountColumns = """
        a.username AS Username, a.display_name AS DisplayName, a.email AS Email, a.kind AS Kind, a.password_hash AS PasswordHash,
        a.must_change_password AS MustChangePassword, a.mfa_required AS MfaRequired, a.mfa_secret AS MfaSecret,
        a.mfa_enabled AS MfaEnabled, a.disabled AS Disabled, a.failed_attempts AS FailedAttempts, a.locked_until AS LockedUntil,
        a.created_at AS CreatedAt, a.last_sign_in_at AS LastSignInAt,
        (SELECT count(*)::int FROM refresh_tokens r WHERE r.username = a.username AND r.expires_at > now()) AS Sessions
        """;

    // ---- Signing in ----

    public async Task<SignInResult> SignInAsync(SignInRequest request, string? remoteAddress, CancellationToken cancellationToken = default)
    {
        if (!Settings.LocalAccounts)
            throw Refused("local_accounts_off", "Openhold accounts aren't used on this server. Sign in with Windows or your organisation's account.");
        var username = (ActorResolver.NormalizeUsername(request.Username) ?? "").ToLowerInvariant();
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            $"SELECT {AccountColumns} FROM accounts a WHERE a.username = @username AND a.kind = 'local' FOR UPDATE", new { username }, transaction);
        var now = clock.GetUtcNow();

        // One answer for "no such user", "wrong password" and "disabled", so sign-in can't be used to find accounts.
        const string wrong = "That username and password don't match. Check them and try again.";
        if (account is null)
        {
            Passwords.Verify(request.Password, null);
            await AuditAsync(connection, transaction, "sign_in_failed", username, new { reason = "unknown_user", remoteAddress });
            await transaction.CommitAsync(cancellationToken);
            throw Refused("sign_in_failed", wrong);
        }
        if (account.LockedUntil is { } until && until > now.UtcDateTime)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw Refused("account_locked", $"Too many wrong attempts. Try again after {until.ToLocalTime():h:mm tt}, or ask an administrator to unlock the account.");
        }

        var passwordOk = Passwords.Verify(request.Password, account.PasswordHash);
        var codeNeeded = passwordOk && account.MfaEnabled && string.IsNullOrWhiteSpace(request.Code);
        var codeOk = !account.MfaEnabled || (account.MfaSecret is { } secret && Totp.Verify(secret, request.Code, now));
        if (codeNeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw Refused("two_factor_code_needed", "Enter the six-digit code from your authenticator app.");
        }
        if (!passwordOk || !codeOk || account.Disabled)
        {
            var attempts = account.FailedAttempts + 1;
            var locked = attempts >= Settings.LockoutAttempts;
            await connection.ExecuteAsync("""
                UPDATE accounts SET failed_attempts = @failed, locked_until = @lockedUntil WHERE username = @username
                """, new { username, failed = locked ? 0 : attempts, lockedUntil = locked ? now.Add(Settings.LockoutDuration).UtcDateTime : (DateTime?)null }, transaction);
            await AuditAsync(connection, transaction, locked ? "account_locked" : "sign_in_failed", username,
                new { reason = account.Disabled ? "disabled" : !passwordOk ? "password" : "two_factor_code", remoteAddress });
            await transaction.CommitAsync(cancellationToken);
            throw Refused("sign_in_failed", passwordOk && !codeOk && !account.Disabled ? "That code isn't right. Check your authenticator app and try again." : wrong);
        }

        await connection.ExecuteAsync(
            "UPDATE accounts SET failed_attempts = 0, locked_until = NULL, last_sign_in_at = now() WHERE username = @username", new { username }, transaction);
        var result = await IssueAsync(connection, transaction, account, request.Machine, account.MfaEnabled ? "mfa" : "pwd");
        await AuditAsync(connection, transaction, "sign_in", username, new { method = account.MfaEnabled ? "password+code" : "password", remoteAddress });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// A new access token for a session. Session tokens are used once and replaced; using one twice means it was
    /// copied, so every session of that account is ended.
    /// </summary>
    public async Task<SignInResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(request.SessionToken);
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Username, DateTime ExpiresAt, DateTime? UsedAt)?>(
            "SELECT id, username, expires_at, used_at FROM refresh_tokens WHERE token_hash = @hash FOR UPDATE", new { hash }, transaction);
        if (session is not { } found || found.ExpiresAt < clock.GetUtcNow().UtcDateTime)
            throw Refused("session_ended", "Your sign-in has ended. Sign in again.");
        if (found.UsedAt is not null)
        {
            await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE username = @username", new { found.Username }, transaction);
            await AuditAsync(connection, transaction, "session_reuse_detected", found.Username, new { });
            await transaction.CommitAsync(cancellationToken);
            throw Refused("session_ended", "Your sign-in has ended for safety. Sign in again.");
        }

        var account = await connection.QuerySingleAsync<AccountRow>($"SELECT {AccountColumns} FROM accounts a WHERE a.username = @username", new { found.Username }, transaction);
        if (account.Disabled)
            throw Refused("account_disabled", "This account has been disabled. Ask your Openhold administrator.");
        await connection.ExecuteAsync("UPDATE refresh_tokens SET used_at = now() WHERE id = @id", new { found.Id }, transaction);
        // Used sessions are kept a day for reuse detection, then removed.
        await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE used_at < now() - interval '1 day' OR expires_at < now()", transaction: transaction);
        var result = await IssueAsync(connection, transaction, account, request.Machine, account.MfaEnabled ? "mfa" : "pwd");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task SignOutAsync(string username, SignOutRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE token_hash = @hash AND username = @username",
            new { hash = HashToken(request.SessionToken), username });
        await AuditAsync(connection, null, "sign_out", username, new { });
    }

    private async Task<SignInResult> IssueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AccountRow account, string? machine, string method)
    {
        var now = clock.GetUtcNow();
        var mustSetUpTwoFactor = account.MfaRequired && !account.MfaEnabled;
        var claims = new Dictionary<string, object> { ["sub"] = account.Username, ["name"] = account.DisplayName, ["amr"] = method };
        if (account.MustChangePassword || mustSetUpTwoFactor)
            claims[RestrictionClaim] = account.MustChangePassword ? "change_password" : "set_up_two_factor";

        var expires = now.Add(Settings.AccessTokenLifetime);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keys.Signing,
        });

        var session = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var sessionExpires = now.Add(Settings.SessionLifetime);
        await connection.ExecuteAsync("""
            INSERT INTO refresh_tokens (id, username, token_hash, machine, expires_at) VALUES (@id, @username, @hash, @machine, @expires)
            """, new { id = Guid.NewGuid(), account.Username, hash = HashToken(session), machine = machine ?? "", expires = sessionExpires.UtcDateTime }, transaction);
        return new SignInResult(account.Username, token, expires.UtcDateTime, session, sessionExpires.UtcDateTime, account.MustChangePassword, mustSetUpTwoFactor);
    }

    private static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    // ---- Your own account ----

    public async Task ChangePasswordAsync(string username, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        var hash = await connection.ExecuteScalarAsync<string?>("SELECT password_hash FROM accounts WHERE username = @username AND kind = 'local'", new { username })
            ?? throw Refused("not_an_openhold_account", "Your password is managed by Windows or your organisation, not by Openhold.");
        if (!Passwords.Verify(request.CurrentPassword, hash))
            throw Refused("wrong_password", "Your current password isn't right.");
        if (Passwords.Weakness(request.NewPassword, username) is { } weakness)
            throw OpenholdException.BadRequest("weak_password", weakness);
        if (Passwords.Verify(request.NewPassword, hash))
            throw OpenholdException.BadRequest("weak_password", "Choose a password you haven't used for this account.");
        await connection.ExecuteAsync("""
            UPDATE accounts SET password_hash = @hash, must_change_password = false WHERE username = @username
            """, new { username, hash = Passwords.Hash(request.NewPassword) });
        await AuditAsync(connection, null, "password_changed", username, new { });
    }

    /// <summary>Starts setting up two-factor sign-in: a new secret, not in force until a code from it is confirmed.</summary>
    public async Task<TwoFactorSetup> BeginTwoFactorAsync(string username, CancellationToken cancellationToken = default)
    {
        var secret = Totp.NewSecret();
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(
            "UPDATE accounts SET mfa_secret = @secret, mfa_enabled = false WHERE username = @username AND kind = 'local' AND NOT mfa_enabled",
            new { username, secret });
        if (updated == 0)
            throw Refused("two_factor_not_available", "Two-factor sign-in is already on, or this isn't an Openhold account (Windows and organisation sign-in use their own).");
        return new TwoFactorSetup(Totp.Base32(secret), Totp.SetupUri(secret, $"Openhold ({options.Value.SiteName})", username));
    }

    public async Task ConfirmTwoFactorAsync(string username, TwoFactorConfirmRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        var secret = await connection.ExecuteScalarAsync<byte[]?>(
            "SELECT mfa_secret FROM accounts WHERE username = @username AND kind = 'local' AND NOT mfa_enabled", new { username });
        if (secret is null || !Totp.Verify(secret, request.Code, clock.GetUtcNow()))
            throw OpenholdException.BadRequest("wrong_code", "That code isn't right. Check the app shows Openhold, then enter the current code.");
        await connection.ExecuteAsync("UPDATE accounts SET mfa_enabled = true WHERE username = @username", new { username });
        await AuditAsync(connection, null, "two_factor_enabled", username, new { });
    }

    // ---- Every request ----

    /// <summary>
    /// Records a Windows or organisation account the first time it's seen, and refuses a disabled account. Cached for
    /// half a minute, so disabling someone takes effect within that on every server.
    /// </summary>
    public async Task EnsureAllowedAsync(string username, string kind, string? displayName, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (_status.TryGetValue(username, out var cached) && now - cached.Checked < StatusCacheTime)
        {
            if (cached.Disabled)
                throw Refused("account_disabled", "This account has been disabled. Ask your Openhold administrator.", StatusCodes.Status403Forbidden);
            return;
        }

        // Read from this site's copy, so a site cut off from the authority still lets people in (and keeps out the
        // disabled). Recording the sign-in is a write to the authority; if it can't be reached, it's done next time.
        bool? disabled;
        await using (var local = await vaults.Default.Db.Local.OpenConnectionAsync(cancellationToken))
            disabled = await local.ExecuteScalarAsync<bool?>("SELECT disabled FROM accounts WHERE username = @username", new { username });
        try
        {
            if (disabled is null && kind != "local")
            {
                await using var connection = await Db.OpenConnectionAsync(cancellationToken);
                await connection.ExecuteAsync("""
                    INSERT INTO accounts (username, display_name, kind, last_sign_in_at) VALUES (@username, @displayName, @kind, now())
                    ON CONFLICT (username) DO NOTHING
                    """, new { username, displayName = displayName ?? username, kind });
                await AuditAsync(connection, null, "account_recorded", username, new { kind });
            }
            else if (cached.Checked == default)
            {
                await using var connection = await Db.OpenConnectionAsync(cancellationToken);
                await connection.ExecuteAsync("UPDATE accounts SET last_sign_in_at = now() WHERE username = @username AND kind <> 'local'", new { username });
            }
        }
        catch (NpgsqlException ex) when (ex.IsTransient && options.Value.Role != ServerRole.Authority)
        {
            _status[username] = (disabled == true, now - StatusCacheTime + TimeSpan.FromSeconds(5)); // check again soon
            if (disabled == true)
                throw Refused("account_disabled", "This account has been disabled. Ask your Openhold administrator.", StatusCodes.Status403Forbidden);
            return;
        }
        _status[username] = (disabled == true, now);
        if (disabled == true)
            throw Refused("account_disabled", "This account has been disabled. Ask your Openhold administrator.", StatusCodes.Status403Forbidden);
    }

    /// <summary>Forgets cached status, so a change to an account applies on this server straight away.</summary>
    private void Forget(string username) => _status.TryRemove(username, out _);

    // ---- Administration ----

    public async Task<IReadOnlyList<AccountInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<AccountRow>($"SELECT {AccountColumns} FROM accounts a ORDER BY a.username"))
            .Select(Info).ToList();
    }

    private AccountInfo Info(AccountRow a) => new(a.Username, a.DisplayName, a.Email, a.Kind, a.Disabled,
        a.LockedUntil is { } until && until > clock.GetUtcNow().UtcDateTime, a.MfaEnabled, a.MfaRequired, a.MustChangePassword,
        a.CreatedAt, a.LastSignInAt, a.Sessions);

    /// <summary>
    /// The first start of a server that has no accounts yet: each administrator in <c>Openhold:Administrators</c> with a
    /// plain username (not a Windows or organisation name) gets an Openhold account, so a server without Windows sign-in
    /// can be entered at all. The temporary password comes from <c>OPENHOLD_INITIAL_ADMIN_PASSWORD</c>, or is generated and
    /// written to a file in <paramref name="passwordFolder"/> that only the server's account can read. Either way it must
    /// be changed at first sign-in.
    /// </summary>
    public async Task CreateFirstAdministratorsAsync(string passwordFolder, ILogger logger, CancellationToken cancellationToken = default)
    {
        var names = options.Value.Administrators.Where(n => n.Trim().Length > 0 && !n.Contains('\\') && !n.Contains('@'))
            .Select(n => n.Trim().ToLowerInvariant()).Distinct().ToList();
        if (!Settings.LocalAccounts || names.Count == 0)
            return;
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM accounts)"))
            return;

        var given = Environment.GetEnvironmentVariable("OPENHOLD_INITIAL_ADMIN_PASSWORD");
        var passwords = names.ToDictionary(n => n, _ => string.IsNullOrEmpty(given) ? Passwords.Temporary() : given);
        foreach (var (username, password) in passwords)
        {
            await connection.ExecuteAsync("""
                INSERT INTO accounts (username, display_name, email, kind, password_hash, must_change_password, mfa_required, created_by)
                VALUES (@username, @username, NULL, 'local', @hash, true, false, 'first start')
                """, new { username, hash = Passwords.Hash(password) });
            await AuditAsync(connection, null, "account_created", username, new { by = "first start" });
        }

        if (!string.IsNullOrEmpty(given))
        {
            logger.LogWarning("Created the first administrator account(s) {Names} with the password from OPENHOLD_INITIAL_ADMIN_PASSWORD. It must be changed at first sign-in.", string.Join(", ", names));
            return;
        }
        Directory.CreateDirectory(passwordFolder);
        var file = Path.Combine(passwordFolder, "initial-admin-password.txt");
        await File.WriteAllLinesAsync(file, passwords.Select(p => $"{p.Key}: {p.Value}"), cancellationToken);
        SigningKeys.RestrictToCurrentAccount(file);
        logger.LogWarning("Created the first administrator account(s) {Names}. The temporary password is in {File}; it must be changed at first sign-in. Delete the file afterwards.", string.Join(", ", names), file);
    }

    public async Task<CreatedAccount> CreateAsync(Actor admin, CreateAccountRequest request, CancellationToken cancellationToken = default)
    {
        var username = ActorResolver.NormalizeUsername(request.Username)?.ToLowerInvariant();
        if (username is null || username.Length > 64 || username.Any(c => char.IsWhiteSpace(c) || c is '\\' or '/' or ':'))
            throw OpenholdException.BadRequest("invalid_username", "Use a username without spaces or slashes, e.g. jsmith or jsmith@partner.com.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw OpenholdException.BadRequest("invalid_name", "Give the person's name.");
        var temporary = Passwords.Temporary();
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO accounts (username, display_name, email, kind, password_hash, must_change_password, mfa_required, created_by)
                VALUES (@username, @displayName, @email, 'local', @hash, true, @mfa, @createdBy)
                """, new { username, displayName = request.DisplayName.Trim(), email = request.Email?.Trim(), hash = Passwords.Hash(temporary), mfa = request.RequireTwoFactor, createdBy = admin.Username });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw OpenholdException.Conflict("account_exists", $"There's already an account called {username}.");
        }
        await AuditAsync(connection, null, "account_created", username, new { by = admin.Username, twoFactorRequired = request.RequireTwoFactor });
        return new CreatedAccount(await GetAsync(connection, username), temporary);
    }

    public async Task<AccountInfo> UpdateAsync(Actor admin, string username, UpdateAccountRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Disabled == true && string.Equals(username, admin.Username, StringComparison.OrdinalIgnoreCase))
            throw OpenholdException.BadRequest("cant_disable_self", "You can't disable your own account.");
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var updated = await connection.ExecuteAsync("""
            UPDATE accounts SET display_name = COALESCE(@displayName, display_name), email = COALESCE(@email, email),
                disabled = COALESCE(@disabled, disabled), mfa_required = COALESCE(@mfaRequired, mfa_required)
            WHERE username = @username
            """, new { username, displayName = request.DisplayName?.Trim(), email = request.Email?.Trim(), disabled = request.Disabled, mfaRequired = request.TwoFactorRequired }, transaction);
        if (updated == 0)
            throw OpenholdException.NotFound($"There's no account called {username}.");
        if (request.Disabled == true)
            await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE username = @username", new { username }, transaction);
        await AuditAsync(connection, transaction, "account_changed", username, new { by = admin.Username, request.Disabled, request.TwoFactorRequired, request.DisplayName, request.Email });
        await transaction.CommitAsync(cancellationToken);
        Forget(username);
        return await GetAsync(connection, username);
    }

    public async Task<ResetPasswordResult> ResetPasswordAsync(Actor admin, string username, CancellationToken cancellationToken = default)
    {
        var temporary = Passwords.Temporary();
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        var updated = await connection.ExecuteAsync("""
            UPDATE accounts SET password_hash = @hash, must_change_password = true, failed_attempts = 0, locked_until = NULL
            WHERE username = @username AND kind = 'local'
            """, new { username, hash = Passwords.Hash(temporary) });
        if (updated == 0)
            throw OpenholdException.BadRequest("not_an_openhold_account", $"{username} doesn't sign in with an Openhold password.");
        await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE username = @username", new { username });
        await AuditAsync(connection, null, "password_reset", username, new { by = admin.Username });
        return new ResetPasswordResult(temporary);
    }

    /// <summary>Turns off two-factor sign-in so the person sets it up again, e.g. on a new phone.</summary>
    public async Task<AccountInfo> ResetTwoFactorAsync(Actor admin, string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("UPDATE accounts SET mfa_secret = NULL, mfa_enabled = false WHERE username = @username", new { username });
        await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE username = @username", new { username });
        await AuditAsync(connection, null, "two_factor_reset", username, new { by = admin.Username });
        return await GetAsync(connection, username);
    }

    public async Task<AccountInfo> UnlockAsync(Actor admin, string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("UPDATE accounts SET failed_attempts = 0, locked_until = NULL WHERE username = @username", new { username });
        await AuditAsync(connection, null, "account_unlocked", username, new { by = admin.Username });
        return await GetAsync(connection, username);
    }

    /// <summary>Signs the person out everywhere: their sessions end now and their access tokens within minutes.</summary>
    public async Task<AccountInfo> EndSessionsAsync(Actor admin, string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM refresh_tokens WHERE username = @username", new { username });
        await AuditAsync(connection, null, "sessions_ended", username, new { by = admin.Username });
        return await GetAsync(connection, username);
    }

    private async Task<AccountInfo> GetAsync(NpgsqlConnection connection, string username) =>
        Info(await connection.QuerySingleOrDefaultAsync<AccountRow>($"SELECT {AccountColumns} FROM accounts a WHERE a.username = @username", new { username })
             ?? throw OpenholdException.NotFound($"There's no account called {username}."));

    public async Task<AccountInfo?> FindAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await Db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>($"SELECT {AccountColumns} FROM accounts a WHERE a.username = @username", new { username });
        return row is null ? null : Info(row);
    }

    /// <summary>The identity vault's configuration, which decides who may manage accounts.</summary>
    public async Task<VaultConfiguration> IdentityConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await vaults.Default.Db.Local.OpenConnectionAsync(cancellationToken);
        return (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;
    }

    private static Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string action, string username, object data) =>
        connection.ExecuteAsync("""
            INSERT INTO audit_events (action, data) VALUES (@action, jsonb_build_object('username', @username::text) || @data::jsonb)
            """, new { action, username, data = System.Text.Json.JsonSerializer.Serialize(data) }, transaction);

    private static OpenholdException Refused(string code, string message, int status = StatusCodes.Status401Unauthorized) => new(status, code, message);
}
