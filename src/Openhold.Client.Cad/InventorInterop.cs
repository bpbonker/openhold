using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Loads Inventor's interop assembly from the Inventor installed on this PC, so Openhold never ships Autodesk's file.
/// Registered as soon as this assembly loads, before anything here touches an Inventor type. Without Inventor
/// installed, Inventor features report that they're unavailable (see <see cref="Apprentice.Installed"/>).
/// </summary>
internal static class InventorInterop
{
    private const string AssemblyName = "Autodesk.Inventor.Interop";

    // Inventor versions Openhold supports, newest first.
    private static readonly string[] Versions = ["Inventor 2027", "Inventor 2026", "Inventor 2025"];

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "The resolver must be in place before any type here touches an Inventor type, whichever program loads this library.")]
    internal static void Register() => AssemblyLoadContext.Default.Resolving += Resolve;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (!string.Equals(name.Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = InstalledPath();
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    /// <summary>The interop assembly of the newest Inventor installed here, or null if there's none.</summary>
    public static string? InstalledPath() => Versions
        .Select(v => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", v, "Bin", AssemblyName + ".dll"))
        .FirstOrDefault(File.Exists);
}
