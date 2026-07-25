using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidiFilter;

/// <summary>
/// Kind of a custom filter entry: a single CC, a single note, or all notes at once.
/// </summary>
public enum FilterKind { Cc, Note, AllNotes }

/// <summary>
/// One persisted custom filter entry (CC number, note number, or All Notes) plus its
/// active state. Used by MainForm and serialized by AppSettings.
/// </summary>
public sealed class CustomFilter
{
    public FilterKind Kind   { get; }
    public int        Value  { get; }   // CC or note number; ignored for AllNotes
    public bool       Active { get; set; }
    public string     Name   { get; set; }  // optional user-given name, "" if none

    public CustomFilter(FilterKind kind, int value, bool active, string name = "")
    {
        Kind   = kind;
        Value  = value;
        Active = active;
        Name   = name ?? string.Empty;
    }
}

/// <summary>
/// Reads and writes persistent user settings (last selected MIDI devices and filter options)
/// to a simple key=value file next to the executable (settings.cfg).
/// The file is parsed once into an in-memory cache; reads hit the cache and writes update
/// the cache and persist it in a single pass. All file access is guarded so a non-writable
/// or locked location never crashes the app (settings simply will not persist in that case).
/// Called by MainForm on startup and whenever devices or filters change.
/// </summary>
internal static class AppSettings
{
    private static readonly string SettingsDir =
        AppContext.BaseDirectory;

    private static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.cfg");

    private const string KeyInput         = "LastInput";
    private const string KeyOutput        = "LastOutput";
    private const string KeyBlockedCCs    = "BlockedCCs";
    private const string KeyCustomFilters = "CustomFilters";
    private const string KeyLogEnabled    = "LogEnabled";

    // Lazily loaded key/value cache; null until first access.
    private static Dictionary<string, string>? _cache;

