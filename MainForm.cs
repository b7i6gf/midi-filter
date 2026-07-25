using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MidiFilter;

/// <summary>
/// Main application window. Shows connection status, device selectors, a scrollable list
/// of filter toggles (the five fixed pedals plus user-added controllers, notes and All
/// Notes), and a live log of filtered messages.
/// Entry point: Program.cs -> Application.Run(new MainForm())
/// </summary>
public class MainForm : Form
{
    private readonly MidiFilterEngine _engine = new();

    // Controls
    private ComboBox   _cmbInput     = null!;
    private ComboBox   _cmbOutput    = null!;
    private Button     _btnStart     = null!;
    private Button     _btnStop      = null!;
    private Button     _btnRestart   = null!;
    private Button     _btnRefresh   = null!;
    private ClickPanel _btnToggleAll = null!;
    private ClickPanel _btnAdd       = null!;
    private Panel      _filterPanel  = null!;
    private Panel      _statusDot    = null!;
    private Label      _statusLabel  = null!;
    private LogView    _logView      = null!;
    private Label      _logLabel     = null!;
    private ClickPanel _btnLogToggle = null!;
    private Label      _lblFiltered  = null!;

    // Activity log state and the geometry needed to grow/shrink the window with it.
    private bool _logEnabled = true;
    private int  _logViewTop;      // top of the log area
    private int  _formHeightBase;  // window height with the log area removed
    private Icon?      _appIcon;

    // Periodically refreshes the filtered-message counter from the engine instead of
    // updating the label on every single filtered message (avoids flooding the UI thread).
    private System.Windows.Forms.Timer _countTimer = null!;
    private long _lastShownCount = 0;

    // Set while a hard restart is in progress, so the closing handler skips the work
    // that OnRestartClick already did.
    private bool _restarting;

    // All filter entries in display order: the five fixed pedals first, then user-added.
    private readonly List<FilterItem> _filters = new();
    // Toggle buttons parallel to _filters (same index), rebuilt by RebuildFilterRows.
    private readonly List<FilterToggleButton> _toggleButtons = new();

    // Shared fonts, created once for the app lifetime so rebuilding rows or reopening
    // dialogs never leaks GDI font handles.
    private static readonly Font _iconFont    = new("Segoe UI", 9f,   FontStyle.Bold);
    private static readonly Font _formFont    = new("Segoe UI", 9.5f);
    private static readonly Font _bigBtnFont  = new("Segoe UI", 10f,  FontStyle.Bold);
    private static readonly Font _twoLineFont = new("Segoe UI", 8f,   FontStyle.Bold);
    private static readonly Font _logFont     = new("Consolas",  8.5f);

    // Maximum entries kept in the activity log.
    private const int MaxLogEntries = 200;

    // Activity log area: view height plus the gap below it. Removed from the window
    // height when the log is disabled.
    private const int LogViewHeight    = 120;
    private const int LogSectionHeight = LogViewHeight + 8;

    // Filter list layout.
    private const int FilterPanelWidth = 428;
    private const int FilterRowHeight  = 30;
    private const int FilterMaxRows    = 10;   // visible rows before the list starts to scroll

    // Fixed pedal CCs - CC number plus display label. These are never removable.
    private static readonly (int CC, string Label)[] CC_DEFINITIONS =
    {
        ( 7, "CC7  - Volume Controller"),
        (11, "CC11 - Soft Pedal"),
        (64, "CC64 - Sustain Pedal"),
        (66, "CC66 - Sostenuto Pedal"),
        (69, "CC69 - Harmonic Pedal"),
    };

    public MainForm()
    {
        _logEnabled = AppSettings.LoadLogEnabled();  // layout depends on it
        LoadFilters();      // build _filters from saved settings (must run before BuildUI)
        BuildUI();
        PopulateDevices();
        WireEvents();

        // Auto-start only when both devices are actually selectable right now.
        // silent: true suppresses the warning popup if a saved device is currently missing.
        Load += (_, _) =>
        {
            if (HasValidSelection(_cmbInput) && HasValidSelection(_cmbOutput))
                StartFilter(silent: true);
        };
    }

    // -------------------------------------------------------------------------
    // FilterItem - one filter entry (data only): kind plus value, active state, and
    // whether the user may remove it. The five fixed pedals are not removable.
    // Built by LoadFilters/OnAddClick; read by ApplyTogglesToEngine and the Collect* methods.
    // -------------------------------------------------------------------------
    private sealed class FilterItem
    {
        public FilterKind Kind      { get; }
        public int        Value     { get; }
        public bool       Active    { get; set; }
        public bool       Removable { get; }   // also means: user-added and renamable
        public string     BaseLabel { get; }   // auto part, e.g. "CC44" or a fixed pedal label
        public string     Name      { get; set; }  // optional user name, "" if none

        // Shown on the toggle: "BaseLabel - Name" when a name is set, otherwise just BaseLabel.
        public string DisplayLabel =>
            string.IsNullOrEmpty(Name) ? BaseLabel : $"{BaseLabel} - {Name}";

