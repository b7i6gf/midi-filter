using System;
using System.Windows.Forms;

namespace MidiFilter;

internal static class Program
{
    /// <summary>
    /// Application entry point. Configures WinForms (visual styles, DPI, text rendering)
    /// through the generated ApplicationConfiguration, then runs the main window.
    /// Called by the .NET runtime on process startup.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
