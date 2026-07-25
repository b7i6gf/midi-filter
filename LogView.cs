using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MidiFilter;

/// <summary>
/// Owner-drawn activity log. Replaces the ListBox so the log can have slim, themed
/// scrollbars (a ListBox only ever gets the native Windows ones), horizontal scrolling,
/// and repeated messages collapsed into a single counted line instead of being dropped.
/// Created and fed by MainForm (BuildUI / AddLog).
/// </summary>
internal sealed class LogView : Panel
{
    // One log line. Repeated identical messages increment Repeat instead of adding a row.
    private sealed class Entry
    {
        public string   Message = string.Empty;
        public DateTime Time;
        public int      Repeat = 1;
        public string   Text   = string.Empty;

        /// <summary>
        /// Rebuilds the cached display text so painting never formats strings.
        /// Called by LogView.Add.
        /// </summary>
        public void Render() => Text = Repeat > 1
            ? $"[{Time:HH:mm:ss}] {Message}  (x{Repeat})"
            : $"[{Time:HH:mm:ss}] {Message}";
    }

    // Layout constants.
    private const int ScrollSize = 8;    // scrollbar thickness
    private const int MinThumb   = 24;
    private const int PadLeft    = 4;
    private const int PadTop     = 2;

    // Theme colours, matching the rest of the window.
    private static readonly Color TrackColor      = Color.FromArgb( 30,  30,  30);
    private static readonly Color ThumbColor      = Color.FromArgb( 70,  70,  90);
    private static readonly Color ThumbHoverColor = Color.FromArgb(110, 100, 150);
    private static readonly Color ThumbDragColor  = Color.FromArgb(160, 140, 255);

    private readonly List<Entry> _entries = new();
    private readonly int _maxEntries;

    // Scroll state: vertical in whole lines, horizontal in pixels.
    private int  _topLine;
    private int  _offsetX;
    private bool _stickToBottom = true;

    // Cached metrics of the current font.
    private int _lineHeight = 12;
    private int _charWidth  = 7;
    // Longest entry in characters; monotonic between trims, which only ever overestimates
    // the horizontal range slightly and avoids re-measuring on every single message.
    private int _maxChars;

    // Drag state.
    private bool _dragV, _dragH, _hoverV, _hoverH;
    private int  _dragOffset;

    private WheelFilter? _wheelFilter;

    public LogView(int maxEntries)
    {
        _maxEntries = maxEntries;
        BorderStyle = BorderStyle.FixedSingle;
        BackColor   = Color.FromArgb(20, 20, 20);
        ForeColor   = Color.FromArgb(100, 220, 100);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>
    /// Recomputes the character metrics whenever the font changes.
    /// Called by WinForms on font assignment and from the constructor path.
    /// </summary>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Size probe = TextRenderer.MeasureText("0000000000", Font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        _charWidth  = Math.Max(1, probe.Width / 10);
        _lineHeight = Math.Max(1, probe.Height + 1);
        Invalidate();
    }

