using System;
using System.Drawing;
using System.Windows.Forms;

namespace Netlane {
    // "Добавить профиль": the user pastes a tt:// link; nothing reads the clipboard on its own.
    // Validation and the actual import stay in MainForm, passed in as a callback.
    internal sealed class ImportForm : Form {
        readonly Func<string, string> validate;
        readonly TextBox link = new TextBox { Multiline = true, MaxLength = 32768, BackColor = Theme.Card, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, ScrollBars = ScrollBars.None, WordWrap = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Font = Theme.Mono(10) };
        internal string Link { get; private set; }
        internal bool OpenTomlRequested { get; private set; }

        // validate returns null for an acceptable link or the message to show.
        internal ImportForm(Func<string, string> validate) {
            this.validate = validate;
            Text = "Импорт профиля"; Icon = AppIcon.Shared; BackColor = Theme.Ground; ForeColor = Theme.Ink; Font = Theme.Font(Theme.BodySize);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, 300); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Gutter, 16, Theme.Gutter, 16), ColumnCount = 1, RowCount = 4, BackColor = Theme.Ground };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Label title = MainForm.TextLabel("Добавить профиль", Theme.HeadingSize, Theme.Ink); title.Font = Theme.Font(Theme.HeadingSize, true);
            Label hint = MainForm.TextLabel("Вставьте ссылку tt:// или ссылку подписки https://…/sub/… из панели. Подключение не начнётся само.", Theme.SmallSize, Theme.Muted); hint.TextAlign = ContentAlignment.TopLeft;
            layout.Controls.Add(title, 0, 0); layout.Controls.Add(hint, 0, 1); layout.Controls.Add(link, 0, 2);
            Button openToml = MainForm.Button("Открыть TOML…", false), cancel = MainForm.Button("Отмена", false), import = MainForm.Button("Импортировать", true);
            TableLayoutPanel buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            openToml.Margin = new Padding(0); cancel.Margin = new Padding(0, 0, 8, 0); import.Margin = new Padding(0);
            buttons.Controls.Add(openToml, 0, 0); buttons.Controls.Add(cancel, 2, 0); buttons.Controls.Add(import, 3, 0);
            layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
            AcceptButton = import; CancelButton = cancel;
            import.Click += delegate { string message; if (!TryAccept(out message)) MessageBox.Show(this, message, "Профиль", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            openToml.Click += delegate { OpenTomlRequested = true; DialogResult = DialogResult.Cancel; };
            FormClosed += delegate { link.Clear(); };
        }

        // The Import button: accepts a trimmed link the validator allows, otherwise reports why not.
        internal bool TryAccept(out string message) {
            string candidate = link.Text.Trim();
            message = candidate.Length == 0 ? "Вставьте ссылку tt:// или ссылку подписки из панели администратора." : validate(candidate);
            if (message != null) return false;
            Link = candidate; DialogResult = DialogResult.OK;
            return true;
        }

        // Preview only: show an example link in the field.
        internal void ShowSample() { link.Text = "tt://?AQEBEnR1bm5lbC5leGFtcGxlLmNvbQ…"; link.SelectionStart = link.Text.Length; }

        protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); int dark = 1; try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch (DllNotFoundException) { } }
    }
}
