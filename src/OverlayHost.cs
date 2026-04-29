namespace UniversalSpellCheck;

// Owns a dedicated STA background thread with its own message loop and a
// pre-constructed LoadingOverlayForm. Show/hide are queued via BeginInvoke
// so the spellcheck hot path returns immediately. The form is reused across
// every hotkey — never disposed and recreated.
internal sealed class OverlayHost : IDisposable
{
    private LoadingOverlayForm? _form;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    public OverlayHost()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "OverlayUIThread"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        _form = new LoadingOverlayForm();
        _ = _form.Handle; // force handle creation on this thread
        _ready.Set();
        Application.Run();
    }

    public void Show()
    {
        var f = _form;
        if (_disposed || f is null || !f.IsHandleCreated) return;
        try
        {
            f.BeginInvoke(new Action(() =>
            {
                if (!f.IsDisposed)
                {
                    f.ShowNearTaskbar();
                }
            }));
        }
        catch
        {
            // best-effort; never break the hot path
        }
    }

    public void Hide()
    {
        var f = _form;
        if (_disposed || f is null || !f.IsHandleCreated) return;
        try
        {
            f.BeginInvoke(new Action(() =>
            {
                if (!f.IsDisposed && f.Visible)
                {
                    f.Hide();
                }
            }));
        }
        catch
        {
            // best-effort
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var f = _form;
        if (f is null) return;
        try
        {
            f.BeginInvoke(new Action(() =>
            {
                if (!f.IsDisposed)
                {
                    f.Close();
                }
                Application.ExitThread();
            }));
        }
        catch
        {
            // last resort
        }
    }
}
