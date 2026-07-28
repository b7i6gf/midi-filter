using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Midi;

namespace MidiFilter;

/// <summary>
/// Core MIDI filter engine. Reads from a named MIDI input, filters specified CCs
/// and notes (individually or all notes at once), and forwards all other messages
/// to a named MIDI output.
/// Device handles are owned exclusively by the watcher thread logic and guarded by
/// _deviceLock plus a generation counter, so a Stop() during an in-flight connect can
/// never leave an orphaned open port behind (that was the state that required a full
/// application restart).
/// Called by MainForm to start/stop filtering and receive status updates.
/// </summary>
public class MidiFilterEngine : IDisposable
{
    // ---------------------------------------------------------------------
    // Filter state. Sets are swapped atomically (never mutated in place), so a
    // volatile reference read is enough for the MIDI thread to see the latest set.
    // ---------------------------------------------------------------------
    private volatile HashSet<int> _blockedCCs   = new() { 11, 64, 66, 69 };
    private volatile HashSet<int> _blockedNotes = new();
    private volatile bool         _blockAllNotes;

    /// <summary>
    /// Replaces the active blocked CC set. Takes effect immediately on the next message.
    /// Called by MainForm whenever a checkbox is toggled.
    /// </summary>
    public void SetBlockedCCs(HashSet<int> ccs) => _blockedCCs = ccs;

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

    // ---------------------------------------------------------------------
    // Device ownership
    // ---------------------------------------------------------------------
    private readonly object _deviceLock = new();
    private MidiIn?  _midiIn;    // guarded by _deviceLock for writes, read directly on MIDI thread
    private MidiOut? _midiOut;   // same
    private int      _generation;// guarded by _deviceLock; bumped on every Disconnect

    private Thread?       _watcherThread;
    private volatile bool _running;
    private volatile bool _connected;
    private volatile bool _disposed;

    // Set by the MIDI callbacks when sending fails or the driver reports an error.
    // The watcher thread owns the actual teardown, so the MIDI thread never blocks and
    // never floods the UI with one status message per dropped MIDI event.
    private volatile bool _faulted;

    // Signals the watcher thread to wake immediately: used by Stop (fast exit) and by
    // the fault path (fast reconnect instead of waiting out the poll interval).
    private readonly ManualResetEventSlim _wakeSignal = new(false);

    // Running total of filtered messages. Incremented on the MIDI thread without any
    // allocation or UI marshaling, and read by MainForm on a UI timer.
    private long _filteredCount;
    public long FilteredCount => Interlocked.Read(ref _filteredCount);

    // Time-based gate for log lines: at most one logged message per LogMinIntervalMs,
    // independent of message rate. Keeps the log readable and the UI cheap when a high
    // volume of notes is filtered (for example with All Notes enabled). The counter
    // above stays exact regardless of this gate. Repeated lines are collapsed by the log
    // view itself, so this gate can stay short without flooding the list.
    private long _lastLogTick;
    private const long LogMinIntervalMs = 60;

    // When false, no MessageFiltered events are raised at all (activity log disabled in
    // the UI). Skips the log string entirely on the MIDI thread. Set by MainForm.
    private volatile bool _logFiltered = true;

    /// <summary>
    /// Enables or disables the per-message log events. Filtering and counting are
    /// unaffected. Called by MainForm when the activity log is switched on or off.
    /// </summary>
    public void SetLogFiltered(bool value) => _logFiltered = value;

    // Last status text actually raised, used to suppress identical repeats (the watcher
    // polls every 1s and would otherwise repeat "Waiting for Input..." forever).
    private string _lastStatus = string.Empty;

