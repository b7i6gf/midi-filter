using System;
using System.IO;
using System.Windows.Forms;

namespace MidiFilter;

internal static class Program
{
    private static readonly string ErrorLog =
        Path.Combine(AppContext.BaseDirectory, "error.log");

    /// <summary>
    /// Application entry point. Installs global exception handlers (so a MIDI driver error
    /// is written to error.log instead of only appearing on the console), configures
    /// WinForms, then runs the main window.
    /// Called by the .NET runtime on process startup.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogFatal(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => LogFatal(e.ExceptionObject as Exception, fatal: true);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    /// <summary>
    /// Appends an exception to error.log next to the executable and informs the user.
    /// Never throws, so error handling can never take the app down itself.
    /// Called by the two global exception handlers in Main.
    /// </summary>
    private static void LogFatal(Exception? ex, bool fatal)
    {
        if (ex == null)
            return;

        try
        {
            File.AppendAllText(ErrorLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL" : "UI")} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }

        try
        {
            MessageBox.Show(
                $"An error occurred:{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Details were written to:{Environment.NewLine}{ErrorLog}",
                "MidiFilter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { }
    }
}
