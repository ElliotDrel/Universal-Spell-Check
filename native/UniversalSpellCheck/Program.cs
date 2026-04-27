namespace UniversalSpellCheck;

static class Program
{
    private const string AppMutexName = "UniversalSpellCheck.NativeSpike";

    [STAThread]
    static void Main()
    {
        using var appMutex = new Mutex(true, AppMutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.Forms.MessageBox.Show(
                "Universal Spell Check native spike is already running.",
                "Universal Spell Check",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new SpellCheckAppContext());
    }
}
