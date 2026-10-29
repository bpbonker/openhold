using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Npgsql;
using Openhold.Core.Content;

namespace Openhold.Server.Data;

/// <summary>One vault as configured: its own database and its own file store.</summary>
public sealed class VaultConfig
{
    public string Name { get; set; } = "";

    /// <summary>The authority's database (writes).</summary>
    public string Primary { get; set; } = "";

    /// <summary>This site's replica (reads); defaults to <see cref="Primary"/>.</summary>
    public string? Local { get; set; }

    public string ChunkStorePath { get; set; } = "";
}

/// <summary>A vault this server hosts. Vaults share nothing: separate database, files, users and settings.</summary>
public sealed class VaultInstance(string name, OpenholdDb db, IChunkStore chunks)
{
    public string Name { get; } = name;

    public OpenholdDb Db { get; } = db;

    public IChunkStore Chunks { get; } = chunks;

    /// <summary>Usernames already known to this vault's database, so remote sites don't cross the WAN per request.</summary>
    public ConcurrentDictionary<string, Guid> UserIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chunks each person has been shown a manifest for, so chunk downloads can be limited to files they may read.</summary>
    public Browsing.ChunkGrants ChunkGrants { get; } = new();
}

/// <summary>
/// The vaults this server hosts, e.g. Production and a Sandbox/Training vault. Requests choose one with a
/// <c>/vaults/&lt;name&gt;/</c> path prefix; requests without it use the first (default) vault.
/// </summary>
public sealed partial class VaultRegistry
{
    public const string DefaultName = "Default";

    private const string RequestItemKey = "openhold.vault";

    private VaultRegistry(IReadOnlyList<VaultInstance> vaults) => Vaults = vaults;

    public IReadOnlyList<VaultInstance> Vaults { get; }

    public VaultInstance Default => Vaults[0];

    /// <summary>
    /// Reads <c>Openhold:Vaults</c>. Without it, a single vault named "Default" is built from
    /// <c>ConnectionStrings:Primary</c>/<c>Local</c> and <c>Openhold:ChunkStorePath</c>, so single-vault setups need no change.
    /// </summary>
    public static VaultRegistry FromConfiguration(IConfiguration configuration, OpenholdOptions options)
    {
        var configured = configuration.GetSection("Openhold:Vaults").Get<List<VaultConfig>>();
        if (configured is null or { Count: 0 })
        {
            configured =
            [
                new VaultConfig
                {
                    Name = DefaultName,
                    Primary = configuration.GetConnectionString("Primary") ?? throw new InvalidOperationException("ConnectionStrings:Primary (or Openhold:Vaults) is required."),
                    Local = configuration.GetConnectionString("Local"),
                    ChunkStorePath = options.ChunkStorePath,
                },
            ];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vaults = new List<VaultInstance>(configured.Count);
        foreach (var vault in configured)
        {
            if (!NamePattern().IsMatch(vault.Name))
                throw new InvalidOperationException($"Vault name '{vault.Name}' must be 1-40 letters, digits, '-' or '_'.");
            if (!names.Add(vault.Name))
                throw new InvalidOperationException($"Vault name '{vault.Name}' is configured twice.");
            if (string.IsNullOrWhiteSpace(vault.Primary) || string.IsNullOrWhiteSpace(vault.ChunkStorePath))
                throw new InvalidOperationException($"Vault '{vault.Name}' needs a Primary connection string and a ChunkStorePath.");

            var primary = NpgsqlDataSource.Create(vault.Primary);
            var local = string.IsNullOrWhiteSpace(vault.Local) || vault.Local == vault.Primary ? primary : NpgsqlDataSource.Create(vault.Local);
            vaults.Add(new VaultInstance(vault.Name, new OpenholdDb(primary, local), new FileSystemChunkStore(vault.ChunkStorePath)));
        }
        return new VaultRegistry(vaults);
    }

    public VaultInstance? Find(string name) => Vaults.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The vault a request addresses (set by <see cref="SelectVaultFromPath"/>), or the default vault.</summary>
    public VaultInstance For(HttpContext context) => context.Items[RequestItemKey] as VaultInstance ?? Default;

    /// <summary>
    /// Middleware: a request to <c>/vaults/&lt;name&gt;/rest</c> is served as <c>/rest</c> against that vault.
    /// Must run before routing.
    /// </summary>
    public async Task SelectVaultFromPath(HttpContext context, Func<Task> next)
    {
        // Trim then split in two: with RemoveEmptyEntries, Split's count ignores the leading empty entry.
        if (context.Request.Path.StartsWithSegments("/vaults", out var rest)
            && rest.Value?.TrimStart('/').Split('/', 2) is [{ Length: > 0 } name, var remainder])
        {
            var vault = Find(Uri.UnescapeDataString(name))
                ?? throw OpenholdException.NotFound($"There is no vault called '{Uri.UnescapeDataString(name)}' on this server.");
            context.Items[RequestItemKey] = vault;
            context.Request.PathBase = context.Request.PathBase.Add($"/vaults/{name}");
            context.Request.Path = "/" + remainder;
        }
        await next();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex NamePattern();
}
