using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MidiFilter;

/// <summary>
/// Main application window. Shows connection status, device selectors,
/// per-pedal filter checkboxes, and a live log of filtered messages.
/// Entry point: Program.cs → Application.Run(new MainForm())
/// </summary>
public class MainForm : Form
{
    private readonly MidiFilterEngine _engine = new();

    // Controls
    private ComboBox _cmbInput   = null!;
    private ComboBox _cmbOutput  = null!;
    private Button   _btnStart   = null!;
    private Button   _btnStop    = null!;
    private Panel    _statusDot  = null!;
    private Label    _statusLabel = null!;
    private ListBox  _logBox      = null!;
    private Label    _lblFiltered = null!;
    private int      _filteredCount = 0;

    // Checkboxes for each pedal CC — order matches CC_DEFINITIONS
    private CheckBox[] _ccCheckboxes = null!;

    // CC number + display label — single source of truth for all pedal definitions
    private static readonly (int CC, string Label)[] CC_DEFINITIONS =
    {
        (11, "CC11 — Soft Pedal"),
        (64, "CC64 — Sustain Pedal"),
        (66, "CC66 — Sostenuto Pedal"),
        (69, "CC69 — Harmonic Pedal"),
    };

    public MainForm()
    {
        BuildUI();
        PopulateDevices();
        WireEvents();

        // Auto-start only if a previous device selection exists
        Load += (_, _) =>
        {
            if (AppSettings.LoadInput() != null && AppSettings.LoadOutput() != null)
                OnStartClick(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Constructs all UI controls programmatically.
    /// Called from constructor.
    /// </summary>
    private void BuildUI()
    {
        Text = "MidiFilter v1.0.0";
        Size = new Size(480, 580);
        MinimumSize = new Size(480, 580);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;

        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("MidiFilter.midi.ico");
        if (stream != null)
            Icon = new Icon(stream);

        int pad = 16;
        int y   = pad;

        // --- Input ---
        AddLabel("MIDI Input (e.g. Synthesia Output):", pad, y);
        y += 22;
        _cmbInput = new ComboBox
        {
            Left = pad, Top = y, Width = 428,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.WhiteSmoke,
            FlatStyle = FlatStyle.Flat
        };
        Controls.Add(_cmbInput);
        y += _cmbInput.Height + 12;

        // --- Output ---
        AddLabel("MIDI Output:", pad, y);
        y += 22;
        _cmbOutput = new ComboBox
        {
            Left = pad, Top = y, Width = 428,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.WhiteSmoke,
            FlatStyle = FlatStyle.Flat
        };
        Controls.Add(_cmbOutput);
        y += _cmbOutput.Height + 16;

        // --- CC Filter Checkboxes ---
        AddLabel("Filtered Pedals:", pad, y);
        y += 22;

        var filterPanel = new Panel
        {
            Left = pad, Top = y, Width = 428,
            Height = CC_DEFINITIONS.Length * 28 + 10,
            BackColor = Color.FromArgb(50, 50, 50),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(filterPanel);

        _ccCheckboxes = new CheckBox[CC_DEFINITIONS.Length];
        for (int i = 0; i < CC_DEFINITIONS.Length; i++)
        {
            var cb = new CheckBox
            {
                Text      = CC_DEFINITIONS[i].Label,
                Left      = 10,
                Top       = 8 + i * 28,
                Width     = 400,
                Checked   = true,
                ForeColor = Color.FromArgb(130, 130, 230),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 9.5f)
            };
            _ccCheckboxes[i] = cb;
            filterPanel.Controls.Add(cb);
        }

        y += filterPanel.Height + 14;

        // --- Buttons ---
        _btnStart = new Button
        {
            Text      = "Start",
            Left      = pad, Top = y, Width = 130, Height = 36,
            BackColor = Color.FromArgb(40, 120, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        _btnStart.FlatAppearance.BorderColor = Color.FromArgb(60, 160, 60);
        Controls.Add(_btnStart);

        _btnStop = new Button
        {
            Text      = "Stop",
            Left      = pad + 140, Top = y, Width = 130, Height = 36,
            BackColor = Color.FromArgb(120, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            Enabled   = false
        };
        _btnStop.FlatAppearance.BorderColor = Color.FromArgb(160, 60, 60);
        Controls.Add(_btnStop);
        y += 44;

        // --- Status bar ---
        var statusPanel = new Panel
        {
            Left = pad, Top = y, Width = 428, Height = 28,
            BackColor = Color.FromArgb(40, 40, 40)
        };
        _statusDot = new Panel
        {
            Left = 8, Top = 8, Width = 12, Height = 12,
            BackColor = Color.Gray
        };
        MakeCircle(_statusDot);
        _statusLabel = new Label
        {
            Left = 28, Top = 5, Width = 390, Height = 20,
            ForeColor = Color.Silver,
            Text = "Nicht gestartet"
        };
        statusPanel.Controls.Add(_statusDot);
        statusPanel.Controls.Add(_statusLabel);
        Controls.Add(statusPanel);
        y += 32;

        // --- Log ---
        AddLabel("Activity Log:", pad, y);
        y += 22;
        _logBox = new ListBox
        {
            Left = pad, Top = y, Width = 428, Height = 120,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.FromArgb(100, 220, 100),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5f),
            SelectionMode = SelectionMode.None
        };
        Controls.Add(_logBox);
        y += 128;

        _lblFiltered = new Label
        {
            Left = pad, Top = y, Width = 428,
            ForeColor = Color.FromArgb(150, 150, 150),
            Text = "Filtered: 0 Messages"
        };
        Controls.Add(_lblFiltered);
    }

    /// <summary>
    /// Reads current checkbox states and pushes the active CC set to the engine.
    /// Changes take effect immediately without restarting the filter.
    /// Called on checkbox change and on filter start.
    /// </summary>
    private void ApplyCheckboxesToEngine()
    {
        var active = new HashSet<int>();
        for (int i = 0; i < CC_DEFINITIONS.Length; i++)
        {
            if (_ccCheckboxes[i].Checked)
                active.Add(CC_DEFINITIONS[i].CC);
        }
        _engine.SetBlockedCCs(active);
    }

    /// <summary>
    /// Helper to add a styled label to the form.
    /// Called by BuildUI.
    /// </summary>
    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text, Left = x, Top = y,
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 180, 180)
        });
    }

    /// <summary>
    /// Makes a panel appear circular by overriding its region.
    /// Called by BuildUI for the status dot.
    /// </summary>
    private static void MakeCircle(Panel p)
    {
        p.Region = new Region(new System.Drawing.Drawing2D.GraphicsPath());
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(0, 0, p.Width, p.Height);
        p.Region = new Region(path);
    }

    /// <summary>
    /// Populates input/output combo boxes with current MIDI devices.
    /// On first load restores last saved selection; adds a blank placeholder entry.
    /// On subsequent calls restores the current selection.
    /// </summary>
    private void PopulateDevices()
    {
        string prevIn  = _cmbInput.Text;
        string prevOut = _cmbOutput.Text;
        bool firstLoad = string.IsNullOrEmpty(prevIn) && string.IsNullOrEmpty(prevOut);

        _cmbInput.Items.Clear();
        _cmbOutput.Items.Clear();

        // Blank placeholder shown when nothing is selected
        _cmbInput.Items.Add(" - ");
        _cmbOutput.Items.Add(" - ");

        foreach (var d in MidiFilterEngine.GetInputDevices())
            _cmbInput.Items.Add(d);

        foreach (var d in MidiFilterEngine.GetOutputDevices())
            _cmbOutput.Items.Add(d);

        if (firstLoad)
        {
            string? savedIn  = AppSettings.LoadInput();
            string? savedOut = AppSettings.LoadOutput();

            _cmbInput.SelectedIndex  = savedIn  != null ? FindItemIndex(_cmbInput,  savedIn)  ?? 0 : 0;
            _cmbOutput.SelectedIndex = savedOut != null ? FindItemIndex(_cmbOutput, savedOut) ?? 0 : 0;
        }
        else
        {
            if (!string.IsNullOrEmpty(prevIn) && _cmbInput.Items.Contains(prevIn))
                _cmbInput.SelectedItem = prevIn;
            else
                _cmbInput.SelectedIndex = 0;

            if (!string.IsNullOrEmpty(prevOut) && _cmbOutput.Items.Contains(prevOut))
                _cmbOutput.SelectedItem = prevOut;
            else
                _cmbOutput.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Finds the index of the first ComboBox item containing the search string (case-insensitive).
    /// Returns null if not found.
    /// Called by PopulateDevices for saved device matching.
    /// </summary>
    private static int? FindItemIndex(ComboBox cmb, string search)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i]?.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
                return i;
        }
        return null;
    }

