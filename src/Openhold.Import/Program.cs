using System.Security.Cryptography;
using System.Text;
using Openhold.Client.Cad;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Client.Ui;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Core.Files;

// Openhold import: brings an existing folder tree (e.g. an old vault's working folder) into an Openhold folder, with each
// file's references (Inventor assemblies and drawings, AutoCAD xrefs), properties (iProperties, title blocks) and
// preview. Uses this PC's Openhold settings and workspace.
//
//   OpenholdImport <source folder> <vault folder> --dry-run [--report import.csv]
//       Reads everything and writes a report (what would be imported, what each file uses, what can't be found),
//       changing nothing in Openhold. Always do this first.
//   OpenholdImport <source folder> <vault folder> [--comment "text"] [--batch 100] [--read-only] [--state Released]
//       Imports: copies the files into the workspace, repoints references to the copies, checks them in (files
//       each one uses first), then optionally makes the folder read-only for everyone and changes the files' state.
//
// The source folder is only read. Files already in Openhold at the destination are skipped, so an interrupted import
// can simply be run again.
if (args.Length < 2 || args[0].StartsWith("--"))
{
    Console.WriteLine("Usage: OpenholdImport <source folder> <vault folder, e.g. $/Legacy/PRJ01> [--dry-run] [--report file.csv] " +
                      "[--comment text] [--batch 100] [--read-only] [--state Released]");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var vaultFolder = "$/" + args[1].Replace('\\', '/').TrimStart('$').Trim('/');
var dryRun = args.Contains("--dry-run");
var reportPath = Path.GetFullPath(Option("--report") ?? $"openhold-import-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
var comment = Option("--comment") ?? $"Imported from {source}";
var batchSize = int.TryParse(Option("--batch"), out var b) && b > 0 ? b : 100;
var state = Option("--state");
if (!Directory.Exists(source))
{
    Console.WriteLine($"{source} doesn't exist.");
    return 2;
}

var settings = ClientSettings.Load();
var client = settings.CreateClient();
var workspace = new OpenholdWorkspace(settings.WorkspaceRoot, client);
var destination = workspace.ToLocalPath(vaultFolder);
if (IsUnder(destination, source) || IsUnder(source, destination))
{
    Console.WriteLine("The source folder and the destination in the workspace overlap; choose a source outside the workspace.");
    return 2;
}

Console.WriteLine($"{(dryRun ? "Dry run: reading" : "Importing")} {source} into {vaultFolder} ({settings.ServerUrl.TrimEnd('/')}, {settings.Vault ?? "default vault"}) as {client.Username}.");
var reader = new LegacyReader(settings.TitleBlock);
if (!reader.CanReadInventor)
    Console.WriteLine("  Note: Inventor isn't installed here, so Inventor references and iProperties won't be read.");
if (!reader.CanReadDrawings)
    Console.WriteLine("  Note: AutoCAD (with the Openhold plugin) isn't installed here, so drawing xrefs and title blocks won't be read.");

// 1. What's there, and what Openhold already has.
var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
    .Where(p => !FileKinds.IsIgnored(p) && !p.Split(Path.DirectorySeparatorChar).Contains(".openhold"))
    .Select(p => new ImportFile(p, Path.GetRelativePath(source, p)))
    .ToList();
foreach (var file in files)
    file.Target = Path.Combine(destination, file.Relative);
var inVault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var chunk in files.Chunk(5000))
{
    foreach (var resolved in await client.ResolvePathsAsync(chunk.Select(f => workspace.ToVaultPath(f.Target))))
    {
        if (resolved.File is not null)
            inVault.Add(resolved.Path);
    }
}
foreach (var file in files.Where(f => inVault.Contains(workspace.ToVaultPath(f.Target))))
    file.Action = "Skipped: already in Openhold";
Console.WriteLine($"  {files.Count} files ({Size(files.Sum(f => new FileInfo(f.Source).Length))}); {inVault.Count} already in Openhold.");

// 2. Copy, so the source is never opened (AutoCAD writes lock files beside a drawing it opens).
var staging = dryRun ? Path.Combine(Path.GetTempPath(), "openhold-import-" + Guid.NewGuid().ToString("N")[..8]) : destination;
foreach (var file in files.Where(f => f.Action is null))
{
    file.Copy = Path.Combine(staging, file.Relative);
    if (File.Exists(file.Copy) && !SameContent(file.Source, file.Copy))
    {
        file.Action = "Skipped: a different file is already in the workspace there";
        continue;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(file.Copy)!);
    if (!File.Exists(file.Copy))
        File.Copy(file.Source, file.Copy);
    File.SetAttributes(file.Copy, File.GetAttributes(file.Copy) & ~FileAttributes.ReadOnly);
    file.Action = "Import";
}

// 3. Read references and properties from the copies, and work out what each reference becomes.
var importing = files.Where(f => f.Action == "Import").ToList();
var bySourcePath = files.ToDictionary(f => f.Source, StringComparer.OrdinalIgnoreCase);
var byCopyPath = importing.ToDictionary(f => f.Copy!, StringComparer.OrdinalIgnoreCase);
var read = 0;
foreach (var file in importing)
{
    file.Facts = reader.Read(file.Copy!);
    if (++read % 25 == 0 || read == importing.Count)
        Console.WriteLine($"  Read {read} of {importing.Count}");
    foreach (var reference in file.Facts.References)
    {
        var target = byCopyPath.TryGetValue(reference.FullPath, out var copied) ? copied.Target
            : bySourcePath.TryGetValue(reference.FullPath, out var original) ? original.Target
            : IsUnder(reference.FullPath, settings.WorkspaceRoot) ? reference.FullPath
            : null;
        if (target is null)
        {
            file.Missing.Add(reference.StoredPath);
            continue;
        }
        file.Uses.Add(target);
        if (bySourcePath.ContainsKey(reference.FullPath) && !string.Equals(reference.FullPath, file.Copy, StringComparison.OrdinalIgnoreCase))
            file.Repoint.Add((reference.FullPath, target)); // stored as a path into the source: point it at the copy
    }
}
var outsideWorkspace = importing.SelectMany(f => f.Uses).Where(u => !files.Any(f => string.Equals(f.Target, u, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
var knownOutside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var chunk in outsideWorkspace.Chunk(5000))
{
    foreach (var resolved in await client.ResolvePathsAsync(chunk.Select(workspace.ToVaultPath)))
    {
        if (resolved.File is not null)
            knownOutside.Add(workspace.ToLocalPath(resolved.Path));
    }
}
foreach (var file in importing)
{
    foreach (var use in file.Uses.Where(u => outsideWorkspace.Contains(u, StringComparer.OrdinalIgnoreCase) && !knownOutside.Contains(u)).ToList())
    {
        file.Uses.Remove(use);
        file.Missing.Add(use);
    }
}

WriteReport(reportPath, files, workspace);
var missingCount = importing.Count(f => f.Missing.Count > 0);
Console.WriteLine($"  Report: {reportPath}");
Console.WriteLine($"  {importing.Count} to import; {missingCount} use files that weren't found; {importing.Count(f => f.Facts!.Problems.Count > 0)} have other notes.");
if (dryRun)
{
    Directory.Delete(staging, recursive: true);
    Console.WriteLine("Dry run finished; nothing was changed in Openhold.");
    return 0;
}

// 4. Repoint references that still name the source folder, then check in, files each one uses first.
IReferenceRepairer[] repairers = [new AcadeProjectRepairer(), new InventorReferenceRepairer(), new AutoCadReferenceRepairer()];
foreach (var file in importing.Where(f => f.Repoint.Count > 0))
{
    foreach (var (from, to) in file.Repoint)
    {
        try
        {
            repairers.FirstOrDefault(r => r.CanRepair(file.Copy!))?.Repair(file.Copy!, from, to);
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException)
        {
            file.Facts = file.Facts! with { Problems = [.. file.Facts.Problems, $"Couldn't repoint {Path.GetFileName(from)}: {ex.Message}"] };
        }
    }
}

var ordered = DependencyOrder(importing);
var imported = new List<string>();
foreach (var batch in ordered.Chunk(batchSize))
{
    var paths = batch.Select(f => f.Target).ToList();
    var facts = batch.ToDictionary(f => f.Target, StringComparer.OrdinalIgnoreCase);
    var result = await workspace.CheckInAsync(paths, comment, new CheckInOptions
    {
        Properties = p => facts.TryGetValue(p, out var f) ? f.Facts!.Properties : null,
        VaultProperties = p => facts.TryGetValue(p, out var f) ? new Dictionary<string, string> { ["Imported From"] = f.Source } : null,
        References = p => facts.TryGetValue(p, out var f) ? f.Uses : null,
        Thumbnail = p => ShellThumbnails.TryCreatePng(p),
        // Batches follow dependency order; pulling the rest of an AutoCAD Electrical project into the first batch
        // would upload drawings without their facts and then refuse them in their own batch.
        WholeProjectSets = false,
    });
    if (!result.Succeeded)
    {
        Console.WriteLine($"Stopped after {imported.Count} files: {result.Summary}");
        foreach (var outcome in result.Files.Take(20))
            Console.WriteLine($"  {outcome.Message}");
        Console.WriteLine("Fix the problem and run the same command again; files already imported are skipped.");
        return 1;
    }
    imported.AddRange(paths);
    Console.WriteLine($"  Checked in {imported.Count} of {ordered.Count}");
}

// 5. Optionally: read-only for everyone, and a lifecycle state.
if (args.Contains("--read-only"))
{
    var current = await client.GetConfigurationAsync();
    if (!(current.Configuration.FolderRules ?? []).Any(r => string.Equals(r.Path, vaultFolder, StringComparison.OrdinalIgnoreCase) && r.Group == "everyone"))
    {
        await client.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            FolderRules = [.. current.Configuration.FolderRules ?? [], new FolderRule(vaultFolder, "everyone", FolderAccess.Read)],
        }, $"Imported files in {vaultFolder} are read-only"));
    }
    Console.WriteLine($"  {vaultFolder} is now read-only for everyone except administrators.");
}
if (state is not null)
{
    foreach (var batch in ordered.Chunk(batchSize))
    {
        var changed = await workspace.ChangeStateAsync(batch.Select(f => f.Target), state, comment);
        if (!changed.Succeeded)
            Console.WriteLine($"  Some files couldn't be made {state}: {changed.Summary}");
    }
}
Console.WriteLine($"Imported {imported.Count} files into {vaultFolder}.");
return 0;

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static bool IsUnder(string path, string folder) =>
    Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

