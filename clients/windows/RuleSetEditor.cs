using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Netlane {
    // Editor of one rule set (spec §3), the former routing window as a control: mode, tabs with
    // counts, the active list, and one footer row «Импорт · Экспорт · Удалить выбранное … Сохранить»
    // docked to the bottom so it stays in place however the window is resized.
    internal sealed class RuleSetEditor : UserControl {
        internal Routing Value { get; private set; }
        internal event Action SaveRequested;
        string original;
        readonly bool readOnly;
        readonly ListBox destinations = List(), apps = List();
        readonly SegmentedControl mode = new SegmentedControl { Items = new[] { "Только список через VPN", "Всё через VPN, кроме списка" }, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        readonly Label explanation = MainForm.TextLabel("", Theme.SmallSize, Theme.Muted);
        readonly TabStrip tabs = new TabStrip { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8), Tabs = new[] { "Домены и IP", "Приложения" } };
        // A set from a subscription or the built-in one cannot be saved, so it never has unsaved changes.
        internal bool Dirty { get { return !readOnly && Serialize(Value) != original; } }
        // For the engine log when the user is asked about unsaved changes: what differs from the saved set.
        internal string DirtyReport { get { return "saved=" + original + " now=" + Serialize(Value); } }
        internal void MarkSaved() { original = Serialize(Value); }
        static string Serialize(Routing routing) { return new JavaScriptSerializer().Serialize(routing); }

        internal RuleSetEditor(RuleSet set, bool readOnly) {
            this.readOnly = readOnly;
            Value = set.Routing.Copy(); original = Serialize(Value);
            BackColor = Theme.Ground; ForeColor = Theme.Ink; Font = Theme.Font(Theme.BodySize);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Theme.Ground, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 44, 34, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            mode.SelectedIndex = Value.Mode == "selective" ? 0 : 1; layout.Controls.Add(mode, 0, 0);
            explanation.Text = Explain(); layout.Controls.Add(explanation, 0, 1);
            layout.Controls.Add(tabs, 0, 2);
            Panel body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Ground }; layout.Controls.Add(body, 0, 3);
            TableLayoutPanel addressPage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), BackColor = Theme.Ground };
            addressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); addressPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TextBox addressInput = Input(); addressInput.AccessibleName = "Домен, IP или подсеть";
            TableLayoutPanel addressAdd = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            addressAdd.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); addressAdd.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); addressAdd.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Button addAddress = MainForm.Button("Добавить", false); addAddress.Margin = new Padding(0); addressAdd.Controls.Add(addressInput, 0, 0); addressAdd.Controls.Add(addAddress, 1, 0);
            addressPage.Controls.Add(addressAdd, 0, 0); addressPage.Controls.Add(Framed(destinations), 0, 1);
            TableLayoutPanel appPage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0), Visible = false, BackColor = Theme.Ground };
            appPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); appPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); appPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); appPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ChoiceButton appAction = new ChoiceButton { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) }; appAction.Items.AddRange(new object[] { "Для выбранного .exe: через VPN", "Для выбранного .exe: напрямую" }); appAction.SelectedIndex = 0; appPage.Controls.Add(appAction, 0, 0);
            TableLayoutPanel addByName = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            addByName.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); addByName.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); addByName.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TextBox nameInput = Input(); nameInput.AccessibleName = "Имя процесса .exe"; Button addName = MainForm.Button("Добавить по имени", false); addName.Margin = new Padding(0);
            addByName.Controls.Add(nameInput, 0, 0); addByName.Controls.Add(addName, 1, 0); appPage.Controls.Add(addByName, 0, 1);
            Button addApp = MainForm.Button("Выбрать приложение .exe…", false); addApp.Margin = new Padding(0, 0, 0, 8); appPage.Controls.Add(addApp, 0, 2); appPage.Controls.Add(Framed(apps), 0, 3);
            body.Controls.Add(addressPage); body.Controls.Add(appPage);
            Label help = MainForm.TextLabel("Без порта и без http://, например example.com, *.example.com, 192.0.2.5, 10.0.0.0/8.", Theme.SmallSize, Theme.Faint); help.Margin = new Padding(0, 6, 0, 0); layout.Controls.Add(help, 0, 4);
            // Footer: one row, buttons sized to their text, «Сохранить» in the right corner.
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0), BackColor = Theme.Ground };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Button import = MainForm.Button("Импорт", false), export = MainForm.Button("Экспорт", false), removeSelected = MainForm.Button("Удалить выбранное", false), save = MainForm.Button("Сохранить", true);
            import.Margin = new Padding(0, 0, 8, 0); export.Margin = new Padding(0, 0, 8, 0); removeSelected.Margin = new Padding(0); save.Margin = new Padding(0);
            footer.Controls.Add(import, 0, 0); footer.Controls.Add(export, 1, 0); footer.Controls.Add(removeSelected, 2, 0); footer.Controls.Add(save, 4, 0);
            layout.Controls.Add(footer, 0, 5);
            Controls.Add(layout);

            Action refresh = delegate {
                destinations.Items.Clear(); destinations.Items.AddRange(Value.Destinations.Cast<object>().ToArray());
                apps.Items.Clear(); apps.Items.AddRange(Value.Applications.Cast<object>().ToArray());
                tabs.Tabs = new[] { "Домены и IP · " + Value.Destinations.Count, "Приложения · " + Value.Applications.Count };
            };
            refresh();
            mode.SelectedIndexChanged += delegate { Value.Mode = mode.SelectedIndex == 0 ? "selective" : "general"; explanation.Text = Explain(); destinations.Invalidate(); };
            tabs.SelectedIndexChanged += delegate {
                bool app = tabs.SelectedIndex == 1; addressPage.Visible = !app; appPage.Visible = app; (app ? (Control)appPage : addressPage).BringToFront();
                help.Text = !app ? "Без порта и без http://, например example.com, *.example.com, 192.0.2.5, 10.0.0.0/8." : EngineSpec.SupportsApplications ? "Правила .exe имеют приоритет над адресами. Если владелец соединения неизвестен, используется VPN." : "Правила .exe требуют расширенной сборки движка Netlane. В официальном CLI этой функции нет.";
            };
            destinations.Tag = (Func<string>)delegate { return Value.Mode == "selective" ? "через VPN" : "напрямую"; };
            export.Click += delegate {
                using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Правила Netlane (*.netlanerules)|*.netlanerules|JSON (*.json)|*.json", FileName = FileNameFor(set.Name), Title = "Экспорт правил" }) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try { Value.Validate(); File.WriteAllText(dialog.FileName, RulesFile.Export(Value), new UTF8Encoding(false)); MessageBox.Show(this, "Файл правил сохранён. Пароля и профиля в нём нет — его можно передать другому человеку.", "Экспорт правил", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch (FormatException error) { MessageBox.Show(this, error.Message, "Экспорт правил", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    catch (Exception) { MessageBox.Show(this, "Не удалось сохранить файл правил.", "Экспорт правил", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            };
            import.Click += delegate {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Правила Netlane (*.netlanerules;*.wttrules;*.rhrules;*.json)|*.netlanerules;*.wttrules;*.rhrules;*.json", Title = "Импорт правил в этот набор" }) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try {
                        if (new FileInfo(dialog.FileName).Length > 65536) throw new FormatException("Файл правил слишком большой.");
                        Value = RulesFile.Import(File.ReadAllText(dialog.FileName)); refresh(); mode.SelectedIndex = Value.Mode == "selective" ? 0 : 1;
                        string note = Value.Applications.Count > 0 && !EngineSpec.SupportsApplications ? " Правила приложений сохранены, но применяются только в полной сборке Netlane." : "";
                        MessageBox.Show(this, "Загружено правил: адресов " + Value.Destinations.Count + ", приложений " + Value.Applications.Count + ". Нажмите «Сохранить», чтобы применить их к набору." + note, "Импорт правил", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (FormatException error) { MessageBox.Show(this, error.Message, "Импорт правил", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    catch (Exception) { MessageBox.Show(this, "Не удалось прочитать файл правил.", "Импорт правил", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            };
            EventHandler addDestination = delegate { if (addressInput.Text.Trim().Length == 0) return; try { Routing candidate = Value.Copy(); candidate.Destinations.Add(Routing.NormalizeDestination(addressInput.Text.Trim())); candidate.Validate(); Value = candidate; addressInput.Clear(); refresh(); } catch (FormatException error) { MessageBox.Show(this, error.Message, "Правило", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            addAddress.Click += addDestination;
            addressInput.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Enter) { addDestination(sender, EventArgs.Empty); args.Handled = args.SuppressKeyPress = true; } };
            addApp.Click += delegate { using (OpenFileDialog picker = new OpenFileDialog { Filter = "Приложение (*.exe)|*.exe", Multiselect = false }) if (picker.ShowDialog(this) == DialogResult.OK) { try { Routing candidate = Value.Copy(); candidate.Applications.Add(new AppRule { Executable = Path.GetFullPath(picker.FileName), Action = appAction.SelectedIndex == 0 ? "vpn" : "direct" }); candidate.Validate(); Value = candidate; refresh(); } catch (FormatException error) { MessageBox.Show(this, error.Message, "Приложение", MessageBoxButtons.OK, MessageBoxIcon.Warning); } } };
            addName.Click += delegate { string name = nameInput.Text.Trim(); if (name.Length == 0) return; try { Routing candidate = Value.Copy(); candidate.Applications.Add(new AppRule { Name = name, Action = appAction.SelectedIndex == 0 ? "vpn" : "direct" }); candidate.Validate(); Value = candidate; nameInput.Clear(); refresh(); } catch (FormatException error) { MessageBox.Show(this, error.Message, "Приложение по имени", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            removeSelected.Click += delegate { if (tabs.SelectedIndex == 0 && destinations.SelectedIndex >= 0) { Value.Destinations.RemoveAt(destinations.SelectedIndex); refresh(); } else if (tabs.SelectedIndex == 1 && apps.SelectedIndex >= 0) { Value.Applications.RemoveAt(apps.SelectedIndex); refresh(); } };
            save.Click += delegate { try { Value.Validate(); Action handler = SaveRequested; if (handler != null) handler(); } catch (FormatException error) { MessageBox.Show(this, error.Message, "Правила", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };

            if (readOnly) {
                foreach (Control c in new Control[] { mode, addressInput, addAddress, nameInput, addName, addApp, appAction, import, removeSelected }) c.Enabled = false;
                save.Visible = false;
                explanation.Text = set.Managed ? "Правила обновляются через подписку. Для изменений создайте личную копию." : "Встроенный набор: весь трафик идёт через VPN. Чтобы настроить свой, создайте новый набор или скопируйте этот.";
            }
            if (!EngineSpec.SupportsApplications) { addApp.Enabled = addName.Enabled = appAction.Enabled = nameInput.Enabled = false; }
        }
        string Explain() { return Value.Mode == "selective" ? "Только домены и IP из списка идут через VPN. Остальной трафик — напрямую." : "Домены и IP из списка идут напрямую. Остальной трафик — через VPN."; }
        internal static string FileNameFor(string name) {
            string safe = new string((name ?? "rules").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            return (safe.Length == 0 ? "rules" : safe) + ".netlanerules";
        }
        // Rules in a monospace font on a card; the address list shows where its entries go on the right.
        static ListBox List() {
            ListBox list = new DarkRuleList { BackColor = Theme.Card, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, IntegralHeight = false, HorizontalScrollbar = false, Margin = new Padding(0), Font = Theme.Mono(10), ItemHeight = 30, DrawMode = DrawMode.OwnerDrawFixed };
            list.DrawItem += delegate(object sender, DrawItemEventArgs args) {
                bool selected = (args.State & DrawItemState.Selected) != 0;
                using (Brush fill = new SolidBrush(selected ? Theme.Selection : Theme.Card)) args.Graphics.FillRectangle(fill, args.Bounds);
                if (args.Index < 0) return;
                Rectangle text = args.Bounds; text.Width = Math.Min(text.Width, list.Parent == null ? text.Width : list.Parent.ClientSize.Width); text.Inflate(-(int)Theme.Px(args.Graphics, 12), 0);
                Func<string> where = list.Tag as Func<string>;
                if (where != null) using (Font small = Theme.Font(Theme.SmallSize)) TextRenderer.DrawText(args.Graphics, where(), small, text, Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(args.Graphics, list.Items[args.Index].ToString(), list.Font, new Rectangle(text.X, text.Y, text.Width - (where != null ? (int)Theme.Px(args.Graphics, 90) : 0), text.Height), Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                using (Pen line = new Pen(Theme.Line)) args.Graphics.DrawLine(line, args.Bounds.Left, args.Bounds.Bottom - 1, args.Bounds.Right, args.Bounds.Bottom - 1);
                if ((args.State & DrawItemState.Focus) != 0 && list.Focused) using (Pen focus = new Pen(Theme.Accent)) args.Graphics.DrawRectangle(focus, args.Bounds.X, args.Bounds.Y, args.Bounds.Width - 1, args.Bounds.Height - 2);
            };
            // Owner-drawn rows leave stale edges when the list grows; redraw everything on resize.
            list.Resize += delegate { list.Invalidate(); };
            return list;
        }
        static Control Framed(Control inner) { RoundedPanel frame = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(2, 6, 2, 6), Margin = new Padding(0) }; frame.Controls.Add(new DarkRuleListViewport((DarkRuleList)inner) { Dock = DockStyle.Fill }); return frame; }
        static TextBox Input() { return new TextBox { Dock = DockStyle.Fill, BackColor = Theme.Card, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 8, 6), MaxLength = 260, Font = Theme.Font(Theme.BodySize) }; }
    }
}
