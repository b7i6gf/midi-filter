using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MidiFilter;

/// <summary>
/// Main application window. Shows connection status, device selectors,
/// per-pedal filter toggle buttons, and a live log of filtered messages.
/// Entry point: Program.cs → Application.Run(new MainForm())
/// </summary>
public class MainForm : Form
{
    private readonly MidiFilterEngine _engine = new();

    // Controls
    private ComboBox   _cmbInput     = null!;
    private ComboBox   _cmbOutput    = null!;
    private Button     _btnStart     = null!;
    private Button     _btnStop      = null!;
    private Button     _btnRefresh   = null!;
    private ClickPanel _btnToggleAll = null!;
    private Panel      _statusDot    = null!;
    private Label      _statusLabel  = null!;
    private ListBox    _logBox       = null!;
    private Label      _lblFiltered  = null!;
    private int        _filteredCount = 0;

    // Toggle buttons for each pedal CC — order matches CC_DEFINITIONS
    private CcToggleButton[] _ccToggles = null!;

    // Shared Font objects — created once, disposed with the form to prevent GDI leaks.
    // These are assigned in BuildUI and reused across all controls that share the same style.
    private readonly Font _fontUi      = new("Segoe UI",  9.5f);
    private readonly Font _fontBold    = new("Segoe UI", 10f,  FontStyle.Bold);
    private readonly Font _fontConsole = new("Consolas",  8.5f);

    // CC number + display label — single source of truth for all pedal definitions
    private static readonly (int CC, string Label)[] CC_DEFINITIONS =
    {
        ( 7, "CC7  — Volume Controller"),
        (11, "CC11 — Soft Pedal"),
        (64, "CC64 — Sustain Pedal"),
        (66, "CC66 — Sostenuto Pedal"),
        (69, "CC69 — Harmonic Pedal"),
    };

    public MainForm()
    {
        BuildUI();
        PopulateDevices();
        RestoreToggleStates();
        WireEvents();

        // Auto-start only if a previous device selection exists
        Load += (_, _) =>
        {
            if (AppSettings.LoadInput() != null && AppSettings.LoadOutput() != null)
                OnStartClick(this, EventArgs.Empty);
        };
    }

