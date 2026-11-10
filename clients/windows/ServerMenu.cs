using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Netlane {
    // Server list shown over the main window (spec §2): flag, name, ping, a check on the selected
    // server, and «+ Добавить подписку или сервер…». Esc or a click elsewhere closes it.
    internal sealed class ServerMenu : Form {
        readonly ClientState state;
        readonly Dictionary<string, string> pings;
        int hover = -1;
        internal event Action<string> Picked;
        internal event Action AddRequested;
        internal int RowCount { get { return state.Servers.Count + 1; } }
        int RowHeight { get { return (int)(40 * DeviceDpi / 96f); } }
        float DeviceDpi { get { using (Graphics g = CreateGraphics()) return g.DpiX; } }

        internal ServerMenu(ClientState state, Dictionary<string, string> pings) {
            this.state = state; this.pings = pings;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; KeyPreview = true; TopMost = false;
            BackColor = Theme.Raised; Font = Theme.Font(Theme.BodySize);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            int pad = (int)(6 * DeviceDpi / 96f);
            ClientSize = new Size(320, RowCount * RowHeight + 2 * pad + pad);
            Padding = new Padding(pad);
            Deactivate += delegate { Close(); };
        }
        Rectangle Row(int index) {
            int pad = Padding.Top, top = pad + index * RowHeight + (index == RowCount - 1 ? pad : 0);
            return new Rectangle(pad, top, ClientSize.Width - 2 * pad, RowHeight);
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Theme.Raised);
            using (Pen edge = new Pen(Theme.LineStrong)) g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            for (int i = 0; i < RowCount; i++) {
                Rectangle row = Row(i);
                bool isAdd = i == RowCount - 1, selected = !isAdd && state.Servers[i].Id == state.SelectedServerId;
                if (selected || i == hover) using (GraphicsPath path = Shapes.Round(row, Theme.Px(g, 9))) using (Brush fill = new SolidBrush(selected ? Theme.Selection : Theme.Blend(Theme.LineStrong, Theme.Raised, 40))) g.FillPath(fill, path);
                if (i == RowCount - 1) {
                    using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, row.Left + 6, row.Top - Padding.Top / 2, row.Right - 6, row.Top - Padding.Top / 2);
                    TextRenderer.DrawText(g, "+ Добавить подписку или сервер…", Font, new Rectangle(row.Left + (int)Theme.Px(g, 12), row.Top, row.Width, row.Height), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    continue;
                }
                Server server = state.Servers[i];
                int x = row.Left + (int)Theme.Px(g, 10);
                if (selected) using (Pen check = new Pen(Theme.Accent, Theme.Px(g, 1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    float cy = row.Top + row.Height / 2f;
                    g.DrawLines(check, new[] { new PointF(x, cy), new PointF(x + Theme.Px(g, 4), cy + Theme.Px(g, 4)), new PointF(x + Theme.Px(g, 11), cy - Theme.Px(g, 4)) });
                }
                x += (int)Theme.Px(g, 18);
                int badge = (int)Theme.Px(g, 22);
                Flags.DrawBadge(g, new Rectangle(x, row.Top + (row.Height - badge) / 2, badge, badge), server.Country, server.Name);
                x += badge + (int)Theme.Px(g, 10);
                string ping; pings.TryGetValue(server.Id, out ping);
                int pingWidth = String.IsNullOrEmpty(ping) ? 0 : TextRenderer.MeasureText(g, ping, Font).Width + (int)Theme.Px(g, 10);
                TextRenderer.DrawText(g, server.Name, Font, new Rectangle(x, row.Top, row.Right - x - pingWidth, row.Height), Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (pingWidth > 0) TextRenderer.DrawText(g, ping, Font, new Rectangle(row.Right - pingWidth, row.Top, pingWidth - (int)Theme.Px(g, 6), row.Height), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }
        int HitRow(Point point) { for (int i = 0; i < RowCount; i++) if (Row(i).Contains(point)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs args) { int row = HitRow(args.Location); if (row != hover) { hover = row; Invalidate(); } base.OnMouseMove(args); }
        protected override void OnMouseLeave(EventArgs args) { hover = -1; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnMouseUp(MouseEventArgs args) { base.OnMouseUp(args); int row = HitRow(args.Location); if (row >= 0) { PickRow(row); Close(); } }
        protected override void OnKeyDown(KeyEventArgs args) {
            if (args.KeyCode == Keys.Escape) { Close(); args.Handled = true; return; }
            if (args.KeyCode == Keys.Down || args.KeyCode == Keys.Up) { hover = Math.Max(0, Math.Min(RowCount - 1, (hover < 0 ? -1 : hover) + (args.KeyCode == Keys.Down ? 1 : -1))); Invalidate(); args.Handled = true; return; }
            if ((args.KeyCode == Keys.Enter || args.KeyCode == Keys.Space) && hover >= 0) { PickRow(hover); Close(); args.Handled = true; return; }
            base.OnKeyDown(args);
        }
        // A row's action without the mouse (keyboard and tests).
        internal void PickRow(int index) {
            if (index == RowCount - 1) { Action add = AddRequested; if (add != null) add(); return; }
            Action<string> handler = Picked;
            if (handler != null && index >= 0 && index < state.Servers.Count) handler(state.Servers[index].Id);
        }
    }
}
