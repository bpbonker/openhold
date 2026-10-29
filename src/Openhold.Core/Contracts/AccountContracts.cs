namespace Openhold.Core.Contracts;

// Sign-in and account management, shared by the server, Openhold Explorer and the CAD add-ins.

/// <summary>How a server lets people sign in, asked before signing in.</summary>
public sealed record SignInOptions(bool Windows, bool LocalAccounts, IReadOnlyList<SignInProvider> Providers);

/// <summary>An organisation identity provider a client can send people to (OpenID Connect, with PKCE).</summary>
public sealed record SignInProvider(string Name, string Authority, string ClientId, IReadOnlyList<string> Scopes);

/// <summary>An Openhold-account sign-in. <see cref="Code"/> is the authenticator app's six digits when two-factor is on.</summary>
public sealed record SignInRequest(string Username, string Password, string? Code = null, string? Machine = null);

/// <summary>
/// A successful sign-in. The access token goes with every request; the session token gets a new access token when it
/// expires. If <see cref="MustChangePassword"/> or <see cref="MustSetUpTwoFactor"/> is set, nothing else is allowed
/// until it's done.
/// </summary>
public sealed record SignInResult(
    string Username, string AccessToken, DateTime AccessTokenExpires, string SessionToken, DateTime SessionExpires,
    bool MustChangePassword, bool MustSetUpTwoFactor);

public sealed record RefreshRequest(string SessionToken, string? Machine = null);

public sealed record SignOutRequest(string SessionToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>A new two-factor secret: shown once as a QR code (<see cref="SetupUri"/>) or typed in (<see cref="Secret"/>).</summary>
public sealed record TwoFactorSetup(string Secret, string SetupUri);

public sealed record TwoFactorConfirmRequest(string Code);

/// <summary>An account, as administrators see it.</summary>
public sealed record AccountInfo(
    string Username, string DisplayName, string? Email, string Kind, bool Disabled, bool Locked, bool TwoFactorEnabled,
    bool TwoFactorRequired, bool MustChangePassword, DateTime CreatedAt, DateTime? LastSignInAt, int Sessions);

public sealed record CreateAccountRequest(string Username, string DisplayName, string? Email = null, bool RequireTwoFactor = true);

/// <summary>A new Openhold account and its one-time temporary password, which must be changed at first sign-in.</summary>
public sealed record CreatedAccount(AccountInfo Account, string TemporaryPassword);

/// <summary>Changes to an account; null leaves a value as it is.</summary>
public sealed record UpdateAccountRequest(string? DisplayName = null, string? Email = null, bool? Disabled = null, bool? TwoFactorRequired = null);

/// <summary>A reset password, given to the person once.</summary>
public sealed record ResetPasswordResult(string TemporaryPassword);

/// <summary>Who the caller is and what they may do, for showing the right menus.</summary>
public sealed record WhoAmI(string Username, string DisplayName, string Kind, IReadOnlyList<string> Privileges, bool TwoFactorEnabled);

/// <summary>One audit-log entry: who did what, when, from which site, to which file.</summary>
public sealed record AuditEntry(long Id, DateTime At, string? Username, string? Site, string Action, string? FileName, string? FolderPath, string Data);

/// <summary>
/// One person in an access review: how they sign in, whether they're active, their two-factor status, last sign-in,
/// and what their groups let them do. Reviewed periodically to confirm everyone still needs their access.
/// </summary>
public sealed record AccessReviewEntry(
    string Username, string DisplayName, string Kind, bool Disabled, bool TwoFactorEnabled, DateTime? LastSignInAt,
    IReadOnlyList<string> Groups, IReadOnlyList<string> Privileges);
