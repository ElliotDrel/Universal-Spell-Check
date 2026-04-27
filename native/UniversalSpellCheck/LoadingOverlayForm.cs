namespace UniversalSpellCheck;

internal sealed class LoadingOverlayForm : Form
{
    private readonly ProgressBar _progressBar = new();

    public LoadingOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(32, 34, 37);
        Padding = new Padding(12);
        ClientSize = new Size(300, 52);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 35;
        _progressBar.Size = new Size(40, 14);
        _progressBar.Anchor = AnchorStyles.Left;

        var label = new Label
        {
            Text = "Spell check loading...",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Regular),
            Anchor = AnchorStyles.Left
        };

        layout.Controls.Add(_progressBar, 0, 0);
        layout.Controls.Add(label, 1, 0);
        Controls.Add(layout);
    }

    public void ShowNearTaskbar()
    {
        var ownerScreen = Screen.FromControl(this);
        var screen = Screen.PrimaryScreen?.WorkingArea ?? ownerScreen.WorkingArea;
        Location = new Point(
            screen.Left + (screen.Width - Width) / 2,
            screen.Bottom - Height - 18);

        if (!Visible)
        {
            Show();
        }

        BringToFront();
    }
}
