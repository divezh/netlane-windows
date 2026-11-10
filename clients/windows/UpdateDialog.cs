using System;
using System.Drawing;
using System.Windows.Forms;

namespace Netlane {
    // «Доступна версия X» with one button that downloads, verifies and installs the update.
    internal sealed class UpdateDialog : Form {
        readonly UpdateManifest update;
        readonly DarkButton install = new DarkButton { Kind = ButtonKind.Primary, Text = "Обновить", Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly DarkButton later = new DarkButton { Kind = ButtonKind.Secondary, Text = "Позже", Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 0) };
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, Font = Theme.Font(Theme.CaptionSize), ForeColor = Theme.Faint, TextAlign = ContentAlignment.MiddleLeft };
        readonly Panel track = new Panel { Dock = DockStyle.Fill, Height = 6, BackColor = Theme.Line };
        readonly Panel fill = new Panel { BackColor = Theme.Accent, Width = 0, Height = 6, Dock = DockStyle.Left };

        internal UpdateDialog(UpdateManifest update) {
            this.update = update;
            Text = "Обновление Netlane";
            BackColor = Theme.Ground; ForeColor = Theme.Ink; Font = Theme.Font(Theme.BodySize);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(420, 300);

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(22, 18, 22, 18), BackColor = Theme.Ground, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 34, 76, 22, 10, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label title = new Label { Text = "Доступна версия " + update.Version, Dock = DockStyle.Fill, Font = Theme.Font(15f, true), ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            Label notes = new Label {
                Text = String.IsNullOrEmpty(update.Notes) ? "Рекомендуем обновиться: исправления и новые функции." : update.Notes,
                Dock = DockStyle.Fill, Font = Theme.Font(Theme.BodySize), ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.TopLeft, AutoEllipsis = false, UseMnemonic = false
            };
            track.Controls.Add(fill);
            TableLayoutPanel buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            buttons.Controls.Add(install, 0, 0); buttons.Controls.Add(later, 1, 0);

            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(notes, 0, 1);
            layout.Controls.Add(status, 0, 2);
            layout.Controls.Add(track, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout);
            AcceptButton = install; CancelButton = later;
            later.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            install.Click += async delegate { await InstallAsync(); };
        }

        async System.Threading.Tasks.Task InstallAsync() {
            install.Enabled = false; later.Enabled = false;
            status.Text = "Скачивание…";
            Progress<int> progress = new Progress<int>(delegate(int percent) {
                status.Text = "Скачивание… " + percent + "%";
                fill.Width = Math.Max(0, track.ClientSize.Width * percent / 100);
            });
            try {
                string unpacked = await Updater.DownloadAndUnpackAsync(update, progress);
                status.Text = "Устанавливаем и перезапускаем…";
                Updater.LaunchInstaller(unpacked, update.Version);
                DialogResult = DialogResult.OK;
                Close();
            } catch (Exception error) {
                status.Text = "Не удалось обновить: " + error.Message;
                install.Enabled = true; later.Enabled = true;
            }
        }
    }
}
