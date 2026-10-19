using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kancelyariya.Services;

namespace Kancelyariya;

public partial class App : Application
{
    private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Глобальный перехват ошибок для предотвращения вылетов приложения
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string dbPath = Path.Combine(appDir, "data", "kancelyariya.db");
            string backupDir = Path.Combine(appDir, "backups");
            using var store = new DatabaseStore(dbPath, backupDir);
            store.SeedDemo(force: true);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"Произошла непредвиденная ошибка:\n{e.Exception.Message}\n\nПриложение продолжит работу. Подробности записаны в crash.log",
            "Внимание",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("AppDomainUnhandledException", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            string log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n" +
                         "--------------------------------------------------\n";
            File.AppendAllText(LogFile, log);
        }
        catch { }
    }
}
