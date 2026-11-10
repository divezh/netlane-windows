using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    // Selected server: one target. A click anywhere opens the list; the flag picks the country and
    // the ping chip re-measures latency. The subscription used to live here too, which read as if
    // the feed were a property of one server.
    internal sealed class ServerCard : ThemedControl {
        Server server;
        string ping, stateLine, title;
        internal event Action Opened, PingClicked, FlagClicked;
        internal Server Server { get { return server; } set { server = value; Invalidate(); } }
        internal string Title { get { return title; } set { title = value; Invalidate(); } }
        internal string PingText { get { return ping; } set { ping = value; Invalidate(); } }
        internal string StateLine { get { return stateLine; } set { stateLine = value; Invalidate(); } }
        internal ServerCard() { BackColor = Theme.Card; Cursor = Cursors.Hand; }
        float PixelScale { get { using (Graphics g = CreateGraphics()) return g.DpiX / 96f; } }
        int Px(float v) { return (int)Math.Round(v * PixelScale); }
        int TitleTop { get { return Px(10); } }
        internal int PreferredCardHeight {
            get { using (Font bold = Theme.Font(Theme.ServerSize, true)) using (Font small = Theme.Font(Theme.SmallSize)) return TitleTop + bold.Height + Px(6) + 2 * small.Height + Px(14); }
        }
        // The ping chip sits at the right, before the chevron that opens the list.
        internal Rectangle RefreshBounds { get { return PingBounds; } }
        internal Rectangle FlagBounds { get { using(Font bold=Theme.Font(Theme.ServerSize,true))using(Font small=Theme.Font(Theme.SmallSize)) return new Rectangle(Px(16),TitleTop+(bold.Height+2*small.Height+Px(6)-Px(28))/2,Px(28),Px(28)); } }
        // The chip exists before the first measurement: an empty right side read as a place where
        // something had been removed. «…» means "not measured yet", a click measures it.
        internal string PingLabel { get { return String.IsNullOrEmpty(ping) ? "…" : ping; } }
        internal Rectangle PingBounds {
            get {
                using (Font font = Theme.Font(Theme.BodySize)) using (Font bold = Theme.Font(Theme.ServerSize, true)) {
                    Size size = TextRenderer.MeasureText(PingLabel, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    int right = Width - Px(16) - Px(20) - Px(10);
                    return new Rectangle(right - size.Width - Px(14), TitleTop + Px(2), size.Width + Px(14), bold.Height);
                }
            }
        }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g=args.Graphics;g.Clear(Parent==null?Theme.Ground:Parent.BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
            if(Width<4||Height<4)return;
            using(GraphicsPath path=Shapes.Round(new RectangleF(.5f,.5f,Width-1,Height-1),Theme.Px(g,12)))
            using(Brush fill=new SolidBrush(Hover?Theme.Blend(Theme.Raised,Theme.Card,50):Theme.Card))
            using(Pen edge=new Pen(ShowFocus?Theme.Accent:Theme.Line)){g.FillPath(fill,path);g.DrawPath(edge,path);}
            int pad=Px(16),top=TitleTop;
            using(Font bold=Theme.Font(Theme.ServerSize,true))using(Font small=Theme.Font(Theme.SmallSize))using(Font body=Theme.Font(Theme.BodySize)) {
                Flags.DrawBadge(g,FlagBounds,server==null?null:server.Country,server==null?"?":server.Name);
                Rectangle pingBox=PingBounds;
                int left=FlagBounds.Right+Px(12),right=pingBox.Left-Px(10);
                TextFormatFlags flags=TextFormatFlags.Left|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g,title??"",bold,new Rectangle(left,top,Math.Max(0,right-left),bold.Height),Theme.Ink,flags);
                int y=top+bold.Height+Px(3),width=Math.Max(0,right-left);
                TextRenderer.DrawText(g,server==null?"":server.Hostname,small,new Rectangle(left,y,width,small.Height),Theme.Muted,flags);
                TextRenderer.DrawText(g,stateLine??"",small,new Rectangle(left,y+small.Height+Px(3),width,small.Height),Theme.Muted,flags);
                // The chip itself is the affordance for "measure again": the glyph that used to sit
                // beside it said the same thing twice and narrowed the title column for nothing.
                using(GraphicsPath chip=Shapes.Round(new RectangleF(pingBox.X,pingBox.Y,pingBox.Width,pingBox.Height),pingBox.Height/2f))
                using(Brush chipFill=new SolidBrush(Theme.Blend(Theme.Accent,Theme.Card,26)))g.FillPath(chipFill,chip);
                TextRenderer.DrawText(g,PingLabel,body,pingBox,Theme.Accent,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
                float cx=Width-pad-Theme.Px(g,4),cy=top+bold.Height/2f;
                using(Pen pen=new Pen(Theme.Ink,Theme.Px(g,1.8f)){StartCap=LineCap.Round,EndCap=LineCap.Round}){g.DrawLine(pen,cx-Theme.Px(g,4),cy-Theme.Px(g,5),cx,cy);g.DrawLine(pen,cx,cy,cx-Theme.Px(g,4),cy+Theme.Px(g,5));}
            }
        }
        protected override void OnClick(EventArgs args) {
            base.OnClick(args);if(!Enabled)return;
            MouseEventArgs mouse=args as MouseEventArgs;
            Action handler=mouse!=null&&(RefreshBounds.Contains(mouse.Location)||PingBounds.Contains(mouse.Location))?PingClicked:mouse!=null&&FlagBounds.Contains(mouse.Location)?FlagClicked:Opened;
            if(handler!=null)handler();
        }
        internal void ClickPingForTests() { Rectangle box=RefreshBounds;OnClick(new MouseEventArgs(MouseButtons.Left,1,box.Left+box.Width/2,box.Top+box.Height/2,0)); }
    }

    // The account card: who is signed in and which subscription feeds the servers. It replaced the
    // Kill Switch card on the main screen; the switch itself lives in Settings → Connection.
    internal sealed class SubscriptionRow : ThemedControl {
        Subscription source;
        string accountName = "";
        string subscriptionName = "";
        int serverCount;
        readonly Timer animation = new Timer { Interval = 80 };
        int angle;
        internal event Action SubscriptionRefreshRequested;
        internal string AccountName { get { return accountName; } set { accountName = value ?? ""; Invalidate(); } }
        internal int ServerCount { get { return serverCount; } set { serverCount = value; Invalidate(); } }
        internal string SubscriptionName { get { return subscriptionName; } set { subscriptionName = value ?? ""; Invalidate(); } }
        internal Subscription Subscription {
            get { return source; }
            set {
                source = value;
                Enabled = value != null && value.PendingRefreshes == 0;
                AccessibleRole = value != null ? AccessibleRole.PushButton : AccessibleRole.StaticText;
                AccessibleName = value != null && value.LastError != null ? "Повторить обновление подписки" : value != null ? "Обновить подписку" : "Аккаунт " + accountName;
                AccessibleDescription = value == null ? "Сервер добавлен вручную." : value.LastError ?? "Серверы, правила и DNS выбранной подписки.";
                UpdateTimer();
                Invalidate();
            }
        }
        internal SubscriptionRow() {
            BackColor = Theme.Ground;
            AccessibleRole = AccessibleRole.StaticText;
            animation.Tick += delegate { if (source != null && source.PendingRefreshes > 0) angle = (angle + 24) % 360; Invalidate(); };
        }
        void UpdateTimer() {
            bool moving = source != null && source.PendingRefreshes > 0 && SystemInformation.IsMenuAnimationEnabled;
            animation.Interval = moving ? 80 : 1000;
            animation.Enabled = source != null && Visible;
            if (!moving) angle = 0;
        }
        protected override void OnVisibleChanged(EventArgs args) { base.OnVisibleChanged(args); UpdateTimer(); }
        protected override void OnClick(EventArgs args) {
            if (Enabled && source != null && source.PendingRefreshes == 0) { Action handler = SubscriptionRefreshRequested; if (handler != null) handler(); }
            base.OnClick(args);
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
        internal string ActionText {
            get { return source == null ? "Без подписки" : source.PendingRefreshes > 0 ? "Обновляем подписку…" :
                source.LastError != null ? "Подписка: " + source.LastError : "Обновить подписку"; }
        }
        // What the second line says: how many locations the account has and how fresh the feed is.
        internal string StatusText {
            get {
                if (source == null) return "без подписки";
                if (source.PendingRefreshes > 0) return "обновляем…";
                if (source.LastError != null) return "не обновилась";
                string count = serverCount <= 0 ? "" : serverCount + (serverCount % 10 == 1 && serverCount % 100 != 11 ? " локация" : serverCount % 10 >= 2 && serverCount % 10 <= 4 && (serverCount % 100 < 12 || serverCount % 100 > 14) ? " локации" : " локаций");
                DateTime updated;
                if (source.UpdatedAt == null || !DateTime.TryParse(source.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out updated)) return count;
                updated = updated.ToUniversalTime();
                double age = Math.Max(0, (DateTime.UtcNow - updated).TotalSeconds);
                string when = age < 60 ? "только что" : age < 3600 ? ((int)(age / 60)) + " мин назад" : age < 86400 ? ((int)(age / 3600)) + " ч назад" : updated.ToLocalTime().ToString("dd.MM");
                return count.Length == 0 ? when : count + " · " + when;
            }
        }
        // Third line: the access the subscription gives («до 17.11 · 12 из 50 ГБ»); null hides it.
        internal string AccountText { get { return Account.Summary(source, DateTime.UtcNow); } }
        // Bottom of the navigation: the VPN login, the feed status and a refresh arrow. No card.
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            if (Width < 4 || Height < 4) return;
            float scale = g.DpiX / 96f;
            int pad = (int)(12 * scale), icon = source == null ? 0 : (int)(26 * scale);
            using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 2 * scale, 0, Width - 3 * scale, 0);
            if (Hover && Enabled) using (GraphicsPath path = Shapes.Round(new RectangleF(0.5f, 4 * scale, Width - 2, Height - 5 * scale), 6 * scale)) using (Brush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
            bool busy = source != null && source.PendingRefreshes > 0, failed = source != null && source.LastError != null;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            using (Font name = Theme.Font(Theme.BodySize + 0.5f, true)) using (Font small = Theme.Font(Theme.SmallSize)) {
                TextRenderer.DrawText(g, String.IsNullOrEmpty(accountName) ? "—" : accountName, name, new Rectangle(pad, (int)(9 * scale), Math.Max(0, Width - 2 * pad - icon), (int)(20 * scale)), Theme.Ink, flags);
                TextRenderer.DrawText(g, StatusText, small, new Rectangle(pad, (int)(29 * scale), Math.Max(0, Width - 2 * pad - icon), (int)(17 * scale)), failed ? Theme.Danger : Theme.Muted, flags);
                string access = AccountText;
                if (access != null) {
                    Account.Level level = Account.LevelOf(source, DateTime.UtcNow);
                    Color tone = level == Account.Level.Danger ? Theme.Danger : level == Account.Level.Warning ? Theme.Warning : Theme.Muted;
                    TextRenderer.DrawText(g, access, small, new Rectangle(pad, (int)(46 * scale), Math.Max(0, Width - 2 * pad - icon), (int)(17 * scale)), tone, flags);
                }
            }
            if (source != null) {
                float r = 5.5f * scale, x = Width - pad - r, y = Height / 2f + 2 * scale;
                using (Pen pen = new Pen(failed ? Theme.Danger : busy ? Theme.Faint : Theme.Muted, 1.4f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    if (busy) g.DrawArc(pen, x - r, y - r, 2 * r, 2 * r, angle - 90, 250);
                    else {
                        g.DrawArc(pen, x - r, y - r, 2 * r, 2 * r, 25, 290);
                        g.DrawLines(pen, new[] { new PointF(x + r - 3 * scale, y - 3 * scale), new PointF(x + r, y - 3 * scale), new PointF(x + r, y - 6 * scale) });
                    }
                }
            }
            if (ShowFocus) using (Pen focus = new Pen(Theme.Accent)) g.DrawRectangle(focus, 1, (int)(4 * scale), Math.Max(0, Width - 4), Math.Max(0, Height - (int)(6 * scale)));
        }
    }

    // «Маршрутизация … <set> ›»: the rule set in use; a click opens the choice of sets.
    internal sealed class RoutingRow : ThemedControl {
        string value = "";
        internal string Value { get { return value; } set { this.value = value ?? ""; Invalidate(); } }
        internal RoutingRow() { BackColor = Theme.Card; Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = args.Graphics;
            g.Clear(Parent == null ? Theme.Ground : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 4 || Height < 4) return;
            using (GraphicsPath path = Shapes.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.Px(g, 12)))
            using (Brush fill = new SolidBrush(Hover ? Theme.Blend(Theme.Raised, Theme.Card, 50) : Theme.Card))
            using (Pen edge = new Pen(ShowFocus ? Theme.Accent : Theme.Line)) { g.FillPath(fill, path); g.DrawPath(edge, path); }
            int pad = (int)Theme.Px(g, 16), chevron = (int)Theme.Px(g, 20);
            using (Font body = Theme.Font(Theme.BodySize + 0.5f)) {
                Size valueSize = TextRenderer.MeasureText(g, value, body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int valueWidth = Math.Min(valueSize.Width, Width / 2);
                TextRenderer.DrawText(g, "Маршрутизация", body, new Rectangle(pad, 0, Width - 2 * pad - chevron - valueWidth - pad, Height), Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, value, body, new Rectangle(Width - pad - chevron - valueWidth, 0, valueWidth, Height), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            float cx = Width - pad - Theme.Px(g, 4), cy = Height / 2f;
            using (Pen pen = new Pen(Theme.Ink, Theme.Px(g, 1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) { g.DrawLine(pen, cx - Theme.Px(g, 4), cy - Theme.Px(g, 5), cx, cy); g.DrawLine(pen, cx, cy, cx - Theme.Px(g, 4), cy + Theme.Px(g, 5)); }
        }
    }

    // Country flag or, until a flag image exists, a circle with the first letter of the name.
    internal static partial class Flags {
        internal static void DrawBadge(Graphics g, Rectangle bounds, string country, string fallbackName) {
            if (DrawImage(g, bounds, country)) return;
            using (Brush fill = new SolidBrush(Theme.Raised)) g.FillEllipse(fill, bounds);
            string letter = String.IsNullOrEmpty(fallbackName) ? "?" : fallbackName.Trim().Substring(0, 1).ToUpperInvariant();
            using (Font font = Theme.Font(Theme.SmallSize, true)) TextRenderer.DrawText(g, letter, font, bounds, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        static partial void DrawImageCore(Graphics g, Rectangle bounds, string country, ref bool drawn);
        static bool DrawImage(Graphics g, Rectangle bounds, string country) { bool drawn = false; if (country != null) DrawImageCore(g, bounds, country, ref drawn); return drawn; }
    }
}
