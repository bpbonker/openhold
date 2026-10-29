using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Openhold.Client.Sdk.Workspace;

/// <summary>What the workspace last received from, or sent to, the vault for one local file.</summary>
public sealed class LocalFileRecord
{
    public Guid FileId { get; set; }
    public int Version { get; set; }
    public string ContentHash { get; set; } = "";
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

/// <summary>A check-in that was started but not confirmed; retried with the same changeset id so it commits once.</summary>
public sealed class PendingCheckIn
{
    public Guid ChangesetId { get; set; }
    public List<string> Paths { get; set; } = [];
    public DateTime StartedUtc { get; set; }

    /// <summary>Content hash per path as uploaded, so the changeset is only resent for exactly this content.</summary>
    public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persists workspace state in <c>&lt;root&gt;/.openhold/state.json</c>, shared by every Openhold program on the
/// PC (the AutoCAD plugin, the Inventor add-in, Openhold Explorer). Each change runs under a machine-wide
/// mutex and re-reads the file first, so programs never overwrite each other's updates. Writes go to a temp
/// file that replaces the original, so a crash never leaves a half-written state file.
/// </summary>
internal sealed class WorkspaceStateStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Mutex _mutex;
    private StateFile _cached = new();
    private DateTime _cachedWriteUtc = DateTime.MinValue;

    public WorkspaceStateStore(string root)
    {
        var directory = Path.Combine(root, ".openhold");
        // Workspaces from the pilot, when Openhold was called Strata, keep their state and backups.
        var pilot = Path.Combine(root, ".strata");
        if (!Directory.Exists(directory) && Directory.Exists(pilot))
            Directory.Move(pilot, directory);
        if (!Directory.Exists(directory))
        {
            var info = Directory.CreateDirectory(directory);
            info.Attributes |= FileAttributes.Hidden;
        }
        _path = Path.Combine(directory, "state.json");

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())))[..24];
        _mutex = new Mutex(false, $@"Local\Openhold-Workspace-{key}");
    }

    public LocalFileRecord? Get(string relativePath) =>
        Locked(state => state.Files.GetValueOrDefault(relativePath), save: false);

    public void Set(string relativePath, LocalFileRecord record) =>
        Locked(state => state.Files[relativePath] = record, save: true);

    public void Remove(string relativePath) =>
        Locked(state => state.Files.Remove(relativePath), save: true);

    public PendingCheckIn? FindPending(IReadOnlyCollection<string> relativePaths) =>
        Locked(state => state.Pending.FirstOrDefault(p =>
            p.Paths.Count == relativePaths.Count && p.Paths.All(x => relativePaths.Contains(x, StringComparer.OrdinalIgnoreCase))), save: false);

    /// <summary>Interrupted check-ins whose files are all among <paramref name="relativePaths"/>.</summary>
    public IReadOnlyList<PendingCheckIn> PendingWithin(IReadOnlyCollection<string> relativePaths) =>
        Locked(state => state.Pending.Where(p => p.Paths.All(x => relativePaths.Contains(x, StringComparer.OrdinalIgnoreCase))).ToList(), save: false);

    public void AddPending(PendingCheckIn pending) =>
        Locked(state => { state.Pending.Add(pending); return pending; }, save: true);

    public void RemovePending(Guid changesetId) =>
        Locked(state => state.Pending.RemoveAll(p => p.ChangesetId == changesetId), save: true);

    public void Dispose() => _mutex.Dispose();

    private T Locked<T>(Func<StateFile, T> action, bool save)
    {
        try
        {
            _mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Another Openhold program exited while holding the lock; the state file itself is always whole.
        }

        try
        {
            var state = Load();
            var result = action(state);
            if (save)
                Save(state);
            return result;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    /// <summary>Re-reads the file only when another program has changed it since we last looked.</summary>
    private StateFile Load()
    {
        if (!File.Exists(_path))
            return _cached;
        var writeUtc = File.GetLastWriteTimeUtc(_path);
        if (writeUtc == _cachedWriteUtc)
            return _cached;

        var loaded = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path)) ?? new StateFile();
        loaded.Files = new Dictionary<string, LocalFileRecord>(loaded.Files, StringComparer.OrdinalIgnoreCase);
        _cached = loaded;
        _cachedWriteUtc = writeUtc;
        return _cached;
    }

    private void Save(StateFile state)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, _path, overwrite: true);
        _cached = state;
        _cachedWriteUtc = File.GetLastWriteTimeUtc(_path);
    }

    private sealed class StateFile
    {
        public Dictionary<string, LocalFileRecord> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PendingCheckIn> Pending { get; set; } = [];
    }
}
