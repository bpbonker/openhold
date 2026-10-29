using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Openhold.AutoCAD.Plugin.Commands))]

namespace Openhold.AutoCAD.Plugin;

public sealed class Commands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Writes the active drawing's index as JSON. Works headless in accoreconsole.exe:
    /// the output path comes from the OPENHOLD_INDEX_OUT environment variable, otherwise
    /// it is written next to the drawing as &lt;name&gt;.openhold.json.
    /// </summary>
    [CommandMethod("OPENHOLDINDEX")]
    public static void IndexActiveDrawing()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var result = DrawingIndex.Read(document.Database);

        var output = Environment.GetEnvironmentVariable("OPENHOLD_INDEX_OUT");
        if (string.IsNullOrWhiteSpace(output))
            output = Path.ChangeExtension(document.Name, ".openhold.json");
        File.WriteAllText(output, JsonSerializer.Serialize(result, Json));

        document.Editor.WriteMessage(
            $"\nOpenhold: {result.References.Count} reference(s), {result.AttributedBlocks.Count} attributed block(s) -> {output}\n");
    }

    /// <summary>The request Openhold Explorer passes to <see cref="RepairReferences"/>: a referenced file's old and new full paths.</summary>
    public sealed record ReferenceRepairRequest(string OldPath, string NewPath);

    /// <summary>
    /// Repoints the active drawing's xrefs, images and underlays after a referenced file was renamed or moved in
    /// Openhold. Headless in accoreconsole, like <see cref="WriteTitleBlock"/>: request from OPENHOLD_REPAIR_IN, result
    /// (how many references changed) to OPENHOLD_REPAIR_OUT. The drawing is saved only if something changed.
    /// </summary>
    [CommandMethod("OPENHOLDREPAIRREFS")]
    public static void RepairReferences()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var request = JsonSerializer.Deserialize<ReferenceRepairRequest>(File.ReadAllText(Environment.GetEnvironmentVariable("OPENHOLD_REPAIR_IN")!))!;
        var database = document.Database;
        var changed = ReferenceRepair.Repoint(database, document.Name, request.OldPath, request.NewPath);
        if (changed > 0)
            database.SaveAs(document.Name, true, database.OriginalFileVersion, database.SecurityParameters);
        File.WriteAllText(Environment.GetEnvironmentVariable("OPENHOLD_REPAIR_OUT")!, JsonSerializer.Serialize(new { Changed = changed }, Json));
        document.Editor.WriteMessage($"\nOpenhold: repointed {changed} reference(s)\n");
    }

    /// <summary>The request Openhold Explorer passes to <see cref="WriteTitleBlock"/>.</summary>
    public sealed record TitleBlockWriteRequest(string BlockNamePattern, Dictionary<string, string> Attributes);

    /// <summary>
    /// Sets title-block attributes in the active drawing, for Openhold Explorer's Edit Properties. Headless in
    /// accoreconsole: the request is read from the OPENHOLD_PROPS_IN file and the result written to OPENHOLD_PROPS_OUT.
    /// The drawing is saved (in its own DWG format) only if something changed.
    /// </summary>
    [CommandMethod("OPENHOLDWRITEPROPS")]
    public static void WriteTitleBlock()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var request = JsonSerializer.Deserialize<TitleBlockWriteRequest>(File.ReadAllText(Environment.GetEnvironmentVariable("OPENHOLD_PROPS_IN")!))!;
        var attributes = new Dictionary<string, string>(request.Attributes, StringComparer.OrdinalIgnoreCase);
        var database = document.Database;
        var result = TitleBlockWriter.Write(database, request.BlockNamePattern, attributes);
        if (result.Updated > 0)
            database.SaveAs(document.Name, true, database.OriginalFileVersion, database.SecurityParameters);
        File.WriteAllText(Environment.GetEnvironmentVariable("OPENHOLD_PROPS_OUT")!, JsonSerializer.Serialize(result, Json));
        document.Editor.WriteMessage($"\nOpenhold: updated {result.Updated} attribute(s) in {result.TitleBlocks} title block(s)\n");
    }

    /// <summary>
    /// Plots every layout of the active drawing into one PDF, for publishing on release. Headless in accoreconsole:
    /// the request (the output path) is read from OPENHOLD_PDF_IN and the result written to OPENHOLD_PDF_OUT. The
    /// drawing isn't saved.
    /// </summary>
    [CommandMethod("OPENHOLDPLOTPDF")]
    public static void PlotPdf()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var request = JsonSerializer.Deserialize<PdfPlotRequest>(File.ReadAllText(Environment.GetEnvironmentVariable("OPENHOLD_PDF_IN")!))!;
        PdfPlotResult result;
        try
        {
            result = PdfPlotter.Plot(document, request.OutputPath, request.DefaultPaper);
        }
        catch (System.Exception ex)
        {
            // Reported to the job worker as the reason, rather than lost when the console exits.
            result = new PdfPlotResult(0, []) { Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
        File.WriteAllText(Environment.GetEnvironmentVariable("OPENHOLD_PDF_OUT")!, JsonSerializer.Serialize(result, Json));
        document.Editor.WriteMessage($"\nOpenhold: plotted {result.Sheets} sheet(s) to PDF {result.Error}\n");
    }

    private sealed record PdfPlotRequest(string OutputPath, string? DefaultPaper);
}
