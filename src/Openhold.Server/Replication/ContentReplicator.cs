using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Content;
using Openhold.Core.Contracts;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Replication;

/// <summary>
/// Makes HTTP connections to other sites' servers. Replaced in tests to reach in-process servers.
/// </summary>
public class PeerHandlerFactory
{
    public virtual HttpMessageHandler Create(Uri baseUrl, bool useWindowsSignIn) =>
        new SocketsHttpHandler
        {
            Credentials = useWindowsSignIn ? CredentialCache.DefaultNetworkCredentials : null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.None, // chunks are already compressed
        };
}

/// <summary>
/// Copies file contents between sites, so everyone gets files from their own office's server. Each server looks for
/// contents (file versions and published PDFs) recorded in the database that its own chunk store lacks, newest
/// versions first, and fetches the missing chunks from whichever peers have them; every chunk is checked against its
/// hash, so no peer has to be trusted. A content counts as arrived only once all its chunks are here.
/// It also fetches on demand: a Get for something that hasn't arrived yet is served by fetching it first.
/// </summary>
public sealed class ContentReplicator(
    VaultRegistry vaults, IOptions<OpenholdOptions> options, PeerHandlerFactory handlers, ILogger<ContentReplicator> logger) : BackgroundService
{
    private const int FetchBatch = 512;

    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new(StringComparer.OrdinalIgnoreCase);

    // Contents that failed recently, to wait before retrying them: content hash (hex) -> retry after.
    private readonly ConcurrentDictionary<string, DateTime> _backOff = new();

    private string SiteId => options.Value.SiteId;

    /// <summary>Registers this server with the authority so the other sites can find it.</summary>
    public async Task RegisterAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("""
            INSERT INTO sites (id, name, base_url, is_authority, last_seen) VALUES (@id, @name, @url, @authority, now())
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, base_url = COALESCE(EXCLUDED.base_url, sites.base_url),
                is_authority = sites.is_authority OR EXCLUDED.is_authority, last_seen = now()
            """, new { id = settings.SiteId, name = settings.SiteName, url = settings.PublicUrl?.TrimEnd('/'), authority = settings.Role == ServerRole.Authority });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registered = new Dictionary<string, DateTime>();
        using var timer = new PeriodicTimer(options.Value.Replication.Interval);
        do
        {
            foreach (var vault in vaults.Vaults)
            {
                try
                {
                    if (registered.GetValueOrDefault(vault.Name) < DateTime.UtcNow.AddMinutes(-1))
                    {
                        await RegisterAsync(vault, stoppingToken); // at start, then once a minute as a heartbeat
                        registered[vault.Name] = DateTime.UtcNow;
                    }
                    await CatchUpAsync(vault, stoppingToken);
                }
                // A peer that stalls past the HTTP timeout throws TaskCanceledException: retry next tick, don't stop the host.
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Replication for vault {Vault} failed; will retry: {Message}", vault.Name, ex.Message);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Fetches contents this site lacks, a batch at a time. Returns how many arrived.</summary>
    public async Task<int> CatchUpAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        var arrived = 0;
        while (true)
        {
            var peers = await PeersAsync(vault, cancellationToken);
            if (peers.Count == 0)
                return arrived;
            await using var connection = await vault.Db.Local.OpenConnectionAsync(cancellationToken);
            var due = (await connection.QueryAsync<Pending>("""
                SELECT c.content_hash AS Hash, c.manifest AS Manifest,
                       (SELECT string_agg(site_id, ',') FROM content_presence p WHERE p.content_hash = c.content_hash) AS Sources
                FROM (
                    SELECT DISTINCT ON (v.content_hash) v.content_hash, v.manifest, (v.version = f.latest_version) AS latest, v.created_at
                    FROM file_versions v JOIN files f ON f.id = v.file_id
                    WHERE NOT EXISTS (SELECT 1 FROM content_presence p WHERE p.content_hash = v.content_hash AND p.site_id = @site)
                    UNION ALL
                    SELECT DISTINCT ON (r.content_hash) r.content_hash, r.manifest, true, r.created_at
                    FROM renditions r
                    WHERE NOT EXISTS (SELECT 1 FROM content_presence p WHERE p.content_hash = r.content_hash AND p.site_id = @site)
                ) c
                ORDER BY c.latest DESC, c.created_at DESC
                LIMIT 200
                """, new { site = SiteId })).ToList();
            var now = DateTime.UtcNow;
            due = due.Where(d => !_backOff.TryGetValue(Convert.ToHexString(d.Hash), out var until) || until < now).ToList();
            if (due.Count == 0)
                return arrived;

            var done = new ConcurrentBag<byte[]>();
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.Replication.Parallel), CancellationToken = cancellationToken },
                async (pending, ct) =>
                {
                    var key = Convert.ToHexString(pending.Hash);
                    try
                    {
                        var manifest = ManifestCodec.Decode(pending.Manifest);
                        var sources = peers.OrderByDescending(p => (pending.Sources ?? "").Split(',').Contains(p.SiteId)).ToList();
                        if (await FetchAsync(vault, manifest.DistinctHashes().ToList(), sources, ct) == 0)
                            done.Add(pending.Hash);
                        else
                            _backOff[key] = DateTime.UtcNow.AddMinutes(1);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OpenholdException or FormatException or ChunkIntegrityException
                                               || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                    {
                        _backOff[key] = DateTime.UtcNow.AddMinutes(2);
                        logger.LogWarning("Couldn't fetch content {Hash} for vault {Vault}: {Message}", key[..12], vault.Name, ex.Message);
                    }
                });
            if (done.IsEmpty)
                return arrived;
            await RecordAsync(vault, done.ToArray(), cancellationToken);
            arrived += done.Count;
        }
    }

    /// <summary>
    /// Fetches whichever of these chunks this site lacks from its peers (those named first are asked first), storing
    /// each after checking its hash. Returns how many are still missing.
    /// </summary>
    public async Task<int> FetchAsync(VaultInstance vault, IReadOnlyList<ContentHash> chunks, IReadOnlyList<PeerConnection>? peers, CancellationToken cancellationToken)
    {
        var missing = (await vault.Chunks.FindMissingAsync(chunks, cancellationToken)).ToList();
        foreach (var peer in peers ?? await PeersAsync(vault, cancellationToken))
        {
            if (missing.Count == 0)
                break;
            try
            {
                foreach (var batch in missing.Chunk(FetchBatch).ToList())
                {
                    using var http = await peer.ClientAsync(vault, cancellationToken);
                    var lacking = await (await http.PostAsJsonAsync("api/chunks/missing", batch.Select(h => h.ToString()), cancellationToken))
                        .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<List<string>>(cancellationToken) ?? [];
                    var available = batch.Where(h => !lacking.Contains(h.ToString())).ToList();
                    if (available.Count == 0)
                        continue;
                    using var response = await http.PostAsJsonAsync("api/chunks/fetch", available.Select(h => h.ToString()), cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await foreach (var (hash, data) in ChunkFrames.ReadAsync(body, FastCdc.DefaultMaxSize, cancellationToken))
                        await vault.Chunks.PutAsync(hash, data, cancellationToken);
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogInformation("Site {Site} couldn't supply chunks: {Message}", peer.SiteId, ex.Message);
            }
            missing = (await vault.Chunks.FindMissingAsync(missing, cancellationToken)).ToList();
        }
        return missing.Count;
    }

    /// <summary>Records that this site now holds these contents (a write to the authority).</summary>
    public async Task RecordAsync(VaultInstance vault, IReadOnlyList<byte[]> contentHashes, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("""
            INSERT INTO content_presence (content_hash, site_id) SELECT unnest(@hashes), @site
            ON CONFLICT DO NOTHING
            """, new { hashes = contentHashes.ToArray(), site = SiteId });
    }

    /// <summary>The other sites' servers that can be reached, the authority first.</summary>
    public async Task<IReadOnlyList<PeerConnection>> PeersAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Local.OpenConnectionAsync(cancellationToken);
        var sites = await connection.QueryAsync<(string Id, string Url)>("""
            SELECT id, base_url FROM sites WHERE id <> @site AND base_url IS NOT NULL ORDER BY is_authority DESC, id
            """, new { site = SiteId });
        return sites.Select(s => _peers.GetOrAdd($"{s.Id}|{s.Url}", _ => new PeerConnection(s.Id, new Uri(s.Url.TrimEnd('/') + "/"), handlers, options.Value))).ToList();
    }

    /// <summary>Every site, and how many contents each still lacks: the replication health view.</summary>
    public static async Task<IReadOnlyList<SiteStatus>> StatusAsync(NpgsqlConnection connection, string localSite)
    {
        var sites = await connection.QueryAsync<(string Id, string Name, string? Url, bool IsAuthority, DateTime? LastSeen, int Missing)>("""
            SELECT s.id, s.name, s.base_url, s.is_authority, s.last_seen,
                   (SELECT count(*)::int FROM (SELECT content_hash FROM file_versions UNION SELECT content_hash FROM renditions) c
                    WHERE NOT EXISTS (SELECT 1 FROM content_presence p WHERE p.content_hash = c.content_hash AND p.site_id = s.id))
            FROM sites s ORDER BY s.is_authority DESC, s.id
            """);
        return sites.Select(s => new SiteStatus(s.Id, s.Name, s.Url, s.IsAuthority, s.Id == localSite, s.LastSeen, s.Missing)).ToList();
    }

    private sealed record Pending(byte[] Hash, byte[] Manifest, string? Sources);
}

