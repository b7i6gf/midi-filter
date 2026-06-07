using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.Midi;

namespace MidiFilter;

/// <summary>
/// Core MIDI filter engine. Reads from a named MIDI input, filters specified CCs
/// and notes (individually or all notes at once), and forwards all other messages
/// to a named MIDI output.
/// Called by MainForm to start/stop filtering and receive status updates.
/// </summary>
public class MidiFilterEngine : IDisposable
{
    // CCs to block on all channels - updated at runtime via SetBlockedCCs.
    // The set is swapped atomically (never mutated in place), so a volatile
    // reference read is enough for the MIDI thread to see the latest set.
    private volatile HashSet<int> _blockedCCs = new() { 11, 64, 66, 69 };

    /// <summary>
    /// Replaces the active blocked CC set. Takes effect immediately on the next message.
    /// Called by MainForm whenever a checkbox is toggled.
    /// </summary>
    public void SetBlockedCCs(HashSet<int> ccs) => _blockedCCs = ccs;

    // Note numbers to block on all channels (Note On and Note Off), swapped atomically.
    private volatile HashSet<int> _blockedNotes = new();

    // When true, every note message is blocked regardless of _blockedNotes.
    private volatile bool _blockAllNotes;

    /// <summary>
    /// Replaces the active blocked-note set. Takes effect on the next message.
    /// Called by MainForm whenever a note filter is toggled.
    /// </summary>
    public void SetBlockedNotes(HashSet<int> notes) => _blockedNotes = notes;

    /// <summary>
    /// Enables or disables blocking of all note messages.
    /// Called by MainForm when the "All Notes" filter is toggled.
    /// </summary>
    public void SetBlockAllNotes(bool value) => _blockAllNotes = value;

    private MidiIn?  _midiIn;
    private MidiOut? _midiOut;
    private Thread?  _watcherThread;
    private volatile bool _running;
    private volatile bool _connected;

    // Signals the watcher thread to wake immediately, so Stop does not have to wait
    // out the full poll interval before the thread can exit and be joined.
    private readonly ManualResetEventSlim _wakeSignal = new(false);

    // Running total of filtered messages. Incremented on the MIDI thread without any
    // allocation or UI marshaling, and read by MainForm on a UI timer.
    private long _filteredCount;
    public long FilteredCount => Interlocked.Read(ref _filteredCount);

    // Time-based gate for log lines: at most one logged message per LogMinIntervalMs,
    // independent of message rate. Keeps the log readable and the UI cheap when a high
    // volume of notes is filtered (for example with All Notes enabled). The counter
    // above stays exact regardless of this gate.
    private long _lastLogTick;
    private const long LogMinIntervalMs = 150;

    // Note names in Pianoteq style (flats), with C4 = note 60 and range C-1 to G9.
    private static readonly string[] NoteNames =
        { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    // Timestamp of the last failed TryConnect attempt.
    // Used to enforce a cooldown before retrying after an error.
    private DateTime _lastConnectError = DateTime.MinValue;
    private static readonly TimeSpan ConnectErrorCooldown = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PollInterval         = TimeSpan.FromMilliseconds(1500);

    private string _inputName  = string.Empty;
    private string _outputName = string.Empty;

    public event Action<string>? StatusChanged;
    public event Action<bool>?   ConnectionChanged;
    public event Action<string>? MessageFiltered;

    public bool IsConnected => _connected;

    /// <summary>
    /// Starts the filter engine with the given input/output device names.
    /// Launches a background watcher thread that auto-reconnects on device loss.
    /// Called from MainForm when user clicks Start or Restart.
    /// </summary>
    public void Start(string inputName, string outputName)
    {
        // Ensure any previous run is fully stopped (and its thread joined) before
        // starting a new one, so we never end up with two watcher threads sharing
        // the device fields. This fixes the duplicate-thread leak on Restart.
        Stop();

        _inputName        = inputName;
        _outputName       = outputName;
        _lastConnectError = DateTime.MinValue;
        Interlocked.Exchange(ref _filteredCount, 0);
        _lastLogTick = 0;

        _wakeSignal.Reset();
        _running = true;

        _watcherThread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name         = "MidiFilterWatcher"
        };
        _watcherThread.Start();
    }

