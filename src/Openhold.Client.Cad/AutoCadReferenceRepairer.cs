using Openhold.Client.Sdk.Workspace;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Repoints a drawing's xrefs, images and underlays after a file it references was renamed or moved, by running
/// the Openhold AutoCAD plugin's OPENHOLDREPAIRREFS command in accoreconsole. AutoCAD itself need not be running.
/// </summary>
/// <param name="pluginPath">The plugin DLL to run; by default the one installed for AutoCAD.</param>
public sealed class AutoCadReferenceRepairer(string? pluginPath = null) : IReferenceRepairer
{
    private readonly string? _plugin = AccoreConsole.Plugin(pluginPath);

    public bool CanRepair(string parentPath) => Path.GetExtension(parentPath).Equals(".dwg", StringComparison.OrdinalIgnoreCase) && _plugin is not null;

    public bool Repair(string parentPath, string oldChildPath, string newChildPath)
    {
        AccoreConsole.RejectIfOpen(parentPath);
        var request = new { OldPath = Path.GetFullPath(oldChildPath), NewPath = Path.GetFullPath(newChildPath) };
        return AccoreConsole.Run<RepairResult>(parentPath, _plugin!, "OPENHOLDREPAIRREFS", "OPENHOLD_REPAIR", request, "updating the references in").Changed > 0;
    }

    private sealed record RepairResult(int Changed);
}
