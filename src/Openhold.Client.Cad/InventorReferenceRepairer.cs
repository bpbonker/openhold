using Inventor;
using Openhold.Client.Sdk.Workspace;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Repoints references inside Inventor files (assemblies, presentations, drawings, derived parts) with Apprentice,
/// so a renamed or moved part is still found by everything that uses it. Inventor itself need not be running.
/// </summary>
public sealed class InventorReferenceRepairer : IReferenceRepairer
{
    // Inventor DWG drawings are left out: a .dwg may equally be an AutoCAD drawing, which Apprentice can't edit.
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".iam", ".ipn", ".idw", ".ipt" };

    public bool CanRepair(string parentPath) => Extensions.Contains(Path.GetExtension(parentPath)) && Apprentice.Installed;

    public bool Repair(string parentPath, string oldChildPath, string newChildPath) =>
        Apprentice.Run($"update the references in {Path.GetFileName(parentPath)}", apprentice =>
        {
            var document = apprentice.Open(parentPath);
            var changed = false;
            var descriptors = document.File.ReferencedFileDescriptors.Cast<FileDescriptor>().ToList();
            // The reference as Inventor resolves it. If it resolves somewhere else (e.g. a file brought in from an old
            // vault still names the old location, which exists on this PC), fall back to the file name: Openhold's
            // Inventor projects use unique file names, as Autodesk Vault's do, so the name identifies the reference.
            var matching = descriptors.Where(d => string.Equals(Path.GetFullPath(d.FullFileName), Path.GetFullPath(oldChildPath), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
                matching = descriptors.Where(d => string.Equals(Path.GetFileName(d.FullFileName), Path.GetFileName(oldChildPath), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var descriptor in matching)
            {
                descriptor.ReplaceReference(newChildPath);
                changed = true;
            }
            if (changed)
            {
                var saver = apprentice.FileSaveAs;
                saver.AddFileToSave(document, document.FullFileName);
                saver.ExecuteSave();
            }
            return changed;
        });
}
