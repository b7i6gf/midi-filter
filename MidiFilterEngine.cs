using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Midi;

namespace MidiFilter;

/// <summary>
/// Core MIDI filter engine. Reads from a named MIDI input, filters specified CCs,
/// and forwards all other messages to a named MIDI output.
/// Called by MainForm to start/stop filtering and receive status updates.
/// </summary>
public class MidiFilterEngine : IDisposable
{
    // CCs to block on all channels — replaced atomically via SetBlockedCCs.
    // Volatile.Read/Write used instead of the 'volatile' keyword: 'volatile' on a
    // reference type only guarantees visibility of the reference itself, not the
    // object's contents. Volatile.Read/Write emits the correct memory barriers on
    // all CLR implementations and makes the intent explicit.
    private HashSet<int> _blockedCCs = new() { 11, 64, 66, 69 };

    /// <summary>
    /// Atomically replaces the active blocked CC set. Takes effect on the next message.
    /// Called by MainForm whenever a toggle button is clicked.
    /// </summary>
    public void SetBlockedCCs(HashSet<int> ccs) =>
        Volatile.Write(ref _blockedCCs, ccs);

    private MidiIn?  _midiIn;
    private MidiOut? _midiOut;
    private Thread?  _watcherThread;
    private volatile bool _running;
    private volatile bool _connected;

    private string _inputName  = string.Empty;
    private string _outputName = string.Empty;

    public event Action<string>? StatusChanged;
    public event Action<bool>?   ConnectionChanged;
    public event Action<string>? MessageFiltered;

    public bool IsConnected => _connected;

    /// <summary>
    /// Starts the filter engine with the given input/output device names.
    /// Launches a background watcher thread that auto-reconnects on device loss.
    /// Called from MainForm when user clicks Start.
    /// </summary>
    public void Start(string inputName, string outputName)
    {
        _inputName  = inputName;
        _outputName = outputName;
        _running    = true;

        _watcherThread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name         = "MidiFilterWatcher"
        };
        _watcherThread.Start();
    }

    /// <summary>
    /// Stops the filter engine and disposes all MIDI resources.
    /// Called from MainForm when user clicks Stop or closes the window.
    /// </summary>
    public void Stop()
    {
        _running = false;
        Disconnect();
    }

    /// <summary>
    /// Background loop that continuously checks device availability and reconnects.
    /// Runs on _watcherThread.
    /// </summary>
    private void WatchLoop()
    {
        while (_running)
        {
            if (!_connected)
                TryConnect();

            Thread.Sleep(1500);
        }
    }

    /// <summary>
    /// Attempts to find and open the configured input and output devices by name.
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

            _connected = true;
            ConnectionChanged?.Invoke(true);
            ReportStatus($"Connected: \"{_inputName}\" → Filter → \"{_outputName}\"");
        }
        catch (Exception ex)
        {
            ReportStatus($"Connection Error: {ex.Message}");
            Disconnect();
        }
    }

    /// <summary>
    /// Handles incoming MIDI messages. Filters blocked CCs, forwards everything else.
    /// Called by NAudio on MIDI message receipt.
    /// </summary>
    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        try
        {
            int status = e.RawMessage & 0xFF;
            int type   = status & 0xF0;

            // CC messages: status byte 0xB0–0xBF
            if (type == 0xB0)
            {
                int cc = (e.RawMessage >> 8) & 0x7F;
                // Volatile.Read ensures we always see the latest reference written
                // by SetBlockedCCs, even across threads without a full lock.
                if (Volatile.Read(ref _blockedCCs).Contains(cc))
                {
                    MessageFiltered?.Invoke($"Blocked: CC{cc} (Channel {(status & 0x0F) + 1})");
                    return;
                }
            }

            _midiOut?.Send(e.RawMessage);
        }
        catch
        {
            // Device was likely disconnected — trigger reconnect cycle
            _connected = false;
            ConnectionChanged?.Invoke(false);
            ReportStatus("Connection lost, reconnecting...");
        }
    }

    /// <summary>
    /// Handles MIDI input errors. Triggers reconnect cycle.
    /// Called by NAudio on MIDI error.
    /// </summary>
    private void OnErrorReceived(object? sender, MidiInMessageEventArgs e)
    {
        _connected = false;
        ConnectionChanged?.Invoke(false);
        ReportStatus("MIDI Error, reconnecting...");
    }

    /// <summary>
    /// Safely closes and disposes current MIDI in/out devices.
    /// Guard against redundant calls: if already disconnected, exits immediately
    /// to avoid firing ConnectionChanged multiple times for a single disconnect event.
    /// Called before reconnect attempts and on Stop.
    /// </summary>
    private void Disconnect()
    {
        // Guard: skip if already in a disconnected state to prevent duplicate events
        if (!_connected && _midiIn == null && _midiOut == null)
            return;

        _connected = false;
        ConnectionChanged?.Invoke(false);

        try { _midiIn?.Stop();    } catch (Exception ex) { ReportStatus($"MidiIn.Stop error: {ex.Message}"); }
        try { _midiIn?.Dispose(); } catch (Exception ex) { ReportStatus($"MidiIn.Dispose error: {ex.Message}"); }
        try { _midiOut?.Dispose();} catch (Exception ex) { ReportStatus($"MidiOut.Dispose error: {ex.Message}"); }

        _midiIn  = null;
        _midiOut = null;
    }

    /// <summary>
    /// Searches for a MIDI device by partial name match (case-insensitive).
    /// Returns device index or -1 if not found.
    /// Called by TryConnect.
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
    private void ReportStatus(string message) => StatusChanged?.Invoke(message);

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

    public void Dispose() => Stop();
}
