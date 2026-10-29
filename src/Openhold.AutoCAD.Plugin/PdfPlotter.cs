using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Openhold.AutoCAD.Plugin;

/// <summary>What publishing a PDF did: the sheets plotted, by layout name, in order.</summary>
public sealed record PdfPlotResult(int Sheets, IReadOnlyList<string> Layouts)
{
    /// <summary>Why nothing was plotted, if it failed.</summary>
    public string? Error { get; init; }

    /// <summary>For each sheet: the layout's own paper and the PDF paper it was plotted on.</summary>
    public IReadOnlyList<string> Papers { get; init; } = [];
}

/// <summary>
/// Plots a drawing to one multi-sheet PDF with AutoCAD's own "DWG To PDF.pc3": every paper-space layout in tab
/// order, each with its own page setup (paper size, scale, plot style), or the model's extents when the drawing
/// has no layouts. The drawing isn't changed.
/// </summary>
public static class PdfPlotter
{
    public const string Device = "DWG To PDF.pc3";

    /// <param name="defaultPaper">
    /// The paper for layouts with no printer set up, e.g. "A3", when sheets are printed bigger than they're drawn.
    /// Without it, such a layout gets the paper closest to what's drawn on it.
    /// </param>
    public static PdfPlotResult Plot(Document document, string outputPath, string? defaultPaper = null)
    {
        var database = document.Database;
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new InvalidOperationException("AutoCAD is already plotting.");
        Application.SetSystemVariable("BACKGROUNDPLOT", 0);

        using var transaction = database.TransactionManager.StartTransaction();
        var layouts = new List<Layout>();
        // DBDictionary's own enumerator yields DBDictionaryEntry; LINQ's Cast would see the non-generic DictionaryEntry.
        foreach (DBDictionaryEntry entry in (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead))
        {
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is Layout { ModelType: false } layout)
                layouts.Add(layout);
        }
        layouts = layouts.OrderBy(l => l.TabOrder).ToList();
        var model = layouts.Count == 0;
        if (model)
        {
            var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
            layouts = [(Layout)transaction.GetObject(modelSpace.LayoutId, OpenMode.ForRead)];
        }

        var papers = new List<string>();
        using (var engine = PlotFactory.CreatePublishEngine())
        {
            engine.BeginPlot(null, null);
            for (var i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                // A layout is plotted as the current one (AutoCAD's own multi-sheet plotting does the same).
                LayoutManager.Current.CurrentLayout = layout.LayoutName;
                var settings = SettingsFor(layout, model, defaultPaper);
                papers.Add($"{layout.LayoutName}: {settings.CanonicalMediaName}");
                var info = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings };
                // Matching lets AutoCAD swap in the PDF device's equivalent of a paper size the layout's own printer called something else.
                new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);

