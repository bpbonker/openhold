using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Openhold.Inventor.AddIn;

/// <summary>
/// Ribbon icons drawn from the Windows icon font (white glyph on a brand-coloured rounded square),
/// so there are no image files to ship. Inventor takes icons as COM IPictureDisp.
/// </summary>
internal static class GlyphIcons
{
    private static readonly Color Brand = Color.FromArgb(0x1F, 0x5F, 0x8B);

    public static object Create(char glyph, int size)
    {
        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var radius = size / 5f;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
            path.AddArc(size - radius * 2 - 1, 0, radius * 2, radius * 2, 270, 90);
            path.AddArc(size - radius * 2 - 1, size - radius * 2 - 1, radius * 2, radius * 2, 0, 90);
            path.AddArc(0, size - radius * 2 - 1, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Brand);
            graphics.FillPath(fill, path);

            using var font = new Font(IconFontFamily(), size * 0.45f, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(glyph.ToString(), font, Brushes.White, new RectangleF(0, 0, size, size), format);
        }
        return PictureConverter.ToPicture(bitmap);
    }

    private static string IconFontFamily()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    /// <summary>AxHost exposes the only built-in Image → IPictureDisp conversion, as a protected member.</summary>
    private sealed class PictureConverter : AxHost
    {
        private PictureConverter() : base(string.Empty)
        {
        }

        public static object ToPicture(Image image) => GetIPictureDispFromPicture(image);
    }
}
