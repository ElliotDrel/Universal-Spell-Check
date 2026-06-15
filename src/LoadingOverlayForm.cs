using System.Diagnostics;

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
    private readonly Label _label;
    private readonly Label _elapsedLabel;
    private readonly Stopwatch _elapsed = new();
    private readonly System.Windows.Forms.Timer _elapsedTimer = new()
    {
        Interval = 100
    };

    public LoadingOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(32, 34, 37);
        Padding = new Padding(12);

        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 35;
        _progressBar.Size = new Size(40, 14);
        _progressBar.Anchor = AnchorStyles.Left;

        _label = new Label
        {
            Text = PhaseText(SpellcheckPhase.Copying),
            ForeColor = Color.White,
            Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Regular),
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _elapsedLabel = new Label
        {
            Text = "0.0s",
            ForeColor = Color.FromArgb(190, 190, 190),
            Font = new Font(FontFamily.GenericMonospace, 9, FontStyle.Regular),
            Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();

        // Size the box once to the widest phase text so it hugs the content
        // and never resizes (or drifts off-center) when the label changes
        // mid-run. The label is locked to that size (AutoSize off, +8px
        // slack) so layout rounding can never clamp it into wrapping —
        // the text is always a single line.
        const int barColumnWidth = 52;
        var maxText = Size.Empty;
        foreach (var phase in new[]
        {
            SpellcheckPhase.Copying,
            SpellcheckPhase.Sending,
            SpellcheckPhase.Waiting,
            SpellcheckPhase.Pasting
        })
        {
            var measured = TextRenderer.MeasureText(PhaseText(phase), _label.Font);
            maxText.Width = Math.Max(maxText.Width, measured.Width);
            maxText.Height = Math.Max(maxText.Height, measured.Height);
        }
        _label.AutoSize = false;
        _label.Size = new Size(maxText.Width + 8, maxText.Height);
        var elapsedSize = TextRenderer.MeasureText("999.9s", _elapsedLabel.Font);
        _elapsedLabel.Size = new Size(elapsedSize.Width + 4, maxText.Height);
        ClientSize = new Size(
            Padding.Left + barColumnWidth + _label.Width + _elapsedLabel.Width + Padding.Right,
            52);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, barColumnWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _elapsedLabel.Width));

        layout.Controls.Add(_progressBar, 0, 0);
        layout.Controls.Add(_label, 1, 0);
        layout.Controls.Add(_elapsedLabel, 2, 0);
        Controls.Add(layout);
    }

    private static string PhaseText(SpellcheckPhase phase) => phase switch
    {
        SpellcheckPhase.Copying => "Copying text...",
        SpellcheckPhase.Sending => "Sending to AI...",
        SpellcheckPhase.Waiting => "Waiting for AI...",
        SpellcheckPhase.Pasting => "Pasting...",
        _ => ""
    };

    // Visibility timing matches the old Show/Hide pair exactly: Copying shows
    // the form (run start), Done hides it (run end). Request/paste phases only
    // swap the label text — they never touch visibility.
    public void SetPhase(SpellcheckPhase phase)
    {
        switch (phase)
        {
            case SpellcheckPhase.Copying:
                _label.Text = PhaseText(phase);
                _elapsed.Restart();
                UpdateElapsedText();
                _elapsedTimer.Start();
                ShowNearTaskbar();
                break;
            case SpellcheckPhase.Sending:
            case SpellcheckPhase.Waiting:
            case SpellcheckPhase.Pasting:
                _label.Text = PhaseText(phase);
                break;
            case SpellcheckPhase.Done:
                _elapsedTimer.Stop();
                _elapsed.Stop();
                UpdateElapsedText();
                if (Visible) Hide();
                break;
        }
    }

    private void UpdateElapsedText()
    {
        _elapsedLabel.Text = $"{_elapsed.Elapsed.TotalSeconds:0.0}s";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _elapsedTimer.Dispose();
            _label.Font.Dispose();
            _elapsedLabel.Font.Dispose();
        }
        base.Dispose(disposing);
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
