using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    // «Подключение»: locations as a plain table — flag, name, transport and ping. One click switches;
    // arrows move the keyboard row and Enter picks it. More rows than fit scroll with the wheel.
    // The table carries its own two actions: a refresh glyph in the «Пинг» header checks every server,
    // and a dashed last row adds a subscription or a server.
    internal sealed class LocationsTable : ThemedControl {
        internal const string AddText = "Добавить подписку или сервер";
        readonly List<Server> servers = new List<Server>();
        Dictionary<string, string> pings = new Dictionary<string, string>();
        readonly ToolTip tip = new ToolTip { InitialDelay = 400 };
        readonly Timer spin = new Timer { Interval = 50 };
        string selectedId;
        int hoverRow = -1, keyRow = -1, first;
        bool hoverRefresh, checking, pingEnabled = true;
        float scale = 1f, spinAngle;
        internal event Action<string> Picked;
        internal event Action PingRequested, AddRequested;
        internal LocationsTable() {
            AccessibleRole = AccessibleRole.List; AccessibleName = "Локации"; Cursor = Cursors.Default;
            spin.Tick += delegate { spinAngle = (spinAngle + 24) % 360; Invalidate(RefreshBounds); };
        }

        internal void SetServers(IEnumerable<Server> items, string selected, Dictionary<string, string> pingMap) {
            servers.Clear(); servers.AddRange(items);
            selectedId = selected; pings = pingMap ?? new Dictionary<string, string>();
            if (keyRow > servers.Count) keyRow = -1;
            ClampScroll();
            Invalidate();
        }
        internal int RowCount { get { return servers.Count; } }
        internal string SelectedName { get { Server s = servers.FirstOrDefault(x => x.Id == selectedId); return s == null ? null : s.Name; } }
        internal string PingFor(int row) { string ping; return row >= 0 && row < servers.Count && pings.TryGetValue(servers[row].Id, out ping) ? ping : null; }
        internal void PickRow(int row) { if (row < 0 || row >= servers.Count || !Enabled) return; Action<string> handler = Picked; if (handler != null) handler(servers[row].Id); }
        // The refresh glyph turns while every server is being pinged and ignores clicks then.
        internal bool Checking { get { return checking; } set { if (checking == value) return; checking = value; if (value) spin.Start(); else { spin.Stop(); spinAngle = 0; } Invalidate(); } }
        internal bool PingEnabled { get { return pingEnabled; } set { if (pingEnabled == value) return; pingEnabled = value; Invalidate(); } }
        internal void RequestPing() { if (!Enabled || !pingEnabled || checking) return; Action handler = PingRequested; if (handler != null) handler(); }
        internal void RequestAdd() { if (!Enabled) return; Action handler = AddRequested; if (handler != null) handler(); }
        internal Rectangle RefreshBounds { get { int size = (int)(22 * scale); return new Rectangle(Width - (int)(12 * scale) - size + (int)(5 * scale), (HeaderHeight - size) / 2, size, size); } }
        internal Rectangle AddRowBounds { get { return RowBounds(servers.Count); } }

        // Servers and then the add row.
        int Total { get { return servers.Count + 1; } }
        int HeaderHeight { get { return (int)(28 * scale); } }
        int RowHeight { get { return (int)(36 * scale); } }
        int VisibleRows { get { return Math.Max(1, (Height - HeaderHeight - (int)(4 * scale)) / Math.Max(1, RowHeight)); } }
        void ClampScroll() { first = Math.Max(0, Math.Min(first, Total - VisibleRows)); }
        internal Rectangle RowBounds(int row) { return new Rectangle(0, HeaderHeight + (int)(4 * scale) + (row - first) * RowHeight, Width, RowHeight - (int)(2 * scale)); }
        int RowAt(Point point) {
            if (point.Y < HeaderHeight + (int)(4 * scale)) return -1;
            int row = first + (point.Y - HeaderHeight - (int)(4 * scale)) / Math.Max(1, RowHeight);
            return row < Total && row < first + VisibleRows ? row : -1;
        }

        protected override void OnMouseMove(MouseEventArgs args) {
            int row = RowAt(args.Location);
            bool overRefresh = RefreshBounds.Contains(args.Location);
            if (row != hoverRow || overRefresh != hoverRefresh) {
                if (overRefresh != hoverRefresh) tip.SetToolTip(this, overRefresh ? (checking ? "Проверяю пинг…" : "Проверить пинг всех серверов") : "");
                hoverRow = row; hoverRefresh = overRefresh;
                Cursor = row >= 0 || (overRefresh && pingEnabled && !checking) ? Cursors.Hand : Cursors.Default; Invalidate();
            }
            base.OnMouseMove(args);
        }
        protected override void OnMouseLeave(EventArgs args) { hoverRow = -1; hoverRefresh = false; tip.SetToolTip(this, ""); base.OnMouseLeave(args); }
        protected override void OnMouseDown(MouseEventArgs args) { Focus(); base.OnMouseDown(args); }
        protected override void OnMouseClick(MouseEventArgs args) {
            if (args.Button == MouseButtons.Left) {
                if (RefreshBounds.Contains(args.Location)) RequestPing();
                else { int row = RowAt(args.Location); if (row >= 0) { keyRow = row; if (row == servers.Count) RequestAdd(); else PickRow(row); } }
            }
            base.OnMouseClick(args);
        }
        protected override void OnMouseWheel(MouseEventArgs args) {
            first -= Math.Sign(args.Delta); ClampScroll(); Invalidate();
            base.OnMouseWheel(args);
        }
        protected override void OnResize(EventArgs args) { ClampScroll(); base.OnResize(args); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs args) {
            if (args.KeyCode == Keys.Up || args.KeyCode == Keys.Down) {
                if (keyRow < 0) keyRow = Math.Max(0, servers.FindIndex(x => x.Id == selectedId));
                else keyRow = Math.Max(0, Math.Min(Total - 1, keyRow + (args.KeyCode == Keys.Down ? 1 : -1)));
                if (keyRow < first) first = keyRow; else if (keyRow >= first + VisibleRows) first = keyRow - VisibleRows + 1;
                Invalidate(); args.Handled = true; return;
            }
            if (args.KeyCode == Keys.Enter && keyRow >= 0) { Activate(keyRow); args.Handled = true; return; }
            if (args.KeyCode == Keys.F5) { RequestPing(); args.Handled = true; return; }
            base.OnKeyDown(args);
        }
        void Activate(int row) { if (row == servers.Count) RequestAdd(); else PickRow(row); }
        // Space and Enter on the control itself do nothing without a keyboard row.
        protected override void OnKeyUp(KeyEventArgs args) { if (args.KeyCode == Keys.Space && keyRow >= 0) { Activate(keyRow); args.Handled = true; } }

        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            scale = g.DpiX / 96f;
            int pad = (int)(12 * scale), pingWidth = (int)(74 * scale), transportWidth = (int)(72 * scale);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            using (Font caption = Theme.Font(Theme.CaptionSize)) using (Font body = Theme.Font(Theme.BodySize + 0.5f)) using (Font bold = Theme.Font(Theme.BodySize + 0.5f, true)) using (Font small = Theme.Font(Theme.SmallSize)) {
                Rectangle head = new Rectangle(0, 0, Width, HeaderHeight);
                Rectangle refresh = RefreshBounds;
                TextRenderer.DrawText(g, "Локация", caption, new Rectangle(pad, 0, Width - 2 * pad - pingWidth - transportWidth, head.Height), Theme.Faint, flags | TextFormatFlags.Left);
                TextRenderer.DrawText(g, "Транспорт", caption, new Rectangle(Width - pad - pingWidth - transportWidth, 0, transportWidth, head.Height), Theme.Faint, flags | TextFormatFlags.Left);
                TextRenderer.DrawText(g, "Пинг", caption, new Rectangle(Width - pad - pingWidth, 0, refresh.Left - (int)(4 * scale) - (Width - pad - pingWidth), head.Height), Theme.Faint, flags | TextFormatFlags.Right);
                bool live = Enabled && pingEnabled;
                if (hoverRefresh && live && !checking) using (GraphicsPath path = Shapes.Round(refresh, 6 * scale)) using (Brush fill = new SolidBrush(Theme.Raised)) g.FillPath(fill, path);
                DrawRefresh(g, refresh, checking ? Theme.Accent : live ? (hoverRefresh ? Theme.Accent : Theme.Muted) : Theme.Faint, checking ? spinAngle : 0);
                using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 0, head.Bottom - 1, Width, head.Bottom - 1);
                int last = Math.Min(Total, first + VisibleRows);
                for (int row = first; row < last; row++) {
                    Rectangle box = RowBounds(row);
                    if (row == servers.Count) { DrawAddRow(g, box, body, row == hoverRow && Enabled, row == keyRow && ShowFocus); continue; }
                    Server server = servers[row];
                    bool selected = server.Id == selectedId, hot = row == hoverRow && Enabled;
                    if (selected || hot) using (GraphicsPath path = Shapes.Round(new RectangleF(box.X + .5f, box.Y + .5f, box.Width - 1, box.Height - 1), 5 * scale)) using (Brush fill = new SolidBrush(selected ? Theme.Selection : Theme.Card)) g.FillPath(fill, path);
                    if (row == keyRow && ShowFocus) using (GraphicsPath path = Shapes.Round(new RectangleF(box.X + .5f, box.Y + .5f, box.Width - 1, box.Height - 1), 5 * scale)) using (Pen edge = new Pen(Theme.Accent)) g.DrawPath(edge, path);
                    int flagWidth = (int)(22 * scale), flagHeight = (int)(16 * scale);
                    Flags.DrawBadge(g, new Rectangle(box.X + pad, box.Y + (box.Height - flagHeight) / 2, flagWidth, flagHeight), server.Country, server.Name);
                    int textLeft = box.X + pad + flagWidth + (int)(12 * scale);
                    Color ink = !Enabled ? Theme.Faint : selected ? Theme.Ink : Theme.Muted;
                    TextRenderer.DrawText(g, server.Name ?? server.Hostname, selected ? bold : body, new Rectangle(textLeft, box.Y, Math.Max(0, Width - pad - pingWidth - transportWidth - textLeft - (int)(8 * scale)), box.Height), selected ? (Enabled ? Theme.Ink : Theme.Faint) : ink, flags | TextFormatFlags.Left);
                    TextRenderer.DrawText(g, server.Protocol == "http3" ? "HTTP/3" : "HTTP/2", small, new Rectangle(Width - pad - pingWidth - transportWidth, box.Y, transportWidth, box.Height), Theme.Faint, flags | TextFormatFlags.Left);
                    string ping; pings.TryGetValue(server.Id, out ping);
                    TextRenderer.DrawText(g, String.IsNullOrEmpty(ping) ? "—" : ping, body, new Rectangle(Width - pad - pingWidth, box.Y, pingWidth, box.Height), ping == "нет ответа" ? Theme.Warning : ink, flags | TextFormatFlags.Right);
                }
                // A thin rail on the right edge says there is more than fits.
                if (Total > VisibleRows) {
                    float track = VisibleRows * RowHeight, thumb = Math.Max(18 * scale, track * VisibleRows / Total);
                    float y = HeaderHeight + 4 * scale + (track - thumb) * first / Math.Max(1, Total - VisibleRows);
                    using (Brush fill = new SolidBrush(Theme.LineStrong)) g.FillRectangle(fill, Width - 3 * scale, y, 2 * scale, thumb);
                }
            }
        }
        void DrawAddRow(Graphics g, Rectangle box, Font font, bool hot, bool focus) {
            RectangleF frame = new RectangleF(box.X + .5f, box.Y + 3 * scale, box.Width - 1, box.Height - 4 * scale);
            using (GraphicsPath path = Shapes.Round(frame, 6 * scale)) {
                if (hot) using (Brush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
                using (Pen edge = new Pen(focus || hot ? Theme.Accent : Theme.LineStrong, scale) { DashStyle = focus ? DashStyle.Solid : DashStyle.Dash }) g.DrawPath(edge, path);
            }
            Size text = TextRenderer.MeasureText(g, AddText, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            float plus = 10 * scale, gap = 7 * scale, width = plus + gap + text.Width;
            float left = frame.X + (frame.Width - width) / 2, cy = frame.Y + frame.Height / 2;
            using (Pen pen = new Pen(Enabled ? Theme.Accent : Theme.Faint, 1.6f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawLine(pen, left, cy, left + plus, cy); g.DrawLine(pen, left + plus / 2, cy - plus / 2, left + plus / 2, cy + plus / 2);
            }
            TextRenderer.DrawText(g, AddText, font, new Rectangle((int)(left + plus + gap), (int)frame.Y, text.Width + 2, (int)frame.Height), !Enabled ? Theme.Faint : hot ? Theme.Ink : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        // Circular arrow: a 290° arc with a head at its end, turned by angle while checking.
        void DrawRefresh(Graphics g, Rectangle box, Color color, float angle) {
            float r = 5.5f * scale, cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f, start = angle - 60, sweep = 290;
            using (Pen pen = new Pen(color, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, start, sweep);
            double e = (start + sweep) * Math.PI / 180;
            PointF end = new PointF(cx + r * (float)Math.Cos(e), cy + r * (float)Math.Sin(e));
            PointF tangent = new PointF(-(float)Math.Sin(e), (float)Math.Cos(e)), radial = new PointF((float)Math.Cos(e), (float)Math.Sin(e));
            float head = 2.6f * scale;
            PointF[] tri = { new PointF(end.X + tangent.X * head * 1.3f, end.Y + tangent.Y * head * 1.3f), new PointF(end.X + radial.X * head, end.Y + radial.Y * head), new PointF(end.X - radial.X * head, end.Y - radial.Y * head) };
            using (Brush fill = new SolidBrush(color)) g.FillPolygon(fill, tri);
        }
        protected override void Dispose(bool disposing) { if (disposing) { spin.Dispose(); tip.Dispose(); } base.Dispose(disposing); }
    }

    // A bare 38 × 20 switch for a table row: the caption stands beside it as a label.
    internal sealed class MiniSwitch : CheckBox {
        internal MiniSwitch() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            AutoSize = false; Size = new Size(38, 20); Cursor = Cursors.Hand; Text = "";
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = Theme.Px(g, 1), h = Height - 2 * inset;
            RectangleF track = new RectangleF(inset, inset, Width - 2 * inset, h);
            bool on = Checked && Enabled;
            using (GraphicsPath path = Shapes.Round(track, h / 2)) using (Brush fill = new SolidBrush(on ? Theme.Accent : Theme.LineStrong)) g.FillPath(fill, path);
            float knob = h - Theme.Px(g, 6), x = Checked ? track.Right - knob - Theme.Px(g, 3) : track.X + Theme.Px(g, 3);
            using (Brush fill = new SolidBrush(on ? Theme.AccentInk : Enabled ? Theme.Ink : Theme.Faint)) g.FillEllipse(fill, x, track.Y + Theme.Px(g, 3), knob, knob);
            if (Focused && ShowFocusCues) using (GraphicsPath path = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Height / 2f)) using (Pen edge = new Pen(Theme.Ink)) g.DrawPath(edge, path);
        }
    }

    // «Журнал»: the tail of the engine log inside the window, with copy and a way to the file.
    internal sealed class LogPage : UserControl {
        const int TailLines = 400;
        readonly LogView box = new LogView { Dock = DockStyle.Fill };
        readonly Label note = MainForm.TextLabel("", Theme.SmallSize, Theme.Faint);
        readonly bool previewOnly;
        internal string TextForTests { get { return box.Text; } }

        internal LogPage(bool preview) {
            previewOnly = preview;
            Dock = DockStyle.Fill; BackColor = Theme.Ground; Padding = new Padding(26, 20, 26, 18);
            Label heading = SettingsForm.Heading("Журнал"); heading.Dock = DockStyle.Top; heading.Height = 34;
            Label hint = SettingsForm.Hint("Последние строки журнала движка. Пригодится, если подключение не устанавливается: приложите их к вопросу администратору."); hint.Dock = DockStyle.Top; hint.Height = 40;
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, WrapContents = false, BackColor = Theme.Ground, Padding = new Padding(0, 12, 0, 0) };
            Button copy = MainForm.Button("Скопировать", true), refresh = MainForm.Button("Обновить", false), reveal = MainForm.Button("Показать файл", false);
            foreach (Button button in new[] { copy, refresh, reveal }) { button.Dock = DockStyle.None; button.Size = new Size(140, 34); button.Margin = new Padding(0, 0, 10, 0); }
            note.Dock = DockStyle.None; note.AutoSize = true; note.Margin = new Padding(4, 9, 0, 0);
            actions.Controls.Add(copy); actions.Controls.Add(refresh); actions.Controls.Add(reveal); actions.Controls.Add(note);
            Panel frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(12, 8, 2, 8) };
            frame.Paint += delegate(object sender, PaintEventArgs args) { using (Pen edge = new Pen(Theme.Line)) args.Graphics.DrawRectangle(edge, 0, 0, frame.Width - 1, frame.Height - 1); };
            frame.Controls.Add(box);
            Controls.Add(frame); Controls.Add(actions); Controls.Add(hint); Controls.Add(heading);
            copy.Click += delegate {
                try { if (box.Text.Length > 0) Clipboard.SetText(box.Text); note.Text = box.Text.Length > 0 ? "Скопировано." : "Журнал пуст."; }
                catch (Exception error) { note.Text = "Не удалось скопировать: " + error.Message; }
            };
            refresh.Click += delegate { Reload(); };
            reveal.Click += delegate {
                if (previewOnly) return;
                try {
                    if (File.Exists(EngineLog.Path)) Process.Start("explorer.exe", "/select,\"" + EngineLog.Path + "\"");
                    else note.Text = "Файл ещё не создан.";
                } catch (Exception error) { note.Text = "Не удалось открыть: " + error.Message; }
            };
        }
        internal void Reload() {
            note.Text = "";
            try {
                if (!File.Exists(EngineLog.Path)) { box.SetLines(new[] { "Журнал движка пуст." }); return; }
                string[] lines;
                // The engine keeps the file open for writing: read it shared.
                using (FileStream stream = new FileStream(EngineLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream)) lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
                box.SetLines(lines.Skip(Math.Max(0, lines.Length - TailLines)));
            } catch (Exception error) { box.SetLines(new[] { "Журнал недоступен: " + error.Message }); }
        }
    }

    // Read-only engine log: monospace, long lines wrap to the width (no sideways scrolling), a thin
    // rail instead of the system scrollbar. Errors stand out, stack frames step back. Ctrl+C / Ctrl+A copy all.
    internal sealed class LogView : Control {
        sealed class Row { internal string Text; internal Color Ink; internal bool Continued; }
        readonly List<string> lines = new List<string>();
        readonly List<Row> rows = new List<Row>();
        readonly Font font = Theme.Mono(9f);
        int first, wrapWidth = -1, dragOffset = -1;
        internal LogView() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Card; TabStop = true; AccessibleRole = AccessibleRole.Text; AccessibleName = "Журнал движка";
        }
        protected override void Dispose(bool disposing) { if (disposing) font.Dispose(); base.Dispose(disposing); }
        internal void SetLines(IEnumerable<string> source) {
            lines.Clear();
            foreach (string line in source) lines.Add((line ?? "").TrimEnd('\r').Replace("\t", "    "));
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            Text = String.Join("\r\n", lines);
            wrapWidth = -1; Rewrap();
            first = Math.Max(0, rows.Count - VisibleRows);
            Invalidate();
        }
        internal int RowCountForTests { get { Rewrap(); return rows.Count; } }

        int RowHeight { get { return Math.Max(1, (int)Math.Ceiling(font.GetHeight() + 3 * DpiScale)); } }
        float DpiScale { get { using (Graphics g = CreateGraphics()) return g.DpiX / 96f; } }
        int RailWidth { get { return (int)(10 * DpiScale); } }
        int VisibleRows { get { return Math.Max(1, ClientSize.Height / RowHeight); } }
        int MaxFirst { get { return Math.Max(0, rows.Count - VisibleRows); } }

        static Color InkFor(string line) {
            string low = line.ToLowerInvariant();
            if (line.StartsWith("   at ") || line.StartsWith("   в ") || line.TrimStart().StartsWith("at System.") || line.TrimStart().StartsWith("в System.")) return Theme.Faint;
            if (low.Contains("error") || low.Contains("exception") || low.Contains("failed") || low.Contains("не удалось") || low.Contains("ошибка") || low.Contains("прерван") || low.Contains("нет связи") || low.Contains("не ответил")) return Theme.Warning;
            return Theme.Muted;
        }
        // Monospace: a row holds a fixed number of characters. Break at the last space when there is one,
        // continuation rows are indented by two characters.
        void Rewrap() {
            int width = Math.Max(1, ClientSize.Width - RailWidth - (int)(10 * DpiScale));
            if (width == wrapWidth) return;
            wrapWidth = width; rows.Clear();
            int charWidth = Math.Max(1, TextRenderer.MeasureText(new string('M', 40), font, Size.Empty, TextFormatFlags.NoPadding).Width / 40);
            int columns = Math.Max(8, width / charWidth);
            foreach (string line in lines) {
                Color ink = InkFor(line);
                string rest = line; bool continued = false;
                do {
                    int room = continued ? columns - 2 : columns;
                    string piece;
                    if (rest.Length <= room) { piece = rest; rest = ""; }
                    else {
                        int cut = rest.LastIndexOf(' ', room, Math.Min(room, room / 2 + 1));
                        if (cut <= 0) cut = room;
                        piece = rest.Substring(0, cut); rest = rest.Substring(cut).TrimStart(' ');
                    }
                    rows.Add(new Row { Text = (continued ? "  " : "") + piece, Ink = ink, Continued = continued });
                    continued = true;
                } while (rest.Length > 0);
            }
            first = Math.Min(first, MaxFirst);
        }
        protected override void OnResize(EventArgs args) {
            bool atEnd = first >= MaxFirst;
            Rewrap(); if (atEnd) first = MaxFirst; first = Math.Min(first, MaxFirst);
            base.OnResize(args);
        }
        void ScrollTo(int row) { int next = Math.Max(0, Math.Min(MaxFirst, row)); if (next != first) { first = next; Invalidate(); } }
        protected override void OnMouseWheel(MouseEventArgs args) { ScrollTo(first - Math.Sign(args.Delta) * 3); base.OnMouseWheel(args); }
        protected override bool IsInputKey(Keys keyData) {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs args) {
            switch (args.KeyCode) {
                case Keys.Up: ScrollTo(first - 1); break;
                case Keys.Down: ScrollTo(first + 1); break;
                case Keys.PageUp: ScrollTo(first - VisibleRows + 1); break;
                case Keys.PageDown: ScrollTo(first + VisibleRows - 1); break;
                case Keys.Home: ScrollTo(0); break;
                case Keys.End: ScrollTo(MaxFirst); break;
                default:
                    if (args.Control && (args.KeyCode == Keys.C || args.KeyCode == Keys.A)) { try { if (Text.Length > 0) Clipboard.SetText(Text); } catch (Exception) { } break; }
                    base.OnKeyDown(args); return;
            }
            args.Handled = true;
        }
        Rectangle Track { get { return new Rectangle(ClientSize.Width - RailWidth, 2, RailWidth, Math.Max(0, ClientSize.Height - 4)); } }
        Rectangle Thumb {
            get {
                Rectangle track = Track;
                if (rows.Count <= VisibleRows || track.Height <= 0) return Rectangle.Empty;
                int height = Math.Max((int)(24 * DpiScale), (int)(track.Height * (double)VisibleRows / rows.Count));
                int top = track.Top + (int)((track.Height - height) * (double)first / Math.Max(1, MaxFirst));
                int w = (int)(4 * DpiScale);
                return new Rectangle(track.Left + (track.Width - w) / 2, top, w, height);
            }
        }
        protected override void OnMouseDown(MouseEventArgs args) {
            Focus();
            Rectangle thumb = Thumb, track = Track;
            if (args.Button == MouseButtons.Left && thumb.Height > 0 && args.X >= track.Left - 2) {
                dragOffset = args.Y >= thumb.Top && args.Y < thumb.Bottom ? args.Y - thumb.Top : thumb.Height / 2;
                Capture = true; DragTo(args.Y);
            }
            base.OnMouseDown(args);
        }
        protected override void OnMouseMove(MouseEventArgs args) { if (dragOffset >= 0) DragTo(args.Y); base.OnMouseMove(args); }
        protected override void OnMouseUp(MouseEventArgs args) { dragOffset = -1; Capture = false; Invalidate(); base.OnMouseUp(args); }
        void DragTo(int y) {
            Rectangle thumb = Thumb, track = Track;
            int travel = track.Height - thumb.Height; if (travel <= 0) return;
            int position = Math.Max(0, Math.Min(travel, y - track.Top - dragOffset));
            ScrollTo((int)Math.Round(position * (double)MaxFirst / travel));
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(BackColor);
            Rewrap();
            int height = RowHeight, last = Math.Min(rows.Count, first + VisibleRows + 1), inset = (int)(3 * DpiScale);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            for (int i = first; i < last; i++)
                TextRenderer.DrawText(g, rows[i].Text, font, new Rectangle(inset, (i - first) * height, ClientSize.Width - RailWidth - inset, height), rows[i].Ink, BackColor, flags);
            Rectangle thumb = Thumb;
            if (thumb.Height > 0) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath shape = Shapes.Round(thumb, thumb.Width / 2f))
                using (Brush fill = new SolidBrush(dragOffset >= 0 ? Theme.Accent : Theme.LineStrong)) g.FillPath(fill, shape);
            }
            if (Focused && ShowFocusCues) using (Pen edge = new Pen(Theme.Line)) g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }
}