        public FilterItem(FilterKind kind, int value, bool active, bool removable,
                          string baseLabel, string name = "")
        {
            Kind      = kind;
            Value     = value;
            Active    = active;
            Removable = removable;
            BaseLabel = baseLabel;
            Name      = name ?? string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    // FilterToggleButton - self-contained button that tracks its own on/off state.
    // Shows a circle (active/blocked) or a cross (inactive) plus the filter label.
    // Created and bound to a FilterItem by RebuildFilterRows.
    // -------------------------------------------------------------------------
    private sealed class FilterToggleButton : Button
    {
        // Shared font for all toggles, so rebuilding the list never leaks fonts.
        private static readonly Font _toggleFont = new("Segoe UI", 9.5f);

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

        public FilterToggleButton(string label)
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
            Font      = _toggleFont;
            UpdateAppearance();

            Click += (_, _) => Active = !_active;
        }

        private void UpdateAppearance()
        {
            Text      = _active ? $"○   {Tag}" : $"✕   {Tag}";
            BackColor = _active
                ? Color.FromArgb(55, 50, 80)     // active: subtle purple tint
                : Color.FromArgb(45, 45, 45);    // inactive: neutral dark
            ForeColor = _active
                ? Color.FromArgb(160, 140, 255)  // active: bright purple
                : Color.FromArgb(100, 100, 100); // inactive: dimmed
        }
    }

    /// <summary>
    /// Constructs all UI controls programmatically.
    /// Called from constructor.
    /// </summary>
    private void BuildUI()
    {
        Text            = "MidiFilter v1.5.1";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.WhiteSmoke;
        Font            = _formFont;
        StartPosition   = FormStartPosition.CenterScreen;

        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("MidiFilter.midi.ico");
        if (stream != null)
        {
            _appIcon = new Icon(stream);   // disposed in the closing handler
            Icon     = _appIcon;
        }

        int pad = 18;
        int y   = pad;

        // --- Input ---
        AddLabel("MIDI Input (e.g. Synthesia Output):", pad, y);
        y += 22;
        _cmbInput = new ComboBox
        {
            Left = pad, Top = y, Width = FilterPanelWidth,
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
            Left = pad, Top = y, Width = FilterPanelWidth,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(50, 50, 50),
            ForeColor     = Color.WhiteSmoke,
            FlatStyle     = FlatStyle.Flat
        };
        Controls.Add(_cmbOutput);
        y += _cmbOutput.Height + 16;

        // --- Filter header row: label + Add + All-toggle ---
        AddLabel("Filters:", pad, y);

        _btnToggleAll = new ClickPanel("All Off")
        {
            Left   = pad + FilterPanelWidth - 78, Top = y - 2,
            Width  = 78,                          Height = 20,
            Cursor = Cursors.Hand
        };
        Controls.Add(_btnToggleAll);

        _btnAdd = new ClickPanel("Add +")
        {
            Left   = pad + FilterPanelWidth - 78 - 6 - 78, Top = y - 2,
            Width  = 78,                                   Height = 20,
            Cursor = Cursors.Hand
        };
        Controls.Add(_btnAdd);
        y += 22;

        // --- Scrollable filter list ---
        // Initial visible height shows all current filters plus one free row as a hint that
        // more can be added, capped at FilterMaxRows. The window stays fixed afterwards; once
        // the visible area is full, AutoScroll provides a scrollbar.
        int initialRows = Math.Min(_filters.Count + 1, FilterMaxRows);
        _filterPanel = new Panel
        {
            Left        = pad, Top = y, Width = FilterPanelWidth,
            Height      = initialRows * FilterRowHeight + 8,
            BackColor   = Color.FromArgb(50, 50, 50),
            BorderStyle = BorderStyle.FixedSingle,
            AutoScroll  = true
        };
        Controls.Add(_filterPanel);
        RebuildFilterRows();

        y += _filterPanel.Height + 14;

        // --- Buttons: Start | Stop | Refresh Devices | Restart App (4 slots) ---
        const int btnGap   = 7;
        const int totalW   = FilterPanelWidth;
        const int btnW     = (totalW - btnGap * 3) / 4;   // ~101px each
        const int lastBtnW = totalW - (btnW + btnGap) * 3; // remainder to avoid rounding gap

        _btnStart = new Button
        {
            Text      = "Start",
            Left      = pad, Top = y, Width = btnW, Height = 36,
            BackColor = Color.FromArgb(40, 120, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _bigBtnFont
        };
        _btnStart.FlatAppearance.BorderColor = Color.FromArgb(60, 160, 60);
        Controls.Add(_btnStart);

        _btnStop = new Button
        {
            Text      = "Stop",
            Left      = pad + (btnW + btnGap), Top = y, Width = btnW, Height = 36,
            BackColor = Color.FromArgb(120, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _bigBtnFont,
            Enabled   = false
        };
        _btnStop.FlatAppearance.BorderColor = Color.FromArgb(160, 60, 60);
        Controls.Add(_btnStop);

        // Slot 2: Refresh Devices - blue, two-line text
        _btnRefresh = new Button
        {
            Text      = "Refresh\nDevices",
            Left      = pad + (btnW + btnGap) * 2, Top = y, Width = btnW, Height = 36,
            BackColor = Color.FromArgb(50, 80, 120),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _twoLineFont
        };
        _btnRefresh.FlatAppearance.BorderColor        = Color.FromArgb(70, 110, 160);
        _btnRefresh.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 100, 145);
        _btnRefresh.FlatAppearance.MouseDownBackColor = Color.FromArgb(40, 65, 100);
        Controls.Add(_btnRefresh);

        // Slot 3: Restart App - dark grey, two-line text, always enabled.
        // Performs a real process restart (see OnRestartClick).
        _btnRestart = new Button
        {
            Text      = "Restart\nApp",
            Left      = pad + (btnW + btnGap) * 3, Top = y, Width = lastBtnW, Height = 36,
            BackColor = Color.FromArgb(65, 65, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = _twoLineFont
        };
        _btnRestart.FlatAppearance.BorderColor        = Color.FromArgb(95, 95, 95);
        _btnRestart.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 80);
        _btnRestart.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 50, 50);
        Controls.Add(_btnRestart);

        y += 44;

        // --- Status bar ---
        var statusPanel = new Panel
        {
            Left      = pad, Top = y, Width = FilterPanelWidth, Height = 28,
            BackColor = Color.FromArgb(40, 40, 40)
        };
        _statusDot = new Panel
        {
            Left = 8, Top = 8, Width = 13, Height = 13,
            BackColor = Color.Gray
        };
        MakeCircle(_statusDot);
        _statusLabel = new Label
        {
            Left = 28, Top = 5, Width = 390, Height = 20,
            ForeColor = Color.Silver,
            Text      = "Not running"
        };
        statusPanel.Controls.Add(_statusDot);
        statusPanel.Controls.Add(_statusLabel);
        Controls.Add(statusPanel);
        y += 32;

        // --- Log header: label + on/off toggle (the header stays visible when the log is off) ---
        _logLabel = new Label
        {
            Text      = "Activity Log:", Left = pad, Top = y,
            AutoSize  = true,
            ForeColor = Color.FromArgb(180, 180, 180)
        };
        Controls.Add(_logLabel);

        _btnLogToggle = new ClickPanel("Log Off")
        {
            Left   = pad + FilterPanelWidth - 78, Top = y - 2,
            Width  = 78,                          Height = 20,
            Cursor = Cursors.Hand
        };
        Controls.Add(_btnLogToggle);
        y += 22;

        // --- Log ---
        _logViewTop = y;
        _logView = new LogView(MaxLogEntries)
        {
            Left = pad, Top = y, Width = FilterPanelWidth, Height = LogViewHeight,
            Font = _logFont
        };
        Controls.Add(_logView);
        y += LogSectionHeight;

        _lblFiltered = new Label
        {
            Left      = pad, Top = y, Width = FilterPanelWidth,
            ForeColor = Color.FromArgb(150, 150, 150),
            Text      = "Filtered: 0 Messages"
        };
        Controls.Add(_lblFiltered);
        y += 24;

        // Fixed window sized to fit all content (including the initial filter list height).
        int formHeight  = y + pad + (Height - ClientSize.Height);
        _formHeightBase = formHeight - LogSectionHeight;
        Size            = new Size(480, formHeight);
        MinimumSize     = new Size(480, formHeight);

        ApplyLogVisibility();
    }

