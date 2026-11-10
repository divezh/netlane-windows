using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    // «Правила» (spec §3): rule sets on the left, the editor of the chosen one on the right.
    internal sealed partial class SettingsForm {
        readonly Panel setList = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, AutoScroll = true };
        readonly Panel editorHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0) };
        readonly Label setTitle = Heading("");
        IconButton setActions;
        Button useSet;
        RuleSetEditor editor;
        string editingId;

        partial void CanLeave(ref bool ok) { if (ok && current == "rules") ok = ConfirmDiscard(); }

        Control BuildRulesPage() {
            TableLayoutPanel page = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, BackColor = Theme.Ground, Margin = new Padding(0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Panel left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ground, Margin = new Padding(0) };
            Label heading = Heading("Наборы"); heading.Dock = DockStyle.Top; heading.Height = 34;
            Button add = MainForm.Button("+ Новый набор", false); add.Dock = DockStyle.Bottom; add.Height = 38; add.Margin = new Padding(0);
            add.Click += delegate { if (!ConfirmDiscard()) return; RuleSet created = NewRuleSet(); if (created != null) OpenSet(created.Id); };
            left.Controls.Add(setList); left.Controls.Add(add); left.Controls.Add(heading);
            page.Controls.Add(left, 0, 0);
            TableLayoutPanel right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Theme.Ground, Margin = new Padding(0) };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            setActions = new IconButton { Glyph = IconGlyph.More, Anchor = AnchorStyles.None, Size = new Size(32, 32), Margin = new Padding(0) };
            tips.SetToolTip(setActions, "Переименовать, копировать, удалить");
            setActions.Click += delegate { SetMenu().Show(setActions, new Point(0, setActions.Height)); };
            useSet = MainForm.Button("Использовать", true); useSet.Margin = new Padding(0, 4, 8, 8);
            useSet.Click += delegate { if (ConfirmDiscard()) UseRuleSet(editingId); };
            tips.SetToolTip(useSet, state.KillSwitch ? MainForm.KillSwitchSwitchHint : "Включить этот набор. Если VPN работает, он переподключится с новыми правилами.");
            right.Controls.Add(setTitle, 0, 0); right.Controls.Add(useSet, 1, 0); right.Controls.Add(setActions, 2, 0);
            right.Controls.Add(editorHost, 0, 1); right.SetColumnSpan(editorHost, 3);
            Panel rightWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 0, 0), BackColor = Theme.Ground, Margin = new Padding(0) };
            rightWrap.Controls.Add(right); page.Controls.Add(rightWrap, 1, 0);
            OpenSet(state.SelectedRuleSet.Id);
            return page;
        }
        ContextMenuStrip SetMenu() {
            RuleSet set = Find(editingId);
            ContextMenuStrip menu = new ContextMenuStrip { BackColor = Theme.Card, ForeColor = Theme.Ink, ShowImageMargin = false, Renderer = new DarkMenuRenderer() };
            ToolStripItem rename = menu.Items.Add("Переименовать…", null, delegate {
                string name = InputDialog.Ask(this, "Название набора", "Новое название набора правил:", set.Name);
                if (name == null) return;
                string error = RenameRuleSet(set.Id, name);
                if (error != null) MessageBox.Show(this, error, "Название набора", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
            menu.Items.Add("Копировать", null, delegate { if (!ConfirmDiscard()) return; RuleSet copy = CopyRuleSet(set.Id); if (copy != null) OpenSet(copy.Id); });
            ToolStripItem delete = menu.Items.Add("Удалить…", null, delegate {
                if (MessageBox.Show(this, "Удалить набор «" + set.Name + "»?", "Удаление набора", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                DeleteRuleSet(set.Id);
            });
            delete.ForeColor = Theme.Danger;
            rename.Enabled = delete.Enabled = set != null && !set.Builtin && !set.Managed;
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            return menu;
        }
        // The set the main window and the engine use; while connected the host reconnects with it.
        internal void UseRuleSet(string id) {
            if (Find(id) == null || !state.AvailableRuleSets.Any(r=>r.Id == id)) return;
            if (host.PreviewOnly) state.SelectRuleSet(id); else host.SwitchTo(null, id);
            if (current == "rules") { if (editingId != id) OpenSet(id); else RebuildSetList(); }
        }
        RuleSet Find(string id) { return state.RuleSets.FirstOrDefault(r => r.Id == id); }

        void RebuildSetList() {
            setList.SuspendLayout();
            foreach (Control old in setList.Controls.Cast<Control>().ToList()) old.Dispose();
            setList.Controls.Clear();
            for (int i = state.RuleSets.Count - 1; i >= 0; i--) {
                RuleSet set = state.RuleSets[i];
                string mark = set.Id == state.SelectedRuleSet.Id ? "  ●" : "";
                NavItem item = new NavItem { Text = set.DisplayName + mark, Dock = DockStyle.Top, Height = 36, Tag = set.Id, Selected = set.Id == editingId };
                tips.SetToolTip(item, set.Managed ? "От администратора. Обновляется через подписку. Для правок создайте личную копию." : set.Builtin ? "Весь трафик через VPN. Изменить нельзя." : (set.Id == state.SelectedRuleSet.Id ? "Используется сейчас" : "Выбрать на главном экране — кнопкой набора"));
                item.Click += delegate { if (set.Id != editingId && ConfirmDiscard()) OpenSet(set.Id); };
                setList.Controls.Add(item);
            }
            setList.ResumeLayout();
            RuleSet editing = Find(editingId);
            if (editing == null) { OpenSet(state.SelectedRuleSet.Id); return; }
            setTitle.Text = editing.DisplayName;
            bool inUse = editing.Id == state.SelectedRuleSet.Id;
            useSet.Text = inUse ? "Используется" : "Использовать"; useSet.Enabled = !inUse && state.AvailableRuleSets.Any(r=>r.Id == editing.Id);
        }
        void OpenSet(string id) {
            RuleSet set = Find(id) ?? state.SelectedRuleSet;
            editingId = set.Id;
            if (editor != null) { editorHost.Controls.Remove(editor); editor.Dispose(); }
            editor = new RuleSetEditor(set, set.Builtin || set.Managed) { Dock = DockStyle.Fill };
            editor.SaveRequested += delegate { SaveEditor(); };
            editorHost.Controls.Add(editor);
            RebuildSetList();
        }
        bool SaveEditor() {
            if (editor == null || Find(editingId) == null || (Find(editingId).Builtin || Find(editingId).Managed)) return true;
            try {
                state.UpdateRuleSet(editingId, editor.Value);
                editor.MarkSaved(); host.SaveState(); host.RulesChanged(editingId);
                return true;
            } catch (FormatException error) { MessageBox.Show(this, error.Message, "Правила", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        }
        // Unsaved edits: save, drop, or stay.
        internal bool RulesDirtyForTests { get { return editor != null && editor.Dirty; } }
        bool ConfirmDiscard() {
            RuleSet editing = Find(editingId);
            if (editor == null || !editor.Dirty || editing == null || editing.Builtin || editing.Managed) return true;
            EngineLog.Write("rules: unsaved changes in " + editing.Name + ": " + editor.DirtyReport);
            DialogResult answer = MessageBox.Show(this, "В наборе «" + editing.Name + "» есть несохранённые изменения. Сохранить их?", "Правила", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) return SaveEditor();
            OpenSet(editingId);
            return true;
        }

        internal RuleSet NewRuleSet() {
            try { RuleSet set = state.AddRuleSet(UniqueSetName("Новый набор"), new Routing()); host.SaveState(); return set; }
            catch (FormatException error) { MessageBox.Show(this, error.Message, "Новый набор", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
        }
        internal RuleSet CopyRuleSet(string id) {
            RuleSet source = Find(id); if (source == null) return null;
            string name = source.Name + " (копия)"; int n = 2;
            while (state.RuleSets.Any(r => String.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) name = source.Name + " (копия " + n++ + ")";
            try { RuleSet set = state.AddRuleSet(name, source.Routing); host.SaveState(); return set; }
            catch (FormatException error) { MessageBox.Show(this, error.Message, "Копирование набора", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
        }
        // Null on success, otherwise the reason.
        internal string RenameRuleSet(string id, string name) {
            try { state.RenameRuleSet(id, name ?? ""); host.SaveState(); return null; }
            catch (FormatException error) { return error.Message; }
        }
        void DeleteRuleSet(string id) {
            try {
                // The set in use: switch to «Весь трафик» first (reconnects while the VPN runs).
                if (state.SelectedRuleSet.Id == id) { RuleSet builtin = state.RuleSets.First(r => r.Builtin); if (!host.PreviewOnly) host.SwitchTo(null, builtin.Id); else state.SelectRuleSet(builtin.Id); }
                state.RemoveRuleSet(id); host.SaveState();
                OpenSet(state.SelectedRuleSet.Id);
            } catch (FormatException error) { MessageBox.Show(this, error.Message, "Удаление набора", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        string UniqueSetName(string name) {
            string candidate = name; int n = 2;
            while (state.RuleSets.Any(r => String.Equals(r.Name, candidate, StringComparison.OrdinalIgnoreCase))) candidate = name + " " + n++;
            return candidate;
        }
    }
}
