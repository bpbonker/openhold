using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Runs an Openhold AutoCAD plugin command on a drawing in accoreconsole (AutoCAD's headless console), without
/// opening AutoCAD. The command reads its request from the file named by <c>{prefix}_IN</c> and writes its result
/// to <c>{prefix}_OUT</c>; it saves the drawing itself when it changes something.
/// </summary>
internal static class AccoreConsole
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    // AutoCAD versions the plugin supports (R25.0-R25.1, .NET 8), newest first.
    private static readonly string[] AutoCadFolders = ["AutoCAD 2026", "AutoCAD 2025"];

    private static readonly Lazy<string?> Exe = new(() => AutoCadFolders
        .Select(f => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", f, "accoreconsole.exe"))
        .FirstOrDefault(File.Exists));

    private static readonly Lazy<string?> InstalledPlugin = new(() => new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.ProgramFiles }
        .Select(root => Path.Combine(Environment.GetFolderPath(root), "Autodesk", "ApplicationPlugins", "Openhold.bundle", "Contents", "Win64", "Openhold.AutoCAD.Plugin.dll"))
        .FirstOrDefault(File.Exists));

    /// <summary>The plugin to run: the given one, or the one installed for AutoCAD. Null if there is none, or no AutoCAD.</summary>
    public static string? Plugin(string? pluginPath) => Exe.Value is null ? null : pluginPath ?? InstalledPlugin.Value;

    /// <summary>Refuses a drawing that is open in AutoCAD (it has a .dwl lock file), so a save can't collide with it.</summary>
    public static void RejectIfOpen(string drawing)
    {
        if (File.Exists(Path.ChangeExtension(drawing, ".dwl")))
            throw new IOException($"{Path.GetFileName(drawing)} looks open in AutoCAD. Close it there first.");
    }

    public static TResult Run<TResult>(string drawing, string plugin, string command, string prefix, object request, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        var name = Path.GetFileName(drawing);
        var work = Path.Combine(Path.GetTempPath(), "openhold-acad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var requestPath = Path.Combine(work, "request.json");
            var resultPath = Path.Combine(work, "result.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            var script = Path.Combine(work, "run.scr");
            // SECURELOAD only changes the isolated throwaway profile below, never the user's AutoCAD settings.
            File.WriteAllLines(script, ["SECURELOAD 0", $"NETLOAD \"{plugin}\"", command, "_.QUIT", "_Y", ""], Encoding.ASCII);

            var start = new ProcessStartInfo(Exe.Value!)
            {
                ArgumentList = { "/i", drawing, "/s", script, "/l", "en-US", "/isolate", "OpenholdConsole", Path.Combine(work, "profile") },
                UseShellExecute = false,
                CreateNoWindow = true,
                // AutoCAD drops diagnostics files in its working folder; keep them in the throwaway one.
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode,
                Environment = { [prefix + "_IN"] = requestPath, [prefix + "_OUT"] = resultPath },
            };
            using var process = Process.Start(start)!;
            var log = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(limit))
            {
                process.Kill(entireProcessTree: true);
                throw new IOException($"AutoCAD's console didn't finish {what} {name} within {limit.TotalMinutes:0} minutes.");
            }
            if (!File.Exists(resultPath))
                throw new IOException($"AutoCAD's console failed {what} {name}. {LastLines(log.Result)}");
            return JsonSerializer.Deserialize<TResult>(File.ReadAllText(resultPath))!;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // accoreconsole can hold its log a moment after exiting; a leftover temp folder is harmless.
            }
        }
    }

    private static string LastLines(string log) =>
        string.Join(" ", log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
}
