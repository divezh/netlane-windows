using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using CancellationToken = System.Threading.CancellationToken;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Netlane {
    internal sealed class MainForm : Form, ISettingsHost {
        // Aliases kept for older layout code and controls; the values live in Theme.
        internal static readonly Color Ground = Theme.Ground, Surface = Theme.Card, Ink = Theme.Ink, Muted = Theme.Muted, Accent = Theme.Accent, Negative = Theme.Danger;
        // Main window (0.6): navigation on the left, one page on the right. «Подключение» holds the
        // state line, the locations table and the session; the settings pages and the engine log
        // open in the same window. Error and first launch are views of «Подключение».
        internal static readonly string[] NavIds = { "home", "rules", "servers", "connection", "log", "about" };
        static readonly string[] NavTitles = { "Подключение", "Правила", "Серверы", "Настройки", "Журнал", "О программе" };
        readonly DarkButton connectButton = (DarkButton)Button("Подключиться", true);
        // Account and feed line at the bottom of the navigation; a click refreshes the subscription.
        readonly SubscriptionRow subscriptionRow = new SubscriptionRow { Dock = DockStyle.Bottom, Height = 54, Margin = new Padding(0) };
        readonly StatusBlock statusBlock = new StatusBlock { Dock = DockStyle.Fill, Margin = new Padding(0), AlignLeft = true };
        readonly SpeedGraph speedGraph = new SpeedGraph { Dock = DockStyle.Top, Height = 78, Margin = new Padding(0) };
        readonly LocationsTable table = new LocationsTable { Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly TryCard tryCard = new TryCard { Dock = DockStyle.Top, Height = 104, Margin = new Padding(0) };
        readonly LogPreview logPreview = new LogPreview { Dock = DockStyle.Top, Height = 96, Margin = new Padding(0) };
        readonly DropZone dropZone = new DropZone { Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly StepsRow steps = new StepsRow { Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly TableLayoutPanel normalView, emptyView, topRow;
        readonly Panel side, pageHost, homePage, sessionPane, errorPane, emptyHost;
        readonly List<NavItem> nav = new List<NavItem>();
        Label footerNote;
        readonly Label clock = ValueLabel(), speed = ValueLabel(), sent = ValueLabel(), received = ValueLabel(), dnsValue = ValueLabel();
        readonly Label graphPeak = TextLabel("", Theme.CaptionSize, Theme.Faint);
        readonly Label errorDetail = TextLabel("", Theme.BodySize, Theme.Muted), emptyTitle = TextLabel("", 20f, Theme.Ink), emptySub = TextLabel("", Theme.BodySize, Theme.Muted), emptyHint = TextLabel("Один сервер по ссылке tt:// тоже подойдёт", Theme.CaptionSize, Theme.Faint);
        readonly DarkButton transportButton = (DarkButton)Button("Сменить транспорт…", false), emptyPrimary = (DarkButton)Button("Вставить ссылку", true), emptySecondary = (DarkButton)Button("Открыть файл TOML…", false);
        readonly DarkButton setButton = (DarkButton)Button("", false);
        // How sites see this computer (PublicIp), and the network block shown while disconnected.
        readonly Label ipValue = ValueLabel(), idleHint = TextLabel("", Theme.SmallSize, Theme.Faint), lastSession = TextLabel("", Theme.SmallSize, Theme.Muted);
        // Under the big button: «В сети 00:42:17 · ↓ 1,8 ГБ · ↑ 210 МБ», or the last session while disconnected.
        readonly Label heroLine = TextLabel("", Theme.SmallSize, Theme.Muted);
        Label sessionCaption;
        Control[] liveOnly, idleOnly;
        PublicIp publicIp;
        bool ipThroughVpn, ipChecking, ipFailed;
        int ipGeneration;
        // «Авто»: the transport being tried for this start, whether the other one was tried already, and a watch.
        string autoAttempt;
        bool autoFellBack;
        int autoGeneration;
        readonly Timer pingNoteTimer = new Timer { Interval = 30 * 1000 };
        DateTime lastPingCheck = DateTime.MinValue;
        readonly MiniSwitch killSwitch = new MiniSwitch { Anchor = AnchorStyles.Right, Margin = new Padding(0), AccessibleName = "Kill Switch" };
        bool refreshingKill;
        // The selected server's transport, the same setting as on «Серверы»; changing it while connected reconnects.
        readonly SegmentedControl transportSwitch = new SegmentedControl { Items = new[] { "Авто", "HTTP/2", "HTTP/3" }, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 28, Margin = new Padding(0, 2, 0, 2), AccessibleName = "Транспорт" };
        internal const string TransportHint = "Авто: сначала HTTP/3, а если сеть его не пропускает — HTTP/2; удачный запоминается. HTTP/3 (QUIC, UDP) ровнее для звонков и видео, HTTP/2 (TCP) — если сеть режет UDP. Во время работы VPN переключение переподключает его.";
        string currentPage = "home";
        SettingsForm settings;
        LogPage logPage;
        ConnectionView view = ConnectionView.Idle;
        string stateTitle, stateNote;
        TrafficMeter traffic;
        readonly IConnection connection;
        readonly Dictionary<string, string> pings = new Dictionary<string, string>();
        internal const string KillSwitchSwitchHint = "При переключении VPN перезапускается: на 1–3 с трафик может пойти напрямую.";
        bool switching, switchPending;
        string pendingServer, pendingSet;
        Task switchLoop = Task.FromResult(0);
        readonly NotifyIcon tray;
        readonly TrayController trayMenu;
        bool announceSwitch, stateLoadFailed;
        readonly Icon appIcon = AppIcon.ForSize(32), trayIcon = AppIcon.ForSize(16);
        readonly Bitmap brandMark = AppIcon.Mark(64);
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        readonly string dataDirectory;
        readonly ClientState state;
        readonly bool previewOnly;
        bool busy, exiting, failed;
        DateTime connectedAt;
        readonly Timer timer = new Timer { Interval = 1000 };
        bool measuring;
        int latencyGeneration, tunnelCheck, retrySeconds, reconnectAttempts;
        string tunnelWarning, pingText;
        DateTime lastProbe = DateTime.MinValue;

        internal ClientState State { get { return state; } }
        ClientState ISettingsHost.State { get { return state; } }
        void ISettingsHost.SaveState() { SaveState(); }
        bool ISettingsHost.SaveDnsServers(List<string> servers) {
            List<string> previous=state.DnsServers;
            try {
                state.DnsServers=DnsSettings.Normalize(servers);
                if(state.SelectedServer!=null)state.SessionProfile().Serialize();
                if(!previewOnly)StateStore.Save(dataDirectory,state);
            } catch(Exception error) {
                state.DnsServers=previous;
                if(error is FormatException)throw;
                EngineLog.Write("DNS settings save failed: "+error.Message);return false;
            }
            state.NotifyChanged();return true;
        }
        Task ISettingsHost.SwitchTo(string serverId, string ruleSetId) { return SwitchTo(serverId, ruleSetId); }
        bool ISettingsHost.VpnActive { get { return connection.Active; } }
        void ISettingsHost.ImportLink() { ImportLink(); }
        void ISettingsHost.ImportFile() { ImportFile(); }
        Dictionary<string, string> ISettingsHost.Pings { get { return pings; } }
        bool ISettingsHost.PreviewOnly { get { return previewOnly; } }
        Task ISettingsHost.RefreshSubscription(string id) { return RefreshSubscription(id); }
        void ISettingsHost.RemoveSubscription(string id) { state.RemoveSubscription(id, ConnectedServerId); SaveState(); RefreshState(); }
        async Task<string> ISettingsHost.CheckForUpdates() {
            if (previewOnly) return "В предпросмотре обновления не проверяются.";
            string problem = null;
            try { remoteUpdate = await UpdateFeed.FetchAsync(); }
            catch (FormatException error) { problem = error.Message; }
            catch (Exception error) { problem = "Не удалось проверить обновления: " + error.Message; }
            await RefreshOnEntry();
            RefreshFooter();
            UpdateManifest update = AvailableUpdate();
            if (update != null) return "Доступна версия " + update.Version + (String.IsNullOrEmpty(update.Url) ? "." : ". Скачать: " + update.Url);
            if (problem != null) return problem;
            return "Установлена последняя версия.";
        }
        void ISettingsHost.RulesChanged(string ruleSetId) { if (ruleSetId == state.SelectedRuleSet.Id && connection.Active) { reapplyPending = true; SwitchTo(null, null); } RefreshState(); }
        bool reapplyPending;
        static bool IsSettingsPage(string page) { return Array.IndexOf(SettingsForm.PageIds, page) >= 0; }
        internal string PageForTests { get { return currentPage; } }
        // One window: a navigation item shows its page on the right. A page with unsaved changes may refuse to leave.
        internal void Navigate(string page) {
            if (Array.IndexOf(NavIds, page) < 0) page = "home";
            if (page == currentPage) return;
            if (IsSettingsPage(page)) {
                if (settings == null) { settings = new SettingsForm(this, true); pageHost.Controls.Add(settings); settings.Show(); }
                settings.ShowPage(page);
                if (settings.CurrentPage != page) return;
            } else if (IsSettingsPage(currentPage) && settings != null && !settings.TryLeave()) return;
            if (page == "log") {
                if (logPage == null) { logPage = new LogPage(previewOnly); pageHost.Controls.Add(logPage); }
                logPage.Reload();
            }
            currentPage = page;
            homePage.Visible = page == "home";
            if (settings != null) settings.Visible = IsSettingsPage(page);
            if (logPage != null) logPage.Visible = page == "log";
            foreach (NavItem item in nav) item.Selected = (string)item.Tag == page;
        }
        // Settings are pages of the main window now; the name stays for the tray and older callers.
        internal void OpenSettings(string page) { Navigate(page ?? "servers"); }
        internal ConnectionView ViewForTests { get { return view; } }
        Server Selected { get { return state.SelectedServer; } }

        internal MainForm(bool preview, string page = null, ConnectionView? previewState = null, string previewHost = null, IConnection connectionOverride = null) {
            EngineLog.Write("startup: form constructor begin");
            previewOnly = preview;
            connection = connectionOverride ?? new Connection();
            Text = Program.ProductName; BackColor = Theme.Ground; ForeColor = Theme.Ink; Font = Theme.Font(Theme.BodySize);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            // The default window is 920 × 560 (outer size) and never narrower than both columns need.
            MinimumSize = new Size(860, 520); Size = new Size(920, 560); StartPosition = FormStartPosition.CenterScreen;
            Icon = appIcon;
            dataDirectory = ProfileStore.DataDirectory;
            string notice = null;
            if (preview) state = PreviewState(page, previewHost);
            else { try { state = StateStore.LoadOrMigrate(dataDirectory, out notice); } catch (Exception error) { EngineLog.Write("state load failed: " + error.Message); state = ClientState.CreateEmpty(); notice = "Не удалось прочитать настройки. Добавьте сервер заново; файл state.bin не тронут."; } }

            // Navigation: the pages on top, the account with its subscription at the bottom.
            side = new Panel { Dock = DockStyle.Left, Width = 196, BackColor = Theme.Ground, Padding = new Padding(8, 10, 9, 6) };
            side.Paint += delegate(object sender, PaintEventArgs args) { using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, side.Width - 1, 0, side.Width - 1, side.Height); };
            side.Controls.Add(subscriptionRow);
            for (int i = NavIds.Length - 1; i >= 0; i--) {
                string id = NavIds[i];
                NavItem item = new NavItem { Text = NavTitles[i], Dock = DockStyle.Top, Height = 36, Tag = id, Glyph = id };
                item.Click += delegate { Navigate(id); };
                nav.Insert(0, item); side.Controls.Add(item);
            }
            nav[0].Selected = true;

            pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0) };
            homePage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Padding = new Padding(24, 16, 24, 8) };

            // Hero of the left column: the state large and centred, the one big action, the session line.
            topRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 176, ColumnCount = 1, RowCount = 4, Margin = new Padding(0), BackColor = Theme.Ground };
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 66, 54, 26, 30 }) topRow.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            statusBlock.TitleSize = 20f; statusBlock.AlignLeft = false; statusBlock.Dock = DockStyle.Fill; statusBlock.Margin = new Padding(0, 8, 0, 0);
            connectButton.Dock = DockStyle.None; connectButton.Anchor = AnchorStyles.None; connectButton.Size = new Size(240, 44); connectButton.Margin = new Padding(0);
            connectButton.Font = Theme.Font(Theme.BodySize + 1f, true); connectButton.ForeColor = Theme.Ink;
            heroLine.Dock = DockStyle.Fill; heroLine.TextAlign = ContentAlignment.MiddleCenter; heroLine.Margin = new Padding(0);
            topRow.Controls.Add(statusBlock, 0, 0); topRow.Controls.Add(connectButton, 0, 1); topRow.Controls.Add(heroLine, 0, 2);

            // Left column: the hero, then the locations (the selected one is the current server).
            Panel locationsPane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0) };
            locationsPane.Controls.Add(table); locationsPane.Controls.Add(topRow);

            // Right column: the session, then the route and protection of the next connection.
            sessionPane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0) };
            setButton.Margin = new Padding(0, 3, 0, 3); setButton.Chevron = ChevronDirection.Down; setButton.TextAlign = ContentAlignment.MiddleLeft; setButton.Glyph = ButtonGlyph.None;
            setButton.AccessibleName = "Набор правил";
            // DNS is a link to its settings: the only row here that is changed elsewhere.
            dnsValue.Cursor = Cursors.Hand; dnsValue.ForeColor = Theme.Accent; dnsValue.AccessibleName = "DNS";
            dnsValue.Click += delegate { OpenSettings("connection"); };
            tips.SetToolTip(dnsValue, "Настроить DNS");
            // Connected: the session with the public IP; disconnected: «Сеть» — the IP and the last session.
            sessionCaption = SectionCaption("Подключение");
            Control ipRow = Lined(Pair("Ваш IP", ipValue, 36, 200)), graphGap = Spacer(14);
            idleHint.Dock = DockStyle.Top; idleHint.Height = 40; idleHint.AutoEllipsis = false; idleHint.TextAlign = ContentAlignment.MiddleLeft;
            ipValue.Cursor = Cursors.Hand; ipValue.AccessibleName = "Ваш IP";
            ipValue.Click += delegate { if (!previewOnly) CheckIp(0); };
            liveOnly = new[] { graphGap, (Control)speedGraph };
            idleOnly = new[] { (Control)idleHint };
            Control[] session = {
                sessionCaption, Spacer(2),
                ipRow, Pair("Набор правил", setButton, 42, 160), Pair("Транспорт", transportSwitch, 40, 196), Lined(Pair("DNS", dnsValue, 36, 160)), Pair("Kill Switch", killSwitch, 36, 60),
                idleHint, graphGap, speedGraph
            };
            for (int i = session.Length - 1; i >= 0; i--) sessionPane.Controls.Add(session[i]);

            // Error: the reason, what to try, the engine tail and the way to the transport setting.
            errorPane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0), Visible = false };
            errorDetail.Dock = DockStyle.Top; errorDetail.Height = 74; errorDetail.TextAlign = ContentAlignment.TopLeft; errorDetail.AutoEllipsis = false; errorDetail.Padding = new Padding(0, 8, 0, 0);
            transportButton.Dock = DockStyle.Top; transportButton.Height = 34; transportButton.Margin = new Padding(0);
            Control[] trouble = { SectionCaption("Что случилось"), errorDetail, Spacer(6), tryCard, Spacer(8), logPreview, Spacer(8), transportButton };
            for (int i = trouble.Length - 1; i >= 0; i--) errorPane.Controls.Add(trouble[i]);
            Panel rightPane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0), Padding = new Padding(24, 0, 0, 0) };
            rightPane.Controls.Add(sessionPane); rightPane.Controls.Add(errorPane);

            normalView = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Ground, Margin = new Padding(0) };
            normalView.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); normalView.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            normalView.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            normalView.Controls.Add(locationsPane, 0, 0); normalView.Controls.Add(rightPane, 1, 0);
            // Both columns sit in the middle of the page when it is taller than what they hold.
            normalView.Resize += delegate { CenterHome(); };
            homePage.VisibleChanged += delegate { CenterHome(); };

            // First launch: one primary action, a drop target and three steps.
            emptyView = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, BackColor = Theme.Ground, Margin = new Padding(0), Visible = false };
            emptyView.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 64, 150, 10, 44, 8, 38, 10, 68, 4, 24 }) emptyView.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            emptyView.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel intro = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), BackColor = Theme.Ground };
            intro.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            emptyTitle.Text = "Подключите первый сервер"; emptyTitle.Font = Theme.Font(17f, true); emptyTitle.TextAlign = ContentAlignment.MiddleLeft; emptyTitle.Margin = new Padding(0);
            emptySub.Text = "Ссылку выдаёт администратор сервера."; emptySub.TextAlign = ContentAlignment.TopLeft; emptySub.Margin = new Padding(0);
            intro.Controls.Add(emptyTitle, 0, 0); intro.Controls.Add(emptySub, 0, 1);
            emptyView.Controls.Add(intro, 0, 0);
            dropZone.Margin = new Padding(0, 6, 0, 0); emptyView.Controls.Add(dropZone, 0, 1);
            emptyPrimary.Margin = new Padding(0); emptyView.Controls.Add(emptyPrimary, 0, 3);
            emptySecondary.Margin = new Padding(0); emptyView.Controls.Add(emptySecondary, 0, 5);
            emptyView.Controls.Add(steps, 0, 7);
            emptyHint.TextAlign = ContentAlignment.MiddleCenter; emptyHint.Margin = new Padding(0); emptyView.Controls.Add(emptyHint, 0, 9);
            emptyHost = new Panel { Dock = DockStyle.Left, Width = 440, BackColor = Theme.Ground, Margin = new Padding(0), Visible = false };
            emptyView.Visible = true; emptyHost.Controls.Add(emptyView);

            // The version sits in the footer; the same line carries a way to the engine log when
            // something went wrong, an update offer when there is one, and the encryption cue otherwise.
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 31, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0, 9, 0, 0), BackColor = Theme.Ground };
            // A hairline keeps the version line apart from «Проверить пинг» above it.
            footer.Paint += delegate(object sender, PaintEventArgs args) { using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, 0, 4, footer.Width, 4); };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label version = new Label { Text = "v" + Program.Version, AutoSize = true, Font = Theme.Font(Theme.CaptionSize), ForeColor = Theme.Faint, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            footerNote = new Label { Text = "Профиль зашифрован Windows", AutoSize = true, Font = Theme.Font(Theme.CaptionSize), ForeColor = Theme.Faint, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            footer.Controls.Add(version, 0, 0); footer.Controls.Add(footerNote, 2, 0);

            homePage.Controls.Add(normalView); homePage.Controls.Add(emptyHost); homePage.Controls.Add(footer);
            pageHost.Controls.Add(homePage);
            Shown += delegate { EngineLog.Write("startup: shown in " + startupClock.ElapsedMilliseconds + " ms"); if (!previewOnly && publicIp == null && !ipChecking) CheckIp(0); };
            Controls.Add(pageHost); Controls.Add(side);
            connectButton.Click += async delegate { if (previewOnly) return; if (connection.Active) await Disconnect(); else await Connect(); };
            // The transport of a server is chosen in its row on «Серверы».
            transportButton.Click += delegate { OpenSettings("servers"); };
            emptyPrimary.Click += delegate { if (!previewOnly) PasteFromClipboard(); };
            emptySecondary.Click += delegate { if (!previewOnly) ImportFile(); };
            table.PingRequested += async delegate { if (!previewOnly) await CheckPings(); };
            table.AddRequested += delegate { if (!previewOnly) ImportLink(); };
            pingNoteTimer.Tick += delegate { RefreshPingNote(); }; pingNoteTimer.Start();
            dropZone.PickRequested += delegate { if (!previewOnly) PasteFromClipboard(); };
            logPreview.CopyRequested += delegate { CopyEngineLog(); };
            table.Picked += delegate(string id) { if (!previewOnly) SwitchTo(id, null); };
            setButton.Click += delegate { OpenRoutingMenu(); };
            transportSwitch.SelectedIndexChanged += delegate { if (!refreshingKill) SetTransport(TransportChoices[transportSwitch.SelectedIndex]); };
            killSwitch.CheckedChanged += delegate { if (refreshingKill) return; state.KillSwitch = killSwitch.Checked; state.NotifyChanged(); SaveState(); };
            emptyPrimary.Glyph = ButtonGlyph.Clipboard;
            emptySecondary.Glyph = ButtonGlyph.None;
            transportButton.Glyph = ButtonGlyph.None;
            subscriptionRow.SubscriptionRefreshRequested += async delegate { if (!previewOnly) await RefreshSelectedSubscription(); };
            footerNote.Click += delegate {
                UpdateManifest offer = footerNote.Tag as UpdateManifest;
                if (offer != null) { ShowUpdateDialog(offer); return; }
                if (footerNote.Cursor == Cursors.Hand) CopyEngineLog();
            };
            tips.SetToolTip(killSwitch, "Блокировать интернет, если VPN оборвался. Применяется при следующем подключении.");
            tips.SetToolTip(subscriptionRow, "Обновить подписку");
            tips.OwnerDraw=true;
            tips.Draw+=delegate(object sender,DrawToolTipEventArgs e){using(Brush fill=new SolidBrush(Theme.Raised))e.Graphics.FillRectangle(fill,e.Bounds);using(Pen edge=new Pen(Theme.LineStrong))e.Graphics.DrawRectangle(edge,0,0,e.Bounds.Width-1,e.Bounds.Height-1);Rectangle text=e.Bounds;text.Inflate(-4,-3);TextRenderer.DrawText(e.Graphics,e.ToolTipText,e.Font,text,Theme.Ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);};
            connection.Changed += OnState;
            state.Changed += delegate { if (!IsDisposed) { RefreshState(); if (trayMenu != null) trayMenu.Rebuild(); } };

            tray = new NotifyIcon { Icon = trayIcon, Text = Program.ProductName + " · Не подключено", Visible = !preview }; tray.DoubleClick += delegate { Restore(); };
            trayMenu = new TrayController(tray, state, () => connection.Active, () => Visible && WindowState != FormWindowState.Minimized);
            trayMenu.ToggleRequested += async delegate { if (previewOnly) return; if (connection.Active) await Disconnect(); else await Connect(); };
            trayMenu.ServerPicked += delegate(string id) { if (previewOnly) return; announceSwitch = connection.Active; SwitchTo(id, null); };
            trayMenu.RuleSetPicked += delegate(string id) { if (previewOnly) return; announceSwitch = connection.Active; SwitchTo(null, id); };
            trayMenu.OpenRequested += delegate { Restore(); };
            trayMenu.SettingsRequested += delegate { Restore(); OpenSettings(null); };
            trayMenu.ExitRequested += async delegate { await Exit(); };
            trayMenu.Rebuild();
            Resize += delegate { if (!preview && WindowState == FormWindowState.Minimized && state.CloseToTray) { Hide(); trayMenu.WindowHidden(); } };
            FormClosing += async delegate(object sender, FormClosingEventArgs args) {
                if (exiting || preview) return;
                args.Cancel = true;
                if (CloseDecision(state.CloseToTray, args.CloseReason) == CloseAction.Hide) { Hide(); trayMenu.WindowHidden(); }
                else if (!IsSettingsPage(currentPage) || settings == null || settings.TryLeave()) await Exit();
            };
            timer.Tick += async delegate { OnTimerTick(); if (Visible && !previewOnly && (DateTime.UtcNow - lastProbe).TotalSeconds >= 60) await ProbeLatency(); };
            // The startup entry refreshes subscriptions and then measures every server.
            SettingsForm.AcceptDrops(this, delegate(string file) {
                if (previewOnly) return;
                if (SettingsForm.DropKind(file) == "server") ImportTomlFile(file);
                else { Navigate("rules"); if (settings != null && currentPage == "rules") settings.Drop(file); }
            });

            RefreshState();
            stateLoadFailed = notice != null;
            if (notice != null) Error(notice);
            else ApplyView(Selected == null ? ConnectionView.NoProfile : ConnectionView.Idle, null, "");
            if (previewState.HasValue) ShowPreviewState(previewState.Value);
            if (IsSettingsPage(page) || page == "log") Shown += delegate { Navigate(page); };
            if (page == "account-servers") Shown += delegate { Navigate("servers"); };
            startupReady = true;
            EngineLog.Write("startup: form ready in " + startupClock.ElapsedMilliseconds + " ms");
            QueueStartup();
        }

        // Demo data for --preview / --render: servers, the built-in set and «Мои правила».
        static ClientState PreviewState(string page, string host) {
            ClientState demo = ClientState.CreateEmpty();
            if (page == "no-servers") return demo;
            // A subscription whose access ended: the panel hands out no servers and says why.
            if (page == "expired") {
                Subscription gone = demo.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/demo");
                gone.Name = "\U0001F1F3\U0001F1F1 Amsterdam"; gone.UpdatedAt = DateTime.UtcNow.AddMinutes(-3).ToString("o");
                gone.AccountStatus = "expired"; gone.ExpiresAt = DateTime.UtcNow.AddDays(-2).ToString("o");
                return demo;
            }
            string sample = host ?? "amsterdam.example.com";
            Server main = Server.FromProfile(Profile.Import("hostname = \"" + sample + "\"\naddresses = [\"" + sample + ":443\"]\nusername = \"adm\"\npassword = \"example-password-123\"\n"));
            Server second = Server.FromProfile(Profile.Import("hostname = \"frankfurt.example.com\"\naddresses = [\"frankfurt.example.com:443\"]\nusername = \"admin\"\npassword = \"example-password-123\"\nupstream_protocol = \"http3\"\n"));
            main.Name = host ?? "Амстердам"; main.Country = "NL"; second.Name = "Франкфурт"; second.Country = "DE";
            demo.Servers.Add(main); demo.SelectedServerId = main.Id;
            if (page != "one-server") demo.Servers.Add(second);
            string[] extraNames = { "Варшава", "Токио", "Нью-Йорк", "Сингапур" };
            string[] extraCodes = { "PL", "JP", "US", "SG" };
            if (page == "three-servers" || page == "six-servers") {
                for (int i = 0; i < 1; i++) {
                    Server item = Server.FromProfile(Profile.Import("hostname = \"warsaw.example.com\"\naddresses = [\"warsaw.example.com:443\"]\nusername = \"adm\"\npassword = \"example-password-123\"\n"));
                    item.Name = extraNames[i]; item.Country = extraCodes[i]; demo.Servers.Add(item);
                }
            }
            if (page == "six-servers") {
                for (int i = 1; i < 4; i++) {
                    Server item = Server.FromProfile(Profile.Import("hostname = \"" + (i == 1 ? "tokyo" : i == 2 ? "newyork" : "singapore") + ".example.com\"\naddresses = [\"" + (i == 1 ? "tokyo" : i == 2 ? "newyork" : "singapore") + ".example.com:443\"]\nusername = \"adm\"\npassword = \"example-password-123\"\n"));
                    item.Name = extraNames[i]; item.Country = extraCodes[i]; demo.Servers.Add(item);
                }
            }
            if (page == "ten-servers") {
                string[] names = { "Варшава", "Токио", "Нью-Йорк", "Сингапур", "Лондон", "Париж", "Мадрид", "Стокгольм" };
                string[] codes = { "PL", "JP", "US", "SG", "GB", "FR", "ES", "SE" };
                string[] hosts = { "warsaw", "tokyo", "newyork", "singapore", "london", "paris", "madrid", "stockholm" };
                for (int i = 0; i < names.Length; i++) {
                    Server item = Server.FromProfile(Profile.Import("hostname = \"" + hosts[i] + ".example.com\"\naddresses = [\"" + hosts[i] + ".example.com:443\"]\nusername = \"adm\"\npassword = \"example-password-123\"\n"));
                    item.Name = names[i]; item.Country = codes[i]; demo.Servers.Add(item);
                }
            }
            RuleSet mine = demo.AddRuleSet("Мои правила", new Routing { Mode = "selective", Destinations = new List<string> { "mos.ru", "*.mos.ru" } });
            demo.SelectedRuleSetId = mine.Id;
            if (page == "many-sets" || page == "routing-menu") foreach (string name in new[] { "Офис", "Стриминг", "Банки", "Игры" }) demo.AddRuleSet(name, new Routing());
            // A feed refreshed a couple of hours ago: the subscription line needs a demo state to be seen.
            if (page == "subscribed" || page == "ten-servers" || page == "account" || page == "account-servers") {
                Subscription sub = demo.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/demo");
                sub.Name = "\U0001F1F3\U0001F1F1 Amsterdam";
                sub.UpdatedAt = DateTime.UtcNow.AddHours(-2).ToString("o");
                if (page == "account" || page == "account-servers") {
                    sub.AccountStatus = "active"; sub.ExpiresAt = DateTime.UtcNow.Date.AddDays(12).ToString("o");
                    sub.QuotaBytes = 50L << 30; sub.QuotaUsedBytes = (long)(12.4 * (1L << 30)); sub.QuotaMonth = DateTime.UtcNow.ToString("yyyy-MM");
                }
                foreach (Server item in demo.Servers) item.SubscriptionId = sub.Id;
            }
            return demo;
        }

        internal static Label TextLabel(string text, float size, Color color) { return new Label { Text = text, Font = Theme.Font(size), ForeColor = color, Dock = DockStyle.Fill, Margin = new Padding(0), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }; }
        // Right-aligned value of a «caption … value» row.
        static Label ValueLabel() { Label label = TextLabel("", Theme.BodySize, Theme.Ink); label.TextAlign = ContentAlignment.MiddleRight; return label; }
        static LinkLabel ActionLink(string text) {
            return new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 18, 0), Font = Theme.Font(Theme.SmallSize), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Accent, DisabledLinkColor = Theme.Faint, LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Theme.Ground };
        }
        static Panel Spacer(int height) { return new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Ground, Margin = new Padding(0) }; }
        // Small caption with a hairline under it: the heading of a group of rows.
        static Label SectionCaption(string text) {
            Label label = TextLabel(text, Theme.CaptionSize, Theme.Faint); label.Dock = DockStyle.Top; label.Height = 28;
            label.Paint += delegate(object sender, PaintEventArgs args) { using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, 0, label.Height - 1, label.Width, label.Height - 1); };
            return label;
        }
        // The empty space of a tall window goes above and below the content instead of only below it.
        internal void CenterHome() {
            if (normalView == null || liveOnly == null) return;
            // The tallest of the right column's states, so connecting or failing never moves the page.
            int shared = sessionPane.Controls.Cast<Control>().Where(c => !liveOnly.Contains(c) && !idleOnly.Contains(c)).Sum(c => c.Height);
            int rightNeed = Math.Max(shared + Math.Max(liveOnly.Sum(c => c.Height), idleOnly.Sum(c => c.Height)), errorPane.Controls.Cast<Control>().Sum(c => c.Height));
            int need = Math.Max(topRow.Height + table.ContentHeight, rightNeed);
            int top = Math.Max(0, (normalView.Height - need) / 2);
            if (normalView.Padding.Top != top) normalView.Padding = new Padding(0, top, 0, 0);
        }
        internal int HomeTopForTests { get { return normalView.Padding.Top; } }
        // A row of the «Подключение» list: a hairline under it, as in a settings list.
        static Control Lined(TableLayoutPanel row) {
            row.Paint += delegate(object sender, PaintEventArgs args) { using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, 0, row.Height - 1, row.Width, row.Height - 1); };
            return row;
        }
        static TableLayoutPanel Pair(string caption, Control value, int height, int valueWidth) {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Top, Height = height, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Theme.Ground };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, valueWidth));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.Controls.Add(TextLabel(caption, Theme.BodySize, Theme.Muted), 0, 0); row.Controls.Add(value, 1, 0);
            return row;
        }
        internal static Button Button(string text, bool primary) {
            Button button = new DarkButton { Kind = primary ? ButtonKind.Primary : ButtonKind.Secondary, Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = primary ? Ink : Surface, ForeColor = primary ? Ground : Ink, Margin = new Padding(0, 0, 0, 6), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = Theme.LineStrong; button.FlatAppearance.BorderSize = primary ? 0 : 1; return button;
        }

        IEnumerable<Server> VisibleLocations {
            get {
                Server server = Selected;
                if (server == null || server.SubscriptionId == null) return state.Servers;
                return state.Servers.Where(x => x.SubscriptionId == server.SubscriptionId);
            }
        }

        // Puts the stored state on screen: the locations, the account line and the route rows.
        internal void RefreshState() {
            Server server = Selected;
            bool hasServer = server != null;
            table.SetServers(VisibleLocations.ToList(), state.SelectedServerId, pings);
            Subscription selectedSubscription = server == null ? null : state.Subscriptions.FirstOrDefault(sub => sub.Id == server.SubscriptionId);
            subscriptionRow.Visible = hasServer;
            subscriptionRow.AccountName = server == null ? "" : server.Username;
            subscriptionRow.ServerCount = server == null ? 0 : VisibleLocations.Count();
            subscriptionRow.SubscriptionName = selectedSubscription == null ? "" : selectedSubscription.DisplayName;
            subscriptionRow.Subscription = selectedSubscription;
            subscriptionRow.Height = subscriptionRow.AccountText == null ? 54 : 70;
            // The first-run page: a subscription that stopped giving servers says why.
            Subscription blockedFeed = state.Servers.Count == 0 ? state.Subscriptions.FirstOrDefault(Account.Blocked) : null;
            emptyTitle.Text = blockedFeed == null ? "Подключите первый сервер" : "Подписка не даёт серверов";
            string blockedReason = blockedFeed == null ? null : Account.Reason(blockedFeed);
            emptySub.Text = blockedReason == null ? "Ссылку выдаёт администратор сервера." : Char.ToUpper(blockedReason[0]) + blockedReason.Substring(1) + " — обратитесь к администратору.";
            tips.SetToolTip(emptySub, blockedFeed == null ? null : "Подписка «" + blockedFeed.DisplayName + "»");
            emptySub.ForeColor = blockedFeed == null ? Theme.Muted : Theme.Warning;
            string accessDetails = Account.Details(selectedSubscription);
            tips.SetToolTip(subscriptionRow, selectedSubscription == null ? "Сервер добавлен вручную." : subscriptionRow.ActionText + (accessDetails == null ? "" : "\n" + accessDetails));
            setButton.Text = state.SelectedRuleSet.DisplayName;
            dnsValue.Text = selectedSubscription != null && selectedSubscription.ManagedDns != null ? "от администратора" : state.DnsServers != null && state.DnsServers.Count > 0 ? "личный" : "стандартный";
            refreshingKill = true;
            try { killSwitch.Checked = state.KillSwitch; if (Selected != null) transportSwitch.SelectedIndex = TransportIndex(Selected); } finally { refreshingKill = false; }
            tips.SetToolTip(transportSwitch, TransportHint);
            tips.SetToolTip(setButton, state.KillSwitch ? KillSwitchSwitchHint : "Набор правил маршрутизации. Переключение во время работы VPN переподключает его.");
            UpdateStatusText();
            ControlsEnabled();
            ShowView();
            if (!previewOnly && !state.HidePublicIp && publicIp == null && !ipChecking && !ipFailed && IsHandleCreated) CheckIp(0);
            WarnAccess();
            RefreshFooter();
        }
        // Views of «Подключение»: first launch instead of the columns, the error pane instead of the session.
        void ShowView() {
            bool error = view == ConnectionView.Error;
            bool empty = Selected == null && !error;
            normalView.Visible = !empty;
            emptyHost.Visible = empty;
            sessionPane.Visible = !error;
            errorPane.Visible = error;
            bool live = Live;
            foreach (Control c in liveOnly) c.Visible = live;
            foreach (Control c in idleOnly) c.Visible = !live;
            RefreshNetwork();
            CenterHome();
        }
        // The engine runs (or is stopping): the session block; otherwise the «Сеть» block.
        bool Live { get { return view == ConnectionView.Connected || view == ConnectionView.Reconnecting || view == ConnectionView.KillSwitch || view == ConnectionView.Disconnecting; } }

        void RefreshNetwork() {
            string tip;
            if (state.HidePublicIp) { ipValue.Text = "не проверяется"; ipValue.ForeColor = Theme.Faint; tip = "Проверка внешнего IP выключена в «Настройках»."; }
            else if (Live && view != ConnectionView.Connected) { ipValue.Text = Blank; ipValue.ForeColor = Theme.Ink; tip = ""; }
            else if (Live && state.SelectedRuleSet.Routing.Mode != "general") { ipValue.Text = "только для «" + state.SelectedRuleSet.DisplayName + "»"; ipValue.ForeColor = Theme.Muted; tip = "Через VPN идут только адреса набора «" + state.SelectedRuleSet.DisplayName + "»: эти сайты видят IP сервера, остальные — ваш обычный IP."; }
            else if (ipChecking || (publicIp != null && ipThroughVpn != (view == ConnectionView.Connected))) { ipValue.Text = "проверяю…"; ipValue.ForeColor = Theme.Faint; tip = ""; }
            else if (publicIp == null) { ipValue.Text = ipFailed ? "не удалось проверить" : Blank; ipValue.ForeColor = Theme.Faint; tip = ipFailed ? "Щёлкните, чтобы проверить ещё раз." : ""; }
            else { ipValue.Text = publicIp.Text; ipValue.ForeColor = Theme.Ink; tip = (ipThroughVpn ? "Так вас видят сайты: адрес VPN-сервера." : "Так вас видят сайты: VPN выключен.") + "\nПроверено через cloudflare.com. Щёлкните, чтобы проверить ещё раз."; }
            tips.SetToolTip(ipValue, tip);
            idleHint.Text = Selected == null ? "" : state.HidePublicIp ? "VPN выключен: трафик идёт напрямую." : "VPN выключен: сайты видят ваш обычный IP.";
            if (!Live && view != ConnectionView.Connecting) heroLine.Text = state.LastSessionEnd == null ? "" : "Последняя сессия: " + HumanDuration(state.LastSessionSeconds);
            else if (view != ConnectionView.Connected) heroLine.Text = "";
            if (state.LastSessionEnd == null) { lastSession.Text = ""; tips.SetToolTip(lastSession, ""); }
            else {
                lastSession.Text = "Последняя сессия: " + HumanDuration(state.LastSessionSeconds) + (state.LastSessionDown > 0 || state.LastSessionUp > 0 ? "\n↓ " + SessionStats.FormatBytes(state.LastSessionDown) + " · ↑ " + SessionStats.FormatBytes(state.LastSessionUp) : "");
                DateTime end; string when = DateTime.TryParse(state.LastSessionEnd, null, System.Globalization.DateTimeStyles.RoundtripKind, out end) ? end.ToLocalTime().ToString("d MMMM, HH:mm", new System.Globalization.CultureInfo("ru-RU")) : "";
                tips.SetToolTip(lastSession, (state.LastSessionServer ?? "") + (when.Length > 0 ? ", закончилась " + when : ""));
            }
            tips.SetToolTip(heroLine, Live || state.LastSessionEnd == null ? "" : lastSession.Text.Replace("\n", " · ") + "\n" + tips.GetToolTip(lastSession));
        }
        internal static string HumanDuration(long seconds) {
            if (seconds < 60) return "меньше минуты";
            long minutes = seconds / 60;
            return minutes < 60 ? minutes + " мин" : (minutes / 60) + " ч " + (minutes % 60) + " мин";
        }
        // Asks Cloudflare which address and country this computer has now; a newer check cancels an older one.
        async void CheckIp(int delayMs) {
            if (previewOnly || state.HidePublicIp || IsDisposed) return;
            int generation = ++ipGeneration;
            ipChecking = true; ipFailed = false; RefreshNetwork();
            if (delayMs > 0) await Task.Delay(delayMs);
            if (generation != ipGeneration || IsDisposed) return;
            bool throughVpn = view == ConnectionView.Connected;
            try {
                PublicIp result = await PublicIp.FetchAsync();
                if (generation != ipGeneration || IsDisposed) return;
                publicIp = result; ipThroughVpn = throughVpn;
            } catch (Exception error) {
                if (generation != ipGeneration || IsDisposed) return;
                publicIp = null; ipFailed = true; EngineLog.Write("public ip: " + error.Message);
            }
            ipChecking = false; RefreshNetwork();
        }
        // Once a day per subscription: access ends within three days or the month's traffic is nearly used.
        void WarnAccess() {
            if (previewOnly || trayMenu == null) return;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (Subscription sub in state.Subscriptions) {
                if (sub.WarnedOn == today || Account.LevelOf(sub, DateTime.UtcNow) != Account.Level.Warning) continue;
                sub.WarnedOn = today; SaveState();
                trayMenu.Notify(Program.ProductName, "Подписка «" + sub.DisplayName + "»: " + Account.Summary(sub, DateTime.UtcNow) + ". Продлите доступ у администратора.");
            }
        }
        // «Вставить»: a tt:// link from the clipboard becomes a server; null on success, else what to say.
        internal string PasteLink(string text) {
            text = (text ?? "").Trim();
            if (Subscription.LooksLikeUrl(text)) { PasteSubscription(text); return null; }
            if (!text.StartsWith("tt://", StringComparison.OrdinalIgnoreCase)) return "В буфере обмена нет ссылки tt:// или ссылки подписки.";
            Import(text);
            return null;
        }

        // Subscriptions (spec 2026-11-04): one link with all locations, refreshed by the app.
        internal IFeedFetcher FeedFetcher = new HttpFeedFetcher();
        readonly Dictionary<string, DateTime> subscriptionAttempts = new Dictionary<string, DateTime>();
        readonly System.Windows.Forms.Timer subscriptionTimer = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
        Task refreshRunning = Task.FromResult(0);
        Task entryRefresh = Task.FromResult(0);
        // An entry always refreshes even a recently updated feed; overlapping entries share work.
        internal Task RefreshOnEntry() {
            if (!entryRefresh.IsCompleted) return entryRefresh;
            entryRefresh = RefreshWhere(sub => sub.PendingRefreshes == 0);
            return entryRefresh;
        }
        // Opening the app again seconds after closing it must not block on the network: a feed
        // refreshed less than two minutes ago is good enough for startup.
        internal Task RefreshOnStartup() {
            DateTime now = DateTime.UtcNow;
            if (state.Subscriptions.Count > 0 && state.Subscriptions.All(sub => sub.LastError == null && RecentlyTouched(sub, now, TimeSpan.FromMinutes(2)))) {
                EngineLog.Write("startup: subscriptions fresh, refresh skipped");
                return Task.FromResult(0);
            }
            return RefreshOnEntry();
        }
        bool RecentlyTouched(Subscription sub, DateTime now, TimeSpan window) {
            DateTime last = DateTime.MinValue, updated, attempt;
            if (sub.UpdatedAt != null && DateTime.TryParse(sub.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out updated)) last = updated.ToUniversalTime();
            if (subscriptionAttempts.TryGetValue(sub.Id, out attempt) && attempt > last) last = attempt;
            return last != DateTime.MinValue && now - last < window;
        }
        // A subscription fetch that takes longer ends with an error (HttpFeedFetcher also stops at 15 s).
        internal static TimeSpan FeedTimeout = TimeSpan.FromSeconds(20);
        async Task<SubscriptionFeed> LoadFeed(string url) {
            Task<string> fetch = FeedFetcher.Fetch(url, CancellationToken.None);
            if (await Task.WhenAny(fetch, Task.Delay(FeedTimeout)) != fetch) { Latency.Observe(fetch); throw new FormatException("Сервер подписки не ответил вовремя."); }
            return SubscriptionFeed.Parse(await fetch);
        }
        string ConnectedServerId { get { return connection.Active ? state.SelectedServerId : null; } }
        async void PasteSubscription(string url) {
            string problem = await AddSubscription(url);
            if (IsDisposed) return;
            if (problem != null) Error(problem);
            else if (!connection.Active) ApplyView(Selected == null ? ConnectionView.NoProfile : ConnectionView.Idle, null, "Подписка добавлена.");
        }
        // Null on success, otherwise why not; a new subscription is kept only when it loaded.
        internal async Task<string> AddSubscription(string url) {
            url = (url ?? "").Trim();
            if (!Subscription.LooksLikeUrl(url)) return "Это не ссылка подписки: нужна ссылка https://…/sub/… из панели.";
            SubscriptionFeed feed;
            try { feed = await LoadFeed(url); }
            catch (FormatException error) { return error.Message; }
            catch (Exception error) { EngineLog.Write("subscription: " + error.Message); return "Не удалось загрузить подписку: " + error.Message; }
            Subscription existing = state.Subscriptions.FirstOrDefault(sub=>sub.Url == url);
            Subscription target = null;
            try { target = state.AddSubscription(url); ApplyFeed(target, feed); return null; }
            catch (FormatException error) {
                if (existing == null && target != null) state.Subscriptions.Remove(target);
                return error.Message;
            }
        }
        void ApplyFeed(Subscription sub, SubscriptionFeed feed) {
            FeedResult result = state.ApplyFeed(sub.Id, feed, ConnectedServerId);
            subscriptionAttempts[sub.Id] = DateTime.UtcNow;
            SaveState(); RefreshState();
            if (result.SettingsChanged) Note("Правила и DNS подписки обновлены. Применятся при следующем подключении.");
            // New credentials of the server in use: one reconnect, like a rule change.
            if (result.CurrentChanged && connection.Active) { reapplyPending = true; SwitchTo(null, null); }
        }
        // Every subscription now (start, «Обновить»); the timer only refreshes the due ones.
        internal Task RefreshSubscriptions(bool manual) { return RefreshWhere(sub => true); }
        Task RefreshSubscription(string id) { return RefreshWhere(sub => sub.Id == id); }
        bool Due(Subscription sub) {
            DateTime last = DateTime.MinValue, updated, attempt;
            if (sub.UpdatedAt != null && DateTime.TryParse(sub.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out updated)) last = updated.ToUniversalTime();
            if (subscriptionAttempts.TryGetValue(sub.Id, out attempt) && attempt > last) last = attempt;
            return DateTime.UtcNow - last >= TimeSpan.FromHours(sub.UpdateHours);
        }
        // Refreshes run one after another: a press of «Обновить» during a background refresh waits
        // for it and then fetches again instead of being dropped.
        internal Task RefreshSelectedSubscription() {
            Subscription sub = subscriptionRow.Subscription;
            if (sub == null || sub.PendingRefreshes > 0) return Task.FromResult(0);
            return RefreshSubscription(sub.Id);
        }
        Task RefreshWhere(Func<Subscription, bool> pick) {
            List<Subscription> targets = state.Subscriptions.Where(pick).ToList();
            foreach (Subscription sub in targets) sub.PendingRefreshes++;
            state.NotifyChanged();
            Task previous = refreshRunning;
            refreshRunning = RefreshAfter(previous, targets);
            return refreshRunning;
        }
        async Task RefreshAfter(Task previous, List<Subscription> targets) {
            try { await previous; } catch (Exception) { }
            foreach (Subscription sub in targets) {
                try {
                    if (IsDisposed || !state.Subscriptions.Contains(sub)) continue;
                    subscriptionAttempts[sub.Id] = DateTime.UtcNow;
                    SubscriptionFeed feed = await LoadFeed(sub.Url);
                    if (IsDisposed || !state.Subscriptions.Contains(sub)) continue;
                    ApplyFeed(sub, feed);
                } catch (Exception error) {
                    if (IsDisposed) continue;
                    sub.LastError = error is FormatException ? error.Message : "Не удалось загрузить подписку: " + error.Message;
                    EngineLog.Write("subscription " + new Uri(sub.Url).Host + ": " + error.Message);
                    SaveState();
                } finally {
                    sub.PendingRefreshes--;
                    if (!IsDisposed) state.NotifyChanged();
                }
            }
        }
        // Servers that left their subscription while connected go when the session ends.
        void DropOrphans() {
            if (connection.Active || !state.Servers.Any(x => x.Orphaned)) return;
            state.DropOrphans(); SaveState(); RefreshState();
        }
        // Rule set choice from «Маршрутизация»: the sets with a check on the one in use, then «Настроить…».
        internal ContextMenuStrip BuildRoutingMenu() {
            ContextMenuStrip menu = DarkMenuRenderer.CreateMenu(Math.Max(setButton.Width, 200));
            foreach (RuleSet set in state.AvailableRuleSets) {
                string id = set.Id;
                ToolStripMenuItem item = new ToolStripMenuItem(set.DisplayName) { Checked = id == state.SelectedRuleSet.Id };
                item.Click += delegate { if (!previewOnly) SwitchTo(null, id); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem manage = menu.Items.Add("Настроить наборы…", null, delegate { OpenSettings("rules"); });
            manage.ForeColor = Theme.Muted;
            return menu;
        }
        internal ContextMenuStrip OpenRoutingMenuForPreview() { return OpenRoutingMenu(); }
        ContextMenuStrip OpenRoutingMenu() {
            ContextMenuStrip menu = BuildRoutingMenu();
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            // Right edges line up: the menu may be wider than the button and must not leave the window.
            menu.Show(setButton, new Point(setButton.Width, setButton.Height + 4), ToolStripDropDownDirection.BelowLeft);
            return menu;
        }
        void SaveState() {
            if (previewOnly) return;
            SyncAutostart();
            try { StateStore.Save(dataDirectory, state); } catch (Exception error) { EngineLog.Write("state save failed: " + error.Message); Error("Не удалось сохранить настройки."); }
        }

        // «Проверить пинг»: every server in parallel; the table and the «Серверы» page show the results.
        bool checkingPings;
        internal Func<Server, Task<string>> PingProbe = async delegate(Server server) {
            LatencyResult result = await Latency.Measure(Profile.Deserialize(server.ToProfile().Serialize()));
            return result.Success ? result.Milliseconds + " мс" : "нет ответа";
        };
        internal Dictionary<string, string> PingsForTests { get { return pings; } }
        internal async Task CheckPings() {
            if (checkingPings) return;
            checkingPings = true; table.Checking = true;
            foreach (Server item in VisibleLocations) pings[item.Id] = "…";
            RefreshPings();
            try {
                await Task.WhenAll(state.Servers.ToList().Select(async delegate(Server server) {
                    try { pings[server.Id] = await PingProbe(server); } catch (Exception error) { EngineLog.Write("ping " + server.Hostname + ": " + error.Message); pings[server.Id] = "нет ответа"; }
                }));
                if (IsDisposed) return;
                latencyGeneration++; lastProbe = lastPingCheck = DateTime.UtcNow; RefreshPingNote();
                if (Selected != null) { pings.TryGetValue(Selected.Id, out pingText); RefreshPings(); }
            } finally { checkingPings = false; if (!IsDisposed) table.Checking = false; }
        }
        void RefreshPingNote() {
            if (lastPingCheck == DateTime.MinValue) { table.Note = null; return; }
            double age = (DateTime.UtcNow - lastPingCheck).TotalSeconds;
            table.Note = "Пинг проверен " + (age < 60 ? "только что" : age < 3600 ? ((int)(age / 60)) + " мин назад" : age < 86400 ? ((int)(age / 3600)) + " ч назад" : lastPingCheck.ToLocalTime().ToString("dd.MM HH:mm"));
        }
        internal string PingNoteForTests { get { return table.Note; } }
        async Task ProbeLatency() {
            Server server = Selected;
            if (measuring || server == null || previewOnly) return;
            measuring = true; int generation = latencyGeneration; lastProbe = DateTime.UtcNow;
            try {
                LatencyResult result = await Latency.Measure(Profile.Deserialize(server.ToProfile().Serialize()));
                if (!IsDisposed && generation == latencyGeneration) { pingText = result.Success ? result.Milliseconds + " мс" : "нет ответа"; pings[server.Id] = pingText; RefreshPings(); tips.SetToolTip(table, result.Detail); }
            } finally { measuring = false; }
        }
        void RefreshPings() {
            table.SetServers(VisibleLocations.ToList(), state.SelectedServerId, pings);
            UpdateStatusText();
        }
        void PasteFromClipboard() {
            string text = null;
            try { text = Clipboard.GetText(); } catch (Exception error) { EngineLog.Write("clipboard: " + error.Message); }
            string problem = PasteLink(text);
            if (problem != null) {
                if (emptyView.Visible) { emptyHint.Text = problem; emptyHint.ForeColor = Theme.Danger; }
                else Note(problem);
            } else if (emptyView.Visible) {
                emptyHint.Text = "Один сервер по ссылке tt:// тоже подойдёт"; emptyHint.ForeColor = Theme.Faint;
            }
        }
        UpdateManifest remoteUpdate;
        bool updateDialogOpen;
        // Two sources: the public update channel and the subscription feed. The newest version wins.
        internal UpdateManifest AvailableUpdate() {
            Version current;
            if (!Version.TryParse(Program.Version, out current)) return null;
            UpdateManifest best = null; Version bestVersion = null;
            Action<UpdateManifest> consider = delegate(UpdateManifest offer) {
                if (offer == null) return;
                Version offered;
                if (!Version.TryParse(offer.Version, out offered) || offered <= current) return;
                if (bestVersion == null || offered > bestVersion) { bestVersion = offered; best = offer; }
            };
            consider(remoteUpdate);
            foreach (Subscription sub in state.Subscriptions)
                consider(new UpdateManifest { Version = sub.LatestClientVersion, Url = sub.ClientDownloadUrl, Notes = sub.ClientNotes });
            return best;
        }
        async Task CheckUpdate() {
            try {
                remoteUpdate = await UpdateFeed.FetchAsync();
                RefreshFooter();
                UpdateManifest update = AvailableUpdate();
                if (update != null && !String.IsNullOrEmpty(update.Sha256) && update.Version != state.SkippedUpdateVersion && !IsDisposed)
                    BeginInvoke(new Action(delegate { ShowUpdateDialog(update); }));
            } catch (Exception error) {
                remoteUpdate = null;
                EngineLog.Write("updates: " + error.Message);
            }
        }
        void ShowUpdateDialog(UpdateManifest update) {
            if (update == null || updateDialogOpen) return;
            // A subscription feed has no checksum: offer its page instead of an unverified installer.
            if (String.IsNullOrEmpty(update.Sha256)) {
                try { Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true }); }
                catch (Exception error) { Note("Не удалось открыть страницу обновления: " + error.Message); }
                return;
            }
            updateDialogOpen = true;
            try {
                using (UpdateDialog dialog = new UpdateDialog(update)) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { exiting = true; Close(); }
                    else { state.SkippedUpdateVersion = update.Version; SaveState(); }
                }
            } finally { updateDialogOpen = false; }
        }
        // The footer carries the version by default and turns into a way to the engine log when
        // something went wrong — the log is what a support question actually needs.
        void RefreshFooter() {
            if (footerNote == null || footerNote.IsDisposed) return;
            bool attention = failed || view == ConnectionView.Error || stateLoadFailed;
            if (!attention) {
                UpdateManifest update = AvailableUpdate();
                if (update != null) {
                    footerNote.Text = "Доступна v" + update.Version;
                    footerNote.ForeColor = Theme.Accent;
                    footerNote.Cursor = Cursors.Hand;
                    footerNote.Tag = update;
                    return;
                }
            }
            footerNote.Tag = null;
            footerNote.Text = attention ? "Скопировать журнал" : "Профиль зашифрован Windows";
            footerNote.ForeColor = attention ? Theme.Accent : Theme.Faint;
            footerNote.Cursor = attention ? Cursors.Hand : Cursors.Default;
        }
        void CopyEngineLog() {
            try {
                string text = File.ReadAllText(EngineLog.Path);
                if (text.Length > 100000) text = text.Substring(text.Length - 100000);
                Clipboard.SetText(text);
                Note("Журнал движка скопирован в буфер обмена.");
            } catch (Exception error) { Note("Не удалось прочитать журнал: " + error.Message); }
        }
        // Changes the server and/or rule set (null = keep). While the VPN runs it restarts the engine with the
        // new selection; calls made during a switch only record the latest target, which runs once afterwards.
        // «Авто» · «HTTP/2» · «HTTP/3», the same on «Серверы».
        internal static readonly string[] TransportChoices = { "auto", "http2", "http3" };
        internal static int TransportIndex(Server server) { return server.AutoTransport ? 0 : server.EffectiveProtocol == "http3" ? 2 : 1; }
        // Saves the selected server's transport choice; a running VPN restarts when the protocol changes.
        internal void SetTransport(string choice) {
            Server server = Selected;
            if (server == null || (server.Transport ?? server.Protocol) == choice && server.Transport != null) return;
            string before = server.EffectiveProtocol;
            server.Transport = choice; state.NotifyChanged(); SaveState();
            if (!previewOnly && connection.Active && server.EffectiveProtocol != before) { reapplyPending = true; reapplyLabel = server.EffectiveProtocol == "http3" ? "HTTP/3" : "HTTP/2"; SwitchTo(null, null); }
        }
        // «Авто»: watches the start; HTTP/3 that does not connect in 15 s (or fails) gives way to HTTP/2, once.
        void BeginAuto(bool fallback) {
            if (!fallback) autoFellBack = false;
            Server server = Selected;
            autoAttempt = server != null && server.AutoTransport ? server.EffectiveProtocol : null;
            int generation = ++autoGeneration;
            if (autoAttempt != null && !autoFellBack) WatchAuto(generation);
        }
        async void WatchAuto(int generation) {
            await Task.Delay(15000);
            if (generation != autoGeneration || IsDisposed || autoAttempt == null) return;
            if (view == ConnectionView.Connecting || view == ConnectionView.Reconnecting) StartFallback();
        }
        bool StartFallback() {
            Server server = Selected;
            if (autoAttempt == null || autoFellBack || server == null || !server.AutoTransport || previewOnly) return false;
            string other = autoAttempt == "http3" ? "http2" : "http3";
            autoFellBack = true; autoGeneration++;
            server.AutoProtocol = other; SaveState();
            EngineLog.Write("auto transport: " + autoAttempt + " did not connect, trying " + other);
            FallBack(other);
            return true;
        }
        async void FallBack(string protocol) {
            ApplyView(ConnectionView.Connecting, null, (protocol == "http2" ? "HTTP/3 не подключился, пробую HTTP/2…" : "HTTP/2 не подключился, пробую HTTP/3…"));
            if (connection.Active) {
                busy = true; ControlsEnabled();
                bool stopped;
                try { stopped = await connection.StopAsync(); } finally { busy = false; ControlsEnabled(); }
                if (!stopped) { Error("Движок ещё завершает работу. Повторите подключение через несколько секунд."); return; }
            }
            failed = false;
            await Connect(true);
        }
        internal int TransportIndexForTests { get { return transportSwitch.SelectedIndex; } }
        internal Task SwitchTo(string serverId, string ruleSetId) {
            if (serverId != null) pendingServer = serverId;
            if (ruleSetId != null) pendingSet = ruleSetId;
            switchPending = true;
            if (!switching) switchLoop = RunSwitches();
            return switchLoop;
        }
        // Applies the queued server/set to the state; returns the name to announce, or null when nothing changed.
        string TakePending() {
            switchPending = false;
            string serverId = pendingServer, setId = pendingSet; pendingServer = pendingSet = null;
            string target = null;
            if (serverId != null && serverId != state.SelectedServerId) { state.SelectServer(serverId); target = Selected.Name; latencyGeneration++; pingText = null; pings.TryGetValue(serverId, out pingText); }
            if (setId != null && setId != state.SelectedRuleSet.Id) { state.SelectRuleSet(setId); target = target ?? state.SelectedRuleSet.Name; }
            if (target == null && reapplyPending) target = reapplyLabel ?? state.SelectedRuleSet.Name;
            reapplyPending = false; reapplyLabel = null;
            return target;
        }
        // What a reapply announces when it is not a rule set («Переключаю на «HTTP/3»…»).
        string reapplyLabel;
        void DropPending() { switchPending = reapplyPending = false; pendingServer = pendingSet = null; reapplyLabel = null; }
        async Task RunSwitches() {
            switching = true;
            try {
                while (switchPending) {
                    string target;
                    try { target = TakePending(); } catch (FormatException error) { Error(error.Message); continue; }
                    if (target == null) continue;
                    SaveState();
                    // A connect or disconnect in progress owns the engine: the choice is only recorded.
                    if (!connection.Active || busy) continue;
                    ApplyView(ConnectionView.Reconnecting, "Переключаю на «" + target + "»…", "");
                    busy = true; ControlsEnabled();
                    try {
                        if (!await connection.StopAsync()) { DropPending(); Error("Движок ещё завершает работу. Повторите переключение через несколько секунд."); return; }
                        // Choices made while the engine stopped join this start: one restart per burst.
                        while (switchPending) { try { TakePending(); } catch (FormatException) { } }
                        SaveState();
                        BeginAuto(false);
                        await connection.StartAsync(state.SessionProfile(), true, 0);
                    } catch (Exception error) { DropPending(); EngineLog.Write("switch failed: " + error); Error("Не удалось переключиться: " + error.Message); return; }
                    finally { busy = false; ControlsEnabled(); }
                }
            } finally { switching = false; }
        }

        // Session time and, in TUN mode, traffic of the VPN adapter; unreadable counters only hide the traffic.
        void UpdateSession() {
            long down = 0, up = 0; bool hasTraffic = false; double downRate = 0, upRate = 0, rateDown, rateUp;
            try {
                if (traffic != null && traffic.TryGetRate(DateTime.UtcNow, out rateDown, out rateUp)) { downRate = rateDown; upRate = rateUp; hasTraffic = traffic.TryGet(out down, out up); }
            } catch (Exception error) { EngineLog.Write("traffic counters: " + error.Message); traffic = null; }
            clock.Text = SessionStats.FormatDuration(DateTime.UtcNow - connectedAt);
            sent.Text = hasTraffic ? "↑ " + SessionStats.FormatBytes(up) : Blank; received.Text = hasTraffic ? "↓ " + SessionStats.FormatBytes(down) : Blank;
            speed.Text = hasTraffic ? SessionStats.SpeedLine(downRate, upRate) : Blank;
            if (hasTraffic) speedGraph.Push(downRate, upRate);
            heroLine.Text = "В сети " + clock.Text + (hasTraffic ? " · ↓ " + SessionStats.FormatBytes(down) + " · ↑ " + SessionStats.FormatBytes(up) : "");
            graphPeak.Text = PeakLine();
            tips.SetToolTip(speedGraph, "Последние 60 секунд" + (graphPeak.Text.Length > 0 ? " · " + graphPeak.Text : ""));
            tips.SetToolTip(speed, hasTraffic ? "" : "Скорость недоступна: нет данных счётчика VPN.");
        }
        string PeakLine() { return speedGraph.Peak > 0 || speedGraph.UpPeak > 0 ? "пик ↓ " + SessionStats.FormatRate(speedGraph.Peak) + " · ↑ " + SessionStats.FormatRate(speedGraph.UpPeak) : ""; }
        // What a session row shows while there is nothing to show.
        const string Blank = "—";
        // Once a second: the reconnect countdown, or the session line while connected (only then).
        internal void OnTimerTick() {
            if (retrySeconds > 0) { retrySeconds--; Note("Переподключение: попытка " + reconnectAttempts + " через " + retrySeconds + " с"); }
            else if (view == ConnectionView.Connected && connectedAt != DateTime.MinValue && !failed) { if (!String.IsNullOrEmpty(tunnelWarning)) Note(tunnelWarning); UpdateSession(); }
        }
        internal static string StatusLineFor(ConnectionView view, bool http3, int attempt = 0) {
            switch (view) {
                case ConnectionView.NoProfile: return "Профиль не добавлен";
                case ConnectionView.Connecting: return "Подключение…";
                case ConnectionView.Disconnecting: return "Отключение…";
                case ConnectionView.Connected: return http3 ? "Защищено · HTTP/3" : "Защищено · HTTP/2";
                case ConnectionView.Reconnecting: return attempt > 0 ? "Переподключение: попытка " + attempt : "Переподключение";
                case ConnectionView.KillSwitch: return "Интернет заблокирован";
                case ConnectionView.Error: return "Требуется внимание";
                default: return "Не подключено";
            }
        }
        // Short state titles next to the dot (B2 shows one line under the ring).
        internal static string TitleFor(ConnectionView view) {
            switch (view) {
                case ConnectionView.NoProfile: return "Добавьте сервер";
                case ConnectionView.Connecting: return "Подключение…";
                case ConnectionView.Disconnecting: return "Отключение…";
                case ConnectionView.Connected: return "Защищено";
                case ConnectionView.Reconnecting: return "Переподключение…";
                case ConnectionView.KillSwitch: return "Интернет заблокирован";
                case ConnectionView.Error: return "Не удалось подключиться";
                default: return "Не подключено";
            }
        }
        // Everything the status block says: the state, the transport and the current location.
        void UpdateStatusText() {
            StateStyle style = Theme.ForState(view);
            bool idle = view == ConnectionView.Idle || view == ConnectionView.NoProfile;
            statusBlock.Title = stateTitle ?? TitleFor(view);
            statusBlock.TitleColor = idle || view == ConnectionView.Connected ? Theme.Ink : style.Dot;
            statusBlock.DotColor = style.Dot; statusBlock.DotHalo = style.DotHalo;
            Server server = Selected;
            string transport = server == null ? "" : (server.EffectiveProtocol == "http3" ? "HTTP/3 · UDP" : "HTTP/2 · TCP") + (server.AutoTransport ? " · авто" : "");
            string ping; pings.TryGetValue(server == null ? "" : server.Id, out ping);
            string place = server == null ? "" : server.Name + " · " + transport;
            string subtitle;
            if (view == ConnectionView.Error) subtitle = server == null ? "Проверьте доступ и настройки" : place;
            else if (!String.IsNullOrEmpty(stateNote)) subtitle = stateNote;
            else if (view == ConnectionView.NoProfile) {
                // A subscription that gives no servers says why, instead of «add a server».
                Subscription blocked = state.Subscriptions.FirstOrDefault(Account.Blocked);
                subtitle = blocked == null ? "Вставьте ссылку подписки или файл TOML" : "Подписка «" + blocked.DisplayName + "»: " + Account.Reason(blocked);
            }
            else if (view == ConnectionView.KillSwitch) subtitle = "Снимите Kill Switch, чтобы вернуть доступ";
            else if (view == ConnectionView.Reconnecting) subtitle = "Восстанавливаем соединение";
            else if (view == ConnectionView.Connecting) subtitle = "Ожидаем подтверждения от сервера";
            else if (view == ConnectionView.Disconnecting) subtitle = "Завершаем соединение";
            else if (server == null) subtitle = "";
            else subtitle = place + (String.IsNullOrEmpty(ping) ? "" : " · " + ping);
            statusBlock.Subtitle = subtitle;
            // A connected tunnel that does not own the default route must not look green: this is
            // exactly the "connected but no internet" case the user needs to see.
            if (view == ConnectionView.Connected && !String.IsNullOrEmpty(tunnelWarning)) {
                statusBlock.Title = "Подключено без туннеля";
                statusBlock.TitleColor = Theme.Warning;
                statusBlock.Subtitle = tunnelWarning;
            }
        }
        void Note(string text) { stateNote = text ?? ""; UpdateStatusText(); }
        // The one place the main screen changes how a connection state looks. title null = the default
        // title of the state; explanation null keeps the current one.
        void ApplyView(ConnectionView next, string title, string explanation) {
            view = next;
            stateTitle = title;
            if (explanation != null) {
                stateNote = explanation;
                if (next == ConnectionView.Error) errorDetail.Text = explanation;
            } else if (next == ConnectionView.Connected) stateNote = "";
            // The button names the action; the state is said by the line beside it.
            bool stops = next == ConnectionView.Connected || next == ConnectionView.Reconnecting || next == ConnectionView.KillSwitch;
            connectButton.Text = stops ? "Отключиться" : next == ConnectionView.Error ? "Повторить" : next == ConnectionView.Connecting ? "Подключение…" : next == ConnectionView.Disconnecting ? "Отключение…" : "Подключиться";
            connectButton.Kind = stops ? ButtonKind.Stop : ButtonKind.Primary;
            connectButton.Invalidate();
            if (next != ConnectionView.Connected) {
                clock.Text = sent.Text = received.Text = speed.Text = Blank; graphPeak.Text = "";
                speedGraph.Reset();
                tips.SetToolTip(speed, "");
            }
            RefreshState();
            RefreshFooter();
        }
        // Preview and screenshot mode only (--state=...): shows a connection state without the engine.
        internal void ShowPreviewState(ConnectionView preview) {
            if (preview == ConnectionView.Connected || preview == ConnectionView.Reconnecting || preview == ConnectionView.KillSwitch) connectedAt = DateTime.UtcNow - new TimeSpan(0, 42, 17);
            // A believable network block for screenshots: no request is made in preview.
            bool vpn = preview == ConnectionView.Connected;
            publicIp = vpn ? new PublicIp { Address = "185.12.40.7", Country = "RU" } : new PublicIp { Address = "37.45.110.21", Country = "BY" };
            ipThroughVpn = vpn;
            state.LastSessionEnd = DateTime.UtcNow.AddHours(-3).ToString("o"); state.LastSessionSeconds = 4325; state.LastSessionDown = 1932735283; state.LastSessionUp = 220200960; state.LastSessionServer = "Амстердам";
            switch (preview) {
                case ConnectionView.NoProfile: state.Servers.Clear(); state.SelectedServerId = null; RefreshState(); ApplyView(preview, null, ""); break;
                case ConnectionView.Connecting: ApplyView(preview, null, "Ожидаем подтверждения от VPN-сервера…"); break;
                case ConnectionView.Connected:
                    ApplyView(preview, null, "");
                    clock.Text = "00:42:17"; sent.Text = "↑ " + SessionStats.FormatBytes(220200960); received.Text = "↓ " + SessionStats.FormatBytes(1932735283);
                    speed.Text = SessionStats.SpeedLine(2.4 * 1024 * 1024, 210 * 1024);
                    heroLine.Text = "В сети " + clock.Text + " · " + received.Text + " · " + sent.Text;
                    SeedGraph(); graphPeak.Text = PeakLine(); lastPingCheck = DateTime.UtcNow.AddMinutes(-2); RefreshPingNote();
                    pingText = "53 мс"; if (Selected != null) pings[Selected.Id] = pingText; RefreshPings(); break;
                case ConnectionView.Reconnecting: ApplyView(preview, null, "Переподключение: попытка 2 через 5 с"); break;
                case ConnectionView.KillSwitch: ApplyView(preview, null, "Нажмите «Отключиться», чтобы снять блокировку интернета."); break;
                case ConnectionView.Disconnecting: ApplyView(preview, null, "Движок завершает соединение."); break;
                case ConnectionView.Error:
                    ApplyView(preview, null, null);
                    errorDetail.Text = "Соединение не установлено. Проверьте доступ, срок, квоту и выбранный транспорт.";
                    logPreview.Lines = new[] { "14:02:11 dial tcp 203.0.113.7:443: timeout", "14:02:14 retry 2/5 after 3 s", "14:02:17 dial tcp: connect timeout" };
                    break;
                default: pingText = "53 мс"; if (Selected != null) pings[Selected.Id] = pingText; RefreshPings(); ApplyView(preview, null, ""); break;
            }
        }
        void SeedGraph() {
            speedGraph.Reset();
            double[] demo = { 1.1, 1.4, 1.2, 1.6, 1.3, 1.8, 1.5, 2.0, 1.7, 2.2, 1.9, 2.4, 2.1, 2.6, 2.3, 2.8, 2.5, 3.0, 2.7, 3.2, 2.9, 3.4, 3.1, 3.6, 3.3, 3.8, 3.5, 4.0, 3.7, 4.2, 3.9, 4.4, 4.1, 4.6, 4.3, 4.8, 4.5, 5.0, 4.7, 5.2, 4.9, 5.4, 5.1, 5.6, 5.3, 5.8, 5.5, 6.0, 5.7, 6.2, 5.9, 6.4, 6.1, 6.6, 6.3, 6.8, 6.5, 7.0, 6.7, 2.4 };
            for (int i = 0; i < demo.Length; i++) speedGraph.Push(demo[i] * 1024 * 1024 * (0.55 + 0.45 * Math.Abs(Math.Sin(i * 1.7))), (90 + 70 * Math.Abs(Math.Cos(i * 1.3)) + (i % 11 == 4 ? 260 : 0)) * 1024);
        }
        void ControlsEnabled() {
            bool hasServer = Selected != null;
            connectButton.Enabled = !busy && (hasServer || connection.Active) && Theme.ForState(view).RingEnabled;
            table.Enabled = !busy;
            emptyPrimary.Enabled = emptySecondary.Enabled = !busy;
            transportButton.Enabled = setButton.Enabled = !busy;
            transportSwitch.Enabled = !busy && hasServer;
            table.PingEnabled = !busy && hasServer;
        }
        internal string UniqueSetName(string name) {
            string candidate = name; int n = 2;
            while (state.RuleSets.Any(r => String.Equals(r.Name, candidate, StringComparison.OrdinalIgnoreCase))) candidate = name + " (" + n++ + ")";
            return candidate;
        }
        internal static string DuplicateText(Server existing) { return "Сервер «" + existing.Name + "» уже есть. Обновить его или добавить копию?"; }
        // Adds an imported server; when the same host and user exist, ask decides (null = cancel).
        internal Server ImportServer(Server incoming, Func<Server, DuplicateChoice?> ask) {
            Server existing = state.FindDuplicate(incoming);
            DuplicateChoice choice = DuplicateChoice.AddCopy;
            if (existing != null) { DuplicateChoice? answer = ask(existing); if (!answer.HasValue) return null; choice = answer.Value; }
            Server stored = state.AddServer(incoming, choice);
            SaveState();
            return stored;
        }
        DuplicateChoice? AskDuplicate(Server existing) {
            using (Form dialog = new Form { Text = "Сервер уже добавлен", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Ground, ForeColor = Theme.Ink, Font = Theme.Font(Theme.BodySize), AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(420, 140), Icon = appIcon }) {
                TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Gutter, 16, Theme.Gutter, 16), ColumnCount = 3, RowCount = 2, BackColor = Theme.Ground };
                for (int i = 0; i < 3; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                Label question = TextLabel(DuplicateText(existing), Theme.BodySize, Theme.Ink); question.TextAlign = ContentAlignment.TopLeft; layout.Controls.Add(question, 0, 0); layout.SetColumnSpan(question, 3);
                Button update = Button("Обновить", true), copy = Button("Добавить копию", false), cancel = Button("Отмена", false);
                update.Margin = new Padding(0, 0, 6, 0); copy.Margin = new Padding(3, 0, 3, 0); cancel.Margin = new Padding(6, 0, 0, 0);
                update.DialogResult = DialogResult.Yes; copy.DialogResult = DialogResult.No; cancel.DialogResult = DialogResult.Cancel;
                layout.Controls.Add(update, 0, 1); layout.Controls.Add(copy, 1, 1); layout.Controls.Add(cancel, 2, 1);
                dialog.Controls.Add(layout); dialog.AcceptButton = update; dialog.CancelButton = cancel;
                DialogResult result = dialog.ShowDialog(this);
                return result == DialogResult.Yes ? DuplicateChoice.Update : result == DialogResult.No ? (DuplicateChoice?)DuplicateChoice.AddCopy : null;
            }
        }
        void Import(string input) {
            try {
                string current = state.SelectedServerId;
                Server stored = ImportServer(Server.FromProfile(Profile.Import(input)), AskDuplicate);
                if (stored == null) return;
                // With the VPN on, a new server must not silently differ from the one the engine runs.
                if (connection.Active && current != null && current != stored.Id) { state.SelectServer(current); SaveState(); }
                failed = false; latencyGeneration++; pings.TryGetValue(stored.Id, out pingText); RefreshState();
                if (!connection.Active) ApplyView(ConnectionView.Idle, null, "Сервер «" + stored.Name + "» добавлен.");
            }
            catch (FormatException error) { Error(error.Message); } catch (Exception) { Error("Не удалось импортировать профиль. Проверьте формат и доступ к файлам."); }
        }
        void ImportFile() { using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Профиль TrustTunnel (*.toml)|*.toml", Multiselect = false }) if (dialog.ShowDialog(this) == DialogResult.OK) ImportTomlFile(dialog.FileName); }
        void ImportTomlFile(string path) { try { if (new FileInfo(path).Length > 32768) throw new FormatException("Профиль слишком большой."); Import(File.ReadAllText(path)); } catch (Exception) { Error("Не удалось открыть TOML. Нужен профиль, экспортированный нашей панелью."); } }
        void ISettingsHost.ImportTomlFile(string path) { ImportTomlFile(path); }
        // Startup: keep the Run value in step with the setting (the exe may have moved) and auto-connect.
        async void OnStartup() {
            EngineLog.Write("startup: onstartup begin in " + startupClock.ElapsedMilliseconds + " ms");
            // After a failed load the empty state must not switch autostart off.
            if (!stateLoadFailed) SyncAutostart();
            if (!previewOnly) {
                DropOrphans();
                await RefreshBeforeAutoConnect(true);
                if (IsDisposed) return;
            }
            if (!previewOnly) { subscriptionTimer.Tick += async delegate { await RefreshWhere(Due); }; subscriptionTimer.Start(); }
            EngineLog.Write("startup: onstartup end in " + startupClock.ElapsedMilliseconds + " ms");
        }
        internal async Task RefreshBeforeAutoConnect(bool startup = false) {
            Task refresh = startup ? RefreshOnStartup() : RefreshOnEntry();
            if (!IsDisposed && !previewOnly && state.Servers.Count > 0) CheckPings();
            if (!IsDisposed && !previewOnly) CheckUpdate();
            if (!IsDisposed && state.AutoConnect && Selected != null && !connection.Active) {
                // A slow subscription must not hold the VPN for the whole feed timeout:
                // connect with the cached profile and let ApplyFeed switch if the feed changes it.
                Task completed = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(5)));
                EngineLog.Write("startup: auto-connect after feed " + (completed == refresh ? "ready" : "timeout"));
                if (!IsDisposed) await Connect();
            }
        }
        void SyncAutostart() {
            if (previewOnly) return;
            try { Autostart.Apply(state.StartWithWindows, new RegistryRunKey()); } catch (Exception error) { EngineLog.Write("autostart: " + error.Message); }
        }
        // Set before Application.Run: the first show only creates the window, it stays in the tray.
        internal bool StartHidden;
        readonly Stopwatch startupClock = Stopwatch.StartNew();
        bool started, startupReady;
        void QueueStartup() {
            if (!startupReady || previewOnly || started || !IsHandleCreated) return;
            started = true;
            BeginInvoke((Action)OnStartup);
        }
        protected override void SetVisibleCore(bool value) {
            if (StartHidden && value) { StartHidden = false; if (!IsHandleCreated) CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }
        void ImportLink() {
            using (ImportForm dialog = new ImportForm(delegate(string link) { if (Subscription.LooksLikeUrl(link)) return null; try { Profile.Import(link); return null; } catch (FormatException error) { return error.Message; } })) {
                DialogResult result = dialog.ShowDialog(this);
                if (result == DialogResult.OK && Subscription.LooksLikeUrl(dialog.Link)) PasteSubscription(dialog.Link);
                else if (result == DialogResult.OK) Import(dialog.Link);
                else if (dialog.OpenTomlRequested) ImportFile();
            }
        }
        internal async Task Connect(bool fallback = false) {
            if (busy || Selected == null || connection.Active) return;
            BeginAuto(fallback);
            Profile session;
            try { session = state.SessionProfile(); } catch (FormatException error) { Error(error.Message); return; }
            if (session.Routing.Applications.Count > 0 && !EngineSpec.SupportsApplications) { Error("Эта сборка не поддерживает правила приложений. Используйте полный комплект Netlane Connect."); return; }
            string startedServer = state.SelectedServerId, startedSet = state.SelectedRuleSet.Id;
            busy = true; failed = false; reconnectAttempts = 0; retrySeconds = 0; ApplyView(ConnectionView.Connecting, null, "Подтвердите запрос Windows на запуск VPN."); ControlsEnabled();
            try { await connection.StartAsync(session, true, 0); Note("Ожидаем подтверждения от VPN-сервера…"); }
            catch (System.ComponentModel.Win32Exception error) { Error(error.NativeErrorCode == 1223 ? "Запрос прав администратора отменён. VPN не включён." : "Не удалось запустить VPN. Распакуйте приложение заново."); }
            catch (Exception error) { EngineLog.Write("connect failed: " + error); Error("Не удалось запустить движок: " + error.Message); }
            finally { busy = false; ControlsEnabled(); }
            // A server or set chosen while the engine was starting: restart once with it.
            if (connection.Active && (state.SelectedServerId != startedServer || state.SelectedRuleSet.Id != startedSet)) { reapplyPending = true; await SwitchTo(null, null); }
        }
        internal async Task Disconnect() { if (busy) return; busy = true; ControlsEnabled(); ApplyView(ConnectionView.Disconnecting, null, ""); bool stopped = await connection.StopAsync(); busy = false; if (stopped) DropOrphans(); if (!stopped) Error("Движок ещё завершает работу. Дождитесь отключения; приложение не будет принудительно закрыто."); else ApplyView(Selected == null ? ConnectionView.NoProfile : ConnectionView.Idle, null, "VPN остановлен."); ControlsEnabled(); }
        void OnState(string engineState) {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate {
                if (IsDisposed) return;
                if (engineState == "CONNECTED") { if (autoAttempt != null && Selected != null && Selected.AutoTransport) { Selected.AutoProtocol = autoAttempt; SaveState(); } autoAttempt = null; autoGeneration++; CheckIp(2000); failed = false; tunnelWarning = null; retrySeconds = 0; ApplyView(ConnectionView.Connected, null, ""); connectedAt = DateTime.UtcNow; traffic = new TrafficMeter(new AdapterTraffic()); traffic.Start(); UpdateSession(); timer.Start(); tray.Text = Program.ProductName + " · Подключено"; TrackTunnel(); if (announceSwitch && Selected != null) trayMenu.Notify(Program.ProductName, "Подключено: " + Selected.Name); announceSwitch = false; }
                else if (engineState.StartsWith("RECONNECTING")) { reconnectAttempts++; if (reconnectAttempts == 1) trayMenu.Notify(Program.ProductName, "Соединение потеряно, переподключаюсь"); int pause = 5; Int32.TryParse(engineState.IndexOf(':') >= 0 ? engineState.Substring(engineState.IndexOf(':') + 1) : "", out pause); retrySeconds = pause > 0 ? pause : 5; ApplyView(ConnectionView.Reconnecting, null, "Переподключение: попытка " + reconnectAttempts + " через " + retrySeconds + " с"); timer.Start(); tray.Text = Program.ProductName + " · Переподключение"; }
                else if (engineState == "KILL_SWITCH_ACTIVE") { failed = true; timer.Stop(); ApplyView(ConnectionView.KillSwitch, null, "Нажмите «Отключиться», чтобы снять блокировку интернета."); tray.Text = Program.ProductName + " · Kill Switch"; trayMenu.Notify(Program.ProductName, "Интернет заблокирован: Kill Switch удерживает защиту"); }
                else if ((engineState == "CONNECTION_ERROR" || engineState == "WORKER_ERROR") && StartFallback()) { }
                else if (engineState == "CONNECTION_ERROR" || engineState == "WORKER_ERROR" || engineState == "ADMIN_REQUIRED") Error("Соединение не установлено. Проверьте доступ, срок, квоту и выбранный транспорт.");
                else if (engineState == "STOP_FAILED") Error("VPN не подтвердил остановку. Дождитесь завершения, прежде чем выходить.");
                else if (engineState == "STOPPED") { if (!switching) { DropOrphans(); RememberSession(); CheckIp(2000); } timer.Stop(); connectedAt = DateTime.MinValue; traffic = null; tunnelCheck++; tunnelWarning = null; retrySeconds = 0; reconnectAttempts = 0; if (!failed && !switching) ApplyView(Selected == null ? ConnectionView.NoProfile : ConnectionView.Idle, null, "VPN остановлен."); if (!switching) tray.Text = Program.ProductName + " · Не подключено"; }
                ControlsEnabled();
            });
        }
        // The finished session for the «Сеть» block: how long, how much, where.
        void RememberSession() {
            if (connectedAt == DateTime.MinValue) return;
            long down = 0, up = 0;
            try { if (traffic != null) traffic.TryGet(out down, out up); } catch (Exception) { }
            state.LastSessionEnd = DateTime.UtcNow.ToString("o");
            state.LastSessionSeconds = (long)Math.Max(0, (DateTime.UtcNow - connectedAt).TotalSeconds);
            state.LastSessionDown = down; state.LastSessionUp = up;
            state.LastSessionServer = Selected == null ? null : Selected.Name;
            SaveState();
        }
        void Error(string message) {
            announceSwitch = false; if (trayMenu != null) trayMenu.Notify(Program.ProductName + " · ошибка", message);
            failed = true; timer.Stop(); tunnelCheck++; tunnelWarning = null;
            ApplyView(ConnectionView.Error, null, null);
            errorDetail.Text = message;
            logPreview.Lines = EngineTail();
            tips.SetToolTip(errorDetail, message);
            tips.SetToolTip(logPreview, message);
        }
        static string[] EngineTail() {
            try {
                if (!File.Exists(EngineLog.Path)) return new[] { "Журнал движка пуст." };
                string[] lines = File.ReadAllLines(EngineLog.Path);
                return lines.Skip(Math.Max(0, lines.Length - 3)).Select(x => x.Length > 110 ? x.Substring(0, 110) + "…" : x).ToArray();
            } catch (Exception error) { return new[] { "Журнал недоступен: " + error.Message }; }
        }

        async void TrackTunnel() {
            int generation = ++tunnelCheck;
            await Task.Delay(2500);
            if (IsDisposed || generation != tunnelCheck || !connection.Active) return;
            string problem = TunnelProblem();
            if (problem == null) return;
            EngineLog.Write("tunnel: " + problem);
            tunnelWarning = "Подключено, но " + problem + " Трафик идёт мимо туннеля.";
            Note(tunnelWarning); tips.SetToolTip(statusBlock, tunnelWarning);
        }
        static string TunnelProblem() {
            try {
                uint best;
                if (Native.GetBestInterface(0x01010101u, out best) != 0) return null;
                NetworkInterface tunnel = null;
                foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
                    if (item.OperationalStatus == OperationalStatus.Up && (item.Name.IndexOf("Netlane", StringComparison.OrdinalIgnoreCase) >= 0 || item.Name.IndexOf("WinTrustTunnel", StringComparison.OrdinalIgnoreCase) >= 0)) { tunnel = item; break; }
                if (tunnel == null) return "системный адаптер VPN не найден.";
                IPv4InterfaceProperties properties = tunnel.GetIPProperties().GetIPv4Properties();
                if (properties == null || properties.Index != (int)best) return "маршрут по умолчанию не проходит через VPN.";
                return null;
            } catch (Exception) { return null; }
        }
        protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); EngineLog.Write("startup: handle created in " + startupClock.ElapsedMilliseconds + " ms"); int dark = 1; try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch (DllNotFoundException) {} QueueStartup(); }
        // The cross hides the window when «Закрытие окна сворачивает в трей» is on; otherwise, and on shutdown, the app exits.
        internal static CloseAction CloseDecision(bool closeToTray, CloseReason reason) { return closeToTray && reason == CloseReason.UserClosing ? CloseAction.Hide : CloseAction.Exit; }
        async void Restore() {
            bool entering = !Visible || WindowState == FormWindowState.Minimized;
            Stopwatch restoreClock = Stopwatch.StartNew();
            EngineLog.Write("restore: begin");
            Show(); WindowState = FormWindowState.Normal; Activate();
            EngineLog.Write("restore: shown in " + restoreClock.ElapsedMilliseconds + " ms");
            if (entering && !previewOnly) await RefreshOnStartup();
            EngineLog.Write("restore: end in " + restoreClock.ElapsedMilliseconds + " ms");
        }
        async Task Exit() { if (busy) { Restore(); return; } if (connection.Active) { await Disconnect(); if (connection.Active) { Restore(); return; } } exiting = true; tray.Visible = false; Close(); }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); pingNoteTimer.Dispose(); subscriptionTimer.Dispose(); tips.Dispose(); if (trayMenu != null) trayMenu.Dispose(); if (tray != null) tray.Dispose(); connection.Dispose(); appIcon.Dispose(); trayIcon.Dispose(); brandMark.Dispose(); } base.Dispose(disposing); }
    }
}
