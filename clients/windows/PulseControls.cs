using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    // The main-screen block that speaks for the connection state: a dot, a title and a quiet subtitle.
    internal sealed class StatusBlock : Control {
        string title = "", subtitle = "";
        Color ink = Theme.Muted, dotColor = Theme.Faint;
        bool halo;
        internal string Title { get { return title; } set { title = value ?? ""; Invalidate(); } }
        internal string Subtitle { get { return subtitle; } set { subtitle = value ?? ""; Invalidate(); } }
        internal Color TitleColor { get { return ink; } set { ink = value; Invalidate(); } }
        internal Color DotColor { get { return dotColor; } set { dotColor = value; Invalidate(); } }
        internal bool DotHalo { get { return halo; } set { halo = value; Invalidate(); } }
        // Page header of the two-pane window: the dot, the title and the line under it start at the left edge.
        internal bool AlignLeft { get; set; }
        internal StatusBlock() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; TabStop = false;
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font bold = Theme.Font(15f, true)) using (Font small = Theme.Font(Theme.SmallSize)) {
                Size titleSize = TextRenderer.MeasureText(g, title, bold, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int dot = (int)Theme.Px(g, 9), gap = (int)Theme.Px(g, 10);
                int total = dot + gap + titleSize.Width;
                int left = AlignLeft ? (int)Theme.Px(g, 7) : Math.Max(0, (Width - total) / 2), top = (int)Theme.Px(g, 3);
                float cy = top + bold.Height / 2f;
                if (halo) using (Brush ring = new SolidBrush(Theme.Soft(dotColor)))
                    g.FillEllipse(ring, left + dot / 2f - dot * 1.15f, cy - dot * 1.15f, dot * 2.3f, dot * 2.3f);
                using (Brush fill = new SolidBrush(dotColor)) g.FillEllipse(fill, left, cy - dot / 2f, dot, dot);
                TextRenderer.DrawText(g, title, bold, new Rectangle(left + dot + gap, top, Math.Max(0, Width - left - dot - gap), bold.Height), ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (subtitle.Length > 0) {
                    int subTop = top + bold.Height + (int)Theme.Px(g, 1);
                    TextRenderer.DrawText(g, subtitle, small, new Rectangle(AlignLeft ? left : 0, subTop, Width - (AlignLeft ? left : 0), small.Height), AlignLeft ? Theme.Muted : Theme.Faint,
                        (AlignLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
        }
    }

    // Two quiet columns under the clock: download and upload, each with its own label and rate.
    internal sealed class TransferStats : Control {
        string download = "", upload = "";
        internal string Download { get { return download; } set { download = value ?? ""; Invalidate(); } }
        internal string Upload { get { return upload; } set { upload = value ?? ""; Invalidate(); } }
        internal TransferStats() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Ground; TabStop = false;
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 4 || Height < 4) return;
            int mid = Width / 2;
            using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, mid, (int)Theme.Px(g, 4), mid, Height - (int)Theme.Px(g, 4));
            using (Font label = Theme.Font(Theme.CaptionSize, true)) using (Font value = Theme.Font(10.75f, true)) {
                TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                int pad = (int)Theme.Px(g, 8), gap = (int)Theme.Px(g, 8);
                int top = (int)Theme.Px(g, 1);
                TextRenderer.DrawText(g, "Загрузка", label, new Rectangle(pad, top, mid - pad - gap, label.Height), Theme.Faint, left);
                TextRenderer.DrawText(g, String.IsNullOrEmpty(download) ? "—" : download, value, new Rectangle(pad, top + label.Height + (int)Theme.Px(g, 1), mid - pad - gap, Height - top - label.Height - (int)Theme.Px(g, 2)), Theme.Ink, left);
                TextRenderer.DrawText(g, "Отправка", label, new Rectangle(mid + gap, top, Width - mid - gap - pad, label.Height), Theme.Faint, right);
                TextRenderer.DrawText(g, String.IsNullOrEmpty(upload) ? "—" : upload, value, new Rectangle(mid + gap, top + label.Height + (int)Theme.Px(g, 1), Width - mid - gap - pad, Height - top - label.Height - (int)Theme.Px(g, 2)), Theme.Ink, right);
            }
        }
    }

    // Last 60 seconds of download speed: the answer to "is the tunnel alive?" without opening graphs.
    internal sealed class SpeedGraph : ThemedControl {
        readonly double[] samples = new double[60];
        int count, head;
        double peak;
        internal int SampleCount { get { return count; } }
        internal double Peak { get { return peak; } }
        internal SpeedGraph() { TabStop = false; Cursor = Cursors.Default; }
        internal void Push(double bytesPerSecond) {
            if (bytesPerSecond < 0) bytesPerSecond = 0;
            samples[head] = bytesPerSecond; head = (head + 1) % samples.Length;
            if (count < samples.Length) count++;
            if (bytesPerSecond > peak) peak = bytesPerSecond;
            Invalidate();
        }
        internal void Reset() { count = 0; head = 0; peak = 0; Invalidate(); }
        double SampleAt(int index) { return samples[(head - count + index + samples.Length * 2) % samples.Length]; }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (count < 2) {
                using (Pen idle = new Pen(Theme.Line)) g.DrawLine(idle, 0, Height - 1, Width, Height - 1);
                return;
            }
            double max = Math.Max(peak * 1.18, 256 * 1024);
            float top = 2, bottom = Math.Max(top + 1, Height - 2);
            PointF[] points = new PointF[count];
            for (int i = 0; i < count; i++) {
                double value = SampleAt(i);
                float x = (float)(Width - 2) * i / (samples.Length - 1);
                float y = (float)(bottom - (bottom - top) * Math.Min(1, value / max));
                points[i] = new PointF(x, y);
            }
            GraphicsPath area = new GraphicsPath();
            area.AddLine(points[0].X, bottom, points[0].X, points[0].Y);
            area.AddLines(points);
            area.AddLine(points[count - 1].X, points[count - 1].Y, points[count - 1].X, bottom);
            area.CloseFigure();
            using (LinearGradientBrush fill = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                Color.FromArgb(70, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), Color.FromArgb(0, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), LinearGradientMode.Vertical))
                g.FillPath(fill, area);
            area.Dispose();
            using (Pen line = new Pen(Theme.Accent, Theme.Px(g, 1.4f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(line, points);
            using (Brush dot = new SolidBrush(Theme.Accent))
                g.FillEllipse(dot, points[count - 1].X - Theme.Px(g, 2), points[count - 1].Y - Theme.Px(g, 2), Theme.Px(g, 4), Theme.Px(g, 4));
        }
    }

    // Two locations: two equal segments across the whole card width. One click switches.
    internal sealed class LocationsStrip : ThemedControl {
        readonly List<Server> servers = new List<Server>();
        readonly Dictionary<string, string> pings = new Dictionary<string, string>();
        string selected;
        int hover = -1;
        internal event Action<string> Picked;
        internal LocationsStrip() { BackColor = Theme.Ground; }
        internal void SetServers(IEnumerable<Server> items, string selectedId, Dictionary<string, string> pingMap) {
            servers.Clear(); if (items != null) servers.AddRange(items.Where(s => s != null).Take(2));
            selected = selectedId; pings.Clear();
            if (pingMap != null) foreach (KeyValuePair<string, string> pair in pingMap) pings[pair.Key] = pair.Value;
            Invalidate();
        }
        Rectangle Segment(int index) {
            int gap = (int)(10 * DeviceDpi / 96f);
            int width = (Width - gap) / Math.Max(1, servers.Count);
            return new Rectangle(index * (width + gap), 0, width, Height);
        }
        int IndexAt(Point point) {
            for (int i = 0; i < servers.Count; i++) if (Segment(i).Contains(point)) return i;
            return -1;
        }
        internal int SegmentWidthForTests(int index) { return index >= 0 && index < servers.Count ? Segment(index).Width : 0; }
        protected override void OnMouseMove(MouseEventArgs args) {
            int next = IndexAt(args.Location);
            if (next != hover) { hover = next; Invalidate(); }
            base.OnMouseMove(args);
        }
        protected override void OnMouseLeave(EventArgs args) { hover = -1; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnMouseDown(MouseEventArgs args) {
            int index = IndexAt(args.Location);
            if (index >= 0 && index < servers.Count) { Action<string> handler = Picked; if (handler != null) handler(servers[index].Id); }
            base.OnMouseDown(args);
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (Width < 4 || Height < 4) return;
            float radius = Theme.Px(g, 12), pad = Theme.Px(g, 12);
            using (Font name = Theme.Font(11.5f, true)) using (Font small = Theme.Font(8.5f)) {
                for (int i = 0; i < servers.Count; i++) {
                    Rectangle box = Segment(i);
                    bool on = servers[i].Id == selected, over = i == hover;
                    using (GraphicsPath path = Shapes.Round(new RectangleF(box.X + .5f, box.Y + .5f, box.Width - 1, box.Height - 1), radius)) {
                        using (Brush fill = new SolidBrush(on ? Theme.Blend(Theme.Accent, Theme.Card, 20) : over ? Theme.Blend(Theme.Raised, Theme.Card, 55) : Theme.Card))
                            g.FillPath(fill, path);
                        using (Pen edge = new Pen(on ? Theme.Accent : Theme.Line, Theme.Px(g, 1f))) g.DrawPath(edge, path);
                    }
                    int badgeW = (int)Theme.Px(g, 24), badgeH = (int)Theme.Px(g, 18);
                    Flags.DrawBadge(g, new Rectangle(box.X + (int)pad, box.Y + (box.Height - badgeH) / 2, badgeW, badgeH), servers[i].Country, servers[i].Name);
                    string ping; pings.TryGetValue(servers[i].Id, out ping);
                    int check = on ? (int)Theme.Px(g, 13) : 0, checkGap = on ? (int)Theme.Px(g, 5) : 0;
                    int textLeft = box.X + (int)pad + badgeW + (int)Theme.Px(g, 8);
                    int textRight = box.Right - (int)pad - check - checkGap;
                    TextRenderer.DrawText(g, servers[i].Name, name, new Rectangle(textLeft, box.Y + (int)Theme.Px(g, 5), Math.Max(0, textRight - textLeft), (int)Theme.Px(g, 20)),
                        on ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    if (!String.IsNullOrEmpty(ping)) TextRenderer.DrawText(g, ping, small, new Rectangle(textLeft, box.Y + (int)Theme.Px(g, 26), Math.Max(0, textRight - textLeft), (int)Theme.Px(g, 16)),
                        on ? Theme.Accent : Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    if (on) {
                        float cx = box.Right - pad - check / 2f, cy = box.Y + box.Height / 2f;
                        using (Pen pen = new Pen(Theme.Accent, Theme.Px(g, 1.9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLines(pen, new[] { new PointF(cx - check * .45f, cy + Theme.Px(g, 0f)), new PointF(cx - check * .1f, cy + check * .35f), new PointF(cx + check * .5f, cy - check * .38f) });
                    }
                }
            }
        }
    }

    // Three or more locations: a scrolled list with a header and an add row.
    internal sealed class LocationsList : ThemedControl {
        readonly List<Server> servers = new List<Server>();
        readonly Dictionary<string, string> pings = new Dictionary<string, string>();
        string selected;
        int offset, hover = -1;
        internal event Action<string> Picked;
        internal event Action AddRequested, EditRequested;
        internal LocationsList() { BackColor = Theme.Ground; }
        internal void SetServers(IEnumerable<Server> items, string selectedId, Dictionary<string, string> pingMap) {
            servers.Clear(); if (items != null) servers.AddRange(items.Where(s => s != null));
            selected = selectedId; pings.Clear();
            if (pingMap != null) foreach (KeyValuePair<string, string> pair in pingMap) pings[pair.Key] = pair.Value;
            offset = Math.Max(0, Math.Min(offset, MaxOffset));
            Invalidate();
        }
        int Header { get { return (int)(26 * DeviceDpi / 96f); } }
        int RowHeight { get { return (int)(44 * DeviceDpi / 96f); } }
        int AddHeight { get { return (int)(30 * DeviceDpi / 96f); } }
        int VisibleRows { get { return Math.Max(1, (Height - Header - AddHeight) / Math.Max(1, RowHeight)); } }
        int MaxOffset { get { return Math.Max(0, servers.Count - VisibleRows); } }
        Rectangle RowBox(int visibleIndex) { return new Rectangle(0, Header + visibleIndex * RowHeight, Width, RowHeight); }
        Rectangle AddBox { get { return new Rectangle(0, Height - AddHeight, Width, AddHeight); } }
        Rectangle EditBox { get { int w = (int)(132 * DeviceDpi / 96f); return new Rectangle(Width - w, 0, w, Header); } }
        protected override void OnMouseWheel(MouseEventArgs args) {
            int next = Math.Max(0, Math.Min(MaxOffset, offset - Math.Sign(args.Delta) * 2));
            if (next != offset) { offset = next; Invalidate(); }
            base.OnMouseWheel(args);
        }
        protected override void OnMouseMove(MouseEventArgs args) {
            int next = -1;
            for (int i = 0; i < VisibleRows && offset + i < servers.Count; i++) if (RowBox(i).Contains(args.Location)) next = offset + i;
            if (next != hover) { hover = next; Invalidate(); }
            base.OnMouseMove(args);
        }
        protected override void OnMouseLeave(EventArgs args) { hover = -1; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnMouseDown(MouseEventArgs args) {
            Focus();
            if (args.Y < Header) {
                if (EditBox.Contains(args.Location)) { Action handler = EditRequested; if (handler != null) handler(); }
            } else if (AddBox.Contains(args.Location)) {
                Action handler = AddRequested; if (handler != null) handler();
            } else {
                for (int i = 0; i < VisibleRows && offset + i < servers.Count; i++) if (RowBox(i).Contains(args.Location)) {
                    Action<string> handler = Picked; if (handler != null) handler(servers[offset + i].Id);
                    break;
                }
            }
            base.OnMouseDown(args);
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (Width < 4 || Height < 4) return;
            float scale = g.DpiX / 96f;
            int radius = (int)Theme.Px(g, 12);
            using (GraphicsPath card = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), radius)) {
                using (Brush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, card);
                using (Pen edge = new Pen(Theme.Line)) g.DrawPath(edge, card);
            }
            using (Font head = Theme.Font(9f, true)) using (Font act = Theme.Font(9f))
            using (Font name = Theme.Font(Theme.ServerSize, true)) using (Font small = Theme.Font(Theme.SmallSize)) {
                TextFormatFlags headFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                int left = (int)Theme.Px(g, 14);
                string header = "ЛОКАЦИИ";
                TextRenderer.DrawText(g, header, head, new Rectangle(left, 0, Width / 2, Header), Theme.Faint, headFlags);
                int used = TextRenderer.MeasureText(g, header, head, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, servers.Count.ToString(), act, new Rectangle(left + used + (int)Theme.Px(g, 5), 0, (int)Theme.Px(g, 30), Header), Theme.Accent, headFlags);
                string edit = MaxOffset > 0 ? (offset + VisibleRows) + " из " + servers.Count + " · изменить" : "изменить";
                TextRenderer.DrawText(g, edit, act, EditBox, Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 0, Header, Width, Header);
                Region clip = g.Clip; g.SetClip(new Rectangle(0, Header, Width, Math.Max(0, Height - Header - AddHeight)));
                for (int i = 0; i < VisibleRows && offset + i < servers.Count; i++) {
                    Server server = servers[offset + i];
                    Rectangle row = RowBox(i);
                    bool on = server.Id == selected, over = offset + i == hover;
                    if (on || over) using (Brush fill = new SolidBrush(on ? Theme.Selection : Theme.Blend(Theme.LineStrong, Theme.Card, 35))) g.FillRectangle(fill, row);
                    int badgeW = (int)Theme.Px(g, 24), badgeH = (int)Theme.Px(g, 18);
                    Flags.DrawBadge(g, new Rectangle(left, row.Y + (row.Height - badgeH) / 2, badgeW, badgeH), server.Country, server.Name);
                    string ping; pings.TryGetValue(server.Id, out ping);
                    int pingWidth = String.IsNullOrEmpty(ping) ? 0 : TextRenderer.MeasureText(g, ping, small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int check = (int)Theme.Px(g, 16);
                    int textLeft = left + badgeW + (int)Theme.Px(g, 11);
                    int textRight = Width - left - pingWidth - (int)Theme.Px(g, 12) - (on ? check + (int)Theme.Px(g, 8) : 0);
                    string subtitle = String.IsNullOrEmpty(server.Country) ? server.Hostname : CountryPicker.NameOf(server.Country);
                    TextRenderer.DrawText(g, server.Name, name, new Rectangle(textLeft, row.Y + (int)Theme.Px(g, 5), Math.Max(0, textRight - textLeft), (int)Theme.Px(g, 19)),
                        on ? Theme.Accent : Theme.Ink, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, subtitle, small, new Rectangle(textLeft, row.Y + (int)Theme.Px(g, 23), Math.Max(0, textRight - textLeft), (int)Theme.Px(g, 16)),
                        Theme.Faint, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    if (pingWidth > 0) TextRenderer.DrawText(g, ping, small, new Rectangle(Width - left - pingWidth, row.Y, pingWidth, row.Height),
                        on ? Theme.Accent : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (on) {
                        float cy = row.Y + row.Height / 2f, cx = Width - left - check / 2f;
                        using (Pen pen = new Pen(Theme.Accent, Theme.Px(g, 1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLines(pen, new[] { new PointF(cx - check * .45f, cy), new PointF(cx - check * .12f, cy + check * .3f), new PointF(cx + check * .5f, cy - check * .35f) });
                    }
                    using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, left, row.Bottom - 1, Width - left, row.Bottom - 1);
                }
                g.Clip = clip;
                if (MaxOffset > 0) {
                    int trackTop = Header + (int)Theme.Px(g, 5), trackHeight = Math.Max(1, Height - Header - AddHeight - (int)Theme.Px(g, 10));
                    int thumb = Math.Max((int)Theme.Px(g, 24), trackHeight * VisibleRows / Math.Max(1, servers.Count));
                    int thumbTop = trackTop + (trackHeight - thumb) * offset / Math.Max(1, MaxOffset);
                    using (Brush track = new SolidBrush(Color.FromArgb(32, 255, 255, 255))) g.FillRectangle(track, Width - (int)Theme.Px(g, 6), trackTop, 3, trackHeight);
                    using (Brush bar = new SolidBrush(Theme.LineStrong)) g.FillRectangle(bar, Width - (int)Theme.Px(g, 6), thumbTop, 3, thumb);
                }
                using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 0, AddBox.Top, Width, AddBox.Top);
                using (Pen plus = new Pen(Theme.Muted, Theme.Px(g, 1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    float cx = left + Theme.Px(g, 6), cy = AddBox.Top + AddBox.Height / 2f;
                    g.DrawLine(plus, cx - Theme.Px(g, 6), cy, cx + Theme.Px(g, 6), cy);
                    g.DrawLine(plus, cx, cy - Theme.Px(g, 6), cx, cy + Theme.Px(g, 6));
                }
                TextRenderer.DrawText(g, "Добавить локацию", small, new Rectangle(left + (int)Theme.Px(g, 22), AddBox.Top, Width - left - (int)Theme.Px(g, 22), AddBox.Height),
                    Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }

    // Compact connect bar for the list mode: ring, action, clock and speed on one card.
    internal sealed class ConnectBar : ThemedControl {
        string caption = "Отключиться", clockText = "", speedText = "";
        ConnectionView view = ConnectionView.Connected;
        internal string Caption { get { return caption; } set { caption = value ?? ""; Invalidate(); } }
        internal string ClockText { get { return clockText; } set { clockText = value ?? ""; Invalidate(); } }
        internal string SpeedText { get { return speedText; } set { speedText = value ?? ""; Invalidate(); } }
        internal ConnectionView View { get { return view; } set { view = value; Invalidate(); } }
        internal ConnectBar() { BackColor = Theme.Ground; }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (Width < 4 || Height < 4) return;
            Color ink = Enabled ? PowerRing.InkFor(view) : Theme.Faint;
            using (GraphicsPath card = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Theme.Px(g, 12))) {
                using (Brush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, card);
                using (Pen edge = new Pen(Theme.Line)) g.DrawPath(edge, card);
            }
            int pad = (int)Theme.Px(g, 14), size = (int)Theme.Px(g, 64);
            RectangleF circle = new RectangleF(pad, (Height - size) / 2f, size, size);
            using (Pen halo = new Pen(Theme.Soft(ink), Theme.Px(g, 7))) if (Enabled) g.DrawEllipse(halo, RectangleF.Inflate(circle, Theme.Px(g, 1), Theme.Px(g, 1)));
            using (Brush fill = new SolidBrush(Theme.Card)) g.FillEllipse(fill, circle);
            using (Pen ring = new Pen(ink, Theme.Px(g, 1.3f))) g.DrawEllipse(ring, circle);
            float cx = circle.X + circle.Width / 2f, cy = circle.Y + circle.Height / 2f, r = Theme.Px(g, 13);
            using (Pen power = new Pen(ink, Theme.Px(g, 2.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawArc(power, cx - r, cy - r, 2 * r, 2 * r, -50, 280);
                g.DrawLine(power, cx, cy - r - Theme.Px(g, 3), cx, cy - r + Theme.Px(g, 1));
            }
            int textLeft = (int)(circle.Right + Theme.Px(g, 14));
            using (Font title = Theme.Font(Theme.ServerSize, true)) using (Font mono = Theme.Mono(15f)) using (Font small = Theme.Font(Theme.CaptionSize))
            using (Font tiny = Theme.Font(Theme.CaptionSize - 0.25f)) {
                TextRenderer.DrawText(g, caption, title, new Rectangle(textLeft, (int)Theme.Px(g, 12), Width - textLeft - pad, (int)Theme.Px(g, 20)), ink,
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, clockText, mono, new Rectangle(textLeft, (int)Theme.Px(g, 34), Width - textLeft - pad, (int)Theme.Px(g, 24)), Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, speedText, small, new Rectangle(textLeft, (int)Theme.Px(g, 62), Width - textLeft - pad, (int)Theme.Px(g, 16)), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    // «Что попробовать» under an error: three short checks, not a wall of text.
    internal sealed class TryCard : Control {
        static readonly string[] Rows = { "Проверить срок и квоту подписки", "Сменить транспорт на HTTP/3", "Проверить доступ в сеть" };
        internal TryCard() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ground;
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics; g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 4 || Height < 4) return;
            using (GraphicsPath card = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Theme.Px(g, 12))) {
                using (Brush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, card);
                using (Pen edge = new Pen(Theme.Line)) g.DrawPath(edge, card);
            }
            int pad = (int)Theme.Px(g, 14), top = (int)Theme.Px(g, 9);
            using (Font label = Theme.Font(Theme.CaptionSize, true)) using (Font body = Theme.Font(Theme.BodySize)) {
                TextRenderer.DrawText(g, "ЧТО ПОПРОБОВАТЬ", label, new Rectangle(pad, top, Width - 2 * pad, (int)Theme.Px(g, 14)), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                for (int i = 0; i < Rows.Length; i++) {
                    int y = top + (int)Theme.Px(g, 22) + i * (int)Theme.Px(g, 22);
                    using (Pen check = new Pen(Theme.Accent, Theme.Px(g, 1.7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                        float cx = pad + Theme.Px(g, 6), cy = y + Theme.Px(g, 7);
                        g.DrawLines(check, new[] { new PointF(cx - Theme.Px(g, 5), cy), new PointF(cx - Theme.Px(g, 1), cy + Theme.Px(g, 4)), new PointF(cx + Theme.Px(g, 6), cy - Theme.Px(g, 5)) });
                    }
                    TextRenderer.DrawText(g, Rows[i], body, new Rectangle(pad + (int)Theme.Px(g, 18), y, Width - pad * 2 - (int)Theme.Px(g, 18), (int)Theme.Px(g, 18)),
                        Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
        }
    }

    // Last engine lines with a copy action: the support question starts here.
    internal sealed class LogPreview : ThemedControl {
        string[] lines = new string[0];
        internal event Action CopyRequested;
        internal string[] Lines { get { return lines; } set { lines = value ?? new string[0]; Invalidate(); } }
        internal LogPreview() { BackColor = Theme.Ground; AccessibleName = "Скопировать журнал"; }
        protected override void OnMouseDown(MouseEventArgs args) { Action handler = CopyRequested; if (handler != null) handler(); base.OnMouseDown(args); }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (Width < 4 || Height < 4) return;
            using (GraphicsPath card = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Theme.Px(g, 12))) {
                using (Brush fill = new SolidBrush(Color.FromArgb(0x0C, 0x12, 0x20))) g.FillPath(fill, card);
                using (Pen edge = new Pen(Theme.Line)) g.DrawPath(edge, card);
            }
            int pad = (int)Theme.Px(g, 12);
            using (Font mono = Theme.Mono(Theme.CaptionSize)) using (Font small = Theme.Font(Theme.CaptionSize)) {
                for (int i = 0; i < lines.Length && i < 3; i++) {
                    string text = lines[i];
                    Color ink = i == lines.Length - 1 ? Theme.Warning : Theme.Muted;
                    TextRenderer.DrawText(g, text, mono, new Rectangle(pad, pad + i * (int)Theme.Px(g, 17), Width - 2 * pad, (int)Theme.Px(g, 15)), ink,
                        TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                TextRenderer.DrawText(g, "Скопировать журнал", small, new Rectangle(pad, Height - pad - (int)Theme.Px(g, 16), Width - 2 * pad, (int)Theme.Px(g, 16)), Theme.Accent,
                    TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }

    // First launch: one obvious action, a drop target and no dead ring.
    internal sealed class DropZone : ThemedControl {
        internal event Action PickRequested;
        internal DropZone() { BackColor = Theme.Ground; AccessibleName = "Вставить ссылку подписки"; }
        protected override void OnMouseDown(MouseEventArgs args) { Action handler = PickRequested; if (handler != null) handler(); base.OnMouseDown(args); }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (Width < 4 || Height < 4) return;
            RectangleF area = new RectangleF(1f, 1f, Width - 2, Height - 2);
            using (GraphicsPath path = Shapes.Round(area, Theme.Px(g, 14))) {
                using (Brush fill = new SolidBrush(Color.FromArgb(140, Theme.Card))) g.FillPath(fill, path);
                using (Pen edge = new Pen(Hover ? Theme.LineStrong : Theme.Line, Theme.Px(g, 1.2f)) { DashStyle = DashStyle.Dash, DashPattern = new[] { 4f, 4f } }) g.DrawPath(edge, path);
            }
            float scale = g.DpiX / 96f;
            int icon = (int)Theme.Px(g, 38), top = (int)(Height * .26f);
            Rectangle iconBox = new Rectangle((Width - icon) / 2, top, icon, icon);
            using (GraphicsPath badge = Shapes.Round(iconBox, Theme.Px(g, 10))) using (Brush fill = new SolidBrush(Color.FromArgb(20, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B))) g.FillPath(fill, badge);
            using (Pen pen = new Pen(Theme.Accent, Theme.Px(g, 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                float cx = Width / 2f, cy = top + icon / 2f + Theme.Px(g, 1);
                g.DrawLine(pen, cx, cy - Theme.Px(g, 5), cx, cy + Theme.Px(g, 7));
                g.DrawLines(pen, new[] { new PointF(cx - Theme.Px(g, 5), cy + Theme.Px(g, 2)), new PointF(cx, cy - Theme.Px(g, 5)), new PointF(cx + Theme.Px(g, 5), cy + Theme.Px(g, 2)) });
                g.DrawLines(pen, new[] { new PointF(cx - Theme.Px(g, 7), cy + Theme.Px(g, 8)), new PointF(cx - Theme.Px(g, 7), cy + Theme.Px(g, 11)), new PointF(cx + Theme.Px(g, 7), cy + Theme.Px(g, 11)), new PointF(cx + Theme.Px(g, 7), cy + Theme.Px(g, 8)) });
            }
            using (Font title = Theme.Font(Theme.ServerSize, true)) using (Font small = Theme.Font(Theme.SmallSize)) {
                int textTop = iconBox.Bottom + (int)Theme.Px(g, 9);
                TextRenderer.DrawText(g, "Перетащите файл .toml", title, new Rectangle(0, textTop, Width, (int)Theme.Px(g, 20)), Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "или вставьте ссылку подписки или tt:// из панели", small, new Rectangle((int)Theme.Px(g, 20), textTop + (int)Theme.Px(g, 22), Width - (int)Theme.Px(g, 40), (int)Theme.Px(g, 18)),
                    Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    // Three steps: enough onboarding to explain the ссылку, not a manual.
    internal sealed class StepsRow : Control {
        static readonly string[] Titles = { "Скопируйте ссылку", "Вставьте в Netlane", "Нажмите «Подключиться»" };
        internal StepsRow() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ground;
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics; g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 4 || Height < 4) return;
            int gap = (int)Theme.Px(g, 8), width = (Width - gap * 2) / 3;
            using (Font num = Theme.Mono(Theme.CaptionSize)) using (Font text = Theme.Font(Theme.CaptionSize)) {
                for (int i = 0; i < 3; i++) {
                    Rectangle box = new Rectangle(i * (width + gap), 0, width, Height);
                    using (GraphicsPath path = Shapes.Round(new RectangleF(box.X + .5f, box.Y + .5f, box.Width - 1, box.Height - 1), Theme.Px(g, 10))) {
                        using (Brush fill = new SolidBrush(Color.FromArgb(140, Theme.Card))) g.FillPath(fill, path);
                        using (Pen edge = new Pen(Theme.Line)) g.DrawPath(edge, path);
                    }
                    TextRenderer.DrawText(g, "0" + (i + 1), num, new Rectangle(box.X + (int)Theme.Px(g, 9), box.Y + (int)Theme.Px(g, 8), box.Width - (int)Theme.Px(g, 18), (int)Theme.Px(g, 14)), Theme.Accent,
                        TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Titles[i], text, new Rectangle(box.X + (int)Theme.Px(g, 9), box.Y + (int)Theme.Px(g, 26), box.Width - (int)Theme.Px(g, 18), box.Height - (int)Theme.Px(g, 32)),
                        Theme.Muted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                }
            }
        }
    }
}
