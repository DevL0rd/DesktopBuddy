using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopBuddyManager;

internal sealed class DescriptionPromptForm : Form
{
    private readonly TextBox _descriptionBox;

    private DescriptionPromptForm()
    {
        Text = "Describe What Happened";
        ClientSize = new Size(560, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(24, 24, 30);
        ForeColor = Color.FromArgb(236, 236, 244);
        Font = new Font("Segoe UI", 9f);

        Controls.Add(new Label
        {
            Text = "Describe what happened before the issue or crash. This will be included in the support report.",
            Location = new Point(16, 16),
            Size = new Size(528, 40),
            ForeColor = ForeColor,
            BackColor = Color.Transparent,
        });

        _descriptionBox = new TextBox
        {
            Location = new Point(16, 64),
            Size = new Size(528, 220),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(34, 34, 42),
            ForeColor = ForeColor,
            AcceptsReturn = true,
            AcceptsTab = true,
        };
        Controls.Add(_descriptionBox);

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(100, 34),
            Location = new Point(336, 304),
        };
        Controls.Add(cancelButton);

        var createButton = new Button
        {
            Text = "Create Report",
            DialogResult = DialogResult.OK,
            Size = new Size(120, 34),
            Location = new Point(424, 304),
        };
        Controls.Add(createButton);

        AcceptButton = createButton;
        CancelButton = cancelButton;
    }

    internal static string? Prompt(IWin32Window owner)
    {
        using var form = new DescriptionPromptForm();
        return form.ShowDialog(owner) == DialogResult.OK
            ? form._descriptionBox.Text.Trim()
            : null;
    }
}