    /// <summary>
    /// Stops the filter engine, waits for the watcher thread to exit, then disposes
    /// all MIDI resources. Safe to call repeatedly.
    /// Called from MainForm when user clicks Stop, Restart, or closes the window.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _wakeSignal.Set();

        Thread? t = _watcherThread;
        if (t != null && t.IsAlive)
            t.Join(TimeSpan.FromSeconds(5));
        _watcherThread = null;

        Disconnect();
    }

    /// <summary>
    /// Background loop that continuously checks device availability and reconnects.
    /// When connected, actively verifies the input device is still present in the OS
    /// device list - catches the case where Synthesia closes silently without triggering
    /// any NAudio error or message event.
    /// Respects a cooldown after a connection error to avoid hammering a port that
    /// Windows has not yet fully released (fixes "unspecifiedError calling midioutopen").
    /// Runs on _watcherThread.
    /// </summary>
    private void WatchLoop()
    {
        while (_running)
        {
            if (_connected)
            {
                // Active liveness check: verify the input device still exists in the OS.
                // When Synthesia closes, its virtual MIDI port disappears from the device
                // list even though NAudio raises no error - this catches that case.
                if (FindDeviceId(_inputName, isInput: true) == -1)
                {
                    ReportStatus($"Input lost: \"{_inputName}\", reconnecting...");
                    Disconnect();
                }
            }
            else
            {
                // Enforce cooldown after an error so Windows has time to fully release
                // the MIDI port before we attempt to open it again.
                bool inCooldown = _lastConnectError != DateTime.MinValue
                    && DateTime.UtcNow - _lastConnectError < ConnectErrorCooldown;

                if (!inCooldown)
                    TryConnect();
            }

            // Wait until the next poll, but wake immediately when Stop signals.
            _wakeSignal.Wait(PollInterval);
        }
    }

    /// <summary>
    /// Attempts to find and open the configured input and output devices by name.
    /// On exception, records the error timestamp to trigger the cooldown in WatchLoop.
    /// Reports status via StatusChanged event.
    /// Called by WatchLoop.
    /// </summary>
    private void TryConnect()
    {
        try
        {
            int inputId  = FindDeviceId(_inputName,  isInput: true);
            int outputId = FindDeviceId(_outputName, isInput: false);

            if (inputId == -1)
            {
                ReportStatus($"Waiting for Input: \"{_inputName}\"...");
                return;
            }

            if (outputId == -1)
            {
                ReportStatus($"Waiting for Output: \"{_outputName}\"...");
                return;
            }

            Disconnect();

            _midiOut = new MidiOut(outputId);
            _midiIn  = new MidiIn(inputId);
            _midiIn.MessageReceived += OnMessageReceived;
            _midiIn.ErrorReceived   += OnErrorReceived;
            _midiIn.Start();

            SetConnected(true);
            ReportStatus($"Connected: \"{_inputName}\" -> Filter -> \"{_outputName}\"");
        }
        catch (Exception ex)
        {
            _lastConnectError = DateTime.UtcNow;
            ReportStatus($"Connection Error: {ex.Message} (retrying in {ConnectErrorCooldown.TotalSeconds}s...)");
            Disconnect();
        }
    }

    /// <summary>
    /// Handles incoming MIDI messages. Filters blocked CCs and notes (or all notes),
    /// forwards everything else. Runs on the MIDI thread, so the path stays allocation
    /// free: it only counts via Interlocked and builds a log string when the time gate
    /// in ShouldLog allows it.
    /// Called by NAudio on MIDI message receipt.
    /// </summary>
    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        try
        {
            int status  = e.RawMessage & 0xFF;
            int type    = status & 0xF0;
            int channel = (status & 0x0F) + 1;

            // CC messages: status 0xB0-0xBF
            if (type == 0xB0)
            {
                int cc = (e.RawMessage >> 8) & 0x7F;
                if (_blockedCCs.Contains(cc))
                {
                    Interlocked.Increment(ref _filteredCount);
                    if (ShouldLog())
                        MessageFiltered?.Invoke($"Blocked: CC{cc} (Channel {channel})");
                    return;
                }
            }
            // Note On (0x90-0x9F) and Note Off (0x80-0x8F); Note On velocity 0 is covered.
            else if (type == 0x90 || type == 0x80)
            {
                int note = (e.RawMessage >> 8) & 0x7F;
                if (_blockAllNotes || _blockedNotes.Contains(note))
                {
                    Interlocked.Increment(ref _filteredCount);
                    if (ShouldLog())
                        MessageFiltered?.Invoke($"Blocked: Note {note} ({NoteName(note)}) (Channel {channel})");
                    return;
                }
            }

            _midiOut?.Send(e.RawMessage);
        }
        catch
        {
            // Device was likely disconnected - trigger reconnect.
            SetConnected(false);
            ReportStatus("Connection lost, reconnecting...");
        }
    }

    /// <summary>
    /// Time gate for log output: returns true at most once per LogMinIntervalMs.
    /// Keeps the activity log and UI cheap under heavy filtering. Uses a monotonic
    /// millisecond tick, so it never allocates.
    /// Called from OnMessageReceived on the MIDI thread.
    /// </summary>
    private bool ShouldLog()
    {
        long now = Environment.TickCount64;
        if (now - _lastLogTick < LogMinIntervalMs)
            return false;
        _lastLogTick = now;
        return true;
    }

    /// <summary>
    /// Returns the Pianoteq-style note name for a MIDI note number (for example 60 to "C4").
    /// Called from OnMessageReceived for note log lines, and by MainForm for filter labels.
    /// </summary>
    public static string NoteName(int note)
    {
        int octave = note / 12 - 1;
        return NoteNames[note % 12] + octave;
    }

    /// <summary>
    /// Handles MIDI input errors. Triggers reconnect cycle.
    /// Called by NAudio on MIDI error.
    /// </summary>
    private void OnErrorReceived(object? sender, MidiInMessageEventArgs e)
    {
        SetConnected(false);
        ReportStatus("MIDI Error, reconnecting...");
    }

    /// <summary>
    /// Updates the connection flag and raises ConnectionChanged only on an actual
    /// state transition, avoiding redundant UI updates during reconnect polling.
    /// Called from TryConnect, Disconnect, and the MIDI callbacks.
    /// </summary>
    private void SetConnected(bool value)
    {
        if (_connected == value)
            return;
        _connected = value;
        ConnectionChanged?.Invoke(value);
    }

    /// <summary>
    /// Safely closes and disposes current MIDI in/out devices.
    /// Called before reconnect attempts and on Stop.
    /// </summary>
    private void Disconnect()
    {
        SetConnected(false);

        try { _midiIn?.Stop();     } catch { }
        try { _midiIn?.Dispose();  } catch { }
        try { _midiOut?.Dispose(); } catch { }

        _midiIn  = null;
        _midiOut = null;
    }

    /// <summary>
    /// Searches for a MIDI device by partial name match (case-insensitive).
    /// Returns device index or -1 if not found.
    /// Called by TryConnect and WatchLoop.
    /// </summary>
    private static int FindDeviceId(string name, bool isInput)
    {
        if (isInput)
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                if (MidiIn.DeviceInfo(i).ProductName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        else
        {
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                if (MidiOut.DeviceInfo(i).ProductName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Fires the StatusChanged event on the calling thread.
    /// Called throughout the engine to report state changes.
    /// </summary>
    private void ReportStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    /// <summary>
    /// Returns all currently available MIDI input device names.
    /// Called by MainForm to populate dropdowns.
    /// </summary>
    public static List<string> GetInputDevices()
    {
        var list = new List<string>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            list.Add(MidiIn.DeviceInfo(i).ProductName);
        return list;
    }

    /// <summary>
    /// Returns all currently available MIDI output device names.
    /// Called by MainForm to populate dropdowns.
    /// </summary>
    public static List<string> GetOutputDevices()
    {
        var list = new List<string>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            list.Add(MidiOut.DeviceInfo(i).ProductName);
        return list;
    }

    public void Dispose()
    {
        Stop();
        _wakeSignal.Dispose();
    }
}
