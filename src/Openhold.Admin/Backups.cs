using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Npgsql;

namespace Openhold.Admin;

/// <summary>What one backup run contains (kept in plain text beside it; no secrets).</summary>
public sealed record BackupManifest(
    string Run, DateTime StartedUtc, DateTime FinishedUtc, string Server, string Machine,
    IReadOnlyList<BackupManifest.VaultEntry> Vaults, string Settings, IReadOnlyList<string>? Keys = null)
{
    public sealed record VaultEntry(string Name, string Dump, long DumpBytes, int ChunksTotal, int ChunksAdded);
}

/// <summary>
/// Backups of an Openhold server, all encrypted:
/// - each vault's database (a full pg_dump per run);
/// - each vault's file store, incrementally: file contents are stored as immutable chunks, so a chunk is copied once
///   and never again;
/// - the server's settings, its token-signing key and its logs, as one archive per run.
/// Runs older than the retention period are removed, always keeping the most recent ones.
/// </summary>
public sealed class Backups(BackupCrypto crypto, PostgresTools postgres, TextWriter log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public BackupManifest Backup(ServerLayout server, string destination, int keepDays, int keepRuns)
    {
        var started = DateTime.UtcNow;
        var run = started.ToString("yyyyMMdd-HHmmss");
        var runFolder = Path.Combine(destination, "runs", run);
        Directory.CreateDirectory(runFolder);
        var work = Path.Combine(Path.GetTempPath(), "openhold-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // A backup that silently leaves out the keys or the files can't bring the server back, so anything that
            // should be there but can't be read (missing, or this account isn't allowed) fails the run.
            var keyFolder = Path.GetDirectoryName(server.SigningKeyPath)!;
            var keys = new List<string>();
            try
            {
                var present = Directory.GetFiles(keyFolder).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!present.Contains(Path.GetFileName(server.SigningKeyPath)))
                    throw new BackupException($"The token-signing key {server.SigningKeyPath} isn't there. Check --server, or start the server once to create it.");
                keys.Add(Path.GetFileName(server.SigningKeyPath));
                // The key for secrets entered in Openhold (e.g. ERP API keys): without it a restored server can't read
                // them. It only exists once a secret has been saved.
                if (present.Contains("settings-secrets.key"))
                    keys.Add("settings-secrets.key");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                throw new BackupException($"Can't read the server's keys in {keyFolder}: {ex.Message} Run the backup as the server's service account.");
            }

            var vaults = new List<BackupManifest.VaultEntry>();
            foreach (var vault in server.Vaults)
            {
                var watch = Stopwatch.StartNew();
                var dump = Path.Combine(work, vault.Name + ".dump");
                postgres.Dump(vault.ConnectionString, dump);
                var dumpBytes = new FileInfo(dump).Length;
                crypto.EncryptFile(dump, Path.Combine(runFolder, vault.Name + ".dump.enc"));
                File.Delete(dump);
                var (total, added) = BackUpChunks(vault, Path.Combine(destination, "chunks", vault.Name));
                log.WriteLine($"  {vault.Name}: database {dumpBytes / 1024} KB, files {total} chunks ({added} new) in {watch.Elapsed.TotalSeconds:0.0}s");
                vaults.Add(new(vault.Name, vault.Name + ".dump.enc", dumpBytes, total, added));
            }

            // Settings, the token-signing key and the logs: what's needed to bring the server itself back.
            var archive = Path.Combine(work, "settings.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (var file in server.SettingsFiles)
                    zip.CreateEntryFromFile(file, "settings/" + Path.GetFileName(file));
                foreach (var key in keys)
                    zip.CreateEntryFromFile(Path.Combine(keyFolder, key), "keys/" + key);
                if (Directory.Exists(server.LogFolder))
                {
                    foreach (var file in Directory.EnumerateFiles(server.LogFolder))
                    {
                        // A log being written is shared for reading; copy what's there now.
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var target = zip.CreateEntry("logs/" + Path.GetFileName(file)).Open();
                        source.CopyTo(target);
                    }
                }
            }
            crypto.EncryptFile(archive, Path.Combine(runFolder, "settings.zip.enc"));

            var manifest = new BackupManifest(run, started, DateTime.UtcNow, server.Folder, Environment.MachineName, vaults, "settings.zip.enc", keys);
            File.WriteAllText(Path.Combine(runFolder, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            WriteLast(destination, new LastBackup(manifest.Run, manifest.FinishedUtc, true, null));
            Prune(destination, keepDays, keepRuns);
            return manifest;
        }
        catch (Exception ex)
        {
            // The run folder has no manifest, so restore and verify never pick it; last-backup.json says why.
            WriteLast(destination, new LastBackup(run, DateTime.UtcNow, false, ex.Message));
            throw;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>The outcome of the latest run, for monitoring: written whether it succeeded or not.</summary>
    public sealed record LastBackup(string Run, DateTime FinishedUtc, bool Succeeded, string? Error);

    private static void WriteLast(string destination, LastBackup last)
    {
        try
        {
            File.WriteAllText(Path.Combine(destination, "last-backup.json"), JsonSerializer.Serialize(last, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The destination itself is unreachable; the log and the exit code still report it.
        }
    }

    /// <summary>Copies chunks the backup doesn't have yet. Chunks never change, so existing ones are skipped.</summary>
    private (int Total, int Added) BackUpChunks(VaultLocation vault, string target)
    {
        if (!Directory.Exists(vault.ChunkStorePath))
        {
            // Missing and unreadable look the same here; it only matters if the vault has files.
            if (HasFiles(vault))
                throw new BackupException($"Vault {vault.Name} has files but its file store {vault.ChunkStorePath} can't be found or read. Run the backup as the server's service account.");
            return (0, 0);
        }
        List<string> chunks;
        try
        {
            chunks = Directory.EnumerateFiles(vault.ChunkStorePath, "*", SearchOption.AllDirectories).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new BackupException($"Can't read vault {vault.Name}'s file store: {ex.Message} Run the backup as the server's service account.");
        }
        int total = 0, added = 0;
        foreach (var chunk in chunks)
        {
            var name = Path.GetFileName(chunk);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.Length < 3)
                continue;
            total++;
            var copy = Path.Combine(target, name[..2], name + ".enc");
            if (File.Exists(copy))
                continue;
            crypto.EncryptFile(chunk, copy);
            added++;
        }
        return (total, added);
    }

    private static bool HasFiles(VaultLocation vault)
    {
        using var connection = new NpgsqlConnection(vault.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM file_versions)", connection);
        return (bool)command.ExecuteScalar()!;
    }

    private void Prune(string destination, int keepDays, int keepRuns)
    {
        // Only complete runs (with a manifest) count towards the runs kept; a failed run never protects anything.
        var runs = Directory.GetDirectories(Path.Combine(destination, "runs")).OrderByDescending(d => d).ToList();
        var kept = runs.Where(r => File.Exists(Path.Combine(r, "manifest.json"))).Take(keepRuns).ToHashSet();
        foreach (var old in runs.Where(r => !kept.Contains(r) && Directory.GetCreationTimeUtc(r) < DateTime.UtcNow.AddDays(-keepDays)))
        {
            Directory.Delete(old, recursive: true);
            log.WriteLine($"  Removed run {Path.GetFileName(old)} (older than {keepDays} days)");
        }
    }

    public static string FindRun(string destination, string run)
    {
        var runs = Path.Combine(destination, "runs");
        var found = run.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? Directory.Exists(runs) ? Directory.GetDirectories(runs).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderByDescending(d => d).FirstOrDefault() : null
            : Path.Combine(runs, run);
        return found is not null && File.Exists(Path.Combine(found, "manifest.json"))
            ? found
            : throw new BackupException($"There's no complete backup run '{run}' in {destination}.");
    }

    /// <summary>
    /// Restores one vault into an empty database and a file-store folder, and optionally the server's settings, key and
    /// logs into a folder. Refuses a database that already has tables, so a restore can't overwrite live data.
    /// </summary>
    public void Restore(string destination, string run, string vault, string connectionString, string chunkStore, string? settingsFolder)
    {
        var runFolder = FindRun(destination, run);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(runFolder, "manifest.json")))!;
        var entry = manifest.Vaults.FirstOrDefault(v => string.Equals(v.Name, vault, StringComparison.OrdinalIgnoreCase))
            ?? throw new BackupException($"Run {manifest.Run} has no vault '{vault}' (it has {string.Join(", ", manifest.Vaults.Select(v => v.Name))}).");

        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
            if ((long)command.ExecuteScalar()! > 0)
                throw new BackupException("The target database isn't empty. Restore into a new, empty database, then point the server at it.");
        }

        var work = Path.Combine(Path.GetTempPath(), "openhold-restore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var dump = Path.Combine(work, "vault.dump");
            crypto.DecryptFile(Path.Combine(runFolder, entry.Dump), dump);
            postgres.Restore(connectionString, dump);
            log.WriteLine($"  Database restored from run {manifest.Run}.");

            var restored = 0;
            var source = Path.Combine(destination, "chunks", entry.Name);
            foreach (var copy in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*.enc", SearchOption.AllDirectories) : [])
            {
                var name = Path.GetFileNameWithoutExtension(copy);
                var target = Path.Combine(chunkStore, name[..2], name);
                if (File.Exists(target))
                    continue;
                crypto.DecryptFile(copy, target);
                restored++;
            }
            log.WriteLine($"  File store: {restored} chunks restored to {chunkStore}.");

            if (settingsFolder is not null)
            {
                var archive = Path.Combine(work, "settings.zip");
                crypto.DecryptFile(Path.Combine(runFolder, manifest.Settings), archive);
                ZipFile.ExtractToDirectory(archive, settingsFolder, overwriteFiles: true);
                log.WriteLine($"  Settings, signing key and logs restored to {settingsFolder}.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Decrypts and checks a run's files and the file-store copies (all, or a sample), changing nothing.</summary>
    public (int Files, int Chunks) Verify(string destination, string run, bool allChunks)
    {
        var runFolder = FindRun(destination, run);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(runFolder, "manifest.json")))!;
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(runFolder, "*.enc"))
        {
            if (Path.GetFileName(file) != manifest.Settings)
            {
                crypto.Decrypt(file, Stream.Null);
            }
            else
            {
                // The keys are what make a restored server usable: check they're really in the archive.
                using var settings = new MemoryStream();
                crypto.Decrypt(file, settings);
                settings.Position = 0;
                using var zip = new ZipArchive(settings, ZipArchiveMode.Read);
                var missing = (manifest.Keys ?? []).Where(k => zip.GetEntry("keys/" + k) is null).ToList();
                if (missing.Count > 0)
                    throw new BackupException($"Run {manifest.Run} is missing {string.Join(", ", missing)} from its settings archive.");
            }
            files++;
        }
        foreach (var vault in manifest.Vaults.Where(v => v.ChunksTotal > 0 && !Directory.Exists(Path.Combine(destination, "chunks", v.Name))))
            throw new BackupException($"Run {manifest.Run} says vault {vault.Name} has {vault.ChunksTotal} chunks, but the backup has none for it.");
        var chunks = 0;
        var chunkRoot = Path.Combine(destination, "chunks");
        if (Directory.Exists(chunkRoot))
        {
            var all = Directory.EnumerateFiles(chunkRoot, "*.enc", SearchOption.AllDirectories);
            foreach (var chunk in allChunks ? all : all.Where((_, i) => i % 50 == 0))
            {
                crypto.Decrypt(chunk, Stream.Null);
                chunks++;
            }
        }
        return (files, chunks);
    }
}