    /// <summary>
    /// Builds the filter list from saved settings: the five fixed pedals (states from
    /// BlockedCCs, default all active) followed by the saved custom filters.
    /// Called from the constructor before BuildUI.
    /// </summary>
    private void LoadFilters()
    {
        _filters.Clear();

        HashSet<int>? savedCCs = AppSettings.LoadBlockedCCs();
        foreach (var (cc, label) in CC_DEFINITIONS)
        {
            bool active = savedCCs == null || savedCCs.Contains(cc);
            _filters.Add(new FilterItem(FilterKind.Cc, cc, active, removable: false, label));
        }

        List<CustomFilter>? custom = AppSettings.LoadCustomFilters();
        if (custom != null)
        {
            foreach (CustomFilter f in custom)
                _filters.Add(new FilterItem(f.Kind, f.Value, f.Active,
                                            removable: true, LabelFor(f.Kind, f.Value), f.Name));
        }
    }

    /// <summary>
    /// Builds the display label for a filter kind and value.
    /// Called by LoadFilters and OnAddClick.
    /// </summary>
    private static string LabelFor(FilterKind kind, int value) => kind switch
    {
        FilterKind.Cc       => $"CC{value}",
        FilterKind.Note     => $"Note {value} ({MidiFilterEngine.NoteName(value)})",
        FilterKind.AllNotes => "All Notes",
        _                   => value.ToString()
    };

