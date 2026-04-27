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
            MessageBox.Show(
                "Universal Spell Check native spike is already running.",
                "Universal Spell Check",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SpellCheckAppContext());
    }
}
