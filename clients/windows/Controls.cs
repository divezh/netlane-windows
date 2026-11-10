using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Netlane {
    // Stop: the filled red «Отключить VPN» of the home page; Danger: an outlined destructive action.
    internal enum ButtonKind { Secondary, Primary, Danger, Stop }
    internal enum IconGlyph { Gear, Back, More, Refresh }

    // Base for the owner-drawn controls below: double buffered, focusable, Space/Enter click.
    internal abstract class ThemedControl : Control {
        protected bool Hover;
        protected ThemedControl() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = true; BackColor = Color.Transparent; ForeColor = Theme.Ink; Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs args) { Hover = true; Invalidate(); base.OnMouseEnter(args); }
        protected override void OnMouseLeave(EventArgs args) { Hover = false; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnGotFocus(EventArgs args) { Invalidate(); base.OnGotFocus(args); }
        protected override void OnLostFocus(EventArgs args) { Invalidate(); base.OnLostFocus(args); }
        protected override void OnEnabledChanged(EventArgs args) { Invalidate(); base.OnEnabledChanged(args); }
        // Enter clicks on press, Space on release like a Button, so holding Space does not repeat clicks.
        protected override void OnKeyDown(KeyEventArgs args) {
            if (Enabled && args.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); args.Handled = true; }
            base.OnKeyDown(args);
        }
        protected override void OnKeyUp(KeyEventArgs args) {
            if (Enabled && args.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); args.Handled = true; }
            base.OnKeyUp(args);
        }
        protected Graphics Prepare(PaintEventArgs args) {
            Graphics graphics = args.Graphics;
            graphics.Clear(Parent == null ? Theme.Ground : Parent.BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            return graphics;
        }
        protected bool ShowFocus { get { return Focused && ShowFocusCues; } }
    }

    // Connect / disconnect ring: circle, 2 px ring and halo coloured by Theme.ForState(View).
    internal sealed class PowerRing : ThemedControl {
        ConnectionView view = ConnectionView.Idle;
        string caption = "Подключиться";
        // Enabled is set by the form (MainForm.ControlsEnabled), not by the view.
        readonly Timer animation = new Timer { Interval = 32 };
        int sweep;
        bool Transitioning { get { return view == ConnectionView.Connecting || view == ConnectionView.Disconnecting; } }
        internal PowerRing() { animation.Tick += delegate { sweep = (sweep + 12) % 360; Invalidate(); }; }
        internal ConnectionView View {
            get { return view; }
            set {
                view = value;
                animation.Enabled = Transitioning && SystemInformation.IsMenuAnimationEnabled;
                if (!animation.Enabled) sweep = 0;
                Invalidate();
            }
        }
        protected override void Dispose(bool disposing) {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }
        internal string Caption { get { return caption; } set { caption = value ?? ""; Invalidate(); } }
        RectangleF CircleBounds(float halo) {
            float diameter = Math.Max(0, Math.Min(Width, Height) - 2 * halo - 2);
            return new RectangleF((Width - diameter) / 2, (Height - diameter) / 2, diameter, diameter);
        }

        protected override void WndProc(ref Message message) {
            const int WmNcHitTest = 0x0084, HtTransparent = -1;
            if (message.Msg == WmNcHitTest) {
                int packed = unchecked((int)message.LParam.ToInt64());
                Point cursor = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
                using (Graphics graphics = CreateGraphics()) {
                    RectangleF circle = CircleBounds(Theme.Px(graphics, 10));
                    float radius = circle.Width / 2;
                    float x = cursor.X - circle.X - radius, y = cursor.Y - circle.Y - radius;
                    if (radius == 0 || x * x + y * y > radius * radius) {
                        message.Result = new IntPtr(HtTransparent);
                        return;
                    }
                }
            }
            base.WndProc(ref message);
        }

        // One ink for the ring, its icon and its caption: the colour of the state. Accent invites
        // an action, Warning means reconnecting, and red is kept for stopping, the kill switch and
        // failures — never for the state that means "the tunnel is up".
        internal static Color InkFor(ConnectionView view) {
            switch (view) {
                case ConnectionView.Reconnecting: return Theme.Warning;
                case ConnectionView.Disconnecting:
                case ConnectionView.KillSwitch:
                case ConnectionView.Error: return Theme.Danger;
                default: return Theme.Accent;
            }
        }

        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            StateStyle style = Theme.ForState(view);
            float halo = Theme.Px(g, 10);
            RectangleF circle = CircleBounds(halo);
            if (circle.IsEmpty) return;
            if (style.RingHalo) {
                using (Pen glow = new Pen(Theme.Soft(style.Ring), halo)) g.DrawEllipse(glow, RectangleF.Inflate(circle, halo / 2, halo / 2));
            }
            using (Brush fill = new SolidBrush(Hover && Enabled ? Theme.Raised : Theme.Card)) g.FillEllipse(fill, circle);
            using (Pen ring = new Pen(style.Ring, Theme.Px(g, 1.25f))) g.DrawEllipse(ring, circle);
            Color icon = Enabled ? InkFor(view) : Theme.Faint;
            if (Transitioning)
                using (Pen arc = new Pen(icon, Theme.Px(g, 2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, circle, sweep - 90, 100);
            float cx = Width / 2f, cy = Height / 2f - (caption.Length > 0 ? Theme.Px(g, 16) : 0), r = Theme.Px(g, caption.Length > 0 ? 24 : 26);
            using (Pen pen = new Pen(icon, Theme.Px(g, 2.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -50, 280);
                g.DrawLine(pen, cx, cy - r - Theme.Px(g, 4), cx, cy - Theme.Px(g, 2));
            }
            Color text = !Enabled ? Theme.Faint : view == ConnectionView.Idle || view == ConnectionView.NoProfile ? Theme.Muted : InkFor(view);
            // The caption may be wider than the circle: «Отключиться» was ellipsized once the ring
            // shrank in a short window.
            Rectangle label = new Rectangle(0, (int)(cy + r + Theme.Px(g, 12)), Width, (int)Theme.Px(g, 32));
            using (Font big = Theme.Font(caption == "Подключиться" ? 12f : 14f)) TextRenderer.DrawText(g, caption, big, label, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (ShowFocus) using (Pen focus = new Pen(Theme.Accent, 1)) g.DrawEllipse(focus, RectangleF.Inflate(circle, Theme.Px(g, 4), Theme.Px(g, 4)));
        }
    }

    // Square icon button: the settings gear in the header, "back" on the settings page.
    internal sealed class IconButton : ThemedControl {
        IconGlyph glyph;
        internal IconGlyph Glyph { get { return glyph; } set { glyph = value; Invalidate(); } }
        internal IconButton() { Size = new Size(34, 34); }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            using (GraphicsPath box = Shapes.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.Px(g, 8))) {
                using (Brush fill = new SolidBrush(Hover && Enabled ? Theme.Raised : Theme.Card)) g.FillPath(fill, box);
                using (Pen edge = new Pen(ShowFocus ? Theme.Accent : Theme.Line)) g.DrawPath(edge, box);
            }
            Color ink = Enabled ? Theme.Muted : Theme.Faint;
            float cx = Width / 2f, cy = Height / 2f, s = Math.Min(Width, Height) / 34f;
            using (Pen pen = new Pen(ink, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                if (glyph == IconGlyph.More) {
                    using (Brush dot = new SolidBrush(ink)) for (int i = -1; i <= 1; i++) g.FillEllipse(dot, cx + i * 5.5f * s - 1.6f * s, cy - 1.6f * s, 3.2f * s, 3.2f * s);
                    return;
                }
                if (glyph == IconGlyph.Back) {
                    g.DrawLines(pen, new[] { new PointF(cx + 2.5f * s, cy - 5.5f * s), new PointF(cx - 3 * s, cy), new PointF(cx + 2.5f * s, cy + 5.5f * s) });
                    return;
                }
                if (glyph == IconGlyph.Refresh) {
                    float radius = 6.2f * s;
                    g.DrawArc(pen, cx - radius, cy - radius, 2 * radius, 2 * radius, 30, 285);
                    g.DrawLines(pen, new[] {
                        new PointF(cx + radius - 2.5f * s, cy - 4.5f * s),
                        new PointF(cx + radius, cy - 4.5f * s),
                        new PointF(cx + radius, cy - 7.5f * s)
                    });
                    return;
                }
                // Gear: a ring, a hub and eight broad, square-ended teeth.
                float inner = 5f * s, outer = 7.8f * s;
                g.DrawEllipse(pen, cx - inner, cy - inner, 2 * inner, 2 * inner);
                g.DrawEllipse(pen, cx - 2f * s, cy - 2f * s, 4f * s, 4f * s);
                using (Pen tooth = new Pen(ink, 2.8f * s) { StartCap = LineCap.Flat, EndCap = LineCap.Flat }) {
                    for (int i = 0; i < 8; i++) {
                        double a = i * Math.PI / 4 + Math.PI / 8;
                        g.DrawLine(tooth, cx + (float)Math.Cos(a) * (inner + 0.6f * s), cy + (float)Math.Sin(a) * (inner + 0.6f * s), cx + (float)Math.Cos(a) * outer, cy + (float)Math.Sin(a) * outer);
                    }
                }
            }
        }
    }

    // Settings switch: title and explanation on the left, 40×22 track on the right.
    internal sealed class ToggleSwitch : CheckBox {
        string detail = "";
        internal string Detail { get { return detail; } set { detail = value ?? ""; Invalidate(); } }
        // Card = true: the switch draws its own rounded card and edge (settings rows), so no square fill shows.
        internal bool Card { get; set; }
        internal ToggleSwitch() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            AutoSize = false; ForeColor = Theme.Ink; Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Card) using (GraphicsPath card = Shapes.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.Px(g, Theme.RadiusCard))) using (Brush fill = new SolidBrush(Theme.Card)) using (Pen edge = new Pen(Theme.Line)) { g.FillPath(fill, card); g.DrawPath(edge, card); }
            float w = Theme.Px(g, 40), h = Theme.Px(g, 22), pad = Theme.Px(g, 14);
            RectangleF track = new RectangleF(Width - w - pad, pad, w, h);
            bool on = Checked && Enabled;
            using (GraphicsPath path = Shapes.Round(track, h / 2)) using (Brush fill = new SolidBrush(on ? Theme.Accent : Theme.LineStrong)) g.FillPath(fill, path);
            float knob = h - Theme.Px(g, 6), kx = Checked ? track.Right - knob - Theme.Px(g, 3) : track.X + Theme.Px(g, 3);
            using (Brush fill = new SolidBrush(on ? Theme.AccentInk : Enabled ? Theme.Ink : Theme.Faint)) g.FillEllipse(fill, kx, track.Y + Theme.Px(g, 3), knob, knob);
            int textWidth = (int)(Width - w - 3 * pad);
            bool compact = Height <= 60;
            int titleY = compact ? (int)Theme.Px(g, 7) : (int)Theme.Px(g, 11);
            int detailY = compact ? (int)Theme.Px(g, 26) : (int)Theme.Px(g, 34);
            using (Font title = Theme.Font(Theme.BodySize)) TextRenderer.DrawText(g, Text, title, new Rectangle((int)pad, titleY, textWidth, (int)Theme.Px(g, 20)), Enabled ? Theme.Ink : Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Font small = Theme.Font(Theme.SmallSize)) TextRenderer.DrawText(g, detail, small, new Rectangle((int)pad, detailY, textWidth, Math.Max(0, Height - detailY - (int)Theme.Px(g, 4))), Enabled ? Theme.Muted : Theme.Faint, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            if (Focused && ShowFocusCues) using (GraphicsPath focus = Shapes.Round(RectangleF.Inflate(track, Theme.Px(g, 3), Theme.Px(g, 3)), h / 2 + Theme.Px(g, 3))) using (Pen pen = new Pen(Theme.Accent)) g.DrawPath(pen, focus);
        }
    }

    // Two or more equal segments, one selected (transport choice).
    internal sealed class SegmentedControl : ThemedControl {
        string[] items = new string[0];
        int selected;
        internal event EventHandler SelectedIndexChanged;
        internal string[] Items { get { return items; } set { items = value ?? new string[0]; selected = Math.Min(selected, Math.Max(0, items.Length - 1)); Invalidate(); } }
        internal int SelectedIndex {
            get { return selected; }
            set {
                if (value < 0 || value >= items.Length) throw new ArgumentOutOfRangeException("value");
                if (value == selected) return;
                selected = value; Invalidate();
                EventHandler handler = SelectedIndexChanged; if (handler != null) handler(this, EventArgs.Empty);
            }
        }
        protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }
        protected override void OnKeyDown(KeyEventArgs args) {
            if (Enabled && items.Length > 0 && args.KeyCode == Keys.Left && selected > 0) { SelectedIndex = selected - 1; args.Handled = true; }
            else if (Enabled && items.Length > 0 && args.KeyCode == Keys.Right && selected < items.Length - 1) { SelectedIndex = selected + 1; args.Handled = true; }
            base.OnKeyDown(args);
        }
        protected override void OnMouseDown(MouseEventArgs args) {
            if (Enabled && items.Length > 0) { Focus(); SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, args.X * items.Length / Math.Max(1, Width))); }
            base.OnMouseDown(args);
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            float radius = Theme.Px(g, Theme.RadiusControl), inset = Theme.Px(g, 3);
            using (GraphicsPath frame = Shapes.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius)) {
                using (Brush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, frame);
                using (Pen edge = new Pen(ShowFocus ? Theme.Accent : Theme.Line)) g.DrawPath(edge, frame);
            }
            if (items.Length == 0) return;
            float cell = (Width - 2 * inset) / items.Length;
            for (int i = 0; i < items.Length; i++) {
                RectangleF area = new RectangleF(inset + i * cell, inset, cell, Height - 2 * inset);
                if (i == selected) using (GraphicsPath pill = Shapes.Round(area, radius - inset)) {
                    using (Brush fill = new SolidBrush(Theme.Raised)) g.FillPath(fill, pill);
                    using (Pen edge = new Pen(Theme.LineStrong)) g.DrawPath(edge, pill);
                }
                Color ink = !Enabled ? Theme.Faint : i == selected ? Theme.Ink : Theme.Muted;
                TextRenderer.DrawText(g, items[i], Font, Rectangle.Round(area), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Status dot with an optional 12% halo.
    internal sealed class StatusDot : Control {
        Color color = Theme.Faint;
        bool halo;
        internal bool Shield { get; set; }
        internal Color Color { get { return color; } set { color = value; Invalidate(); } }
        internal bool Halo { get { return halo; } set { halo = value; Invalidate(); } }
        internal StatusDot() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; Size = new Size(16, 16); TabStop = false; }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Theme.Px(g, 8), cx = Width / 2f, cy = Height / 2f;
            if (Shield) {
                float scale = g.DpiX / 96f;
                using (Pen pen = new Pen(color, 1.3f * scale) { LineJoin = LineJoin.Round }) {
                    g.DrawPolygon(pen, new[] {
                        new PointF(cx, cy - 7 * scale), new PointF(cx + 6 * scale, cy - 4 * scale),
                        new PointF(cx + 5 * scale, cy + 3 * scale), new PointF(cx, cy + 7 * scale),
                        new PointF(cx - 5 * scale, cy + 3 * scale), new PointF(cx - 6 * scale, cy - 4 * scale)
                    });
                }
                return;
            }
            if (halo) using (Brush ring = new SolidBrush(Theme.Soft(color))) g.FillEllipse(ring, cx - d, cy - d, 2 * d, 2 * d);
            using (Brush dot = new SolidBrush(color)) g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);
        }
    }

    // Text tabs with a 2 px accent underline under the selected one (routing window).
    internal sealed class TabStrip : ThemedControl {
        string[] tabs = new string[0];
        int selected;
        internal event EventHandler SelectedIndexChanged;
        internal string[] Tabs { get { return tabs; } set { tabs = value ?? new string[0]; selected = Math.Min(selected, Math.Max(0, tabs.Length - 1)); Invalidate(); } }
        internal int SelectedIndex {
            get { return selected; }
            set {
                if (value < 0 || value >= tabs.Length) throw new ArgumentOutOfRangeException("value");
                if (value == selected) return;
                selected = value; Invalidate();
                EventHandler handler = SelectedIndexChanged; if (handler != null) handler(this, EventArgs.Empty);
            }
        }
        Rectangle[] MeasureTabs(Graphics g) {
            Rectangle[] boxes = new Rectangle[tabs.Length];
            int x = 0, gap = (int)Theme.Px(g, 18);
            for (int i = 0; i < tabs.Length; i++) {
                int width = TextRenderer.MeasureText(g, tabs[i], Font).Width;
                boxes[i] = new Rectangle(x, 0, width, Height); x += width + gap;
            }
            return boxes;
        }
        protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }
        protected override void OnKeyDown(KeyEventArgs args) {
            if (Enabled && args.KeyCode == Keys.Left && selected > 0) { SelectedIndex = selected - 1; args.Handled = true; }
            else if (Enabled && args.KeyCode == Keys.Right && selected < tabs.Length - 1) { SelectedIndex = selected + 1; args.Handled = true; }
            base.OnKeyDown(args);
        }
        protected override void OnMouseDown(MouseEventArgs args) {
            if (Enabled) using (Graphics g = CreateGraphics()) {
                Rectangle[] boxes = MeasureTabs(g);
                for (int i = 0; i < boxes.Length; i++) if (boxes[i].Contains(args.Location)) { Focus(); SelectedIndex = i; break; }
            }
            base.OnMouseDown(args);
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            Rectangle[] boxes = MeasureTabs(g);
            using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            for (int i = 0; i < tabs.Length; i++) {
                Color ink = !Enabled ? Theme.Faint : i == selected ? Theme.Ink : Theme.Muted;
                TextRenderer.DrawText(g, tabs[i], Font, boxes[i], ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (i == selected) using (Brush bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, boxes[i].X, Height - Theme.Px(g, 2), boxes[i].Width, Theme.Px(g, 2));
            }
            if (ShowFocus && selected < boxes.Length) using (Pen focus = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot }) g.DrawRectangle(focus, boxes[selected].X - 2, 2, boxes[selected].Width + 4, Height - 6);
        }
    }
}
