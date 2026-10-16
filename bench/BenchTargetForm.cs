using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal sealed class BenchTargetForm : Form
{
    private readonly TargetTextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, AcceptsReturn = true, AcceptsTab = true, MaxLength = 1000000 };
    internal long LastPasteTicks => _text.LastPasteTicks;
    internal string CurrentText => _text.Text;
    internal bool Foreground => GetForegroundWindow() == Handle;
    internal BenchTargetForm()
    {
        Text = "Universal Spell Check baseline — leave computer idle";
        Width = 700; Height = 250; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_text);
    }
    internal void LoadAndSelect(BenchCase c)
    {
        _text.Html = c.Html; _text.Text = c.Text;
        Activate(); _text.Focus(); _text.SelectAll();
    }
    internal void VerifySelection(string input)
    {
        if (!Foreground || !_text.Focused || _text.SelectionLength != _text.TextLength || _text.TextLength == 0
            || BenchHarness.Normalize(_text.Text) != BenchHarness.Normalize(input))
            throw new InvalidOperationException("Fixture is not foreground with the full input selected; refusing input injection.");
    }
    internal void Arm() => _text.LastPasteTicks = 0;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private sealed class TargetTextBox : TextBox
    {
        internal string Html = "";
        internal long LastPasteTicks;
        protected override void WndProc(ref Message m)
        {
            // A synthetic source supplies stable CF_HTML; this is not a claim about browser copy behavior.
            if (m.Msg == 0x301 && Html.Length > 0)
            {
                var data = new DataObject(); data.SetData(DataFormats.UnicodeText, SelectedText);
                data.SetData(DataFormats.Html, Html); Clipboard.SetDataObject(data, true); return;
            }
            base.WndProc(ref m);
            // WM_PASTE is observed even when unchanged text is replaced with identical text.
            if (m.Msg == 0x302 || (m.Msg == 0x102 && m.WParam == new IntPtr(0x16)))
                LastPasteTicks = Stopwatch.GetTimestamp();
        }
    }
}