static bool SameContent(string a, string b)
{
    if (new FileInfo(a).Length != new FileInfo(b).Length)
        return false;
    using var first = File.OpenRead(a);
    using var second = File.OpenRead(b);
    return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
}

static string Size(long bytes) => bytes switch
{
    >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
    _ => $"{bytes / 1024.0:0} KB",
};

/// <summary>Files each file uses come before it, so every check-in can refer to what's already in Openhold.</summary>
static List<ImportFile> DependencyOrder(List<ImportFile> files)
{
    var byTarget = files.ToDictionary(f => f.Target, StringComparer.OrdinalIgnoreCase);
    var depth = new Dictionary<ImportFile, int>();
    int Depth(ImportFile file, HashSet<ImportFile> visiting)
    {
        if (depth.TryGetValue(file, out var known))
            return known;
        if (!visiting.Add(file))
            return 0; // a reference cycle: break it here
        var children = file.Uses.Select(u => byTarget.GetValueOrDefault(u)).OfType<ImportFile>().Where(c => c != file);
        var result = children.Select(c => Depth(c, visiting) + 1).DefaultIfEmpty(0).Max();
        visiting.Remove(file);
        return depth[file] = result;
    }
    return files.OrderBy(f => Depth(f, [])).ThenBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ToList();
}