    /// <summary>
    /// Connects button clicks, checkbox changes, and engine events to handlers.
    /// Called from constructor after BuildUI.
    /// </summary>
    private void WireEvents()
    {
        _btnStart.Click += OnStartClick;
        _btnStop.Click  += OnStopClick;
        FormClosing     += (_, _) => _engine.Stop();

        // Live CC update on checkbox change — no restart needed
        foreach (var cb in _ccCheckboxes)
            cb.CheckedChanged += (_, _) => ApplyCheckboxesToEngine();

        _engine.StatusChanged += msg => SafeInvoke(() =>
        {
            _statusLabel.Text = msg;
            AddLog(msg);
        });

        _engine.ConnectionChanged += connected => SafeInvoke(() =>
        {
            _statusDot.BackColor = connected
                ? Color.FromArgb(40, 200, 40)
                : Color.FromArgb(200, 80, 40);
            MakeCircle(_statusDot);
        });

        _engine.MessageFiltered += msg => SafeInvoke(() =>
        {
            _filteredCount++;
            _lblFiltered.Text = $"Filtered: {_filteredCount} Messages";
            if (_filteredCount % 10 == 1)
                AddLog(msg);
        });
    }

    /// <summary>
    /// Starts the filter engine with selected devices and current CC selection.
    /// Saves the device selection to disk for next launch.
    /// Called when Start button is clicked, and automatically on app launch if saved devices exist.
    /// </summary>
    private void OnStartClick(object? sender, EventArgs e)
    {
        if (_cmbInput.SelectedItem is not string inputName
            || string.IsNullOrWhiteSpace(inputName)
            || inputName.Trim() == "-")
        {
            MessageBox.Show("Please choose a MIDI Input.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_cmbOutput.SelectedItem is not string outputName
            || string.IsNullOrWhiteSpace(outputName)
            || outputName.Trim() == "-")
        {
            MessageBox.Show("Please choose a MIDI Output.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _filteredCount = 0;
        _lblFiltered.Text = "Filtered: 0 Messages";
        _logBox.Items.Clear();

        ApplyCheckboxesToEngine();
        AppSettings.Save(inputName, outputName);
        _engine.Start(inputName, outputName);

        _btnStart.Enabled  = false;
        _btnStop.Enabled   = true;
        _cmbInput.Enabled  = false;
        _cmbOutput.Enabled = false;

        AddLog($"Filter activated: {inputName} → {outputName}");
    }

    /// <summary>
    /// Stops the filter engine.
    /// Called when Stop button is clicked.
    /// </summary>
    private void OnStopClick(object? sender, EventArgs e)
    {
        _engine.Stop();

        _btnStart.Enabled  = true;
        _btnStop.Enabled   = false;
        _cmbInput.Enabled  = true;
        _cmbOutput.Enabled = true;

        _statusDot.BackColor = Color.Gray;
        MakeCircle(_statusDot);
        _statusLabel.Text = "Stopped";
        AddLog("Filter deactivated.");
    }

    /// <summary>
    /// Adds a timestamped entry to the log listbox, keeps max 200 entries.
    /// Called from engine event handlers.
    /// </summary>
    private void AddLog(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logBox.Items.Add(entry);
        if (_logBox.Items.Count > 200)
            _logBox.Items.RemoveAt(0);
        _logBox.TopIndex = _logBox.Items.Count - 1;
    }

    /// <summary>
    /// Thread-safe UI invoke helper.
    /// Called from engine background thread event handlers.
    /// </summary>
    private void SafeInvoke(Action action)
    {
        if (InvokeRequired) Invoke(action);
        else action();
    }
}