using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Openhold.Admin;

/// <summary>One vault as the server is configured: its database and its file store.</summary>
public sealed record VaultLocation(string Name, string ConnectionString, string ChunkStorePath);

/// <summary>
/// What an Openhold server is made of on disk, read from its own settings files (appsettings.json, the environment's
/// appsettings file and Openhold__ environment variables), so backups always follow the server's real configuration.
/// </summary>
public sealed class ServerLayout
{
    private ServerLayout(string folder, IReadOnlyList<VaultLocation> vaults, string signingKeyPath, string logFolder)
    {
        Folder = folder;
        Vaults = vaults;
        SigningKeyPath = signingKeyPath;
        LogFolder = logFolder;
    }

    public string Folder { get; }

    public IReadOnlyList<VaultLocation> Vaults { get; }

    public string SigningKeyPath { get; }

    public string LogFolder { get; }

    /// <summary>The server's settings files, which a restore puts back first.</summary>
    public IEnumerable<string> SettingsFiles =>
        Directory.EnumerateFiles(Folder, "appsettings*.json").Where(f => !f.Contains(".example.", StringComparison.OrdinalIgnoreCase));

    public static ServerLayout Read(string folder, string environment)
    {
        if (!File.Exists(Path.Combine(folder, "appsettings.json")))
            throw new BackupException($"{folder} doesn't look like an Openhold server folder (no appsettings.json).");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetFullPath(folder))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        string Full(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
        var vaults = configuration.GetSection("Openhold:Vaults").GetChildren()
            .Select(v => new VaultLocation(v["Name"] ?? "", v["Primary"] ?? "", Full(v["ChunkStorePath"] ?? "")))
            .Where(v => v.Name.Length > 0)
            .ToList();
        if (vaults.Count == 0 && configuration.GetConnectionString("Primary") is { } primary)
            vaults.Add(new VaultLocation("Default", primary, Full(configuration["Openhold:ChunkStorePath"] ?? "data/chunks")));
        if (vaults.Count == 0)
            throw new BackupException("The server's settings name no vaults (Openhold:Vaults).");

        return new ServerLayout(Path.GetFullPath(folder), vaults,
            Full(configuration["Openhold:Authentication:SigningKeyPath"] ?? "data/token-signing-key.pem"),
            Full(configuration["Openhold:LogFolder"] ?? "data/logs"));
    }
}

/// <summary>
/// Runs PostgreSQL's own dump and restore tools, installed (<c>--pg-bin</c> or on PATH) or inside the database's Docker
/// container (<c>--pg-docker</c>), with the password passed through the environment, never on the command line.
/// </summary>
public sealed class PostgresTools(string? binFolder, string? dockerContainer)
{
    public void Dump(string connectionString, string outputFile)
    {
        var c = new NpgsqlConnectionStringBuilder(connectionString);
        using var output = File.Create(outputFile);
        Run("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", .. Connection(c)], c.Password, stdout: output);
    }

    public void Restore(string connectionString, string dumpFile)
    {
        var c = new NpgsqlConnectionStringBuilder(connectionString);
        using var input = File.OpenRead(dumpFile);
        Run("pg_restore", ["--no-owner", "--no-privileges", "--exit-on-error", .. Connection(c)], c.Password, stdin: input);
    }

    private string[] Connection(NpgsqlConnectionStringBuilder c) => dockerContainer is null
        ? ["--host", c.Host ?? "localhost", "--port", c.Port.ToString(), "--username", c.Username ?? "", "--dbname", c.Database ?? ""]
        // Inside the container the database is local, on PostgreSQL's own port.
        : ["--host", "localhost", "--port", "5432", "--username", c.Username ?? "", "--dbname", c.Database ?? ""];

    private void Run(string tool, string[] arguments, string? password, Stream? stdout = null, Stream? stdin = null)
    {
        var start = dockerContainer is null
            ? new System.Diagnostics.ProcessStartInfo(Locate(tool))
            : new System.Diagnostics.ProcessStartInfo("docker") { ArgumentList = { "exec", "-i", "-e", "PGPASSWORD", dockerContainer, tool } };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // Without a password in the connection string, libpq's own sources apply: PGPASSWORD from the environment,
        // the account's pgpass.conf, or Windows sign-in (SSPI).
        if (!string.IsNullOrEmpty(password))
            start.Environment["PGPASSWORD"] = password;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = stdout is not null;
        start.RedirectStandardInput = stdin is not null;
        start.RedirectStandardError = true;

        using var process = System.Diagnostics.Process.Start(start) ?? throw new BackupException($"Couldn't start {tool}.");
        var errors = process.StandardError.ReadToEndAsync();
        var copyOut = stdout is null ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(stdout);
        if (stdin is not null)
        {
            stdin.CopyTo(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }
        copyOut.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new BackupException($"{tool} failed: {errors.Result.Trim()}");
    }

    private string Locate(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        if (binFolder is not null)
            return Path.Combine(binFolder, exe);
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        var installed = Directory.Exists(programFiles)
            ? Directory.GetDirectories(programFiles)
                .OrderByDescending(d => int.TryParse(Path.GetFileName(d).Split('.')[0], out var major) ? major : 0).Select(d => Path.Combine(d, "bin", exe)).FirstOrDefault(File.Exists)
            : null;
        return installed ?? exe; // otherwise rely on PATH
    }
}
