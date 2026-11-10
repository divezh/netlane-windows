using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace Netlane {
    internal enum ChevronDirection { None, Down, Right, Left }
    // The .ico is embedded with csc /resource and also applied to the executable with /win32icon.
    internal static class AppIcon {
        static Icon shared;
        // Dialog windows must set Form.Icon themselves: WinForms would otherwise show its default application icon.
        internal static Icon Shared { get { if (shared == null) shared = ForSize(32); return shared; } }
        // Header artwork: the embedded PNG of the app icon, or the drawn fallback mark.
        internal static Bitmap Mark(int size) {
            try {
                using (Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Netlane.png")) if (stream != null) using (Image source = Image.FromStream(stream)) {
                    Bitmap scaled = new Bitmap(size, size);
                    using (Graphics graphics = Graphics.FromImage(scaled)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.DrawImage(source, 0, 0, size, size); }
                    return scaled;
                }
            } catch (Exception) {}
            return DrawMark(size);
        }
        // Simplified Netlane mark: navy rounded square, cyan arch, white W.
        static Bitmap DrawMark(int size) {
            Bitmap bitmap = new Bitmap(size, size);
            using (Graphics graphics = Graphics.FromImage(bitmap)) {
                graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath square = Shapes.Round(new RectangleF(0, 0, size - 1, size - 1), size * .22f)) using (Brush fill = new SolidBrush(Theme.IconNavy)) graphics.FillPath(fill, square);
                using (Pen arch = new Pen(Theme.IconCyan, Math.Max(1.2f, size * .08f))) {
                    graphics.DrawArc(arch, size * .2f, size * .2f, size * .6f, size * .6f, 180, 180);
                    graphics.DrawLine(arch, size * .2f, size * .5f, size * .2f, size * .78f); graphics.DrawLine(arch, size * .8f, size * .5f, size * .8f, size * .78f);
                }
                using (Font font = new Font("Segoe UI", Math.Max(5f, size * .3f), FontStyle.Bold, GraphicsUnit.Pixel)) using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    graphics.DrawString("W", font, Brushes.White, new RectangleF(0, size * .18f, size, size * .6f), center);
            }
            return bitmap;
        }
        internal static Icon ForSize(int size) {
            try {
                using (Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Netlane.ico")) if (stream != null) return new Icon(stream, new Size(size, size));
            } catch (Exception) {}
            using (Bitmap bitmap = DrawMark(size)) {
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon temporary = Icon.FromHandle(handle)) return (Icon)temporary.Clone(); } finally { Native.DestroyIcon(handle); }
            }
        }
    }
    internal static class Shapes {
        internal static GraphicsPath Round(RectangleF rectangle, float radius) {
            if (rectangle.Width < 2f || rectangle.Height < 2f) return new GraphicsPath();
            float diameter = Math.Max(1f, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90); path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
    }
    internal sealed class RoundedPanel : Panel {
        internal RoundedPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); BackColor = MainForm.Surface; }
        protected override void OnPaintBackground(PaintEventArgs args) {
            args.Graphics.Clear(Parent == null ? MainForm.Ground : Parent.BackColor); args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Shapes.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.Px(args.Graphics, Theme.RadiusCard))) {
                using (Brush fill = new SolidBrush(BackColor)) args.Graphics.FillPath(fill, path);
                using (Pen edge = new Pen(Theme.Line)) args.Graphics.DrawPath(edge, path);
            }
        }
    }
    // The viewport clips the system scrollbar. The adjacent rail controls the same scroll position.
    internal sealed class DarkScrollPage : Panel {
        internal DarkScrollPage(Panel page) {
            BackColor = Theme.Ground;
            Panel viewport = new Panel { BackColor = Theme.Ground };
            ScrollRail rail = new ScrollRail(page);
            viewport.Controls.Add(page);
            Controls.Add(viewport);
            Controls.Add(rail);
            bool arranging = false;
            Action arrange = delegate {
                if (arranging || IsDisposed) return;
                arranging = true;
                try {
                    page.Size = ClientSize;
                    int width = page.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
                    viewport.SetBounds(0, 0, Math.Max(0, ClientSize.Width - width), ClientSize.Height);
                    rail.SetBounds(viewport.Right, 0, width, ClientSize.Height);
                    rail.Visible = width > 0;
                    rail.Invalidate();
                } finally { arranging = false; }
            };
            Layout += delegate { arrange(); };
            page.Layout += delegate { arrange(); };
            page.Scroll += delegate { rail.Invalidate(); };
            page.MouseWheel += delegate { rail.Invalidate(); };
        }

        sealed class ScrollRail : Control {
            readonly Panel page;
            int dragOffset;

            internal ScrollRail(Panel page) {
                this.page = page;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Theme.Ground;
                TabStop = false;
            }

            int ScrollRange { get { return Math.Max(0, page.VerticalScroll.Maximum - page.VerticalScroll.LargeChange + 1); } }

            Rectangle Thumb {
                get {
                    int track = Math.Max(0, Height - 8);
                    int viewport = Math.Max(1, page.VerticalScroll.LargeChange);
                    int thumb = Math.Min(track, Math.Max(20, (int)(track * (double)viewport / (viewport + ScrollRange))));
                    int position = ScrollRange == 0 ? 0 : (int)((track - thumb) * (double)(-page.AutoScrollPosition.Y) / ScrollRange);
                    return new Rectangle((Width - 6) / 2, 4 + position, 6, thumb);
                }
            }

            protected override void OnPaint(PaintEventArgs args) {
                args.Graphics.Clear(Theme.Ground);
                Rectangle thumb = Thumb;
                if (thumb.Height <= 0) return;
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Brush track = new SolidBrush(Theme.Card))
                    args.Graphics.FillRectangle(track, thumb.X, 4, thumb.Width, Math.Max(0, Height - 8));
                using (GraphicsPath shape = Shapes.Round(thumb, 3))
                using (Brush fill = new SolidBrush(Theme.Blend(Theme.Accent, Theme.LineStrong, Capture ? 150 : 75)))
                    args.Graphics.FillPath(fill, shape);
            }

            protected override void OnMouseDown(MouseEventArgs args) {
                base.OnMouseDown(args);
                if (args.Button != MouseButtons.Left) return;
                Rectangle thumb = Thumb;
                dragOffset = args.Y >= thumb.Top && args.Y < thumb.Bottom ? args.Y - thumb.Top : thumb.Height / 2;
                Capture = true;
                MoveThumb(args.Y);
            }

            protected override void OnMouseMove(MouseEventArgs args) {
                base.OnMouseMove(args);
                if (Capture) MoveThumb(args.Y);
            }

            protected override void OnMouseUp(MouseEventArgs args) {
                base.OnMouseUp(args);
                if (args.Button == MouseButtons.Left) Capture = false;
                Invalidate();
            }

            void MoveThumb(int y) {
                int travel = Height - 8 - Thumb.Height;
                if (travel <= 0) return;
                int position = Math.Max(0, Math.Min(travel, y - 4 - dragOffset));
                page.AutoScrollPosition = new Point(-page.AutoScrollPosition.X, (int)Math.Round(position * (double)ScrollRange / travel));
                Invalidate();
            }
        }
    }

    // Keep native list keyboard/selection behavior, but clip its non-client scrollbar.
    internal sealed class DarkRuleList : ListBox {
        internal event Action Scrolled;
        protected override void WndProc(ref Message message) {
            base.WndProc(ref message);
            if (message.Msg == 0x115 || message.Msg == 0x20A || message.Msg == 0x100 ||
                message.Msg == 0x197 || message.Msg == 0x180 || message.Msg == 0x181 || message.Msg == 0x184) {
                Action handler = Scrolled; if (handler != null) handler();
            }
        }
    }
    internal sealed class DarkRuleListViewport : Panel {
        readonly DarkRuleList list;
        readonly Panel viewport = new Panel { BackColor = Theme.Card };
        readonly RuleRail rail;
        internal DarkRuleListViewport(DarkRuleList list) {
            this.list = list; BackColor = Theme.Card;
            rail = new RuleRail(list);
            viewport.Controls.Add(list); Controls.Add(viewport); Controls.Add(rail);
            list.Scrolled += RefreshRail;
        }
        void RefreshRail() { rail.Invalidate(); }
        protected override void OnLayout(LayoutEventArgs args) {
            base.OnLayout(args);
            int width = Math.Max(14, (int)(14 * DeviceDpi / 96f));
            viewport.SetBounds(0, 0, Math.Max(0, Width - width), Height);
            list.SetBounds(0, 0, viewport.Width + SystemInformation.VerticalScrollBarWidth, Height);
            rail.SetBounds(viewport.Right, 0, width, Height);
            rail.Invalidate();
        }
        sealed class RuleRail : Control {
            readonly DarkRuleList list;
            int offset;
            internal RuleRail(DarkRuleList list) {
                this.list = list; TabStop = false; BackColor = Theme.Card;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            int Rows { get { return Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight)); } }
            int Range { get { return Math.Max(0, list.Items.Count - Rows); } }
            Rectangle Thumb {
                get {
                    int track = Math.Max(0, Height - 8);
                    int height = Math.Min(track, Math.Max(24, track * Rows / Math.Max(Rows, list.Items.Count)));
                    return new Rectangle((Width - 5) / 2, 4 + (Range == 0 ? 0 : (track - height) * Math.Min(Range, list.TopIndex) / Range), 5, height);
                }
            }
            protected override void OnPaint(PaintEventArgs args) {
                args.Graphics.Clear(Theme.Card);
                if (Range == 0 || Thumb.Height <= 0) return;
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Shapes.Round(Thumb, 2.5f))
                using (Brush brush = new SolidBrush(Capture ? Theme.Accent : Theme.LineStrong)) args.Graphics.FillPath(brush, path);
            }
            protected override void OnMouseDown(MouseEventArgs args) {
                base.OnMouseDown(args);
                if (args.Button != MouseButtons.Left || Range == 0) return;
                Rectangle thumb = Thumb; offset = thumb.Contains(args.Location) ? args.Y - thumb.Y : thumb.Height / 2;
                list.Focus(); Capture = true; MoveThumb(args.Y);
            }
            protected override void OnMouseMove(MouseEventArgs args) { base.OnMouseMove(args); if (Capture) MoveThumb(args.Y); }
            protected override void OnMouseUp(MouseEventArgs args) { base.OnMouseUp(args); if (args.Button == MouseButtons.Left) Capture = false; Invalidate(); }
            protected override void OnMouseWheel(MouseEventArgs args) {
                base.OnMouseWheel(args);
                list.TopIndex = Math.Max(0, Math.Min(Range, list.TopIndex - Math.Sign(args.Delta) * Math.Max(1, SystemInformation.MouseWheelScrollLines)));
            }
            void MoveThumb(int y) {
                int travel = Height - 8 - Thumb.Height;
                if (travel > 0) list.TopIndex = (int)Math.Round(Math.Max(0, Math.Min(travel, y - 4 - offset)) * (double)Range / travel);
                Invalidate();
            }
        }
    }

    internal enum ButtonGlyph { None, Plus, Clipboard }
    internal class DarkButton : Button {
        internal ButtonKind Kind { get; set; }
        // Small line icon drawn before the text (the text and icon are centred together).
        internal ButtonGlyph Glyph { get; set; }
        bool Primary { get { return Kind == ButtonKind.Primary; } }
        internal ChevronDirection Chevron { get; set; }
        bool hover;
        bool pressed;
        internal DarkButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
        protected override void OnMouseEnter(EventArgs args) { hover = true; Invalidate(); base.OnMouseEnter(args); }
        protected override void OnMouseLeave(EventArgs args) { hover = false; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnMouseDown(MouseEventArgs args) { pressed = true; Invalidate(); base.OnMouseDown(args); }
        protected override void OnMouseUp(MouseEventArgs args) { pressed = false; Invalidate(); base.OnMouseUp(args); }
        protected override void OnPaint(PaintEventArgs args) {
            args.Graphics.Clear(Parent == null ? MainForm.Ground : Parent.BackColor); args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool danger = Kind == ButtonKind.Danger;
            Color background = Enabled && Primary ? Theme.Accent : danger ? (hover && Enabled ? Theme.Soft(Theme.Danger) : (Parent == null ? Theme.Ground : Parent.BackColor)) : hover && Enabled ? Theme.Raised : Theme.Card;
            if (pressed && Enabled) background = Theme.Darken(background, 12);
            Color foreground = !Enabled ? Theme.Faint : Primary ? Theme.AccentInk : danger ? Theme.Danger : ForeColor;
            using (GraphicsPath path = Shapes.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.Px(args.Graphics, Theme.RadiusControl))) {
                using (Brush fill = new SolidBrush(background)) args.Graphics.FillPath(fill, path);
                if (!Primary || !Enabled) using (Pen border = new Pen(danger && Enabled ? Theme.Fade(Theme.Danger, 110) : Theme.Line)) args.Graphics.DrawPath(border, path);
            }
            float dpi = args.Graphics.DpiX / 96f;
            Rectangle text = ClientRectangle; text.Inflate(-(int)Theme.Px(args.Graphics, Glyph == ButtonGlyph.None ? 12 : 6), 0);
            if (Chevron != ChevronDirection.None) { int room = (int)(16 * dpi); text.Width -= room; if (Chevron == ChevronDirection.Left) { text.X += room; } }
            TextFormatFlags alignment = TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            if (Glyph != ButtonGlyph.None) {
                int icon = (int)(16 * dpi), gap = (int)(7 * dpi), textWidth = TextRenderer.MeasureText(args.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                int left = Math.Max(text.Left, (Width - icon - gap - textWidth) / 2);
                DrawGlyph(args.Graphics, new RectangleF(left, (Height - icon) / 2f, icon, icon), foreground, dpi);
                text = new Rectangle(left + icon + gap, text.Top, Math.Max(0, text.Right - left - icon - gap), text.Height);
                alignment = TextFormatFlags.Left;
            }
            TextRenderer.DrawText(args.Graphics, Text, Font, text, foreground, alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (Glyph != ButtonGlyph.None ? TextFormatFlags.NoPadding : 0));
            if (Chevron != ChevronDirection.None) DrawChevron(args.Graphics, foreground, dpi);
            if (Focused && ShowFocusCues) { Rectangle focus = ClientRectangle; focus.Inflate(-3, -3); using (GraphicsPath path = Shapes.Round(focus, 4)) using (Pen edge = new Pen(MainForm.Accent)) args.Graphics.DrawPath(edge, path); }
        }
        void DrawGlyph(Graphics graphics, RectangleF box, Color color, float dpi) {
            using (Pen pen = new Pen(color, 1.7f * dpi) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2, r = box.Width / 2;
                if (Glyph == ButtonGlyph.Plus) { graphics.DrawLine(pen, cx - r * .8f, cy, cx + r * .8f, cy); graphics.DrawLine(pen, cx, cy - r * .8f, cx, cy + r * .8f); return; }
                RectangleF board = new RectangleF(box.X + box.Width * .14f, box.Y + box.Height * .12f, box.Width * .72f, box.Height * .84f);
                using (GraphicsPath path = Shapes.Round(board, 2 * dpi)) graphics.DrawPath(pen, path);
                RectangleF clip = new RectangleF(cx - box.Width * .2f, box.Y, box.Width * .4f, box.Height * .24f);
                using (Brush fill = new SolidBrush(Parent == null ? Theme.Card : Theme.Card)) graphics.FillRectangle(fill, clip);
                using (GraphicsPath path = Shapes.Round(clip, 1.5f * dpi)) graphics.DrawPath(pen, path);
            }
        }
        // Drawn instead of the "⌄"/"›" characters: those fall back to fonts whose baseline and weight do not match Segoe UI.
        void DrawChevron(Graphics graphics, Color color, float dpi) {
            float size = 4.4f * dpi, x = Chevron == ChevronDirection.Left ? 15 * dpi : Width - 15 * dpi, y = Height / 2f;
            PointF[] points;
            if (Chevron == ChevronDirection.Down) points = new[] { new PointF(x - size, y - size * .55f), new PointF(x, y + size * .45f), new PointF(x + size, y - size * .55f) };
            else if (Chevron == ChevronDirection.Left) points = new[] { new PointF(x + size * .55f, y - size), new PointF(x - size * .45f, y), new PointF(x + size * .55f, y + size) };
            else points = new[] { new PointF(x - size * .55f, y - size), new PointF(x + size * .45f, y), new PointF(x - size * .55f, y + size) };
            using (Pen pen = new Pen(color, 1.6f * dpi) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) graphics.DrawLines(pen, points);
        }
    }
    internal sealed class ChoiceButton : DarkButton {
        internal readonly List<object> Items = new List<object>();
        ContextMenuStrip menu;
        int selected = -1;
        internal event EventHandler SelectedIndexChanged;
        internal int SelectedIndex { get { return selected; } set { if (selected == value) return; selected = value; Text = selected >= 0 && selected < Items.Count ? Items[selected].ToString() : ""; EventHandler handler = SelectedIndexChanged; if (handler != null) handler(this, EventArgs.Empty); Invalidate(); } }
        internal ChoiceButton() { TextAlign = ContentAlignment.MiddleLeft; AccessibleRole = AccessibleRole.ComboBox; Chevron = ChevronDirection.Down; }
        protected override void OnKeyDown(KeyEventArgs args) { if(Enabled&&Items.Count>0&&(args.KeyCode==Keys.Up||args.KeyCode==Keys.Down)){SelectedIndex=(selected+(args.KeyCode==Keys.Down?1:-1)+Items.Count)%Items.Count;args.Handled=true;return;}base.OnKeyDown(args); }
        // The menu lives as long as the control: disposing it inside its own Closed handler races WinForms internals and throws ObjectDisposedException.
        void BuildMenu() {
            if (menu == null) { menu = DarkMenuRenderer.CreateMenu(Width); }
            while (menu.Items.Count > 0) menu.Items[0].Dispose();
            for (int i = 0; i < Items.Count; i++) { int index = i; ToolStripMenuItem item = new ToolStripMenuItem(Items[i].ToString()) { ForeColor = MainForm.Ink, BackColor = MainForm.Surface, Checked = i == selected }; item.Click += delegate { SelectedIndex = index; }; menu.Items.Add(item); }
        }
        protected override void OnClick(EventArgs args) {
            base.OnClick(args);
            if (!Enabled || Items.Count == 0) return;
            BuildMenu();
            menu.Show(this, new Point(0, Height));
        }
        protected override void Dispose(bool disposing) { if (disposing && menu != null) { menu.Dispose(); menu = null; } base.Dispose(disposing); }
    }
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer {
        internal static ContextMenuStrip CreateMenu(int minimumWidth) {
            ContextMenuStrip menu = new ContextMenuStrip {
                BackColor = Theme.Card, ForeColor = Theme.Ink, Font = Theme.Font(Theme.BodySize),
                ShowImageMargin = false, ShowCheckMargin = true, Renderer = new DarkMenuRenderer(),
                Padding = new Padding(4), MinimumSize = new Size(minimumWidth, 0)
            };
            menu.ItemAdded += delegate(object sender, ToolStripItemEventArgs args) {
                if (args.Item is ToolStripMenuItem) args.Item.Padding = new Padding(6, 6, 14, 6);
            };
            menu.Layout += delegate {
                // DisplayRectangle excludes the check gutter; item bounds already include it.
                int width = Math.Max(0, menu.ClientSize.Width - 1);
                foreach (ToolStripItem item in menu.Items) {
                    if (item.AutoSize) { int height = item.GetPreferredSize(Size.Empty).Height; item.AutoSize = false; item.Height = height; }
                    if (item.Width != width) item.Width = width;
                }
            };
            return menu;
        }
        public DarkMenuRenderer() { RoundedEdges = false; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs args) { args.Graphics.Clear(Theme.Card); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs args) { using (Brush fill = new SolidBrush(Theme.Card)) args.Graphics.FillRectangle(fill, args.AffectedBounds); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs args) {
            using (Brush fill = new SolidBrush(args.Item.Selected && args.Item.Enabled ? Theme.Raised : Theme.Card)) args.Graphics.FillRectangle(fill, new Rectangle(Point.Empty, args.Item.Size));
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs args) {
            Rectangle box = args.ImageRectangle;
            float scale = args.Graphics.DpiX / 96f, x = box.Left + box.Width / 2f, y = box.Top + box.Height / 2f;
            GraphicsState saved = args.Graphics.Save(); args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen tick = new Pen(args.Item.Enabled ? Theme.Accent : Theme.Muted, 1.8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                args.Graphics.DrawLines(tick, new[] { new PointF(x - 4 * scale, y), new PointF(x - scale, y + 3 * scale), new PointF(x + 5 * scale, y - 4 * scale) });
            args.Graphics.Restore(saved);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs args) {
            using (Brush fill = new SolidBrush(Theme.Card)) args.Graphics.FillRectangle(fill, new Rectangle(Point.Empty, args.Item.Size));
            using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, 8, args.Item.Height / 2, args.Item.Width - 9, args.Item.Height / 2);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs args) { args.TextColor = args.Item.Enabled ? Theme.Ink : Theme.Muted; base.OnRenderItemText(args); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs args) { args.ArrowColor = args.Item.Enabled ? Theme.Ink : Theme.Muted; base.OnRenderArrow(args); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs args) { using (Pen edge = new Pen(Theme.LineStrong)) args.Graphics.DrawRectangle(edge, 0, 0, args.ToolStrip.Width - 1, args.ToolStrip.Height - 1); }
    }
}
