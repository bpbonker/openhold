using System.Runtime.InteropServices;
using Inventor;
using Microsoft.Win32;

namespace Openhold.Client.Cad;

/// <summary>Runs work against Inventor's Apprentice Server, which reads and writes Inventor files without Inventor.</summary>
internal static class Apprentice
{
    // Inventor's own interop assembly must be found first (Openhold doesn't ship it); only then can its types be touched.
    private static readonly Lazy<bool> InstalledLazy = new(() => InventorInterop.InstalledPath() is not null && Registered());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool Registered() =>
        Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{typeof(ApprenticeServerComponentClass).GUID}}}") is not null;

    public static bool Installed => InstalledLazy.Value;

    /// <summary>
    /// Runs <paramref name="work"/> with a fresh Apprentice instance on its own STA thread (Apprentice is a
    /// single-threaded COM component). Failures surface as <see cref="IOException"/> with <paramref name="what"/>.
    /// </summary>
    public static T Run<T>(string what, Func<ApprenticeServerComponent, T> work)
    {
        T result = default!;
        Exception? failure = null;
        // Every exception is caught on this thread: one escaping a background thread would end the whole program.
        var thread = new Thread(() =>
        {
            ApprenticeServerComponent? apprentice = null;
            try
            {
                apprentice = new ApprenticeServerComponent();
                result = work(apprentice);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (apprentice is not null)
                {
                    try
                    {
                        apprentice.Close();
                    }
                    catch (COMException)
                    {
                        // Closing after a failure can fail too; the first error is the one worth reporting.
                    }
                    Marshal.FinalReleaseComObject(apprentice);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure switch
        {
            null => result,
            IOException io => throw io,
            _ => throw new IOException($"Couldn't {what}: {failure.Message}", failure),
        };
    }
}
