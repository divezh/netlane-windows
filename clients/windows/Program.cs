using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyVersion("0.6.8.0")]
[assembly: AssemblyFileVersion("0.6.8.0")]
[assembly: AssemblyProduct("Netlane Connect")]

namespace Netlane {
    internal static class Program {
        internal const string Version = "0.6.8";
        internal const string ProductName = "Netlane Connect";
        internal static bool Rendering;
        // Autostart runs «Netlane.exe --tray»: a normal start with the window hidden in the tray.
        internal static bool StartHidden(string[] args) { return args != null && args.Length == 1 && args[0] == "--tray"; }
        [STAThread]
        static int Main(string[] args) {
            // A downloaded update installs itself and restarts the app (Updater.LaunchInstaller).
            if (args.Length == 3 && args[0] == "--apply-update") { int parent; return Int32.TryParse(args[2], out parent) ? Updater.Apply(args[1], parent) : 2; }
            if (args.Length == 3 && args[0] == "--worker") { int parent; return Int32.TryParse(args[2], out parent) ? Worker.Run(args[1], parent) : 2; }
            try { EngineLog.Write("startup: main begin; process age " + (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds + " ms"); } catch (Exception) { }
            Tls.Enable();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args2) {
                EngineLog.Write("ui exception: " + args2.Exception);
                if (Rendering) { Console.Error.WriteLine("ui exception: " + args2.Exception); return; }
                MessageBox.Show("Внутренняя ошибка интерфейса записана в журнал: " + EngineLog.Path, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args2) { EngineLog.Write("unhandled: " + args2.ExceptionObject); };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs args2) { EngineLog.Write("unobserved: " + args2.Exception); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            PreviewRequest request;
            try { request = PreviewRequest.Parse(args); }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
            if (request != null) {
                Rendering = request.Render;
                if (request.Page == "settings") { request.Settings = true; request.Page = "connection"; }
                if (request.Page == "import") {
                    using (ImportForm dialog = new ImportForm(delegate(string link) { return null; })) {
                        dialog.ShowSample();
                        if (!request.Render) { Application.Run(dialog); return 0; }
                        dialog.Show(); Application.DoEvents();
                        using (Bitmap bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(request.Output); }
                    }
                    return 0;
                }
                // Settings are pages of the main window: --page=servers|rules|connection|about opens it there.
                using (Form preview = new MainForm(true, request.Page == "home" ? null : request.Page, request.State, request.Host)) {
                    if (!request.Render) { Application.Run(preview); return 0; }
                    try {
                        preview.Show(); Application.DoEvents();
                        string dump = Environment.GetEnvironmentVariable("NETLANE_LAYOUT_DUMP");
                        if (!String.IsNullOrEmpty(dump)) DumpLayout(preview, dump);
                        // A popup menu is a separate window: take it from the screen, DWM corners and shadow included.
                        if (request.Page == "routing-menu") {
                            preview.Activate(); ContextMenuStrip menu = ((MainForm)preview).OpenRoutingMenuForPreview();
                            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
                            Rectangle screen = Rectangle.Union(preview.RectangleToScreen(preview.ClientRectangle), Rectangle.Inflate(menu.Bounds, 16, 16));
                            using (Bitmap shot = new Bitmap(screen.Width, screen.Height)) {
                                using (Graphics g = Graphics.FromImage(shot)) g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
                                shot.Save(request.Output);
                            }
                            return 0;
                        }
                        // Only the client area: DrawToBitmap paints the window frame in the classic style,
                        // which is not what Windows 10 and 11 show.
                        using (Bitmap bitmap = new Bitmap(preview.Width, preview.Height)) {
                            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size));
                            Point client = preview.PointToScreen(Point.Empty);
                            Rectangle area = Rectangle.Intersect(new Rectangle(client.X - preview.Left, client.Y - preview.Top, preview.ClientSize.Width, preview.ClientSize.Height), new Rectangle(Point.Empty, bitmap.Size));
                            using (Bitmap content = bitmap.Clone(area, bitmap.PixelFormat)) content.Save(request.Output);
                        }
                    } catch (Exception error) { Console.Error.WriteLine("render failed: " + error); return 3; }
                }
                return 0;
            }
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            // An instance of the former product name may still be running during an upgrade.
            if (LegacyInstanceRunning(sid)) { if (StartHidden(args)) return 0; MessageBox.Show(ProductName + " уже запущен. Откройте его значок в трее.", ProductName); return 0; }
            using (Mutex instance = new Mutex(false, @"Local\Netlane." + sid)) {
                if (!instance.WaitOne(0, false)) { if (StartHidden(args)) return 0; MessageBox.Show(ProductName + " уже запущен. Откройте его значок в трее.", ProductName); return 0; }
                Native.FreeConsole();
                try { Application.Run(new MainForm(false) { StartHidden = StartHidden(args) }); }
                finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
        static void DumpLayout(Control root, string path) {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            Action<Control, int> walk = null;
            walk = delegate(Control control, int depth) {
                string caption = control.Text ?? "";
                StatusBlock status = control as StatusBlock;
                if (status != null) caption = status.Title + " | " + status.Subtitle;
                ConnectBar bar = control as ConnectBar;
                if (bar != null) caption = bar.Caption + " | " + bar.ClockText + " | " + bar.SpeedText;
                text.Append(' ', depth * 2).Append(control.GetType().Name).Append(" visible=").Append(control.Visible)
                    .Append(" bounds=").Append(control.Bounds).Append(" text=\"").Append(caption.Replace("\n", " ")).AppendLine("\"");
                foreach (Control child in control.Controls) walk(child, depth + 1);
            };
            walk(root, 0);
            File.WriteAllText(path, text.ToString(), new System.Text.UTF8Encoding(false));
        }
        static bool LegacyInstanceRunning(string sid) {
            try {
                using (Mutex legacy = new Mutex(false, @"Local\WinTrustTunnel." + sid)) {
                    try { if (legacy.WaitOne(0, false)) { legacy.ReleaseMutex(); return false; } return true; }
                    catch (AbandonedMutexException) { legacy.ReleaseMutex(); return false; }
                }
            } catch (Exception) { return false; }
        }
    }
    // Screenshot and preview modes for checking every screen and connection state:
    //   --render out.png [--state=connected] [--page=home|settings|import|no-servers|many-sets|servers-menu], --render-settings out.png,
    //   --render-routing out.png (the rules page), --preview [--state=...] [--page=...], --preview-settings / --render-settings out.png
    //   [--page=servers|rules|connection|about]. Returns null for a normal start.
    internal sealed class PreviewRequest {
        internal bool Render;
        internal string Output;
        internal string Page = "home";
        internal ConnectionView? State;
        internal string Host;
        internal static readonly string[] Pages = { "home", "settings", "import", "no-servers", "many-sets", "servers-menu", "subscribed", "servers", "rules", "connection", "about", "log", "one-server", "three-servers", "six-servers", "ten-servers", "account", "account-servers", "expired", "routing-menu" };
        internal bool Settings;
        internal static PreviewRequest Parse(string[] args) {
            if (args == null || args.Length == 0) return null;
            PreviewRequest request = new PreviewRequest();
            int next;
            switch (args[0]) {
                case "--preview": next = 1; break;
                case "--preview-settings": request.Settings = true; request.Page = "servers"; next = 1; break;
                case "--render": case "--render-settings": case "--render-routing":
                    if (args.Length < 2 || args[1].StartsWith("--")) throw new ArgumentException(args[0] + " needs an output .png path.");
                    request.Render = true; request.Output = args[1]; next = 2;
                    if (args[0] == "--render-settings") { request.Settings = true; request.Page = "servers"; }
                    if (args[0] == "--render-routing") { request.Settings = true; request.Page = "rules"; }
                    break;
                default: return null;
            }
            for (int i = next; i < args.Length; i++) {
                string arg = args[i];
                if (arg.StartsWith("--state=")) {
                    string name = arg.Substring(8);
                    ConnectionView view;
                    if (name.Length == 0 || Char.IsDigit(name[0]) || !Enum.TryParse(name, true, out view) || !Enum.IsDefined(typeof(ConnectionView), view)) throw new ArgumentException("Unknown state: " + name + ". Use one of: " + String.Join(", ", Enum.GetNames(typeof(ConnectionView))) + ".");
                    request.State = view;
                } else if (arg.StartsWith("--page=")) {
                    string page = arg.Substring(7).ToLowerInvariant();
                    if (Array.IndexOf(Pages, page) < 0) throw new ArgumentException("Unknown page: " + page + ". Use " + String.Join(", ", Pages) + ".");
                    request.Page = page;
                } else if (arg.StartsWith("--host=") && arg.Length > 7) {
                    request.Host = arg.Substring(7);
                } else throw new ArgumentException("Unknown preview argument: " + arg);
            }
            return request;
        }
    }

}
