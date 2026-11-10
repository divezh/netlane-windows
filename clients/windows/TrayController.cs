using System;
using System.Windows.Forms;

namespace Netlane {
    internal enum CloseAction { Hide, Exit }

    // Tray icon menu and balloon notifications (spec §4): «Подключить»/«Отключить», «Сервер ›»,
    // «Правила ›», «Открыть», «Настройки», «Выход». Balloons appear only while the window is hidden.
    internal sealed class TrayController : IDisposable {
        readonly NotifyIcon icon;
        readonly ClientState state;
        readonly Func<bool> connected, windowVisible;
        bool toldAboutTray;
        internal readonly ContextMenuStrip Menu = new ContextMenuStrip { BackColor = Theme.Card, ForeColor = Theme.Ink, ShowImageMargin = false, ShowCheckMargin = false, Renderer = new DarkMenuRenderer() };
        internal event Action ToggleRequested, OpenRequested, SettingsRequested, ExitRequested;
        internal event Action<string> ServerPicked, RuleSetPicked;
        internal int Notifications { get; private set; }
        internal string LastText { get; private set; }

        internal TrayController(NotifyIcon icon, ClientState state, Func<bool> connected, Func<bool> windowVisible) {
            this.icon = icon; this.state = state; this.connected = connected; this.windowVisible = windowVisible;
            icon.ContextMenuStrip = Menu;
            Menu.Opening += delegate { Rebuild(); };
        }
        internal ToolStripMenuItem Item(string name) { ToolStripItem[] found = Menu.Items.Find(name, false); return found.Length == 0 ? null : found[0] as ToolStripMenuItem; }

        internal void Rebuild() {
            Menu.Items.Clear();
            Add(connected() ? "Отключить" : "Подключить", "toggle").Enabled = connected() || state.SelectedServer != null;
            Menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem servers = new ToolStripMenuItem("Сервер") { Name = "servers" };
            foreach (Server server in state.Servers) {
                string id = server.Id;
                ToolStripMenuItem item = new ToolStripMenuItem(server.Name) { Checked = id == state.SelectedServerId };
                item.Click += delegate { Action<string> handler = ServerPicked; if (handler != null) handler(id); };
                servers.DropDownItems.Add(item);
            }
            servers.Enabled = state.Servers.Count > 0;
            ToolStripMenuItem rules = new ToolStripMenuItem("Правила") { Name = "rules" };
            foreach (RuleSet set in state.AvailableRuleSets) {
                string id = set.Id;
                ToolStripMenuItem item = new ToolStripMenuItem(set.DisplayName) { Checked = id == state.SelectedRuleSet.Id };
                item.Click += delegate { Action<string> handler = RuleSetPicked; if (handler != null) handler(id); };
                rules.DropDownItems.Add(item);
            }
            foreach (ToolStripMenuItem parent in new[] { servers, rules }) { parent.DropDown.BackColor = Theme.Card; parent.DropDown.ForeColor = Theme.Ink; parent.DropDown.Renderer = new DarkMenuRenderer(); }
            Menu.Items.Add(servers); Menu.Items.Add(rules);
            Menu.Items.Add(new ToolStripSeparator());
            Add("Открыть", "open");
            Add("Настройки", "settings");
            Add("Выход", "exit");
        }
        ToolStripMenuItem Add(string text, string name) {
            ToolStripMenuItem item = new ToolStripMenuItem(text) { Name = name };
            item.Click += delegate {
                Action handler = name == "toggle" ? ToggleRequested : name == "open" ? OpenRequested : name == "settings" ? SettingsRequested : ExitRequested;
                if (handler != null) handler();
            };
            Menu.Items.Add(item);
            return item;
        }

        // Shows a balloon unless the window is on screen; returns whether it was shown.
        internal bool Notify(string title, string text) {
            if (windowVisible()) return false;
            Notifications++; LastText = text;
            if (icon.Visible) { icon.BalloonTipTitle = title; icon.BalloonTipText = text; icon.ShowBalloonTip(3000); }
            return true;
        }
        // Called when the window goes to the tray; the first time says so.
        internal void WindowHidden() {
            if (toldAboutTray) return;
            toldAboutTray = true;
            Notify(Program.ProductName, Program.ProductName + " работает в трее");
        }
        public void Dispose() { Menu.Dispose(); }
    }
}