/// <summary>A signed-in connection to one peer site's server.</summary>
public sealed class PeerConnection(string siteId, Uri baseUrl, PeerHandlerFactory handlers, OpenholdOptions options)
{
    private readonly SemaphoreSlim _signIn = new(1, 1);
    private readonly HttpMessageHandler _handler = handlers.Create(baseUrl, useWindowsSignIn: options.AuthMode != "Dev" && string.IsNullOrEmpty(options.Replication.Username));
    private (string Access, DateTime Expires, string Session)? _token;

    public string SiteId { get; } = siteId;

    /// <summary>A client for one vault on the peer (the same vault name as here), signed in as this server.</summary>
    public async Task<HttpClient> ClientAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        var http = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl, $"vaults/{Uri.EscapeDataString(vault.Name)}/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        var replication = options.Replication;
        if (options.AuthMode == "Dev")
            http.DefaultRequestHeaders.Add(ActorResolver.UserHeader, replication.Username ?? "openhold-replication");
        else if (!string.IsNullOrEmpty(replication.Username))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken));
        return http;
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _signIn.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } current && current.Expires > DateTime.UtcNow.AddMinutes(1))
                return current.Access;
            using var http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = baseUrl };
            // Renew the existing session rather than start a new one every quarter hour; sign in again if it has ended.
            using var response = _token is { } previous
                ? await http.PostAsJsonAsync("api/auth/refresh", new RefreshRequest(previous.Session), cancellationToken)
                : await SignInAsync(http, cancellationToken);
            using var answer = response.IsSuccessStatusCode || _token is null ? response : await SignInAsync(http, cancellationToken);
            answer.EnsureSuccessStatusCode();
            var result = (await answer.Content.ReadFromJsonAsync<SignInResult>(cancellationToken))!;
            if (result.MustChangePassword || result.MustSetUpTwoFactor)
                throw new HttpRequestException($"The replication account {result.Username} must have its password changed (and no two-factor) before servers can use it.");
            _token = (result.AccessToken, result.AccessTokenExpires.ToUniversalTime(), result.SessionToken);
            return result.AccessToken;
        }
        finally
        {
            _signIn.Release();
        }
    }

    private Task<HttpResponseMessage> SignInAsync(HttpClient http, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync("api/auth/sign-in", new SignInRequest(options.Replication.Username!, options.Replication.Password ?? "", Machine: Environment.MachineName), cancellationToken);
}
