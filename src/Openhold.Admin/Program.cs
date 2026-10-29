using Openhold.Admin;

// openhold-admin: backs up, restores and verifies an Openhold server. The backup passphrase is read from the
// OPENHOLD_BACKUP_PASSPHRASE environment variable or a file (--passphrase-file), never from the command line.
//
//   openhold-admin backup  --server <folder> --to <destination> [--environment Production] [--keep-days 30] [--keep-runs 7]
//   openhold-admin restore --from <destination> [--run latest] --vault <name> --connection "<connection string>"
//                        --chunks <file-store folder> [--settings-to <folder>]
//   openhold-admin verify  --from <destination> [--run latest] [--all-chunks]
//
//   PostgreSQL tools: --pg-bin <folder> (default: the installed PostgreSQL, or PATH), or --pg-docker <container>.
try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        return Usage();
    var command = args[0];
    string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    string Required(string name) => Option(name) ?? throw new BackupException($"{command} needs {name}.");
    bool Flag(string name) => args.Contains(name);

    var passphrase = Option("--passphrase-file") is { } file ? File.ReadAllText(file).Trim()
        : Environment.GetEnvironmentVariable("OPENHOLD_BACKUP_PASSPHRASE")
          ?? throw new BackupException("Set OPENHOLD_BACKUP_PASSPHRASE or give --passphrase-file.");
    // The database password, for a scheduled task that doesn't run in the server's environment: a file only the
    // backup account can read. Without it, PGPASSWORD, pgpass.conf or Windows sign-in apply as usual.
    if (Option("--db-password-file") is { } dbPasswordFile)
        Environment.SetEnvironmentVariable("PGPASSWORD", File.ReadAllText(dbPasswordFile).Trim());
    var postgres = new PostgresTools(Option("--pg-bin"), Option("--pg-docker"));

    switch (command)
    {
        case "backup":
        {
            var destination = Required("--to");
            // Every run is logged beside the backups, as a scheduled task's console output goes nowhere.
            Directory.CreateDirectory(Path.Combine(destination, "logs"));
            using var logFile = new StreamWriter(Path.Combine(destination, "logs", $"backup-{DateTime.UtcNow:yyyyMMdd}.log"), append: true) { AutoFlush = true };
            var log = new Tee(Console.Out, logFile);
            log.WriteLine($"{DateTime.UtcNow:u} backup on {Environment.MachineName} as {Environment.UserName}");
            try
            {
                var server = ServerLayout.Read(Required("--server"), Option("--environment") ?? "Production");
                log.WriteLine($"Backing up {server.Folder} ({server.Vaults.Count} vault(s)) to {destination}");
                var manifest = new Backups(BackupCrypto.Open(destination, passphrase), postgres, log)
                    .Backup(server, destination, int.Parse(Option("--keep-days") ?? "30"), int.Parse(Option("--keep-runs") ?? "7"));
                log.WriteLine($"Backup {manifest.Run} complete in {(manifest.FinishedUtc - manifest.StartedUtc).TotalSeconds:0}s.");
                return 0;
            }
            catch (Exception ex)
            {
                log.WriteLine($"FAILED: {ex.Message}");
                throw;
            }
        }
        case "restore":
        {
            var destination = Required("--from");
            new Backups(BackupCrypto.Open(destination, passphrase), postgres, Console.Out)
                .Restore(destination, Option("--run") ?? "latest", Required("--vault"), Required("--connection"), Required("--chunks"), Option("--settings-to"));
            Console.WriteLine("Restore complete. Point the server's settings at the restored database and file store, then start it.");
            return 0;
        }
        case "verify":
        {
            var destination = Required("--from");
            var (files, chunks) = new Backups(BackupCrypto.Open(destination, passphrase), postgres, Console.Out)
                .Verify(destination, Option("--run") ?? "latest", Flag("--all-chunks"));
            Console.WriteLine($"Verified: {files} backup file(s) and {chunks} file-store chunk(s) decrypt and are intact.");
            return 0;
        }
        default:
            return Usage();
    }
}
catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or Npgsql.NpgsqlException)
{
    Console.Error.WriteLine($"openhold-admin: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        openhold-admin: back up, restore and verify an Openhold server (all backups encrypted).

          backup  --server <folder> --to <destination> [--environment Production] [--keep-days 30] [--keep-runs 7]
          restore --from <destination> [--run latest] --vault <name> --connection "<connection string>"
                  --chunks <file-store folder> [--settings-to <folder>]
          verify  --from <destination> [--run latest] [--all-chunks]

        Database password: --db-password-file <file>, else PGPASSWORD, pgpass.conf or Windows sign-in.
        The passphrase comes from OPENHOLD_BACKUP_PASSPHRASE or --passphrase-file. Keep it off the server.
        PostgreSQL tools: --pg-bin <folder>, or --pg-docker <container> to run them inside the database container.
        """);
    return 2;
}

/// <summary>Writes to the console and the run's log file.</summary>
sealed class Tee(TextWriter first, TextWriter second) : TextWriter
{
    public override System.Text.Encoding Encoding => first.Encoding;
    public override void Write(char value) { first.Write(value); second.Write(value); }
    public override void Write(string? value) { first.Write(value); second.Write(value); }
    public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
}
