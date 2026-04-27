namespace UniversalSpellCheck;

internal sealed class LoadingOverlayForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);
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

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return createParams;
        }
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
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }

        SetWindowPos(
            Handle,
            HwndTopmost,
            Location.X,
            Location.Y,
            Width,
            Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
