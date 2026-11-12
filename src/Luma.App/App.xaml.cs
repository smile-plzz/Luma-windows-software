using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Luma.App;

public sealed record CrashReport(DateTime Timestamp, string OSVersion, string Message, string Details);

public partial class App : Application
{
    private Window? window;

    public App()
    {
        UnhandledException += (s, e) =>
        {
            LogCrash(e.Exception, e.Message);
            // In smoke tests or critical crashes, do not leave unhandled loop
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash(e.ExceptionObject as Exception, "AppDomain unhandled exception");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => WriteLog("XamlResourceReferenceFailed: " + e.Message);

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Failed to initialize application components.");
            throw;
        }
    }

    public static void WriteLog(string message)
    {
        try
        {
            var dir = AppSettings.LocalRoot;
            Directory.CreateDirectory(dir);
            var redacted = PathRedactor.Redact(message);
            File.AppendAllText(Path.Combine(dir, "startup-error.txt"), DateTime.UtcNow.ToString("O") + " " + redacted + Environment.NewLine);
        }
        catch { }
    }

    public static void LogCrash(Exception? exception, string? message = null)
    {
        try
        {
            var dir = AppSettings.LocalRoot;
            Directory.CreateDirectory(dir);

            var rawDetails = exception?.ToString() ?? message ?? "Unknown error";
            var redactedDetails = PathRedactor.Redact(rawDetails);
            var redactedMessage = PathRedactor.Redact(message ?? exception?.Message ?? "Unexpected error");

            // Write plain text report
            var textReport = $"Time: {DateTime.UtcNow:O}\nOS: {Environment.OSVersion}\nMessage: {redactedMessage}\n\n{redactedDetails}\n";
            File.WriteAllText(Path.Combine(dir, "last-error.txt"), textReport);

            // Write structured JSON report
            var jsonReport = new CrashReport(DateTime.UtcNow, Environment.OSVersion.ToString(), redactedMessage, redactedDetails);
            File.WriteAllText(Path.Combine(dir, "last-error.json"), JsonSerializer.Serialize(jsonReport, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Failed to launch main window.");
            throw;
        }
    }
}
