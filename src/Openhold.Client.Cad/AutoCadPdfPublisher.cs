using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Makes a PDF of a drawing without opening AutoCAD: runs the Openhold AutoCAD plugin's OPENHOLDPLOTPDF command in
/// accoreconsole, which plots every layout (each with its own page setup) into one PDF with "DWG To PDF.pc3".
/// Used by the job worker to publish drawings when they're released.
/// </summary>
/// <param name="pluginPath">The plugin DLL to run; by default the one installed for AutoCAD.</param>
/// <param name="defaultPaper">Paper for layouts with no printer set up (e.g. "A3"); by default the size drawn.</param>
public sealed class AutoCadPdfPublisher(string? pluginPath = null, string? defaultPaper = null)
{
    private readonly string? _plugin = AccoreConsole.Plugin(pluginPath);

    /// <summary>Whether this PC can make PDFs of drawings (AutoCAD and the Openhold plugin are installed).</summary>
    public bool Available => _plugin is not null;

    /// <summary>Plots <paramref name="drawing"/> to <paramref name="pdf"/>. Returns the number of sheets.</summary>
    public int Publish(string drawing, string pdf)
    {
        if (_plugin is null)
            throw new IOException("AutoCAD and the Openhold AutoCAD plugin must be installed on this PC to make PDFs of drawings.");
        // Big sets (dozens of sheets) take a while; well past this, AutoCAD is stuck rather than busy.
        var result = AccoreConsole.Run<PlotResult>(drawing, _plugin, "OPENHOLDPLOTPDF", "OPENHOLD_PDF", new { OutputPath = pdf, DefaultPaper = defaultPaper }, "making a PDF of", TimeSpan.FromMinutes(15));
        if (result.Error is { } error)
            throw new IOException($"AutoCAD couldn't make a PDF of {Path.GetFileName(drawing)}: {error}");
        if (!File.Exists(pdf) || result.Sheets == 0)
            throw new IOException($"AutoCAD's console didn't produce a PDF of {Path.GetFileName(drawing)}.");
        return result.Sheets;
    }

    private sealed record PlotResult(int Sheets, IReadOnlyList<string> Layouts, string? Error);
}
