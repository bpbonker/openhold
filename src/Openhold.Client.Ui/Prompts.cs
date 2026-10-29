using System.Windows.Forms;

namespace Openhold.Client.Ui;

/// <summary>Plain-language messages and questions shown over the CAD application's main window.</summary>
public static class Prompts
{
    /// <summary>A Yes/No question with No as the default button, so pressing Enter never does anything risky.</summary>
    public static bool Confirm(IntPtr owner, string title, string message) =>
        MessageBox.Show(new OwnerWindow(owner), message, $"Openhold: {title}", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static void Inform(IntPtr owner, string message) =>
        MessageBox.Show(new OwnerWindow(owner), message, "Openhold", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private sealed class OwnerWindow(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
