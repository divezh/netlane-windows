using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Netlane {
    // What the settings window needs from the main window: one shared state, saving, switching
    // (which reconnects when needed), imports and the cached pings.
    internal interface ISettingsHost {
        ClientState State { get; }
        void SaveState();
        bool SaveDnsServers(List<string> servers);
        Task SwitchTo(string serverId, string ruleSetId);
        bool VpnActive { get; }
        void ImportLink();
        void ImportFile();
        Dictionary<string, string> Pings { get; }
        bool PreviewOnly { get; }
        // The rules of the set in use changed: while connected the engine restarts with them.
        void RulesChanged(string ruleSetId);
        // A dropped .toml profile: the same import as «Открыть TOML…».
        void ImportTomlFile(string path);
        // Subscriptions: refresh one now, or remove it with its servers.
        Task RefreshSubscription(string id);
        void RemoveSubscription(string id);
        // Manual update check: returns a ready-to-show sentence.
        Task<string> CheckForUpdates();
    }

    // «Настройки» (spec §3): tabs on the left, one page at a time on the right.
    internal sealed partial class SettingsForm : Form {
        readonly ISettingsHost host;
        readonly ClientState state;
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Padding = new Padding(26, 20, 26, 18) };
        readonly List<NavItem> nav = new List<NavItem>();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly Dictionary<string, long> pingMs = new Dictionary<string, long>();
        string current;
        internal static readonly string[] PageIds = { "servers", "rules", "connection", "about" };
        static readonly string[] PageTitles = { "Серверы", "Правила", "Подключение", "О программе" };

        // embedded = a borderless child of the main window: the navigation is the main window's own.
        readonly bool embedded;
        internal string CurrentPage { get { return current; } }
        // False when the page has unsaved changes and the user chose to stay.
        internal bool TryLeave() { return CanLeavePage(); }
        internal SettingsForm(ISettingsHost host, bool embedded = false) {
            this.host = host; state = host.State; this.embedded = embedded;
            Text = "Настройки · " + Program.ProductName; BackColor = Theme.Ground; ForeColor = Theme.Ink; Font = Theme.Font(Theme.BodySize);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(980, 600); MinimumSize = new Size(880, 540); StartPosition = FormStartPosition.CenterScreen; Icon = AppIcon.Shared;
            Panel side = new Panel { Dock = DockStyle.Left, Width = 184, BackColor = Theme.Ground, Padding = new Padding(10, 14, 10, 12) };
            side.Paint += delegate(object sender, PaintEventArgs args) { using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, side.Width - 1, 0, side.Width - 1, side.Height); };
            Label version = new Label { Text = "v" + Program.Version + " · движок " + EngineSpec.Version, Dock = DockStyle.Bottom, Height = 22, Font = Theme.Font(Theme.CaptionSize), ForeColor = Theme.Faint, Padding = new Padding(10, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            side.Controls.Add(version);
            for (int i = PageIds.Length - 1; i >= 0; i--) {
                string id = PageIds[i];
                NavItem item = new NavItem { Text = PageTitles[i], Dock = DockStyle.Top, Height = 38, Tag = id };
                item.Click += delegate { ShowPage(id); };
                nav.Insert(0, item); side.Controls.Add(item);
            }
            Controls.Add(content);
            if (embedded) { TopLevel = false; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; MinimumSize = Size.Empty; Dock = DockStyle.Fill; nav.Clear(); side.Dispose(); }
            else Controls.Add(side);
            state.Changed += OnStateChanged;
            FormClosed += delegate { state.Changed -= OnStateChanged; };
            ShowPage("servers");
            FormClosing += delegate(object sender, FormClosingEventArgs args) { if (!CanLeavePage()) args.Cancel = true; };
            // Inside the main window its own drop handler covers the whole tree.
            if (!embedded) AcceptDrops(this, Drop);
        }
        void OnStateChanged() { if (IsDisposed) return; if (current == "servers") RebuildServers(); else if (current == "rules") { if (editor != null && !editor.Dirty) OpenSet(editingId); else RebuildSetList(); } else if (current == "connection") RefreshConnection(); }

        internal void ShowPage(string page) {
            if (Array.IndexOf(PageIds, page) < 0) page = "servers";
            if (current == page) return;
            if (!CanLeavePage()) return;
            current = page;
            foreach (NavItem item in nav) item.Selected = (string)item.Tag == page;
            Control control;
            if (!pages.TryGetValue(page, out control)) {
                control = page == "servers" ? BuildServersPage() : page == "rules" ? BuildRulesPage() : page == "connection" ? BuildConnectionPage() : BuildAboutPage();
                Panel scrollPage = control as Panel;
                if (scrollPage != null && scrollPage.AutoScroll) control = new DarkScrollPage(scrollPage);
                control.Dock = DockStyle.Fill; pages[page] = control;
            }
            content.Controls.Clear(); content.Controls.Add(control);
            if (page == "servers") { RebuildServers(); MeasureAll(); } else if (page == "connection") RefreshConnection();
        }
        bool CanLeavePage() { bool ok = ConfirmDnsChanges(); CanLeave(ref ok); return ok; }
        partial void CanLeave(ref bool ok);

        internal static Label Heading(string text) { Label label = MainForm.TextLabel(text, Theme.HeadingSize + 2, Theme.Ink); label.Font = Theme.Font(Theme.HeadingSize + 2, true); return label; }
        internal static Label Hint(string text) { Label label = MainForm.TextLabel(text, Theme.SmallSize, Theme.Faint); label.TextAlign = ContentAlignment.TopLeft; return label; }

        // ---- Серверы ---------------------------------------------------------------------------
        readonly Panel serverList = new Panel { Dock = DockStyle.Top, BackColor = Theme.Ground, Margin = new Padding(0) };
        readonly Panel subscriptionList = new Panel { Dock = DockStyle.Top, BackColor = Theme.Ground, Margin = new Padding(0) };
        const int SubscriptionRowHeight = 60;
        const int ServerRowHeight = 58;
        // Top-docked stack: heading, hint, the rows, the import buttons.
        Control BuildServersPage() {
            Panel page = new Panel { BackColor = Theme.Ground, AutoScroll = true };
            Label heading = Heading("Серверы"); heading.Dock = DockStyle.Top; heading.Height = 34;
            Label hint = Hint("Выбранный сервер используется кнопкой на главном экране. Новый профиль добавляется в список, прежние остаются."); hint.Dock = DockStyle.Top; hint.Height = 40;
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 62, WrapContents = false, BackColor = Theme.Ground, Padding = new Padding(0, 16, 0, 0) };
            Button paste = MainForm.Button("Вставить ссылку tt://", true), toml = MainForm.Button("Открыть TOML…", false);
            paste.Dock = toml.Dock = DockStyle.None; paste.Size = new Size(210, 40); toml.Size = new Size(160, 40); paste.Margin = new Padding(0, 0, 10, 0); toml.Margin = new Padding(0);
            paste.Click += delegate { if (!host.PreviewOnly) host.ImportLink(); }; toml.Click += delegate { if (!host.PreviewOnly) host.ImportFile(); };
            actions.Controls.Add(paste); actions.Controls.Add(toml);
            page.Controls.Add(actions); page.Controls.Add(serverList); page.Controls.Add(subscriptionList); page.Controls.Add(hint); page.Controls.Add(heading);
            return page;
        }
        internal void RebuildServers() {
            RebuildSubscriptions();
            serverList.SuspendLayout();
            foreach (Control old in serverList.Controls.Cast<Control>().ToList()) old.Dispose();
            serverList.Controls.Clear();
            string fastest = FastestLabel(pingMs);
            for (int i = state.Servers.Count - 1; i >= 0; i--) {
                Control row = ServerRow(state.Servers[i], i == 0, state.Servers[i].Id == fastest && state.Servers.Count > 1);
                row.Dock = DockStyle.Top; row.Height = ServerRowHeight; serverList.Controls.Add(row);
            }
            if (state.Servers.Count == 0) { Label none = Hint("Серверов пока нет. Вставьте ссылку tt:// из панели или откройте TOML."); none.Dock = DockStyle.Top; none.Height = 40; serverList.Controls.Add(none); }
            serverList.Height = Math.Max(40, state.Servers.Count * ServerRowHeight);
            serverList.ResumeLayout();
        }
        // One row per subscription above the servers: name, last update or error, «Обновить», «Удалить подписку…».
        void RebuildSubscriptions() {
            subscriptionList.SuspendLayout();
            foreach (Control old in subscriptionList.Controls.Cast<Control>().ToList()) old.Dispose();
            subscriptionList.Controls.Clear();
            for (int i = state.Subscriptions.Count - 1; i >= 0; i--) {
                Control row = SubscriptionRow(state.Subscriptions[i]);
                row.Dock = DockStyle.Top; row.Height = SubscriptionRowHeight; subscriptionList.Controls.Add(row);
            }
            subscriptionList.Height = state.Subscriptions.Count * SubscriptionRowHeight;
            subscriptionList.ResumeLayout();
        }
        Control SubscriptionRow(Subscription sub) {
            TableLayoutPanel row = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, BackColor = Theme.Ground, Margin = new Padding(0), Padding = new Padding(0, 8, 0, 8) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); row.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            int count = state.Servers.Count(x => x.SubscriptionId == sub.Id);
            Label title = MainForm.TextLabel("Подписка «" + sub.DisplayName + "»", Theme.BodySize + 0.75f, Theme.Ink); title.TextAlign = ContentAlignment.BottomLeft;
            string status; Color tone = Theme.Faint;
            if (sub.LastError != null) { status = "Ошибка: " + sub.LastError; tone = Theme.Danger; }
            else if (count == 0 && sub.UpdatedAt != null) status = "Подписка не даёт серверов: доступ выключен или истёк";
            else status = "Обновлено " + Updated(sub) + " · серверов: " + count + " · наборов: " + state.RuleSets.Count(r=>r.SubscriptionId==sub.Id)
                + (sub.ManagedDns == null ? "" : " · DNS администратора");
            Label line = MainForm.TextLabel(status, Theme.SmallSize, tone); line.TextAlign = ContentAlignment.TopLeft; line.AutoEllipsis = true;
            tips.SetToolTip(line, status); tips.SetToolTip(title, sub.Url.Substring(0, sub.Url.IndexOf("/sub/", StringComparison.Ordinal) + 5) + "…");
            Button refresh = MainForm.Button("Обновить", false), remove = MainForm.Button("Удалить подписку…", false);
            // Compact, vertically centred buttons: the row is a status line, not a toolbar.
            refresh.Dock = remove.Dock = DockStyle.None; refresh.Anchor = remove.Anchor = AnchorStyles.Right;
            refresh.Size = new Size(96, 32); remove.Size = new Size(150, 32);
            refresh.Margin = new Padding(0, 0, 8, 0); remove.Margin = new Padding(0); remove.ForeColor = Theme.Danger;
            refresh.Click += async delegate { if (host.PreviewOnly) return; refresh.Enabled = false; try { await host.RefreshSubscription(sub.Id); } finally { if (!refresh.IsDisposed) refresh.Enabled = true; } };
            remove.Click += delegate {
                if (MessageBox.Show(this, "Удалить подписку «" + sub.DisplayName + "» и её серверы с этого компьютера? Подключённый сервер останется до отключения.", "Подписка", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                host.RemoveSubscription(sub.Id);
            };
            row.Controls.Add(title, 0, 0); row.Controls.Add(line, 0, 1); row.Controls.Add(refresh, 1, 0); row.SetRowSpan(refresh, 2); row.Controls.Add(remove, 2, 0); row.SetRowSpan(remove, 2);
            return row;
        }
        static string Updated(Subscription sub) {
            DateTime when;
            if (sub.UpdatedAt == null || !DateTime.TryParse(sub.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out when)) return "—";
            return when.ToLocalTime().ToString("d MMMM, HH:mm", new System.Globalization.CultureInfo("ru-RU"));
        }
        internal ContextMenuStrip ServerMenuForTests(Server server) { return ServerMenuFor(server); }
        Control ServerRow(Server server, bool first, bool fastest) {
            RowPanel row = new RowPanel { Dock = DockStyle.Fill, Margin = new Padding(0), TopLine = first };
            TableLayoutPanel cells = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Theme.Ground, Margin = new Padding(0), Padding = new Padding(4, 0, 0, 0) };
            foreach (float w in new float[] { 30, 40 }) cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            cells.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            RadioDot radio = new RadioDot { Checked = server.Id == state.SelectedServerId, Anchor = AnchorStyles.None, Size = new Size(20, 20), Margin = new Padding(0) };
            radio.Click += delegate { if (!host.PreviewOnly) host.SwitchTo(server.Id, null); else { state.SelectServer(server.Id); } };
            tips.SetToolTip(radio, "Выбрать этот сервер");
            FlagView flag = new FlagView { Country = server.Country, FallbackName = server.Name, Anchor = AnchorStyles.None, Size = new Size(28, 28), Margin = new Padding(0) };
            string ping; host.Pings.TryGetValue(server.Id, out ping);
            Label name = MainForm.TextLabel(server.Name, Theme.BodySize + 0.75f, Theme.Ink), sub = MainForm.TextLabel(server.Username + (server.SubscriptionId != null ? " · из подписки" : "") + (ping == null ? "" : " · " + ping) + (fastest ? " · быстрее всех" : ""), Theme.SmallSize, fastest ? Theme.Protected : Theme.Faint);
            name.TextAlign = ContentAlignment.BottomLeft; sub.TextAlign = ContentAlignment.TopLeft;
            TableLayoutPanel text = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(6, 0, 0, 0), BackColor = Theme.Ground };
            text.RowStyles.Add(new RowStyle(SizeType.Percent, 52)); text.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            text.Controls.Add(name, 0, 0); text.Controls.Add(sub, 0, 1);
            tips.SetToolTip(name, server.Hostname); tips.SetToolTip(sub, server.Address);
            SegmentedControl transport = new SegmentedControl { Items = new[] { "HTTP/2", "HTTP/3" }, SelectedIndex = server.Protocol == "http3" ? 1 : 0, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 32, Margin = new Padding(0, 0, 8, 0) };
            transport.Enabled = !(host.VpnActive && server.Id == state.SelectedServerId);
            if (!transport.Enabled) tips.SetToolTip(transport, "Отключите VPN, чтобы сменить транспорт выбранного сервера.");
            transport.SelectedIndexChanged += delegate { SetProtocol(server.Id, transport.SelectedIndex == 1 ? "http3" : "http2"); };
            IconButton more = new IconButton { Glyph = IconGlyph.More, Anchor = AnchorStyles.None, Size = new Size(32, 32), Margin = new Padding(0) };
            tips.SetToolTip(more, "Переименовать, сменить флаг, проверить пинг, удалить");
            more.Click += delegate { ServerMenuFor(server).Show(more, new Point(0, more.Height)); };
            cells.Controls.Add(radio, 0, 0); cells.Controls.Add(flag, 1, 0); cells.Controls.Add(text, 2, 0); cells.Controls.Add(transport, 3, 0); cells.Controls.Add(more, 4, 0);
            row.Controls.Add(cells);
            return row;
        }
        ContextMenuStrip ServerMenuFor(Server server) {
            ContextMenuStrip menu = new ContextMenuStrip { BackColor = Theme.Card, ForeColor = Theme.Ink, ShowImageMargin = false, Renderer = new DarkMenuRenderer() };
            // Names and flags of subscription servers come from the main server.
            if (server.SubscriptionId == null) menu.Items.Add("Переименовать…", null, delegate { string name = InputDialog.Ask(this, "Название сервера", "Новое название (до 64 символов):", server.Name); if (name != null) { string error = RenameServer(server.Id, name); if (error != null) MessageBox.Show(this, error, "Название сервера", MessageBoxButtons.OK, MessageBoxIcon.Warning); } });
            if (server.SubscriptionId == null) menu.Items.Add("Сменить флаг…", null, delegate { string code; if (CountryPicker.Pick(this, server.Country, out code)) SetCountry(server.Id, code); });
            menu.Items.Add("Проверить пинг", null, async delegate { await Measure(server); RebuildServers(); });
            ToolStripItem delete = menu.Items.Add("Удалить…", null, delegate { DeleteServer(server); });
            delete.ForeColor = Theme.Danger;
            delete.Enabled = CanDelete(server, host.VpnActive);
            if (!delete.Enabled) delete.ToolTipText = "Отключите VPN, чтобы удалить выбранный сервер.";
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            return menu;
        }

        internal void SetProtocol(string id, string protocol) {
            Server server = state.Servers.First(s => s.Id == id);
            if (server.Protocol == protocol) return;
            server.Protocol = protocol; state.NotifyChanged(); host.SaveState();
        }
        // Null on success, otherwise the reason (shown to the user).
        internal string RenameServer(string id, string name) {
            Server server = state.Servers.First(s => s.Id == id);
            try { Server.ValidateName(name); } catch (FormatException error) { return error.Message; }
            server.Name = name.Trim(); state.NotifyChanged(); host.SaveState();
            return null;
        }
        internal void SetCountry(string id, string country) {
            Server server = state.Servers.First(s => s.Id == id);
            server.Country = country; state.NotifyChanged(); host.SaveState();
        }
        internal bool CanDelete(Server server, bool vpnActive) { return !(vpnActive && server.Id == state.SelectedServerId); }
        void DeleteServer(Server server) {
            if (!CanDelete(server, host.VpnActive)) return;
            if (MessageBox.Show(this, "Удалить сервер «" + server.Name + "» с этого компьютера? Доступ на сервере не удаляется.", "Удаление сервера", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            state.RemoveServer(server.Id); host.SaveState();
        }
        // The id with the smallest answered ping (negative = no answer), or null.
        internal static string FastestLabel(Dictionary<string, long> ms) {
            KeyValuePair<string, long>[] answered = ms.Where(p => p.Value >= 0).OrderBy(p => p.Value).ToArray();
            return answered.Length == 0 ? null : answered[0].Key;
        }
        async Task Measure(Server server) {
            if (host.PreviewOnly) return;
            try {
                LatencyResult result = await Latency.Measure(Profile.Deserialize(server.ToProfile().Serialize()));
                pingMs[server.Id] = result.Success ? result.Milliseconds : -1;
                host.Pings[server.Id] = result.Success ? result.Milliseconds + " мс" : "нет ответа";
            } catch (Exception error) { EngineLog.Write("ping " + server.Hostname + ": " + error.Message); }
        }
        // All servers in parallel when the page opens; the fastest gets «быстрее всех».
        async void MeasureAll() {
            if (host.PreviewOnly || state.Servers.Count == 0) return;
            await Task.WhenAll(state.Servers.ToList().Select(Measure));
            if (!IsDisposed && current == "servers") RebuildServers();
        }
        protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); int dark = 1; try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch (DllNotFoundException) { } }
        protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    }

    // Left tab of the settings window.
    internal sealed class NavItem : ThemedControl {
        bool selected;
        internal bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
        internal NavItem() { Font = Theme.Font(Theme.BodySize + 0.75f); Margin = new Padding(0, 0, 0, 2); }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            Rectangle box = new Rectangle(0, 1, Width - 1, Height - 3);
            if (selected || Hover) using (GraphicsPath path = Shapes.Round(box, Theme.Px(g, 9))) using (Brush fill = new SolidBrush(selected ? Theme.AccentSoft : Theme.Card)) g.FillPath(fill, path);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(box.X + (int)Theme.Px(g, 12), box.Y, box.Width, box.Height), selected ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (ShowFocus) using (Pen focus = new Pen(Theme.Accent)) using (GraphicsPath path = Shapes.Round(box, Theme.Px(g, 9))) g.DrawPath(focus, path);
        }
    }
    // Row with a hairline under it (and over the first row), no card.
    internal sealed class RowPanel : Panel {
        internal bool TopLine;
        internal RowPanel() { BackColor = Theme.Ground; Padding = new Padding(0, 1, 0, 1); SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs args) {
            base.OnPaint(args);
            using (Pen line = new Pen(Theme.Line)) { args.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1); if (TopLine) args.Graphics.DrawLine(line, 0, 0, Width, 0); }
        }
    }
    internal sealed class RadioDot : ThemedControl {
        bool isChecked;
        internal bool Checked { get { return isChecked; } set { isChecked = value; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs args) {
            Graphics g = Prepare(args);
            float d = Math.Min(Width, Height) - 4; RectangleF circle = new RectangleF((Width - d) / 2, (Height - d) / 2, d, d);
            using (Pen ring = new Pen(isChecked ? Theme.Accent : Theme.LineStrong, Theme.Px(g, 2))) g.DrawEllipse(ring, circle);
            if (isChecked) using (Brush dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, RectangleF.Inflate(circle, -d * 0.27f, -d * 0.27f));
            if (ShowFocus) using (Pen focus = new Pen(Theme.Accent)) g.DrawEllipse(focus, RectangleF.Inflate(circle, 2, 2));
        }
    }
    internal sealed class FlagView : Control {
        internal string Country, FallbackName;
        internal FlagView() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs args) { args.Graphics.Clear(Parent == null ? Theme.Ground : Parent.BackColor); args.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Flags.DrawBadge(args.Graphics, new Rectangle(1, 1, Width - 2, Height - 2), Country, FallbackName); }
    }

    // One-line text question with OK / Отмена; null when cancelled.
    internal static class InputDialog {
        internal static string Ask(IWin32Window owner, string title, string prompt, string value) {
            using (Form dialog = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Ground, ForeColor = Theme.Ink, Font = Theme.Font(Theme.BodySize), AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(400, 150), Icon = AppIcon.Shared }) {
                TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Gutter, 14, Theme.Gutter, 14), ColumnCount = 2, RowCount = 3, BackColor = Theme.Ground };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Label label = MainForm.TextLabel(prompt, Theme.SmallSize, Theme.Muted); layout.Controls.Add(label, 0, 0); layout.SetColumnSpan(label, 2);
                TextBox box = new TextBox { Text = value ?? "", Dock = DockStyle.Fill, BackColor = Theme.Card, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, MaxLength = 128, Margin = new Padding(0, 4, 0, 4) };
                layout.Controls.Add(box, 0, 1); layout.SetColumnSpan(box, 2);
                Button ok = MainForm.Button("Сохранить", true), cancel = MainForm.Button("Отмена", false);
                ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel; ok.Margin = new Padding(0, 8, 5, 0); cancel.Margin = new Padding(5, 8, 0, 0);
                layout.Controls.Add(ok, 0, 2); layout.Controls.Add(cancel, 1, 2);
                dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                return dialog.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