    /// <summary>
    /// Returns the in-memory settings cache, loading and parsing the file on first use.
    /// An unreadable or missing file yields an empty cache so callers fall back to defaults.
    /// Called by every Read/Save in this class.
    /// </summary>
    private static Dictionary<string, string> Cache()
    {
        if (_cache != null)
            return _cache;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(SettingsFile))
            {
                foreach (string line in File.ReadAllLines(SettingsFile))
                {
                    int sep = line.IndexOf('=');
                    if (sep < 1)
                        continue;

                    string k = line[..sep].Trim();
                    string v = line[(sep + 1)..].Trim();
                    if (k.Length > 0)
                        dict[k] = v;
                }
            }
        }
        catch
        {
            // Unreadable file - treat as empty so defaults apply.
        }

        _cache = dict;
        return dict;
    }

    /// <summary>
    /// Returns the last saved MIDI input device name, or null if none saved.
    /// Called by MainForm.PopulateDevices on startup.
    /// </summary>
    public static string? LoadInput() => Read(KeyInput);

    /// <summary>
    /// Returns the last saved MIDI output device name, or null if none saved.
    /// Called by MainForm.PopulateDevices on startup.
    /// </summary>
    public static string? LoadOutput() => Read(KeyOutput);

    /// <summary>
    /// Returns the saved set of blocked CC numbers, or null if the key does not exist yet.
    /// An existing but empty entry means "all fixed pedals off" and returns an empty set,
    /// so that state survives a restart instead of falling back to the all-on default.
    /// Called by MainForm on startup to restore toggle states.
    /// </summary>
    public static HashSet<int>? LoadBlockedCCs()
    {
        if (!Cache().TryGetValue(KeyBlockedCCs, out string? raw) || raw == null)
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
    /// Returns the saved activity-log state, defaulting to enabled when nothing is saved.
    /// Called by MainForm before building the window (the layout depends on it).
    /// </summary>
    public static bool LoadLogEnabled()
    {
        if (!Cache().TryGetValue(KeyLogEnabled, out string? raw) || string.IsNullOrEmpty(raw))
            return true;
        return raw.Trim() != "0";
    }

    /// <summary>
    /// Persists the activity-log state.
    /// Called by MainForm when the log is switched on or off.
    /// </summary>
    public static void SaveLogEnabled(bool enabled)
    {
        Cache()[KeyLogEnabled] = enabled ? "1" : "0";
        Persist();
    }

    /// <summary>
    /// Persists the selected input and output device names in a single write.
    /// Called by MainForm when the filter starts, on restart, and on form close.
    /// </summary>
    public static void Save(string inputName, string outputName)
    {
        Cache()[KeyInput]  = inputName;
        Cache()[KeyOutput] = outputName;
        Persist();
    }

    /// <summary>
    /// Returns the saved custom filter entries, or null if the key does not exist yet.
    /// An existing but empty entry returns an empty list (user removed all custom filters).
    /// Called by MainForm on startup to restore the custom filters.
    /// </summary>
    public static List<CustomFilter>? LoadCustomFilters()
    {
        if (!Cache().TryGetValue(KeyCustomFilters, out string? raw) || raw == null)
            return null;

        var list = new List<CustomFilter>();
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = part.Split(':');

            // allnotes:<0|1>[:name]
            if (f.Length >= 2 && f[0].Trim().Equals("allnotes", StringComparison.OrdinalIgnoreCase))
            {
                bool   active = f[1].Trim() == "1";
                string name   = f.Length >= 3 ? UnescapeName(f[2]) : string.Empty;
                list.Add(new CustomFilter(FilterKind.AllNotes, 0, active, name));
            }
            // cc:<num>:<0|1>[:name]  or  note:<num>:<0|1>[:name]
            else if (f.Length >= 3 && int.TryParse(f[1].Trim(), out int value))
            {
                string type   = f[0].Trim().ToLowerInvariant();
                bool   active = f[2].Trim() == "1";
                string name   = f.Length >= 4 ? UnescapeName(f[3]) : string.Empty;
                if (type == "cc" && value is >= 0 and <= 127)
                    list.Add(new CustomFilter(FilterKind.Cc, value, active, name));
                else if (type == "note" && value is >= 0 and <= 127)
                    list.Add(new CustomFilter(FilterKind.Note, value, active, name));
            }
        }
        return list;
    }

    /// <summary>
    /// Persists the fixed-pedal blocked CCs and the custom filter entries together in a
    /// single write. Called by MainForm whenever a filter changes, on start, and on close.
    /// </summary>
    public static void SaveFilters(HashSet<int> blockedCCs, IEnumerable<CustomFilter> customFilters)
    {
        Cache()[KeyBlockedCCs]    = string.Join(",", blockedCCs.OrderBy(x => x));
        Cache()[KeyCustomFilters] = SerializeCustom(customFilters);
        Persist();
    }

    /// <summary>
    /// Serializes custom filter entries to the compact "type:value:active[:name]" form.
    /// Called by SaveFilters.
    /// </summary>
    private static string SerializeCustom(IEnumerable<CustomFilter> customFilters)
    {
        var parts = new List<string>();
        foreach (CustomFilter f in customFilters)
        {
            string a      = f.Active ? "1" : "0";
            string suffix = string.IsNullOrEmpty(f.Name) ? string.Empty : ":" + EscapeName(f.Name);
            switch (f.Kind)
            {
                case FilterKind.Cc:       parts.Add($"cc:{f.Value}:{a}{suffix}");   break;
                case FilterKind.Note:     parts.Add($"note:{f.Value}:{a}{suffix}"); break;
                case FilterKind.AllNotes: parts.Add($"allnotes:{a}{suffix}");       break;
            }
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// Escapes characters that would break the comma/colon-delimited custom-filter format
    /// (and newlines, since the settings file is line based). Reversed by UnescapeName.
    /// Called by SerializeCustom.
    /// </summary>
    private static string EscapeName(string s) => s
        .Replace("%", "%25")
        .Replace(",", "%2C")
        .Replace(":", "%3A")
        .Replace("\r", "%0D")
        .Replace("\n", "%0A");

    /// <summary>
    /// Reverses EscapeName.
    /// Called by LoadCustomFilters.
    /// </summary>
    private static string UnescapeName(string s) => s
        .Replace("%0A", "\n")
        .Replace("%0D", "\r")
        .Replace("%3A", ":")
        .Replace("%2C", ",")
        .Replace("%25", "%");

    /// <summary>
    /// Reads a single value by key from the cache. Returns null if missing or empty.
    /// Called by LoadInput and LoadOutput (where an empty value is meaningless).
    /// </summary>
    private static string? Read(string key)
    {
        Cache().TryGetValue(key, out string? value);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Writes the full cache to disk via a temporary file, so an interrupted write can
    /// never leave a half-written settings.cfg behind.
    /// Failures (read-only location, locked file) are swallowed so the app never crashes.
    /// Called by Save and SaveFilters.
    /// </summary>
    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);

            string tmp = SettingsFile + ".tmp";
            File.WriteAllLines(tmp, _cache!.Select(kv => $"{kv.Key}={kv.Value}"));

            if (File.Exists(SettingsFile))
                File.Replace(tmp, SettingsFile, null);
            else
                File.Move(tmp, SettingsFile);
        }
        catch
        {
            // Settings location not writable - ignore; settings will not persist this run.
        }
    }
}
