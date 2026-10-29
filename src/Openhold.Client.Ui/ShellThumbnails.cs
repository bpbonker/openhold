using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Openhold.Client.Ui;

/// <summary>
/// Thumbnails from the Windows shell, the same images Windows Explorer shows. CAD applications install the
/// handlers (AutoCAD for DWG, Inventor for IPT/IAM/IDW), so this works for every file type the PC can preview.
/// Call from an STA thread (a UI thread): some thumbnail handlers require it.
/// </summary>
public static class ShellThumbnails
{
    /// <summary>A PNG thumbnail of the file, or null when Windows has no thumbnail for it (only a generic icon).</summary>
    public static byte[]? TryCreatePng(string path, int size = 256)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                if (factory.GetImage(new NativeSize(size, size), ThumbnailOnly | BiggerSizeOk, out var bitmapHandle) != 0)
                    return null;
                try
                {
                    using var bitmap = Image.FromHbitmap(bitmapHandle);
                    using var png = new MemoryStream();
                    bitmap.Save(png, ImageFormat.Png);
                    return png.ToArray();
                }
                finally
                {
                    DeleteObject(bitmapHandle);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            // No handler, or the file can't be read: a missing thumbnail must never stop a check-in.
            return null;
        }
    }

    private const int BiggerSizeOk = 0x1;
    private const int ThumbnailOnly = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