    // -------------------------------------------------------------------------
    // CcToggleButton — self-contained button that tracks its own on/off state.
    // Displays ✕ (active/blocked) or ○ (inactive) with the CC label.
    // Called by BuildUI; state read by ApplyTogglesToEngine and CollectBlockedCCs.
    // -------------------------------------------------------------------------
    private sealed class CcToggleButton : Button
    {
        private bool _active;

        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                UpdateAppearance();
            }
        }

        public CcToggleButton(string label, Font sharedFont)
        {
            _active   = true;
            Tag       = label;
            Height    = 26;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor        = Color.FromArgb(70, 70, 90);
            FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 65, 85);
            FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 80, 100);
            TextAlign = ContentAlignment.MiddleLeft;
            Padding   = new Padding(6, 0, 0, 0);
            Cursor    = Cursors.Hand;
            // Use the shared font — do NOT set Font = new Font(...) here,
            // as Button.Dispose would not dispose a font it did not create,
            // and we manage the lifetime centrally in MainForm.
            Font = sharedFont;
            UpdateAppearance();

            Click += (_, _) => Active = !_active;
        }

        private void UpdateAppearance()
        {
            Text      = _active ? $"○   {Tag}" : $"✕   {Tag}";
            BackColor = _active
                ? Color.FromArgb(55, 50, 80)    // active: subtle purple tint
                : Color.FromArgb(45, 45, 45);   // inactive: neutral dark
            ForeColor = _active
                ? Color.FromArgb(160, 140, 255) // active: bright purple
                : Color.FromArgb(100, 100, 100);// inactive: dimmed
        }
    }

    /// <summary>
    /// Constructs all UI controls programmatically.
    /// Shared Font fields (_fontUi, _fontBold, _fontConsole) are reused here
    /// instead of allocating new Font instances per control.
    /// Called from constructor.
    /// </summary>
    private void BuildUI()
    {
        Text            = "MidiFilter v1.2.0";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.WhiteSmoke;
        Font            = _fontUi;
        StartPosition   = FormStartPosition.CenterScreen;

        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("MidiFilter.midi.ico");
        if (stream != null)
            Icon = new Icon(stream);

        int pad = 18;
        int y   = pad;

        // --- Input ---
        AddLabel("MIDI Input (e.g. Synthesia Output):", pad, y);
        y += 22;
        _cmbInput = new ComboBox
        {
            Left          = pad, Top = y, Width = 428,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(50, 50, 50),
            ForeColor     = Color.WhiteSmoke,
            FlatStyle     = FlatStyle.Flat
        };
        Controls.Add(_cmbInput);
        y += _cmbInput.Height + 12;

        // --- Output ---
        AddLabel("MIDI Output:", pad, y);
        y += 22;
        _cmbOutput = new ComboBox
        {
            Left          = pad, Top = y, Width = 428,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(50, 50, 50),
            ForeColor     = Color.WhiteSmoke,
            FlatStyle     = FlatStyle.Flat
        };
        Controls.Add(_cmbOutput);
        y += _cmbOutput.Height + 16;

        // --- CC Filter header row: label + All-toggle button ---
        AddLabel("Filtered Controllers:", pad, y);

        _btnToggleAll = new ClickPanel("All Off")
        {
            Left   = pad + 428 - 80, Top = y - 2,
            Width  = 80,             Height = 20,
            Cursor = Cursors.Hand
        };
        Controls.Add(_btnToggleAll);
        y += 22;

        // --- CC Toggle buttons panel ---
        var filterPanel = new Panel
        {
            Left        = pad, Top = y, Width = 428,
            Height      = CC_DEFINITIONS.Length * 30 + 8,
            BackColor   = Color.FromArgb(50, 50, 50),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(filterPanel);

        _ccToggles = new CcToggleButton[CC_DEFINITIONS.Length];
        for (int i = 0; i < CC_DEFINITIONS.Length; i++)
        {
            var tb = new CcToggleButton(CC_DEFINITIONS[i].Label, _fontUi)
            {
                Left  = 6,
                Top   = 6 + i * 30,
                Width = 412
            };
            _ccToggles[i] = tb;
            filterPanel.Controls.Add(tb);
        }

        y += filterPanel.Height + 14;

        // --- Buttons ---
        const int btnGap = 7;
        const int btnW   = (428 - btnGap * 2) / 3; // 138px each

        _btnStart = new Button
        {
            Text      = "Start",
            Left      = pad, Top = y, Width = btnW, Height = 36,
            BackColor = Color.FromArgb(40, 120, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _fontBold
        };
        _btnStart.FlatAppearance.BorderColor = Color.FromArgb(60, 160, 60);
        Controls.Add(_btnStart);

        _btnStop = new Button
        {
            Text      = "Stop",
            Left      = pad + btnW + btnGap, Top = y, Width = btnW, Height = 36,
            BackColor = Color.FromArgb(120, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _fontBold,
            Enabled   = false
        };
        _btnStop.FlatAppearance.BorderColor = Color.FromArgb(160, 60, 60);
        Controls.Add(_btnStop);

        _btnRefresh = new Button
        {
            Text      = "Refresh",
            Left      = pad + (btnW + btnGap) * 2, Top = y,
            Width     = 428 - (btnW + btnGap) * 2, Height = 36,
            BackColor = Color.FromArgb(50, 80, 120),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _fontBold
        };
        _btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 160);
        Controls.Add(_btnRefresh);

        y += 44;

        // --- Status bar ---
        var statusPanel = new Panel
        {
            Left      = pad, Top = y, Width = 428, Height = 28,
            BackColor = Color.FromArgb(40, 40, 40)
        };
        _statusDot = new Panel
        {
            Left      = 8, Top = 8, Width = 13, Height = 13,
            BackColor = Color.Gray
        };
        MakeCircle(_statusDot);
        _statusLabel = new Label
        {
            Left      = 28, Top = 5, Width = 390, Height = 20,
            ForeColor = Color.Silver,
            Text      = "Not running"
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
            Left          = pad, Top = y, Width = 428, Height = 120,
            BackColor     = Color.FromArgb(20, 20, 20),
            ForeColor     = Color.FromArgb(100, 220, 100),
            BorderStyle   = BorderStyle.FixedSingle,
            Font          = _fontConsole,
            SelectionMode = SelectionMode.None
        };
        Controls.Add(_logBox);
        y += 128;

        _lblFiltered = new Label
        {
            Left      = pad, Top = y, Width = 428,
            ForeColor = Color.FromArgb(150, 150, 150),
            Text      = "Filtered: 0 Messages"
        };
        Controls.Add(_lblFiltered);
        y += 24;

        // Auto-size form height to fit all content — adapts when filters are added
        int formHeight = y + pad + (Height - ClientSize.Height);
        Size        = new Size(480, formHeight);
        MinimumSize = new Size(480, formHeight);
    }

    /// <summary>
    /// Loads saved CC toggle states from disk and applies them to the UI.
    /// Falls back to all-active (default) if no setting is stored.
    /// Called from constructor after BuildUI.
    /// </summary>
    private void RestoreToggleStates()
    {
        HashSet<int>? saved = AppSettings.LoadBlockedCCs();

        // null means no entry in file — keep all toggles at their default (active)
        if (saved == null)
            return;

        for (int i = 0; i < CC_DEFINITIONS.Length; i++)
            _ccToggles[i].Active = saved.Contains(CC_DEFINITIONS[i].CC);

        UpdateToggleAllLabel();
    }

    /// <summary>
    /// Reads current toggle states and pushes the active CC set to the engine.
    /// Changes take effect immediately without restarting the filter.
    /// Called on toggle click and on filter start.
    /// </summary>
    private void ApplyTogglesToEngine()
    {
        var active = new HashSet<int>();
        for (int i = 0; i < CC_DEFINITIONS.Length; i++)
        {
            if (_ccToggles[i].Active)
                active.Add(CC_DEFINITIONS[i].CC);
        }
        _engine.SetBlockedCCs(active);
    }

    /// <summary>
    /// Collects current toggle states into a HashSet of blocked CC numbers.
    /// Called by OnStartClick and FormClosing to persist the selection.
    /// </summary>
    private HashSet<int> CollectBlockedCCs()
    {
        var result = new HashSet<int>();
        for (int i = 0; i < CC_DEFINITIONS.Length; i++)
        {
            if (_ccToggles[i].Active)
                result.Add(CC_DEFINITIONS[i].CC);
        }
        return result;
    }

    /// <summary>
    /// Toggles all CC buttons to the opposite of their current combined state.
    /// If all are active, turns all off — otherwise turns all on.
    /// Called when the All-toggle button is clicked.
    /// </summary>
    private void OnToggleAllClick(object? sender, EventArgs e)
    {
        bool allActive = Array.TrueForAll(_ccToggles, t => t.Active);
        bool target    = !allActive;

        foreach (var t in _ccToggles)
            t.Active = target;

        UpdateToggleAllLabel();
        ApplyTogglesToEngine();
    }

    /// <summary>
    /// Updates the All-toggle button label based on the current combined toggle state.
    /// Shows "All Off" when all are active (next click turns all off),
    /// and "All On" when any is inactive (next click turns all on).
    /// Called after any toggle state change.
    /// </summary>
    private void UpdateToggleAllLabel()
    {
        bool allActive = Array.TrueForAll(_ccToggles, t => t.Active);
        _btnToggleAll.SetLabel(allActive ? "All Off" : "All On");
    }

    /// <summary>
    /// Helper to add a styled label to the form.
    /// Called by BuildUI.
    /// </summary>
    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text      = text, Left = x, Top = y,
            AutoSize  = true,
            ForeColor = Color.FromArgb(180, 180, 180)
        });
    }

    /// <summary>
    /// Makes a panel appear circular by overriding its region.
    /// Called by BuildUI for the status dot, and by WireEvents/OnStopClick on color change.
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
    /// Connects button clicks, toggle changes, and engine events to handlers.
    /// Called from constructor after BuildUI.
    /// </summary>
    private void WireEvents()
    {
        _btnStart.Click       += OnStartClick;
        _btnStop.Click        += OnStopClick;
        _btnRefresh.Click     += OnRefreshClick;
        _btnToggleAll.Clicked += OnToggleAllClick;

        FormClosing += (_, _) =>
        {
            _engine.Stop();

            // Persist last device selection and CC filter state on every close
            string inputName  = _cmbInput.SelectedItem  as string ?? string.Empty;
            string outputName = _cmbOutput.SelectedItem as string ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(inputName)  && inputName.Trim()  != "-" &&
                !string.IsNullOrWhiteSpace(outputName) && outputName.Trim() != "-")
            {
                AppSettings.Save(inputName, outputName);
            }

            AppSettings.SaveBlockedCCs(CollectBlockedCCs());
        };

        // Live CC update on toggle click — no restart needed
        foreach (var tb in _ccToggles)
            tb.Click += (_, _) =>
            {
                ApplyTogglesToEngine();
                UpdateToggleAllLabel();
            };

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
    /// Saves device selection and blocked CC set to disk for next launch.
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

        _filteredCount    = 0;
        _lblFiltered.Text = "Filtered: 0 Messages";
        _logBox.Items.Clear();

        ApplyTogglesToEngine();
        AppSettings.Save(inputName, outputName);
        AppSettings.SaveBlockedCCs(CollectBlockedCCs());
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
    /// Refreshes the MIDI device lists in both combo boxes without interrupting a running filter.
    /// Called when the Refresh button is clicked.
    /// </summary>
    private void OnRefreshClick(object? sender, EventArgs e)
    {
        PopulateDevices();
        AddLog("Device list refreshed.");
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

    /// <summary>
    /// Disposes managed resources including shared Font objects.
    /// Called by WinForms when the form is closed.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Dispose();
            _fontUi.Dispose();
            _fontBold.Dispose();
            _fontConsole.Dispose();
        }
        base.Dispose(disposing);
    }

