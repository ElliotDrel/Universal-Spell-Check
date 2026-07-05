using Forms = System.Windows.Forms;

namespace UniversalSpellCheck;

internal sealed class UpdatePromptForm : Forms.Form
{
    public UpdatePromptForm(string version, Action install)
    {
        Text = "Update ready";
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new System.Drawing.Size(360, 150);
        BackColor = System.Drawing.Color.White;

        var title = new Forms.Label
        {
            AutoSize = true,
            Font = new System.Drawing.Font("Segoe UI", 13, System.Drawing.FontStyle.Bold),
            Location = new System.Drawing.Point(24, 22),
            Text = $"Universal Spell Check v{version} is ready",
        };
        var detail = new Forms.Label
        {
            AutoSize = true,
            Font = new System.Drawing.Font("Segoe UI", 9),
            ForeColor = System.Drawing.Color.FromArgb(75, 85, 99),
            Location = new System.Drawing.Point(25, 56),
            Text = "The update is downloaded. The app will restart.",
        };
        var installButton = new Forms.Button
        {
            AutoSize = true,
            Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold),
            Location = new System.Drawing.Point(238, 96),
            Padding = new Forms.Padding(10, 3, 10, 3),
            Text = "Install now",
        };
        installButton.Click += (_, _) => install();

        Controls.Add(title);
        Controls.Add(detail);
        Controls.Add(installButton);
        AcceptButton = installButton;
    }
}
