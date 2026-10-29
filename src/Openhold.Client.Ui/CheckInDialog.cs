using System.Drawing;
using System.Windows.Forms;

namespace Openhold.Client.Ui;

/// <summary>
/// The check-in dialog: any title-block warning, the properties Openhold read from the drawing, a comment
/// and "keep checked out". A dialog rather than command-line prompts, because the command line shows only
/// a few lines and is easy to type into at the wrong moment. Enter checks in; Esc cancels.
/// </summary>
public sealed class CheckInDialog : Form
{
    private readonly TextBox _comment;
    private readonly CheckBox _keepCheckedOut;

    public CheckInDialog(string fileName, IReadOnlyList<string> warnings, IReadOnlyDictionary<string, string> properties)
    {
        Text = $"Openhold: Check in {fileName}";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, MaximumSize = new Size(560, 0) };

        if (warnings.Count > 0)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(540, 0),
                BackColor = Color.FromArgb(255, 244, 206),
                ForeColor = Color.FromArgb(92, 60, 0),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 0, 10),
                Text = "The title blocks don't all agree:\n\n" +
                       string.Join("\n", warnings.Select(w => "•  " + w)) +
                       "\n\nOpenhold never changes title blocks for you. Until the sheets agree, the vault won't record a single value for these. You can still check in.",
            });
        }

        if (properties.Count > 0)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(540, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 10),
                Text = "From the title blocks:  " + string.Join("   ·   ", properties.Select(p => $"{p.Key}: {p.Value}")),
            });
        }

        layout.Controls.Add(new Label { AutoSize = true, Text = "What changed?", Margin = new Padding(0, 0, 0, 4) });
        _comment = new TextBox { Width = 540, Height = 70, Multiline = true, AcceptsReturn = false, ScrollBars = ScrollBars.Vertical };
        layout.Controls.Add(_comment);

        _keepCheckedOut = new CheckBox { AutoSize = true, Text = "Keep checked out so I can carry on editing", Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(_keepCheckedOut);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var checkIn = new Button { Text = "Check In", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(checkIn);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = checkIn;
        CancelButton = cancel;
        Shown += (_, _) => _comment.Focus();
    }

    public string Comment => _comment.Text.Trim();

    public bool KeepCheckedOut => _keepCheckedOut.Checked;
}