    /// <summary>
    /// Installs the wheel router so the log scrolls under the mouse pointer without
    /// having to take keyboard focus away from the rest of the window.
    /// Called by WinForms when the control gets its handle.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _wheelFilter ??= new WheelFilter(this);
        Application.AddMessageFilter(_wheelFilter);
    }

    /// <summary>
    /// Removes the wheel router again. Called by WinForms on handle destruction.
    /// </summary>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_wheelFilter != null)
            Application.RemoveMessageFilter(_wheelFilter);
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// Appends a message. An identical message directly after the previous one only bumps
    /// its repeat counter and timestamp, so bursts stay visible as "(xN)" instead of being
    /// silently dropped by a duplicate filter.
    /// Called by MainForm.AddLog.
    /// </summary>
    public void Add(string message)
    {
        Entry entry;
        if (_entries.Count > 0 && _entries[^1].Message == message)
        {
            entry = _entries[^1];
            entry.Repeat++;
            entry.Time = DateTime.Now;
        }
        else
        {
            entry = new Entry { Message = message, Time = DateTime.Now };
            _entries.Add(entry);

            if (_entries.Count > _maxEntries)
            {
                _entries.RemoveAt(0);
                if (_topLine > 0)
                    _topLine--;
            }
        }

        entry.Render();
        if (entry.Text.Length > _maxChars)
            _maxChars = entry.Text.Length;

        if (_stickToBottom)
            _topLine = MaxTopLine;

        Invalidate();
    }

    /// <summary>
    /// Drops all entries and resets the scroll position.
    /// Called by MainForm on filter start and when the log is disabled.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        _maxChars      = 0;
        _topLine       = 0;
        _offsetX       = 0;
        _stickToBottom = true;
        Invalidate();
    }

    // ---------------------------------------------------------------------
    // Geometry
    // ---------------------------------------------------------------------

    private int ContentWidth => _maxChars * _charWidth + PadLeft * 2;

    /// <summary>
    /// Resolves the visible area and which scrollbars are needed. Both bars steal space
    /// from each other, so the decision is iterated twice.
    /// Called by painting and all mouse handling.
    /// </summary>
    private void GetLayout(out int viewW, out int viewH, out bool vBar, out bool hBar)
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        vBar  = false;
        hBar  = false;
        viewW = w;
        viewH = h;

        for (int i = 0; i < 2; i++)
        {
            viewW = w - (vBar ? ScrollSize : 0);
            viewH = h - (hBar ? ScrollSize : 0);
            vBar  = _entries.Count > Math.Max(1, (viewH - PadTop) / _lineHeight);
            hBar  = ContentWidth > viewW;
        }

        viewW = w - (vBar ? ScrollSize : 0);
        viewH = h - (hBar ? ScrollSize : 0);
    }

    private int VisibleLines
    {
        get
        {
            GetLayout(out _, out int viewH, out _, out _);
            return Math.Max(1, (viewH - PadTop) / _lineHeight);
        }
    }

    private int MaxTopLine => Math.Max(0, _entries.Count - VisibleLines);

    private int MaxOffsetX
    {
        get
        {
            GetLayout(out int viewW, out _, out _, out _);
            return Math.Max(0, ContentWidth - viewW);
        }
    }

    // ---------------------------------------------------------------------
    // Painting
    // ---------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        GetLayout(out int viewW, out int viewH, out bool vBar, out bool hBar);
        int visible = Math.Max(1, (viewH - PadTop) / _lineHeight);

        // Clip the text to the area not covered by the scrollbars.
        g.SetClip(new Rectangle(0, 0, viewW, viewH));

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        for (int i = 0; i < visible; i++)
        {
            int index = _topLine + i;
            if (index < 0 || index >= _entries.Count)
                break;

            TextRenderer.DrawText(g, _entries[index].Text, Font,
                new Point(PadLeft - _offsetX, PadTop + i * _lineHeight), ForeColor, flags);
        }

        g.ResetClip();

        if (vBar)
            DrawBar(g, VerticalTrack(viewH), VerticalThumb(viewH), _dragV, _hoverV, vertical: true);
        if (hBar)
            DrawBar(g, HorizontalTrack(viewW, viewH), HorizontalThumb(viewW, viewH), _dragH, _hoverH, vertical: false);
    }

    /// <summary>
    /// Draws one slim scrollbar: flat track plus a rounded thumb that brightens on hover
    /// and turns to the accent colour while dragging.
    /// Called by OnPaint.
    /// </summary>
    private static void DrawBar(Graphics g, Rectangle track, Rectangle thumb,
                                bool dragging, bool hover, bool vertical)
    {
        using var trackBrush = new SolidBrush(TrackColor);
        g.FillRectangle(trackBrush, track);

        Color color = dragging ? ThumbDragColor : hover ? ThumbHoverColor : ThumbColor;
        using var thumbBrush = new SolidBrush(color);

        // Inset by one pixel on the thin axis so the thumb does not touch the edges.
        Rectangle r = vertical
            ? new Rectangle(thumb.X + 1, thumb.Y, thumb.Width - 2, thumb.Height)
            : new Rectangle(thumb.X, thumb.Y + 1, thumb.Width, thumb.Height - 2);

        if (r.Width <= 0 || r.Height <= 0)
            return;

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int radius = Math.Min(3, Math.Min(r.Width, r.Height) / 2);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (radius <= 0)
        {
            g.FillRectangle(thumbBrush, r);
        }
        else
        {
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(thumbBrush, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
    }

    private Rectangle VerticalTrack(int viewH)
        => new(ClientSize.Width - ScrollSize, 0, ScrollSize, viewH);

    private Rectangle HorizontalTrack(int viewW, int viewH)
        => new(0, viewH, viewW, ScrollSize);

    /// <summary>
    /// Thumb rectangle of the vertical bar, sized by the visible fraction of the content.
    /// Called by painting and mouse handling.
    /// </summary>
    private Rectangle VerticalThumb(int viewH)
    {
        Rectangle track = VerticalTrack(viewH);
        int visible = Math.Max(1, (viewH - PadTop) / _lineHeight);
        int total   = Math.Max(1, _entries.Count);

        int size = Math.Max(MinThumb, (int)((long)track.Height * visible / total));
        size     = Math.Min(size, track.Height);

        int max = MaxTopLine;
        int pos = max <= 0 ? 0 : (int)((long)(track.Height - size) * _topLine / max);
        return new Rectangle(track.X, track.Y + pos, track.Width, size);
    }

    /// <summary>
    /// Thumb rectangle of the horizontal bar.
    /// Called by painting and mouse handling.
    /// </summary>
    private Rectangle HorizontalThumb(int viewW, int viewH)
    {
        Rectangle track = HorizontalTrack(viewW, viewH);
        int content = Math.Max(1, ContentWidth);

        int size = Math.Max(MinThumb, (int)((long)track.Width * viewW / content));
        size     = Math.Min(size, track.Width);

        int max = Math.Max(0, content - viewW);
        int pos = max <= 0 ? 0 : (int)((long)(track.Width - size) * _offsetX / max);
        return new Rectangle(track.X + pos, track.Y, size, track.Height);
    }

    // ---------------------------------------------------------------------
    // Interaction
    // ---------------------------------------------------------------------

    /// <summary>
    /// Scrolls by wheel: vertical by three lines, horizontal when Shift is held.
    /// Called by the WheelFilter for wheel events over this control.
    /// </summary>
    internal void ScrollByWheel(int delta, Keys modifiers)
    {
        int steps = delta / 120;
        if (steps == 0)
            return;

        if ((modifiers & Keys.Shift) != 0)
            SetOffsetX(_offsetX - steps * _charWidth * 4);
        else
            SetTopLine(_topLine - steps * 3);
    }

    /// <summary>
    /// Applies a new first visible line and remembers whether the view sits at the bottom
    /// (auto follow stays on only while the user has not scrolled up).
    /// Called by wheel, drag and Add.
    /// </summary>
    private void SetTopLine(int value)
    {
        int max = MaxTopLine;
        int v   = Math.Clamp(value, 0, max);
        if (v == _topLine)
        {
            _stickToBottom = _topLine >= max;
            return;
        }
        _topLine       = v;
        _stickToBottom = _topLine >= max;
        Invalidate();
    }

    /// <summary>
    /// Applies a new horizontal pixel offset.
    /// Called by wheel and drag handling.
    /// </summary>
    private void SetOffsetX(int value)
    {
        int v = Math.Clamp(value, 0, MaxOffsetX);
        if (v == _offsetX)
            return;
        _offsetX = v;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        GetLayout(out int viewW, out int viewH, out bool vBar, out bool hBar);

        if (vBar && VerticalTrack(viewH).Contains(e.Location))
        {
            Rectangle thumb = VerticalThumb(viewH);
            if (thumb.Contains(e.Location))
            {
                _dragV      = true;
                _dragOffset = e.Y - thumb.Y;
            }
            else
            {
                // Click on the track pages towards the pointer.
                SetTopLine(_topLine + (e.Y < thumb.Y ? -VisibleLines : VisibleLines));
            }
            Capture = true;
            Invalidate();
            return;
        }

        if (hBar && HorizontalTrack(viewW, viewH).Contains(e.Location))
        {
            Rectangle thumb = HorizontalThumb(viewW, viewH);
            if (thumb.Contains(e.Location))
            {
                _dragH      = true;
                _dragOffset = e.X - thumb.X;
            }
            else
            {
                SetOffsetX(_offsetX + (e.X < thumb.X ? -viewW : viewW));
            }
            Capture = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        GetLayout(out int viewW, out int viewH, out bool vBar, out bool hBar);

        if (_dragV)
        {
            Rectangle track = VerticalTrack(viewH);
            int size  = VerticalThumb(viewH).Height;
            int span  = Math.Max(1, track.Height - size);
            int pos   = Math.Clamp(e.Y - _dragOffset - track.Y, 0, span);
            SetTopLine((int)((long)pos * MaxTopLine / span));
            return;
        }

        if (_dragH)
        {
            Rectangle track = HorizontalTrack(viewW, viewH);
            int size  = HorizontalThumb(viewW, viewH).Width;
            int span  = Math.Max(1, track.Width - size);
            int pos   = Math.Clamp(e.X - _dragOffset - track.X, 0, span);
            SetOffsetX((int)((long)pos * MaxOffsetX / span));
            return;
        }

        bool hoverV = vBar && VerticalTrack(viewH).Contains(e.Location);
        bool hoverH = hBar && HorizontalTrack(viewW, viewH).Contains(e.Location);
        if (hoverV != _hoverV || hoverH != _hoverH)
        {
            _hoverV = hoverV;
            _hoverH = hoverH;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragV || _dragH)
        {
            _dragV  = false;
            _dragH  = false;
            Capture = false;
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverV || _hoverH)
        {
            _hoverV = false;
            _hoverH = false;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    // -----------------------------------------------------------------------------
    // WheelFilter - routes wheel messages to the log while the pointer is over it,
    // so scrolling works without the control grabbing keyboard focus.
    // Installed by LogView.OnHandleCreated.
    // -----------------------------------------------------------------------------
    private sealed class WheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly LogView _owner;

        public WheelFilter(LogView owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL)
                return false;
            if (!_owner.IsHandleCreated || !_owner.Visible || _owner.IsDisposed)
                return false;

            long lparam = m.LParam.ToInt64();
            var  point  = new Point(unchecked((short)lparam), unchecked((short)(lparam >> 16)));
            if (!_owner.RectangleToScreen(_owner.ClientRectangle).Contains(point))
                return false;

            _owner.ScrollByWheel(unchecked((short)(m.WParam.ToInt64() >> 16)), Control.ModifierKeys);
            return true;
        }
    }
}