    /// <summary>
    /// Rebuilds the toggle rows in the scrollable filter panel from _filters. Disposes the
    /// previous row controls first so no GDI or control handles leak. Each removable row gets
    /// a small remove button. Only called on add/remove and at startup, never on plain toggling.
    /// </summary>
    private void RebuildFilterRows()
    {
        _filterPanel.SuspendLayout();

        // Dispose and clear previous rows (Controls.Clear alone would not dispose them).
        for (int i = _filterPanel.Controls.Count - 1; i >= 0; i--)
        {
            Control c = _filterPanel.Controls[i];
            _filterPanel.Controls.RemoveAt(i);
            c.Dispose();
        }
        _toggleButtons.Clear();

        const int leftPad       = 6;
        const int topPad        = 6;
        const int gap           = 4;
        const int btnW          = 24;   // rename and remove buttons (equal width)
        const int scrollReserve = 20;   // keep room for the vertical scrollbar
        // All toggles share one width; the rename/remove columns are reserved on every row
        // so fixed and custom rows line up (fixed rows simply leave those columns empty).
        int toggleWidth = FilterPanelWidth - 2 - leftPad - gap - btnW - gap - btnW - scrollReserve;
        int renameLeft  = leftPad + toggleWidth + gap;
        int removeLeft  = renameLeft + btnW + gap;

        for (int i = 0; i < _filters.Count; i++)
        {
            FilterItem item = _filters[i];
            int top = topPad + i * FilterRowHeight;

            var toggle = new FilterToggleButton(item.DisplayLabel)
            {
                Active = item.Active,
                Left   = leftPad,
                Top    = top,
                Width  = toggleWidth
            };
            toggle.Click += (_, _) =>
            {
                item.Active = toggle.Active;   // the toggle has already flipped its own state
                ApplyTogglesToEngine();
                SaveFilterState();
                UpdateToggleAllLabel();
            };
            _filterPanel.Controls.Add(toggle);
            _toggleButtons.Add(toggle);

            if (item.Removable)
            {
                var btnRename = new Button
                {
                    Text      = "✎",
                    Left      = renameLeft,
                    Top       = top,
                    Width     = btnW,
                    Height    = 26,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(45, 55, 70),
                    ForeColor = Color.FromArgb(150, 180, 220),
                    Font      = _iconFont,
                    Cursor    = Cursors.Hand,
                    TabStop   = false
                };
                btnRename.FlatAppearance.BorderColor        = Color.FromArgb(70, 90, 120);
                btnRename.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 80, 110);
                btnRename.FlatAppearance.MouseDownBackColor = Color.FromArgb(40, 55, 80);
                btnRename.Click += (_, _) => RenameFilter(item);
                _filterPanel.Controls.Add(btnRename);

                var btnRemove = new Button
                {
                    Text      = "x",
                    Left      = removeLeft,
                    Top       = top,
                    Width     = btnW,
                    Height    = 26,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 45, 45),
                    ForeColor = Color.FromArgb(210, 140, 140),
                    Font      = _iconFont,
                    Cursor    = Cursors.Hand,
                    TabStop   = false
                };
                btnRemove.FlatAppearance.BorderColor        = Color.FromArgb(90, 60, 60);
                btnRemove.FlatAppearance.MouseOverBackColor = Color.FromArgb(110, 50, 50);
                btnRemove.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 40, 40);
                btnRemove.Click += (_, _) => RemoveFilter(item);
                _filterPanel.Controls.Add(btnRemove);
            }
        }

        // Match the scrollable area to the exact row height so the list never shows a
        // phantom empty row (a spurious scrollbar caused by control margins) and scrolls in
        // whole-row steps. Show the top after a rebuild.
        _filterPanel.AutoScrollMinSize = new Size(0, topPad + _filters.Count * FilterRowHeight);

        _filterPanel.ResumeLayout();
        _filterPanel.AutoScrollPosition = Point.Empty;
        UpdateToggleAllLabel();
    }

    /// <summary>
    /// Removes a user-added filter, rebuilds the rows, then applies and saves the change.
    /// Called by a row's remove button.
    /// </summary>
    private void RemoveFilter(FilterItem item)
    {
        _filters.Remove(item);
        RebuildFilterRows();
        ApplyTogglesToEngine();
        SaveFilterState();
    }

    /// <summary>
    /// Opens a small dialog to rename a user-added filter. An empty name clears the custom
    /// name (back to the base label). Does not change filtering, only the displayed label.
    /// Called by a row's rename button.
    /// </summary>
    private void RenameFilter(FilterItem item)
    {
        using var dlg = new RenameDialog(item.BaseLabel, item.Name);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        item.Name = dlg.FilterName;
        RebuildFilterRows();
        SaveFilterState();
    }

    /// <summary>
    /// Pushes the active filter sets to the engine. Takes effect immediately, no restart.
    /// Called on any toggle change, on add/remove, and on filter start.
    /// </summary>
    private void ApplyTogglesToEngine()
    {
        var ccs      = new HashSet<int>();
        var notes    = new HashSet<int>();
        bool allNotes = false;

        foreach (FilterItem f in _filters)
        {
            if (!f.Active)
                continue;
            switch (f.Kind)
            {
                case FilterKind.Cc:       ccs.Add(f.Value);   break;
                case FilterKind.Note:     notes.Add(f.Value); break;
                case FilterKind.AllNotes: allNotes = true;    break;
            }
        }

        _engine.SetBlockedCCs(ccs);
        _engine.SetBlockedNotes(notes);
        _engine.SetBlockAllNotes(allNotes);
    }

    /// <summary>
    /// Active CC numbers among the fixed pedals only (persisted under BlockedCCs).
    /// Called by SaveFilterState.
    /// </summary>
    private HashSet<int> CollectFixedBlockedCCs()
    {
        var result = new HashSet<int>();
        foreach (FilterItem f in _filters)
        {
            if (!f.Removable && f.Kind == FilterKind.Cc && f.Active)
                result.Add(f.Value);
        }
        return result;
    }

    /// <summary>
    /// The user-added filters as CustomFilter entries (persisted under CustomFilters).
    /// Called by SaveFilterState.
    /// </summary>
    private List<CustomFilter> CollectCustomFilters()
    {
        var result = new List<CustomFilter>();
        foreach (FilterItem f in _filters)
        {
            if (f.Removable)
                result.Add(new CustomFilter(f.Kind, f.Value, f.Active, f.Name));
        }
        return result;
    }

    /// <summary>
    /// Persists fixed-pedal states and custom filters in a single write.
    /// Called on any filter change, on start, on restart, and on close.
    /// </summary>
    private void SaveFilterState()
    {
        AppSettings.SaveFilters(CollectFixedBlockedCCs(), CollectCustomFilters());
    }

    /// <summary>
    /// Persists the current device selection if both entries are real devices.
    /// Called by StartFilter, OnRestartClick, and the closing handler.
    /// </summary>
    private void SaveDeviceSelection()
    {
        string inputName  = _cmbInput.SelectedItem  as string ?? string.Empty;
        string outputName = _cmbOutput.SelectedItem as string ?? string.Empty;

        if (IsRealDevice(inputName) && IsRealDevice(outputName))
            AppSettings.Save(inputName, outputName);
    }

    /// <summary>
    /// True when the given combo box entry is an actual device and not the placeholder.
    /// Called by SaveDeviceSelection and StartFilter.
    /// </summary>
    private static bool IsRealDevice(string name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim() != "-";

    /// <summary>
    /// Opens the picker dialog and adds the chosen filters. Entries already present are
    /// excluded by the dialog, so no duplicates are created.
    /// Called when the Add button is clicked.
    /// </summary>
    private void OnAddClick(object? sender, EventArgs e)
    {
        var usedCCs   = new HashSet<int>();
        var usedNotes = new HashSet<int>();
        bool hasAll   = false;

        foreach (FilterItem f in _filters)
        {
            switch (f.Kind)
            {
                case FilterKind.Cc:       usedCCs.Add(f.Value);   break;
                case FilterKind.Note:     usedNotes.Add(f.Value); break;
                case FilterKind.AllNotes: hasAll = true;          break;
            }
        }

        using var dlg = new FilterPickerDialog(usedCCs, usedNotes, hasAll);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result.Count == 0)
            return;

        foreach (CustomFilter f in dlg.Result)
            _filters.Add(new FilterItem(f.Kind, f.Value, f.Active,
                                        removable: true, LabelFor(f.Kind, f.Value), f.Name));

        RebuildFilterRows();
        ApplyTogglesToEngine();
        SaveFilterState();
    }

    /// <summary>
    /// Toggles all filters to the opposite of their current combined state.
    /// If all are active, turns all off - otherwise turns all on.
    /// Called when the All-toggle button is clicked.
    /// </summary>
    private void OnToggleAllClick(object? sender, EventArgs e)
    {
        bool allActive = _filters.Count > 0 && _filters.All(f => f.Active);
        bool target    = !allActive;

        for (int i = 0; i < _filters.Count; i++)
        {
            _filters[i].Active = target;
            if (i < _toggleButtons.Count)
                _toggleButtons[i].Active = target;
        }

        UpdateToggleAllLabel();
        ApplyTogglesToEngine();
        SaveFilterState();
    }

    /// <summary>
    /// Updates the All-toggle label: "All Off" when everything is active (next click turns
    /// all off), "All On" otherwise. Called after any filter state change.
    /// </summary>
    private void UpdateToggleAllLabel()
    {
        bool allActive = _filters.Count > 0 && _filters.All(f => f.Active);
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
    /// Called by BuildUI for the status dot.
    /// </summary>
    private static void MakeCircle(Panel p)
    {
        // Dispose the previous region and the path so we do not leak GDI handles.
        // The status dot only needs its circular region set once (in BuildUI).
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(0, 0, p.Width, p.Height);
        p.Region?.Dispose();
        p.Region = new Region(path);
    }

    /// <summary>
    /// Populates input/output combo boxes with current MIDI devices.
    /// On first load restores last saved selection; adds a blank placeholder entry.
    /// On subsequent calls restores the current selection.
    /// </summary>
    private void PopulateDevices()
    {
        string prevIn  = _cmbInput.SelectedItem  as string ?? string.Empty;
        string prevOut = _cmbOutput.SelectedItem as string ?? string.Empty;
        bool firstLoad = _cmbInput.Items.Count == 0 && _cmbOutput.Items.Count == 0;

        _cmbInput.BeginUpdate();
        _cmbOutput.BeginUpdate();
        try
        {
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

                _cmbInput.SelectedIndex  = savedIn  != null ? EnsureItem(_cmbInput,  savedIn)  : 0;
                _cmbOutput.SelectedIndex = savedOut != null ? EnsureItem(_cmbOutput, savedOut) : 0;
            }
            else
            {
                _cmbInput.SelectedIndex  = IsRealDevice(prevIn)  ? EnsureItem(_cmbInput,  prevIn)  : 0;
                _cmbOutput.SelectedIndex = IsRealDevice(prevOut) ? EnsureItem(_cmbOutput, prevOut) : 0;
            }
        }
        finally
        {
            _cmbInput.EndUpdate();
            _cmbOutput.EndUpdate();
        }
    }

    /// <summary>
    /// Returns the index of the item matching the given device name, adding the name to the
    /// list when the device is currently offline. Keeping an absent device selectable lets
    /// the engine wait for it to appear (Synthesia or a virtual port started later) instead
    /// of forcing the user to restart the app once the device shows up.
    /// Called by PopulateDevices.
    /// </summary>
    private static int EnsureItem(ComboBox cmb, string name)
    {
        int? found = FindItemIndex(cmb, name);
        if (found.HasValue)
            return found.Value;

        return cmb.Items.Add(name);
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
    /// Connects button clicks and engine events to handlers, and creates the counter timer.
    /// Toggle and remove handlers are wired per row in RebuildFilterRows.
    /// Called from constructor after BuildUI.
    /// </summary>
    private void WireEvents()
    {
        _btnStart.Click       += OnStartClick;
        _btnStop.Click        += OnStopClick;
        _btnRestart.Click     += OnRestartClick;
        _btnRefresh.Click     += OnRefreshClick;
        _btnToggleAll.Clicked += OnToggleAllClick;
        _btnAdd.Clicked       += OnAddClick;
        _btnLogToggle.Clicked += OnLogToggleClick;

        // Counter refresh timer - runs only while filtering (started in StartFilter).
        _countTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _countTimer.Tick += OnCountTick;

        FormClosing += (_, _) =>
        {
            _countTimer.Stop();

            // Full dispose so the watcher thread and the wake handle are released too.
            try { _engine.Dispose(); } catch { }

            // Persist last device selection and the full filter state on every close,
            // unless a restart already did it a moment ago.
            if (!_restarting)
            {
                try
                {
                    SaveDeviceSelection();
                    SaveFilterState();
                }
                catch
                {
                    // Settings location not writable - ignore so closing never fails.
                }
            }

            _countTimer.Dispose();
            Icon = null;
            _appIcon?.Dispose();
        };

        _engine.StatusChanged += msg => SafeInvoke(() =>
        {
            _statusLabel.Text = msg;
            AddLog(msg);
        });

        _engine.ConnectionChanged += connected => SafeInvoke(() =>
        {
            // Region was set once in BuildUI; only the colour changes here.
            _statusDot.BackColor = connected
                ? Color.FromArgb(40, 200, 40)
                : Color.FromArgb(200, 80, 40);
        });

        // The engine time-gates these and tracks the running total itself (shown via the timer).
        _engine.MessageFiltered += msg => SafeInvoke(() => AddLog(msg));
    }

    /// <summary>
    /// Click handler for the Start button. Delegates to StartFilter with popups enabled.
    /// </summary>
    private void OnStartClick(object? sender, EventArgs e) => StartFilter(silent: false);

    /// <summary>
    /// Returns true if the combo box holds a real, non-placeholder device selection.
    /// Called by the auto-start Load handler.
    /// </summary>
    private static bool HasValidSelection(ComboBox cmb)
        => cmb.SelectedItem is string s && IsRealDevice(s);

    /// <summary>
    /// Starts the filter engine with the selected devices and current filter selection,
    /// saves the selection to disk, and updates the UI. Returns false (without starting)
    /// if a device is missing. When silent is true the warning popups are suppressed,
    /// used by auto-start so a missing saved device does not nag the user on launch.
    /// Called from OnStartClick and the auto-start Load handler.
    /// </summary>
    private bool StartFilter(bool silent)
    {
        if (_cmbInput.SelectedItem is not string inputName || !IsRealDevice(inputName))
        {
            if (!silent)
                MessageBox.Show("Please choose a MIDI Input.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (_cmbOutput.SelectedItem is not string outputName || !IsRealDevice(outputName))
        {
            if (!silent)
                MessageBox.Show("Please choose a MIDI Output.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _lastShownCount   = 0;
        _lblFiltered.Text = "Filtered: 0 Messages";
        _logView.Clear();

        ApplyTogglesToEngine();
        AppSettings.Save(inputName, outputName);
        SaveFilterState();
        _engine.Start(inputName, outputName);
        _countTimer.Start();

        _btnStart.Enabled  = false;
        _btnStop.Enabled   = true;
        _cmbInput.Enabled  = false;
        _cmbOutput.Enabled = false;

        AddLog($"Filter activated: {inputName} -> {outputName}");
        return true;
    }

    /// <summary>
    /// Timer tick: refreshes the filtered-message counter label from the engine, but only
    /// when the value actually changed. Runs only while filtering is active.
    /// Wired in WireEvents; started/stopped together with the filter.
    /// </summary>
    private void OnCountTick(object? sender, EventArgs e)
    {
        long count = _engine.FilteredCount;
        if (count == _lastShownCount)
            return;
        _lastShownCount   = count;
        _lblFiltered.Text = $"Filtered: {count} Messages";
    }

    /// <summary>
    /// Stops the filter engine.
    /// Called when Stop button is clicked.
    /// </summary>
    private void OnStopClick(object? sender, EventArgs e)
    {
        _btnStop.Enabled = false;
        UseWaitCursor    = true;
        try
        {
            _engine.Stop();
            _countTimer.Stop();
        }
        finally
        {
            UseWaitCursor = false;
        }

        _btnStart.Enabled  = true;
        _cmbInput.Enabled  = true;
        _cmbOutput.Enabled = true;

        _statusDot.BackColor = Color.Gray;
        _statusLabel.Text    = "Stopped";
        AddLog("Filter deactivated.");
    }

    /// <summary>
    /// Hard restart: persists the current state, releases the MIDI ports, launches a fresh
    /// instance and exits this one. A cold process is the only reliable recovery when a MIDI
    /// driver or a peer application left a port in a broken state.
    /// Called when the Restart App button is clicked.
    /// </summary>
    private void OnRestartClick(object? sender, EventArgs e)
    {
        _btnRestart.Enabled = false;
        _restarting         = true;
        UseWaitCursor       = true;

        _statusLabel.Text = "Restarting...";
        AddLog(">>>>  Restarting application  <<<<");
        Application.DoEvents();   // make the status visible before the window goes away

        // Save first: the new process reads settings.cfg during its startup.
        try
        {
            SaveDeviceSelection();
            SaveFilterState();
        }
        catch { }

        // Release the MIDI ports before the new instance tries to open them.
        _countTimer.Stop();
        try { _engine.Stop(); } catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                Application.Exit();
                return;
            }
        }
        catch
        {
            // Fall through to the framework restart below.
        }

        try
        {
            Application.Restart();
        }
        catch (Exception ex)
        {
            _restarting         = false;
            _btnRestart.Enabled = true;
            UseWaitCursor       = false;
            MessageBox.Show($"Restart failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
    /// Adds an entry to the activity log. Repeated messages are collapsed into a counted
    /// line by the log view instead of being dropped. Does nothing while the log is off.
    /// Called from engine event handlers.
    /// </summary>
    private void AddLog(string message)
    {
        if (!_logEnabled)
            return;
        _logView.Add(message);
    }

    /// <summary>
    /// Switches the activity log on or off, persists the state, and resizes the window.
    /// Disabling drops all entries and stops the engine from raising log events at all.
    /// Called when the log toggle in the log header is clicked.
    /// </summary>
    private void OnLogToggleClick(object? sender, EventArgs e)
    {
        _logEnabled = !_logEnabled;
        AppSettings.SaveLogEnabled(_logEnabled);
        ApplyLogVisibility();
    }

    /// <summary>
    /// Applies the current log state: shows or hides the log area, moves the counter label,
    /// resizes the window, updates the header texts, and tells the engine whether to raise
    /// per-message log events.
    /// Called at the end of BuildUI and by OnLogToggleClick.
    /// </summary>
    private void ApplyLogVisibility()
    {
        if (!_logEnabled)
            _logView.Clear();

        _logView.Visible  = _logEnabled;
        _logLabel.Text    = _logEnabled ? "Activity Log:" : "Activity Log (disabled)";
        _btnLogToggle.SetLabel(_logEnabled ? "Log Off" : "Log On");
        _lblFiltered.Top  = _logViewTop + (_logEnabled ? LogSectionHeight : 0);

        int height  = _formHeightBase + (_logEnabled ? LogSectionHeight : 0);
        MinimumSize = new Size(480, height);   // set first so Size is never clamped
        Size        = new Size(480, height);

        _engine.SetLogFiltered(_logEnabled);
    }

    /// <summary>
    /// Thread-safe UI invoke helper.
    /// Called from engine background thread event handlers.
    /// </summary>
    private void SafeInvoke(Action action)
    {
        // Async BeginInvoke so the MIDI/watcher thread never blocks on the UI thread:
        // prevents added MIDI latency under load and avoids a deadlock when Stop joins
        // the watcher thread. The guards cover the window where the form is closing.
        if (IsDisposed || !IsHandleCreated || _restarting)
            return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // -----------------------------------------------------------------------------
    // ClickPanel - owner-drawn panel that behaves like a button.
    // Text is always drawn centered via GDI+ - immune to the WinForms pressed-state
    // text offset that affects standard Button controls.
    // Used for _btnToggleAll and _btnAdd in MainForm.
    // -----------------------------------------------------------------------------
    private sealed class ClickPanel : Panel
    {
        private static readonly Color _bgNormal  = Color.FromArgb(50,  80, 120);
        private static readonly Color _bgHover   = Color.FromArgb(60,  95, 140);
        private static readonly Color _bgPressed = Color.FromArgb(40,  65, 100);
        private static readonly Color _border    = Color.FromArgb(70, 110, 160);

        // Shared GDI resources, created once instead of per paint. Lifetime is the whole app.
        private static readonly Brush       _brNormal  = new SolidBrush(_bgNormal);
        private static readonly Brush       _brHover   = new SolidBrush(_bgHover);
        private static readonly Brush       _brPressed = new SolidBrush(_bgPressed);
        private static readonly Brush       _brText    = new SolidBrush(Color.White);
        private static readonly Pen         _penBorder = new Pen(_border);
        private static readonly Font        _font      = new("Segoe UI", 8f, FontStyle.Bold);
        private static readonly StringFormat _fmt      = new()
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

            // Background - pick the cached brush for the current state.
            Brush bg = _pressed ? _brPressed : _hover ? _brHover : _brNormal;
            g.FillRectangle(bg, rect);

            // Border
            g.DrawRectangle(_penBorder, 0, 0, rect.Width - 1, rect.Height - 1);

            // Text - always centered, never offset.
            g.DrawString(_label, _font, _brText, rect, _fmt);
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
    }

    // -----------------------------------------------------------------------------
    // FilterPickerDialog - modal dialog to add filters: an All Notes checkbox plus two
    // multi-select lists (controllers and notes, Pianoteq-style names). Entries already
    // in use are not listed. Used by MainForm.OnAddClick; Result holds the chosen filters.
    // -----------------------------------------------------------------------------
    private sealed class FilterPickerDialog : Form
    {
        private readonly CheckBox _chkAllNotes;
        private readonly ListBox  _lstControllers;
        private readonly ListBox  _lstNotes;

        public List<CustomFilter> Result { get; } = new();

        // List item carrying the underlying MIDI number plus its display text.
        private sealed class NumItem
        {
            public int Number { get; }
            private readonly string _text;
            public NumItem(int number, string text) { Number = number; _text = text; }
            public override string ToString() => _text;
        }

        public FilterPickerDialog(HashSet<int> usedCCs, HashSet<int> usedNotes, bool hasAllNotes)
        {
            Text            = "Add Filter";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ShowInTaskbar   = false;
            BackColor       = Color.FromArgb(30, 30, 30);
            ForeColor       = Color.WhiteSmoke;
            Font            = _formFont;
            ClientSize      = new Size(452, 500);

            _chkAllNotes = new CheckBox
            {
                Text      = "All Notes (filter every note)",
                Left      = 14, Top = 12, Width = 420, Height = 22,
                ForeColor = Color.FromArgb(160, 140, 255),
                Enabled   = !hasAllNotes
            };
            Controls.Add(_chkAllNotes);

            Controls.Add(new Label { Text = "Controllers", Left = 14,  Top = 42, AutoSize = true,
                                     ForeColor = Color.FromArgb(180, 180, 180) });
            Controls.Add(new Label { Text = "Notes",       Left = 234, Top = 42, AutoSize = true,
                                     ForeColor = Color.FromArgb(180, 180, 180) });

            _lstControllers = MakeList(14);
            _lstNotes       = MakeList(234);
            Controls.Add(_lstControllers);
            Controls.Add(_lstNotes);

            for (int cc = 0; cc <= 127; cc++)
                if (!usedCCs.Contains(cc))
                    _lstControllers.Items.Add(new NumItem(cc, $"Controller {cc}"));

            for (int n = 0; n <= 127; n++)
                if (!usedNotes.Contains(n))
                    _lstNotes.Items.Add(new NumItem(n, $"Note {n} ({MidiFilterEngine.NoteName(n)})"));

            var btnAdd = new Button
            {
                Text      = "Add",
                Left      = 234, Top = 462, Width = 100, Height = 28,
                BackColor = Color.FromArgb(40, 120, 40),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnAdd.FlatAppearance.BorderColor = Color.FromArgb(60, 160, 60);
            btnAdd.Click += (_, _) => { BuildResult(); DialogResult = DialogResult.OK; };
            Controls.Add(btnAdd);

            var btnCancel = new Button
            {
                Text      = "Cancel",
                Left      = 340, Top = 462, Width = 94, Height = 28,
                BackColor = Color.FromArgb(65, 65, 65),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(95, 95, 95);
            btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnAdd;
            CancelButton = btnCancel;
        }

        /// <summary>
        /// Creates a dark, multi-select list box at the given left position.
        /// Called by the constructor.
        /// </summary>
        private static ListBox MakeList(int left) => new()
        {
            Left           = left, Top = 64, Width = 204, Height = 386,
            BackColor      = Color.FromArgb(20, 20, 20),
            ForeColor      = Color.WhiteSmoke,
            BorderStyle    = BorderStyle.FixedSingle,
            SelectionMode  = SelectionMode.MultiSimple,
            IntegralHeight = false
        };

        /// <summary>
        /// Fills Result from the current selection (controllers, notes, and All Notes).
        /// Newly chosen filters start active. Called when Add is clicked.
        /// </summary>
        private void BuildResult()
        {
            Result.Clear();

            if (_chkAllNotes.Enabled && _chkAllNotes.Checked)
                Result.Add(new CustomFilter(FilterKind.AllNotes, 0, true));

            foreach (NumItem it in _lstControllers.SelectedItems)
                Result.Add(new CustomFilter(FilterKind.Cc, it.Number, true));

            foreach (NumItem it in _lstNotes.SelectedItems)
                Result.Add(new CustomFilter(FilterKind.Note, it.Number, true));
        }
    }

    // -----------------------------------------------------------------------------
    // RenameDialog - small modal input for naming a custom filter. Returns the entered
    // name (trimmed) via FilterName; an empty result clears the name.
    // Used by MainForm.RenameFilter.
    // -----------------------------------------------------------------------------
    private sealed class RenameDialog : Form
    {
        private readonly TextBox _txt;

        public string FilterName => _txt.Text.Trim();

        public RenameDialog(string baseLabel, string currentName)
        {
            Text            = "Rename Filter";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ShowInTaskbar   = false;
            BackColor       = Color.FromArgb(30, 30, 30);
            ForeColor       = Color.WhiteSmoke;
            Font            = _formFont;
            ClientSize      = new Size(320, 132);

            Controls.Add(new Label
            {
                Text      = $"Name for \"{baseLabel}\":",
                Left      = 14, Top = 14, Width = 292, Height = 18,
                ForeColor = Color.FromArgb(180, 180, 180)
            });

            _txt = new TextBox
            {
                Left        = 14, Top = 38, Width = 292,
                Text        = currentName,
                MaxLength   = 40,
                BackColor   = Color.FromArgb(50, 50, 50),
                ForeColor   = Color.WhiteSmoke,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_txt);

            var btnOk = new Button
            {
                Text      = "OK",
                Left      = 118, Top = 86, Width = 90, Height = 28,
                BackColor = Color.FromArgb(40, 120, 40),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnOk.FlatAppearance.BorderColor = Color.FromArgb(60, 160, 60);
            btnOk.Click += (_, _) => DialogResult = DialogResult.OK;
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text      = "Cancel",
                Left      = 214, Top = 86, Width = 92, Height = 28,
                BackColor = Color.FromArgb(65, 65, 65),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(95, 95, 95);
            btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // Focus and preselect the text once shown, so the user can type over it.
            Shown += (_, _) => { _txt.Focus(); _txt.SelectAll(); };
        }
    }
}