// ---------------------------------------------------------------------------
// ClickPanel — owner-drawn panel that behaves like a button.
// Text is always drawn centered via GDI+ — immune to WinForms pressed-state
// text offset that affects standard Button controls.
// Used for _btnToggleAll in MainForm.
// ---------------------------------------------------------------------------
internal sealed class ClickPanel : Panel
{
    private static readonly Color _bgNormal  = Color.FromArgb(50,  80, 120);
    private static readonly Color _bgHover   = Color.FromArgb(60,  95, 140);
    private static readonly Color _bgPressed = Color.FromArgb(40,  65, 100);
    private static readonly Color _border    = Color.FromArgb(70, 110, 160);

    // Shared paint resources — created once, never reallocated during painting.
    // Disposed in Dispose(bool) to avoid per-paint GDI allocations.
    private readonly Font        _paintFont   = new("Segoe UI", 8f, FontStyle.Bold);
    private readonly SolidBrush  _paintBrush  = new(Color.White);
    private readonly StringFormat _paintFormat = new()
    {
        Alignment     = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    private string _label;
    private bool   _hover;
    private bool   _pressed;

    public event EventHandler? Clicked;

    public ClickPanel(string label)
    {
        _label         = label;
        DoubleBuffered = true;
        BorderStyle    = BorderStyle.None;
    }

    /// <summary>
    /// Updates the displayed label text and redraws the panel.
    /// Called by MainForm.UpdateToggleAllLabel.
    /// </summary>
    public void SetLabel(string label)
    {
        _label = label;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g    = e.Graphics;
        var rect = ClientRectangle;

        // Background — use static color directly to avoid per-paint Brush allocation
        Color bg = _pressed ? _bgPressed : _hover ? _bgHover : _bgNormal;
        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, rect);

        // Border
        using (var pen = new Pen(_border))
            g.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);

        // Text — always centered, never offset; reuse pre-allocated resources
        g.DrawString(_label, _paintFont, _paintBrush, rect, _paintFormat);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover   = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover   = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true;  Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        if (ClientRectangle.Contains(e.Location))
            Clicked?.Invoke(this, EventArgs.Empty);
        base.OnMouseUp(e);
    }

    /// <summary>
    /// Disposes GDI paint resources allocated by this panel.
    /// Called by WinForms when the control is destroyed.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paintFont.Dispose();
            _paintBrush.Dispose();
            _paintFormat.Dispose();
        }
        base.Dispose(disposing);
    }
}

}