static void WriteReport(string path, List<ImportFile> files, OpenholdWorkspace workspace)
{
    static string Cell(string? value) => value is null ? "" : value.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    var text = new StringBuilder("Source,Vault path,Size,Action,Part Number,Description,Revision,Uses,Not found,Notes\n");
    foreach (var file in files.OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase))
    {
        var properties = file.Facts?.Properties ?? new Dictionary<string, string>();
        text.AppendJoin(',', new[]
        {
            Cell(file.Source), Cell(workspace.ToVaultPath(file.Target)), new FileInfo(file.Source).Length.ToString(), Cell(file.Action),
            Cell(properties.GetValueOrDefault("Part Number")), Cell(properties.GetValueOrDefault("Description")), Cell(properties.GetValueOrDefault("Revision")),
            file.Uses.Count.ToString(), Cell(string.Join("; ", file.Missing)), Cell(string.Join("; ", file.Facts?.Problems ?? [])),
        }).Append('\n');
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}

/// <summary>One file being imported: where it is, where it goes, and what was found in it.</summary>
internal sealed class ImportFile(string source, string relative)
{
    public string Source { get; } = source;

    public string Relative { get; } = relative;

    /// <summary>Its place in the workspace (and so in the vault).</summary>
    public string Target { get; set; } = "";

    /// <summary>The copy that is read (and, when importing, checked in).</summary>
    public string? Copy { get; set; }

    public string? Action { get; set; }

    public LegacyFileFacts? Facts { get; set; }

    /// <summary>Workspace paths of the files it uses.</summary>
    public List<string> Uses { get; } = [];

    /// <summary>References that point at files that aren't being imported and aren't in Openhold.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>References stored as paths into the source folder, to repoint at the imported copies.</summary>
    public List<(string From, string To)> Repoint { get; } = [];
}
