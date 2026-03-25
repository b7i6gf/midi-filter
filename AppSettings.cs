using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidiFilter;

/// <summary>
/// Reads and writes persistent user settings (last selected MIDI devices and filter options)
/// to a simple key=value file next to the executable (settings.cfg).
/// Called by MainForm on startup and when the user starts the filter.
/// </summary>
internal static class AppSettings
{
    private static readonly string SettingsDir =
        AppContext.BaseDirectory;

    private static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.cfg");

    private const string KeyInput      = "LastInput";
    private const string KeyOutput     = "LastOutput";
    private const string KeyBlockedCCs = "BlockedCCs";

    /// <summary>
    /// Returns the last saved MIDI input device name, or null if none saved.
    /// Called by MainForm.PopulateDevices on startup.
    /// </summary>
    public static string? LoadInput()  => Read(KeyInput);

    /// <summary>
    /// Returns the last saved MIDI output device name, or null if none saved.
    /// Called by MainForm.PopulateDevices on startup.
    /// </summary>
    public static string? LoadOutput() => Read(KeyOutput);

    /// <summary>
    /// Returns the saved set of blocked CC numbers, or null if no entry exists in the file.
    /// A null return means the caller should apply its own default (all CCs blocked).
    /// Called by MainForm on startup to restore checkbox states.
    /// </summary>
    public static HashSet<int>? LoadBlockedCCs()
    {
        string? raw = Read(KeyBlockedCCs);
        if (raw == null)
            return null;

        var result = new HashSet<int>();
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int cc))
                result.Add(cc);
        }
        return result;
    }

    /// <summary>
    /// Persists the selected input and output device names to disk.
    /// Called by MainForm.OnStartClick when the user starts the filter.
    /// </summary>
    public static void Save(string inputName, string outputName)
    {
        Write(KeyInput,  inputName);
        Write(KeyOutput, outputName);
    }

    /// <summary>
    /// Persists the currently blocked CC numbers as a comma-separated list.
    /// Called by MainForm.OnStartClick when the user starts the filter.
    /// </summary>
    public static void SaveBlockedCCs(HashSet<int> blockedCCs)
    {
        string value = string.Join(",", blockedCCs.OrderBy(x => x));
        Write(KeyBlockedCCs, value);
    }

    /// <summary>
    /// Reads a single value by key from the settings file.
    /// Returns null if the file or key does not exist.
    /// </summary>
    private static string? Read(string key)
    {
        if (!File.Exists(SettingsFile))
            return null;

        foreach (string line in File.ReadAllLines(SettingsFile))
        {
            int sep = line.IndexOf('=');
            if (sep < 1) continue;

            string k = line[..sep].Trim();
            string v = line[(sep + 1)..].Trim();

            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(v) ? null : v;
        }

        return null;
    }

    /// <summary>
    /// Writes or updates a single key=value pair in the settings file.
    /// Creates the directory and file if they do not yet exist.
    /// </summary>
    private static void Write(string key, string value)
    {
        Directory.CreateDirectory(SettingsDir);

        string[] existing = File.Exists(SettingsFile)
            ? File.ReadAllLines(SettingsFile)
            : Array.Empty<string>();

        bool found = false;
        var lines = new List<string>();

        foreach (string line in existing)
        {
            int sep = line.IndexOf('=');
            if (sep > 0 && string.Equals(line[..sep].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{key}={value}");
                found = true;
            }
            else
            {
                lines.Add(line);
            }
        }

        if (!found)
            lines.Add($"{key}={value}");

        File.WriteAllLines(SettingsFile, lines);
    }
}
