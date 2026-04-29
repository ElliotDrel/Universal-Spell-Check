using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace UniversalSpellCheck;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Velopack's first-run / restart-after-update hooks must execute
        // before any other startup code. This is a no-op outside an installed
        // build, so it is safe for Dev / dotnet-run.
        VelopackApp.Build().Run();

        if (args.Contains("--dashboard-smoke", StringComparer.OrdinalIgnoreCase))
        {
            return RunDashboardSmoke();
        }

        var startupLogger = new DiagnosticsLogger(AppPaths.LogPath);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                startupLogger.Log(
                    $"appdomain_unhandled is_terminating={e.IsTerminating} " +
                    $"error_type={ex?.GetType().Name ?? "Unknown"} " +
                    $"error=\"{Escape(ex?.Message)}\" " +
                    $"stack=\"{Escape(ex?.ToString())}\"");
            }
            catch
            {
                // swallow — last resort logger
            }
        };

        using var appMutex = new Mutex(true, BuildChannel.MutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.Forms.MessageBox.Show(
                $"{BuildChannel.DisplayName} is already running.",
                BuildChannel.DisplayName,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            // Anchor WPF: instantiating System.Windows.Application registers the
            // pack:// scheme, sets Application.Current, and gives DynamicResource
            // a top-level Resources dictionary to fall back to. Without this,
            // resource lookups in Frame/Page Background bindings resolved to
            // DependencyProperty.UnsetValue and crashed the dashboard.
            var wpfApp = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            LoadGlobalWpfResources(wpfApp, startupLogger);
            startupLogger.Log("wpf_app_initialized");

            System.Windows.Forms.Application.Run(new SpellCheckAppContext());
            return 0;
        }
        catch (Exception ex)
        {
            startupLogger.Log(
                $"startup_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\" " +
                $"stack=\"{Escape(ex.ToString())}\"");
            throw;
        }
    }

    private static void LoadGlobalWpfResources(System.Windows.Application app, DiagnosticsLogger logger)
    {
        try
        {
            var styles = new System.Windows.ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/UniversalSpellCheck;component/UI/Styles.xaml",
                    UriKind.Absolute)
            };
            var components = new System.Windows.ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/UniversalSpellCheck;component/UI/Components.xaml",
                    UriKind.Absolute)
            };
            app.Resources.MergedDictionaries.Add(styles);
            app.Resources.MergedDictionaries.Add(components);
            logger.Log(
                "wpf_resources_loaded " +
                $"styles_keys={styles.Count} components_keys={components.Count}");
        }
        catch (Exception ex)
        {
            logger.Log(
                $"wpf_resources_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\" " +
                $"stack=\"{Escape(ex.ToString())}\"");
            throw;
        }
    }

    private static int RunDashboardSmoke()
    {
        try
        {
            var logger = new DiagnosticsLogger(Path.Combine(
                AppPaths.LogDirectory,
                $"dashboard-smoke-{DateTime.Now:yyyy-MM-dd-HHmmss}.log"));

            var wpfApp = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            LoadGlobalWpfResources(wpfApp, logger);

            var settingsStore = new SettingsStore(logger);
            var window = new UI.MainWindow(settingsStore, logger);

            Exception? renderError = null;
            wpfApp.Dispatcher.UnhandledException += (_, e) =>
            {
                renderError = e.Exception;
                e.Handled = true;
            };

            window.Show();
            // Pump the dispatcher so layout, resource resolution, and template
            // expansion actually execute. Show()+Close() alone never ran them,
            // which is why the smoke test was a false positive.
            PumpDispatcher(wpfApp.Dispatcher, TimeSpan.FromSeconds(3));
            window.Close();
            PumpDispatcher(wpfApp.Dispatcher, TimeSpan.FromMilliseconds(250));

            if (renderError is not null)
            {
                Console.Error.WriteLine(renderError);
                logger.Log(
                    $"dashboard_smoke_failed error_type={renderError.GetType().Name} " +
                    $"error=\"{Escape(renderError.Message)}\" " +
                    $"stack=\"{Escape(renderError.ToString())}\"");
                return 1;
            }

            Console.WriteLine("dashboard_smoke_ok");
            logger.Log("dashboard_smoke_ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PumpDispatcher(Dispatcher dispatcher, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
