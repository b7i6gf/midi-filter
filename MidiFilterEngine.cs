using System;
using System.Collections.Generic;
using System.Linq;
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
    // CCs to block on all channels - updated at runtime via SetBlockedCCs
    private volatile HashSet<int> _blockedCCs = new() { 11, 64, 66, 69 };

    /// <summary>
    /// Replaces the active blocked CC set. Takes effect immediately on the next message.
    /// Called by MainForm whenever a checkbox is toggled.
    /// </summary>
    public void SetBlockedCCs(HashSet<int> ccs) => _blockedCCs = ccs;

    private MidiIn?  _midiIn;
    private MidiOut? _midiOut;
    private Thread?  _watcherThread;
    private volatile bool _running;
    private volatile bool _connected;

    // Timestamp of the last failed TryConnect attempt.
    // Used to enforce a cooldown before retrying after an error.
    private DateTime _lastConnectError = DateTime.MinValue;
    private static readonly TimeSpan ConnectErrorCooldown = TimeSpan.FromSeconds(4);

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
        _inputName        = inputName;
        _outputName       = outputName;
        _running          = true;
        _lastConnectError = DateTime.MinValue;

        _watcherThread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name         = "MidiFilterWatcher"
        };
        _watcherThread.Start();
    }

    /// <summary>
    /// Stops the filter engine and disposes all MIDI resources.
    /// Called from MainForm when user clicks Stop, Restart, or closes the window.
    /// </summary>
    public void Stop()
    {
        _running = false;
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

            Thread.Sleep(1500);
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

            _connected = true;
            ConnectionChanged?.Invoke(true);
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
    /// Handles incoming MIDI messages. Filters blocked CCs, forwards everything else.
    /// Called by NAudio on MIDI message receipt.
    /// </summary>
    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        try
        {
            int status = e.RawMessage & 0xFF;
            int type   = status & 0xF0;

            // CC messages: status 0xB0-0xBF
            if (type == 0xB0)
            {
                int cc = (e.RawMessage >> 8) & 0x7F;
                if (_blockedCCs.Contains(cc))
                {
                    MessageFiltered?.Invoke($"Blocked: CC{cc} (Channel {(status & 0x0F) + 1})");
                    return;
                }
            }

            _midiOut?.Send(e.RawMessage);
        }
        catch
        {
            // Device was likely disconnected - trigger reconnect
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
    /// Called before reconnect attempts and on Stop.
    /// </summary>
    private void Disconnect()
    {
        _connected = false;
        ConnectionChanged?.Invoke(false);

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
    }
}