                if (i == 0)
                    engine.BeginDocument(info, document.Name, null, 1, true, outputPath);
                engine.BeginPage(new PlotPageInfo(), info, i == layouts.Count - 1, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
            }
            engine.EndDocument(null);
            engine.EndPlot(null);
        }
        transaction.Commit();
        return new PdfPlotResult(layouts.Count, layouts.Select(l => l.LayoutName).ToList()) { Papers = papers };
    }

    /// <summary>The PDF device's papers and their sizes (mm, as the page comes out), read once.</summary>
    private static List<(string Media, double Width, double Height)>? _papers;

    private static List<(string Media, double Width, double Height)> Papers(PlotSettings settings)
    {
        if (_papers is not null)
            return _papers;
        var validator = PlotSettingsValidator.Current;
        _papers = [];
        foreach (var media in validator.GetCanonicalMediaNameList(settings).OfType<string>())
        {
            validator.SetCanonicalMediaName(settings, media);
            _papers.Add((media, settings.PlotPaperSize.X, settings.PlotPaperSize.Y));
        }
        return _papers;
    }

    /// <summary>
    /// The PDF paper whose name contains <paramref name="name"/> (e.g. "A3"), the right way round for the sheet
    /// (so the PDF page opens upright), preferring full bleed.
    /// </summary>
    private static string? PaperNamed(PlotSettings settings, string name, bool landscape) =>
        Papers(settings)
            .Where(p => p.Media.Contains($"_{name}_", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Media, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Width >= p.Height == landscape)
            .ThenByDescending(p => p.Media.Contains("full_bleed", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Media)
            .FirstOrDefault();

    /// <summary>
    /// The PDF paper closest in size that holds <paramref name="width"/> × <paramref name="height"/> mm (either way round),
    /// allowing it to be <paramref name="tolerance"/> smaller (a border drawn just inside the sheet edge). Full-bleed
    /// papers are preferred, so nothing near the edge is cut off. Null if none is big enough.
    /// </summary>
    private static string? PaperFor(PlotSettings settings, double width, double height, double tolerance)
    {
        var (shorter, longer) = (Math.Min(width, height), Math.Max(width, height));
        // Closest in size wins (297 × 210 is A4, not the slightly smaller US Letter), then the right way round so
        // the page opens upright, then full bleed.
        return Papers(settings)
            .Select(p => (p.Media, p.Width, p.Height, Shorter: Math.Min(p.Width, p.Height), Longer: Math.Max(p.Width, p.Height)))
            .Where(p => p.Shorter >= shorter * (1 - tolerance) && p.Longer >= longer * (1 - tolerance))
            .OrderBy(p => Math.Round(Math.Abs(p.Shorter - shorter) + Math.Abs(p.Longer - longer)))
            .ThenByDescending(p => p.Width >= p.Height == width >= height)
            .ThenByDescending(p => p.Media.Contains("full_bleed", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Media)
            .FirstOrDefault();
    }

    /// <summary>
    /// The layout's page setup, sent to the PDF device. A layout set up for a printer keeps its paper size (matched
    /// by size when the PDF device calls it something else). A layout with no printer ("None", a placeholder paper)
    /// gets the paper that fits what's drawn on it, e.g. A3 for an A3 border, and its contents are plotted to fit.
    /// </summary>
    private static PlotSettings SettingsFor(Layout layout, bool model, string? defaultPaper)
    {
        var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);
        var validator = PlotSettingsValidator.Current;

        var noPrinter = string.Equals(layout.CanonicalMediaName, "none_user_media", StringComparison.OrdinalIgnoreCase)
                        || layout.PlotConfigurationName is "" or "None";
        if (!model && noPrinter && DrawnSize(layout) is { } drawn)
        {
            validator.SetPlotConfigurationName(settings, Device, null);
            var paper = (defaultPaper is { Length: > 0 } ? PaperNamed(settings, defaultPaper, drawn.Width >= drawn.Height) : null)
                        ?? PaperFor(settings, drawn.Width, drawn.Height, 0.1);
            if (paper is not null)
                validator.SetCanonicalMediaName(settings, paper);
            FitToPaper(settings, landscape: drawn.Width >= drawn.Height);
            return settings;
        }

        try
        {
            validator.SetPlotConfigurationName(settings, Device, layout.CanonicalMediaName);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // The PDF device doesn't know that paper by name (the layout was set up for another printer): use the
            // PDF paper of the same size, so an A3 sheet stays A3 rather than falling back to the device's default.
            validator.SetPlotConfigurationName(settings, Device, null);
            var size = layout.PlotPaperSize;
            if (PaperFor(settings, size.X, size.Y, 0.01) is { } paper)
                validator.SetCanonicalMediaName(settings, paper);
        }
        if (model)
            FitToPaper(settings, landscape: true);
        return settings;
    }

    /// <summary>Plots everything drawn, scaled to fit and centred, turned to match the paper's orientation.</summary>
    private static void FitToPaper(PlotSettings settings, bool landscape)
    {
        var validator = PlotSettingsValidator.Current;
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
        validator.SetPlotCentered(settings, true);
        var paper = settings.PlotPaperSize;
        var paperLandscape = paper.X >= paper.Y;
        validator.SetPlotRotation(settings, paperLandscape == landscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
    }

    /// <summary>The size (mm) of everything drawn on a paper-space layout, or null if it's empty.</summary>
    private static (double Width, double Height)? DrawnSize(Layout layout)
    {
        var transaction = layout.Database.TransactionManager.TopTransaction;
        var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        Extents3d? total = null;
        foreach (ObjectId id in space)
        {
            // The layout's own overall viewport (number 1) is the sheet itself, not something drawn on it.
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity { Visible: true } entity || entity is Viewport { Number: 1 })
                continue;
            try
            {
                var bounds = entity.GeometricExtents;
                if (total is { } sum)
                {
                    sum.AddExtents(bounds);
                    total = sum;
                }
                else
                {
                    total = bounds;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Some entities (e.g. empty text) have no extents; they don't affect the sheet size.
            }
        }
        if (total is not { } extents)
            return null;
        var toMm = layout.PlotPaperUnits == PlotPaperUnit.Inches ? 25.4 : 1;
        return ((extents.MaxPoint.X - extents.MinPoint.X) * toMm, (extents.MaxPoint.Y - extents.MinPoint.Y) * toMm);
    }
}
