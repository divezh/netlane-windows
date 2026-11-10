using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Netlane {
    // «Подключение» and «О программе» (spec §3).
    internal sealed partial class SettingsForm {
        readonly List<KeyValuePair<ToggleSwitch, Func<ClientState, bool>>> toggles = new List<KeyValuePair<ToggleSwitch, Func<ClientState, bool>>>();
        bool refreshingToggles;

        Control BuildConnectionPage() {
            Panel page = new Panel { BackColor = Theme.Ground, AutoScroll = true };
            List<Control> stack = new List<Control>();
            Label heading = Heading(embedded ? "Настройки" : "Подключение"); heading.Height = 34; stack.Add(heading);
            stack.Add(Group("ЗАЩИТА"));
            stack.Add(Toggle("Kill Switch", "Блокировать интернет, если VPN оборвался. Обычная сеть вернётся после «Отключиться».", s => s.KillSwitch, (s, v) => s.KillSwitch = v));
            stack.Add(Toggle("Anti-DPI", "Дополнительные меры против распознавания VPN. Включайте при обрывах и блокировках.", s => s.AntiDpi, (s, v) => s.AntiDpi = v));
            Label applies = Hint("Kill Switch и Anti-DPI применяются при следующем подключении."); applies.Height = 26; stack.Add(applies);
            stack.Add(Group("DNS ДЛЯ VPN")); stack.Add(BuildDnsSettings());
            stack.Add(Group("ЗАПУСК"));
            stack.Add(Toggle("Запускать вместе с Windows", "Программа стартует свёрнутой в трей после входа в Windows.", s => s.StartWithWindows, (s, v) => s.StartWithWindows = v));
            stack.Add(Toggle("Подключаться к последнему серверу при запуске", "Сразу включить VPN с выбранным сервером и набором правил.", s => s.AutoConnect, (s, v) => s.AutoConnect = v));
            stack.Add(Toggle("Закрытие окна сворачивает в трей", "Крестик прячет окно, VPN продолжает работать. Выход — в меню значка в трее.", s => s.CloseToTray, (s, v) => s.CloseToTray = v));
            for (int i = stack.Count - 1; i >= 0; i--) { stack[i].Dock = DockStyle.Top; page.Controls.Add(stack[i]); }
            return page;
        }
        static Label Group(string text) { Label label = MainForm.TextLabel(text, Theme.CaptionSize, Theme.Muted); label.TextAlign = ContentAlignment.BottomLeft; label.Padding = new Padding(0, 0, 0, 6); label.Height = 34; return label; }
        Control Toggle(string title, string detail, Func<ClientState, bool> get, Action<ClientState, bool> set) {
            Panel spacer = new Panel { Height = 84, BackColor = Theme.Ground, Padding = new Padding(0, 0, 0, 8) };
            ToggleSwitch toggle = new ToggleSwitch { Text = title, Detail = detail, Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0), Card = true };
            toggle.CheckedChanged += delegate {
                if (refreshingToggles) return;
                set(state, toggle.Checked); state.NotifyChanged(); host.SaveState();
            };
            toggles.Add(new KeyValuePair<ToggleSwitch, Func<ClientState, bool>>(toggle, get));
            spacer.Controls.Add(toggle);
            return spacer;
        }
        void RefreshConnection() {
            RefreshDns();
            refreshingToggles = true;
            try { foreach (var pair in toggles) pair.Key.Checked = pair.Value(state); }
            finally { refreshingToggles = false; }
        }

        Control BuildAboutPage() {
            Panel page = new Panel { BackColor = Theme.Ground, AutoScroll = true };
            List<Control> stack = new List<Control>();
            TableLayoutPanel brand = new TableLayoutPanel { Height = 84, ColumnCount = 2, RowCount = 2, BackColor = Theme.Ground, Margin = new Padding(0) };
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            brand.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); brand.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            PictureBox mark = new PictureBox { Image = AppIcon.Mark(64), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            Label name = Heading(Program.ProductName); name.TextAlign = ContentAlignment.BottomLeft;
            Label version = MainForm.TextLabel("Версия " + Program.Version + " · движок TrustTunnel " + EngineSpec.Version, Theme.BodySize, Theme.Muted); version.TextAlign = ContentAlignment.TopLeft;
            brand.Controls.Add(mark, 0, 0); brand.SetRowSpan(mark, 2); brand.Controls.Add(name, 1, 0); brand.Controls.Add(version, 1, 1);
            stack.Add(brand);
            Label purpose = Hint("Netlane Connect — VPN-клиент для Windows на базе TrustTunnel. Профили и пароли хранятся только на этом компьютере в зашифрованном виде."); purpose.Height = 48; stack.Add(purpose);
            stack.Add(Group("ОБНОВЛЕНИЯ"));
            LinkLabel updates = Link("Проверить обновления", "");
            updates.LinkClicked += async delegate {
                string result = await host.CheckForUpdates();
                MessageBox.Show(this, result, Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            tips.SetToolTip(updates, "Проверить версию, которую сообщает панель подписки");
            stack.Add(updates);
            stack.Add(Group("ДИАГНОСТИКА"));
            LinkLabel log = Link("Журнал движка", EngineLog.Path);
            log.LinkClicked += delegate { Reveal(EngineLog.Path); };
            tips.SetToolTip(log, EngineLog.Path);
            stack.Add(log);
            Label logHint = Hint("Пригодится, если подключение не устанавливается: приложите файл к вопросу администратору."); logHint.Height = 30; stack.Add(logHint);
            stack.Add(Group("ЛИЦЕНЗИИ"));
            Label licenses = Hint("Движок TrustTunnel CLI — Apache-2.0.\nДрайвер Wintun — бинарная лицензия WireGuard LLC.\nПолные тексты — в папке licenses рядом с программой."); licenses.Height = 64; stack.Add(licenses);
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "licenses");
            LinkLabel licenseLink = Link("Открыть папку лицензий", folder);
            licenseLink.LinkClicked += delegate { Reveal(folder); };
            stack.Add(licenseLink);
            for (int i = stack.Count - 1; i >= 0; i--) { stack[i].Dock = DockStyle.Top; page.Controls.Add(stack[i]); }
            return page;
        }
        static LinkLabel Link(string text, string target) {
            return new LinkLabel { Text = text, Tag = target, Height = 28, Font = Theme.Font(Theme.BodySize), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Ground };
        }
        // Shows a file in Explorer (or opens a folder); a missing path opens its nearest parent.
        void Reveal(string path) {
            if (host.PreviewOnly) return;
            try {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else {
                    string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                    if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                    else MessageBox.Show(this, "Файл ещё не создан: " + path, Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            } catch (Exception error) { MessageBox.Show(this, "Не удалось открыть: " + error.Message, Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
