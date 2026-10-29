using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
using QRCoder;

namespace Openhold.Jobs;

public enum StampCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Stamps every page of a PDF with a QR code and a few words, e.g. "Released rev B · 28 Oct 2026 · v7 / Scan to
/// check it's current." The QR code opens the vault's check page for exactly this version, so anyone holding a printout
/// can tell whether it's still the current release. Drawn on a white box so it reads over linework.
/// </summary>
public static class PdfStamper
{
    private const double Millimetre = 72 / 25.4;

    static PdfStamper()
    {
        // PDFsharp doesn't look for fonts itself; the stamp uses Arial, which every Windows machine has.
        GlobalFontSettings.FontResolver ??= new WindowsArial();
    }

    private sealed class WindowsArial : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) => new(isBold ? "arialbd" : "arial");

        public byte[] GetFont(string faceName) =>
            File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), faceName + ".ttf"));
    }

    /// <param name="lines">A few short lines of text, shown beside the code.</param>
    /// <param name="sizeMm">The QR code's width in millimetres.</param>
    /// <param name="marginMm">Distance from the page edge, clear of the drawing border's zone markers.</param>
    public static void Stamp(string input, string output, string url, IReadOnlyList<string> lines, StampCorner corner = StampCorner.TopLeft, double sizeMm = 14, double marginMm = 12)
    {
        var modules = QrModules(url);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var font = new XFont("Arial", 7);
        foreach (var page in document.Pages)
        {
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var size = sizeMm * Millimetre;
            var margin = marginMm * Millimetre;
            var textWidth = lines.Max(line => graphics.MeasureString(line, font).Width);
            var boxWidth = size + 4 + textWidth + 4;
            var boxHeight = size;
            var x = corner is StampCorner.TopLeft or StampCorner.BottomLeft ? margin : page.Width.Point - margin - boxWidth;
            var y = corner is StampCorner.TopLeft or StampCorner.TopRight ? margin : page.Height.Point - margin - boxHeight;

            graphics.DrawRectangle(XBrushes.White, x - 2, y - 2, boxWidth + 4, boxHeight + 4);
            DrawQr(graphics, modules, x, y, size);
            var lineHeight = font.Size * 1.3;
            var top = y + (size - lineHeight * lines.Count) / 2 + font.Size;
            for (var i = 0; i < lines.Count; i++)
                graphics.DrawString(lines[i], font, XBrushes.Black, new XPoint(x + size + 4, top + i * lineHeight));
        }
        document.Save(output);
    }

    /// <summary>The QR code's dark and light modules, including the quiet zone around it.</summary>
    private static bool[][] QrModules(string url)
    {
        using var generator = new QRCodeGenerator();
        // Medium error correction survives a crease or a smudge on a shop-floor printout.
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return data.ModuleMatrix.Select(row => Enumerable.Range(0, row.Length).Select(i => row[i]).ToArray()).ToArray();
    }

    /// <summary>Draws the code as vector squares: sharp at any print size, and no image format to go wrong.</summary>
    private static void DrawQr(XGraphics graphics, bool[][] modules, double x, double y, double size)
    {
        var cell = size / modules.Length;
        for (var row = 0; row < modules.Length; row++)
        {
            for (var column = 0; column < modules[row].Length; column++)
            {
                if (modules[row][column])
                    graphics.DrawRectangle(XBrushes.Black, x + column * cell, y + row * cell, cell + 0.05, cell + 0.05);
            }
        }
    }
}
