using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    // Drag and drop (spec §4): a .toml adds a server, a .netlanerules/.wttrules/.rhrules becomes a new rule set.
    internal sealed partial class SettingsForm {
        // Drops land on whatever control is under the cursor: accept them on the whole tree, each control once.
        internal static void AcceptDrops(Control root, Action<string> dropped) {
            HashSet<Control> wired = new HashSet<Control>();
            DragEventHandler enter = delegate(object sender, DragEventArgs args) { args.Effect = DroppedFile(args.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; };
            DragEventHandler drop = delegate(object sender, DragEventArgs args) { string file = DroppedFile(args.Data); if (file != null) dropped(file); };
            ControlEventHandler added = null;
            Action<Control> wire = null;
            wire = delegate(Control c) {
                if (!wired.Add(c)) return;
                c.AllowDrop = true; c.DragEnter += enter; c.DragDrop += drop; c.ControlAdded += added;
                c.Disposed += delegate { wired.Remove(c); };
                foreach (Control child in c.Controls) wire(child);
            };
            added = delegate(object sender, ControlEventArgs args) { wire(args.Control); };
            wire(root);
        }
        // "server", "rules" or null for files the app does not take.
        internal static string DropKind(string path) {
            string ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            return ext == ".toml" ? "server" : ext == ".netlanerules" || ext == ".wttrules" || ext == ".rhrules" ? "rules" : null;
        }
        internal static string DroppedFile(IDataObject data) {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length == 1 && DropKind(files[0]) != null ? files[0] : null;
        }
        internal void Drop(string file) {
            if (DropKind(file) == "server") { ShowPage("servers"); host.ImportTomlFile(file); return; }
            ShowPage("rules");
            if (current != "rules") return;
            RuleSet created = ImportRulesFile(file);
            if (created != null) OpenSet(created.Id);
        }
        // A rules file as a new set named after the file; « (2)» and up on a clash.
        internal RuleSet ImportRulesFile(string path) {
            try {
                if (new FileInfo(path).Length > 65536) throw new FormatException("Файл правил слишком большой.");
                Routing routing = RulesFile.Import(File.ReadAllText(path));
                string stem = Path.GetFileNameWithoutExtension(path).Trim();
                if (stem.Length == 0) stem = "Импорт";
                if (stem.Length > 40) stem = stem.Substring(0, 40).Trim();
                string name = stem; int n = 2;
                while (state.RuleSets.Any(r => String.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) name = stem + " (" + n++ + ")";
                RuleSet set = state.AddRuleSet(name, routing); host.SaveState();
                return set;
            }
            catch (FormatException error) { if (Visible) MessageBox.Show(this, error.Message, "Импорт правил", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            catch (IOException) { if (Visible) MessageBox.Show(this, "Не удалось прочитать файл правил.", "Импорт правил", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
        }
    }
}
