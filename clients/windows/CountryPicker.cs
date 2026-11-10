using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    // «Сменить флаг»: search by Russian name, «Без флага» first.
    internal sealed class CountryPicker : Form {
        readonly TextBox search = new TextBox { Dock = DockStyle.Top, BackColor = Theme.Card, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0) };
        readonly ListBox list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Ground, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false };
        string[] codes = new string[0];
        internal string Result { get; private set; }

        internal static IEnumerable<string> Search(string query) {
            string q = (query ?? "").Trim();
            for (int i = 0; i < CountryNames.All.GetLength(0); i++)
                if (q.Length == 0 || CountryNames.All[i, 1].IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || String.Equals(CountryNames.All[i, 0], q, StringComparison.OrdinalIgnoreCase)) yield return CountryNames.All[i, 0];
        }
        internal static string NameOf(string code) {
            for (int i = 0; i < CountryNames.All.GetLength(0); i++) if (CountryNames.All[i, 0] == code) return CountryNames.All[i, 1];
            return code;
        }
        // True when the user chose something; code null = «Без флага».
        internal static bool Pick(IWin32Window owner, string current, out string code) {
            using (CountryPicker picker = new CountryPicker(current)) {
                code = null;
                if (picker.ShowDialog(owner) != DialogResult.OK) return false;
                code = picker.Result; return true;
            }
        }
        CountryPicker(string current) {
            Text = "Флаг сервера"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Ground; ForeColor = Theme.Ink; Font = Theme.Font(Theme.BodySize); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(360, 460); Icon = AppIcon.Shared;
            Padding = new Padding(14);
            list.ItemHeight = 34;
            Label prompt = new Label { Text = "Поиск страны", Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Muted, Font = Theme.Font(Theme.SmallSize) };
            Panel gap = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Ground };
            Controls.Add(list); Controls.Add(gap); Controls.Add(search); Controls.Add(prompt);
            search.TextChanged += delegate { Fill(); };
            list.DrawItem += DrawRow;
            list.DoubleClick += delegate { Accept(); };
            list.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Enter) { Accept(); args.Handled = true; } };
            search.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = Math.Min(1, list.Items.Count - 1); args.Handled = true; } else if (args.KeyCode == Keys.Enter) { Accept(); args.Handled = true; } };
            KeyPreview = true; KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; } };
            Fill();
            int index = current == null ? 0 : Array.IndexOf(codes, current) + 1;
            if (index >= 0 && index < list.Items.Count) list.SelectedIndex = index;
        }
        void Fill() {
            codes = Search(search.Text).ToArray();
            list.BeginUpdate(); list.Items.Clear(); list.Items.Add("Без флага"); foreach (string code in codes) list.Items.Add(NameOf(code)); list.EndUpdate();
            if (list.Items.Count > 1 && search.Text.Trim().Length > 0) list.SelectedIndex = 1; else if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        void Accept() { if (list.SelectedIndex < 0) return; Result = list.SelectedIndex == 0 ? null : codes[list.SelectedIndex - 1]; DialogResult = DialogResult.OK; }
        void DrawRow(object sender, DrawItemEventArgs args) {
            if (args.Index < 0) return;
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (Brush fill = new SolidBrush(selected ? Theme.Selection : Theme.Ground)) args.Graphics.FillRectangle(fill, args.Bounds);
            args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int badge = args.Bounds.Height - 12;
            string code = args.Index == 0 ? null : codes[args.Index - 1];
            Flags.DrawBadge(args.Graphics, new Rectangle(args.Bounds.X + 8, args.Bounds.Y + 6, badge, badge), code, args.Index == 0 ? "—" : (string)list.Items[args.Index]);
            TextRenderer.DrawText(args.Graphics, (string)list.Items[args.Index], Font, new Rectangle(args.Bounds.X + badge + 18, args.Bounds.Y, args.Bounds.Width - badge - 24, args.Bounds.Height), Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