    // Note names in Pianoteq style (flats), with C4 = note 60 and range C-1 to G9.
    private static readonly string[] NoteNames =
        { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    // Timestamp of the last failed connect or of a runtime fault.
    // Used to enforce a cooldown before retrying, so Windows can release the port.
    private DateTime _lastConnectError = DateTime.MinValue;
    private static readonly TimeSpan ConnectErrorCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval         = TimeSpan.FromMilliseconds(1000);

    private string _inputName  = string.Empty;
    private string _outputName = string.Empty;

    public event Action<string>? StatusChanged;
    public event Action<bool>?   ConnectionChanged;
    public event Action<string>? MessageFiltered;

    public bool IsConnected => _connected;
    public bool IsRunning   => _running;

    /// <summary>
    /// Starts the filter engine with the given input/output device names.
    /// Launches a background watcher thread that auto-reconnects on device loss.
    /// Called from MainForm when the user clicks Start.
    /// </summary>
    public void Start(string inputName, string outputName)
    {
        // Ensure any previous run is fully stopped (and its thread joined) before
        // starting a new one, so we never end up with two watcher threads sharing
        // the device fields.
        Stop();

        _inputName        = inputName;
        _outputName       = outputName;
        _lastConnectError = DateTime.MinValue;
        _lastStatus       = string.Empty;
        _faulted          = false;
        Interlocked.Exchange(ref _filteredCount, 0);
        Interlocked.Exchange(ref _lastLogTick, 0);

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
    /// all MIDI resources. Safe to call repeatedly. Returns true when the watcher
    /// thread exited cleanly (false means it is stuck in a driver call).
    /// Called from MainForm on Stop, on restart, and on window close.
    /// </summary>
    public bool Stop()
    {
        _running = false;
        Wake();

        bool clean = true;
        Thread? t  = _watcherThread;
        if (t != null && t.IsAlive)
            clean = t.Join(TimeSpan.FromSeconds(3));
        _watcherThread = null;

        Disconnect();
        return clean;
    }

    /// <summary>
    /// Wakes the watcher thread without throwing if the engine is already disposed.
    /// Called by Stop and by the MIDI fault path.
    /// </summary>
    private void Wake()
    {
        if (_disposed)
            return;
        try { _wakeSignal.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Background loop: handles runtime faults, verifies both devices are still present
    /// in the OS device list, and reconnects with a cooldown after errors.
    /// The whole body is fault tolerant, so a driver exception can never kill the thread
    /// (which previously left the app running but permanently disconnected).
    /// Runs on _watcherThread.
    /// </summary>
    private void WatchLoop()
    {
        while (_running)
        {
            try
            {
                if (_faulted)
                {
                    // A send or driver error was flagged by the MIDI thread: tear the
                    // connection down here, exactly once, and let the cooldown apply.
                    _lastConnectError = DateTime.UtcNow;
                    ReportStatus("Connection lost, reconnecting...");
                    Disconnect();
                }
                else if (_connected)
                {
                    // Active liveness check on both ports: when the peer app (Synthesia,
                    // loopMIDI, a DAW) closes, its virtual port disappears from the device
                    // list without NAudio raising anything.
                    if (FindDeviceId(_inputName, isInput: true) == -1)
                    {
                        ReportStatus($"Input lost: \"{_inputName}\", reconnecting...");
                        Disconnect();
                    }
                    else if (FindDeviceId(_outputName, isInput: false) == -1)
                    {
                        ReportStatus($"Output lost: \"{_outputName}\", reconnecting...");
                        Disconnect();
                    }
                }
                else
                {
                    bool inCooldown = _lastConnectError != DateTime.MinValue
                        && DateTime.UtcNow - _lastConnectError < ConnectErrorCooldown;

                    if (!inCooldown)
                        TryConnect();
                }
            }
            catch (Exception ex)
            {
                _lastConnectError = DateTime.UtcNow;
                ReportStatus($"Watcher error: {ex.Message}");
                try { Disconnect(); } catch { }
            }

            // Wait until the next poll, but wake immediately on Stop or on a fault.
            try
            {
                _wakeSignal.Wait(PollInterval);
                if (_running)
                    _wakeSignal.Reset();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Attempts to find and open the configured input and output devices by name.
    /// Always tears down any previous handles first, so a half-open state can never
    /// keep a port busy while the engine reports "waiting for device".
    /// The opened devices are adopted only if the generation is still current, so a
    /// concurrent Stop() can never be overwritten by a late-finishing connect.
    /// Called by WatchLoop.
    /// </summary>
    private void TryConnect()
    {
        Disconnect();
        if (!_running)
            return;

        int gen;
        lock (_deviceLock)
            gen = _generation;

        MidiIn?  midiIn  = null;
        MidiOut? midiOut = null;

        try
        {
            int inputId  = FindDeviceId(_inputName,  isInput: true);
            int outputId = FindDeviceId(_outputName, isInput: false);

            if (inputId == -1)
            {
                ReportStatus($"Waiting for Input: \"{_inputName}\"...", suppressRepeat: true);
                return;
            }

            if (outputId == -1)
            {
                ReportStatus($"Waiting for Output: \"{_outputName}\"...", suppressRepeat: true);
                return;
            }

            midiOut = new MidiOut(outputId);
            midiIn  = new MidiIn(inputId);
            midiIn.MessageReceived += OnMessageReceived;
            midiIn.ErrorReceived   += OnErrorReceived;

            bool adopted = false;
            lock (_deviceLock)
            {
                if (_running && _generation == gen)
                {
                    _midiIn  = midiIn;
                    _midiOut = midiOut;
                    adopted  = true;
                }
            }

            if (!adopted)
            {
                // Stopped or reconnected while we were opening: throw the handles away.
                CloseDevices(midiIn, midiOut);
                return;
            }

            _faulted = false;
            midiIn.Start();

            SetConnected(true);
            ReportStatus($"Connected: \"{_inputName}\" -> Filter -> \"{_outputName}\"");
        }
        catch (Exception ex)
        {
            _lastConnectError = DateTime.UtcNow;
            ReportStatus($"Connection Error: {ex.Message} (retrying in {ConnectErrorCooldown.TotalSeconds}s...)");
            Disconnect();
            CloseDevices(midiIn, midiOut);
        }
    }

    /// <summary>
    /// Handles incoming MIDI messages. Filters blocked CCs and notes (or all notes),
    /// forwards everything else. Runs on the MIDI thread, so the path stays allocation
    /// free and never touches the UI: a send failure only raises the _faulted flag and
    /// wakes the watcher, which does the teardown once.
    /// Called by NAudio on MIDI message receipt.
    /// </summary>
    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        if (_faulted || !_running)
            return;

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
                if (_logFiltered && ShouldLog())
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
                if (_logFiltered && ShouldLog())
                    MessageFiltered?.Invoke($"Blocked: Note {note} ({NoteName(note)}) (Channel {channel})");
                return;
            }
        }

        MidiOut? outDevice = _midiOut;
        if (outDevice == null)
            return;

        try
        {
            outDevice.Send(e.RawMessage);
        }
        catch
        {
            // Target port died (peer app closed or changed). Flag once, let the watcher
            // reconnect; do not report per message or the UI thread gets flooded.
            _faulted = true;
            Wake();
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
        long now  = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastLogTick);
        if (now - last < LogMinIntervalMs)
            return false;
        Interlocked.Exchange(ref _lastLogTick, now);
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
    /// Handles MIDI input errors by flagging a fault for the watcher thread.
    /// Called by NAudio on MIDI error.
    /// </summary>
    private void OnErrorReceived(object? sender, MidiInMessageEventArgs e)
    {
        _faulted = true;
        Wake();
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
    /// Detaches the current devices under the lock (bumping the generation so an in-flight
    /// TryConnect discards its result), then closes them outside the lock so a blocking
    /// driver call can never deadlock the MIDI callback path.
    /// Called before every connect attempt and on Stop.
    /// </summary>
    private void Disconnect()
    {
        MidiIn?  midiIn;
        MidiOut? midiOut;

        lock (_deviceLock)
        {
            _generation++;
            midiIn   = _midiIn;
            midiOut  = _midiOut;
            _midiIn  = null;
            _midiOut = null;
        }

        _faulted = false;
        SetConnected(false);
        CloseDevices(midiIn, midiOut);
    }

    /// <summary>
    /// Unhooks events and disposes a pair of MIDI devices, swallowing driver errors.
    /// Called by Disconnect and by TryConnect for handles that were not adopted.
    /// </summary>
    private void CloseDevices(MidiIn? midiIn, MidiOut? midiOut)
    {
        if (midiIn != null)
        {
            try { midiIn.MessageReceived -= OnMessageReceived; } catch { }
            try { midiIn.ErrorReceived   -= OnErrorReceived;   } catch { }
            try { midiIn.Stop();    } catch { }
            try { midiIn.Dispose(); } catch { }
        }

        if (midiOut != null)
        {
            try { midiOut.Reset();   } catch { }
            try { midiOut.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Searches for a MIDI device by partial name match (case-insensitive).
    /// Enumeration errors (a device vanishing mid-scan) are treated as "not found"
    /// instead of propagating onto the watcher thread.
    /// Returns device index or -1 if not found.
    /// Called by TryConnect and WatchLoop.
    /// </summary>
    private static int FindDeviceId(string name, bool isInput)
    {
        if (string.IsNullOrWhiteSpace(name))
            return -1;

        try
        {
            int count = isInput ? MidiIn.NumberOfDevices : MidiOut.NumberOfDevices;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    string product = isInput
                        ? MidiIn.DeviceInfo(i).ProductName
                        : MidiOut.DeviceInfo(i).ProductName;

                    if (product.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
                catch
                {
                    // Device disappeared between the count and the query - skip it.
                }
            }
        }
        catch
        {
            // Driver enumeration failed entirely - report as not found.
        }
        return -1;
    }

    /// <summary>
    /// Fires the StatusChanged event on the calling thread. Only the polling messages pass
    /// suppressRepeat, so they do not fill the log every cycle while waiting for a device;
    /// every real state change is always reported, even if its text repeats.
    /// Called throughout the engine to report state changes.
    /// </summary>
    private void ReportStatus(string message, bool suppressRepeat = false)
    {
        if (suppressRepeat && message == _lastStatus)
            return;
        _lastStatus = message;
        StatusChanged?.Invoke(message);
    }

    /// <summary>
    /// Returns all currently available MIDI input device names.
    /// Called by MainForm to populate dropdowns.
    /// </summary>
    public static List<string> GetInputDevices()
    {
        var list = new List<string>();
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                try { list.Add(MidiIn.DeviceInfo(i).ProductName); } catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Returns all currently available MIDI output device names.
    /// Called by MainForm to populate dropdowns.
    /// </summary>
    public static List<string> GetOutputDevices()
    {
        var list = new List<string>();
        try
        {
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                try { list.Add(MidiOut.DeviceInfo(i).ProductName); } catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Stops the engine and releases the wake handle. The handle is only disposed when
    /// the watcher thread actually exited, otherwise it is deliberately leaked (process
    /// is ending anyway) to avoid an ObjectDisposedException on a stuck driver call.
    /// Called by MainForm on form close.
    /// </summary>
    public void Dispose()
    {
        bool clean = Stop();
        _disposed  = true;
        if (clean)
        {
            try { _wakeSignal.Dispose(); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
