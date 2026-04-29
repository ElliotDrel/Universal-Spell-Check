using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

/// <summary>
/// Hidden borderless window with a single TextBox. The bench loads the trial
/// input into the textbox, focuses + selects it, fires the hotkey, then waits
/// on TextChanged to know when the paste landed.
/// </summary>
internal sealed class BenchTargetForm : Form
{
    private readonly TextBox _textBox;
    public event EventHandler? TargetTextChanged;

    public BenchTargetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // Position offscreen-ish but still on a real monitor so focus is valid.
        // Fully offscreen windows can be denied focus on some Windows setups.
        Location = new System.Drawing.Point(0, 0);
        Size = new System.Drawing.Size(400, 100);
        Opacity = 0.01;  // effectively invisible but still focusable
        TopMost = true;

        _textBox = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            AcceptsReturn = true,
            AcceptsTab = true,
        };
        _textBox.TextChanged += (_, _) => TargetTextChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_textBox);
    }

    /// <summary>Current textbox contents.</summary>
    public string CurrentText => _textBox.Text;

    /// <summary>Replace the textbox text, select all, and focus it. Must run on the UI thread.</summary>
    public void LoadAndSelect(string text)
    {
        _textBox.Text = text;
        _textBox.SelectAll();
        _textBox.Focus();
        Activate();
    }
}
