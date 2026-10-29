using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Openhold.Core.Contracts;

namespace Openhold.Client.Sdk;

/// <summary>A signed-in Openhold or organisation account session, as saved on this PC.</summary>
public sealed record SavedSession(string Username, string AccessToken, DateTime AccessTokenExpires, string SessionToken, DateTime SessionExpires);

/// <summary>
/// Keeps the current sign-in for each server, shared by every Openhold program on the PC (Explorer, AutoCAD, Inventor)
/// so people sign in once. Encrypted for the Windows user (DPAPI): another account on the PC, or a copied file,
/// can't use it.
/// </summary>
public static class SessionStore
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("Openhold sign-in session");

    private static string PathFor(Uri server)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.GetLeftPart(UriPartial.Authority).ToLowerInvariant())))[..24];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Openhold", "sessions", key + ".bin");
    }

    public static SavedSession? Load(Uri server)
    {
        var path = PathFor(server);
        try
        {
            if (!File.Exists(path))
                return null;
            var session = JsonSerializer.Deserialize<SavedSession>(Unprotect(File.ReadAllBytes(path)));
            return session is not null && session.SessionExpires > DateTime.UtcNow ? session : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            return null; // unreadable or from another Windows account: sign in again
        }
    }

    public static void Save(Uri server, SavedSession session)
    {
        var path = PathFor(server);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(JsonSerializer.SerializeToUtf8Bytes(session)));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(Uri server)
    {
        var path = PathFor(server);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static SavedSession From(SignInResult result) =>
        new(result.Username, result.AccessToken, result.AccessTokenExpires, result.SessionToken, result.SessionExpires);

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, Purpose, DataProtectionScope.CurrentUser) : data;

    private static byte[] Unprotect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, Purpose, DataProtectionScope.CurrentUser) : data;
}

/// <summary>Raised when the saved sign-in has ended (expired, signed out, or the account was disabled) and the person must sign in again.</summary>
public sealed class SignInRequiredException(string message) : Exception(message);

/// <summary>
/// Puts the saved session's access token on every request and renews it shortly before it expires, saving the new
/// tokens so the other Openhold programs on the PC pick them up. Renewal is serialised, so a burst of requests renews once.
/// </summary>
public sealed class SessionHandler(Uri server) : DelegatingHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _renewing = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var session = await CurrentAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<SavedSession> CurrentAsync(CancellationToken cancellationToken)
    {
        var session = SessionStore.Load(server) ?? throw new SignInRequiredException("Sign in to Openhold to carry on.");
        if (session.AccessTokenExpires > DateTime.UtcNow.AddMinutes(1))
            return session;

        await _renewing.WaitAsync(cancellationToken);
        try
        {
            // Another request (or program) may have renewed it while this one waited.
            session = SessionStore.Load(server) ?? throw new SignInRequiredException("Sign in to Openhold to carry on.");
            if (session.AccessTokenExpires > DateTime.UtcNow.AddMinutes(1))
                return session;

            using var refresh = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/auth/refresh"))
            {
                Content = JsonContent.Create(new RefreshRequest(session.SessionToken, Environment.MachineName), options: Json),
            };
            using var response = await base.SendAsync(refresh, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                SessionStore.Delete(server);
                throw new SignInRequiredException("Your Openhold sign-in has ended. Sign in again.");
            }
            response.EnsureSuccessStatusCode();
            var renewed = SessionStore.From((await response.Content.ReadFromJsonAsync<SignInResult>(Json, cancellationToken))!);
            SessionStore.Save(server, renewed);
            return renewed;
        }
        finally
        {
            _renewing.Release();
        }
    }
}
