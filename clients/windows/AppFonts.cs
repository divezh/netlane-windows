using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace Netlane {
    // Inter (SIL OFL 1.1), Regular and SemiBold, ships inside Netlane.exe: subset to Latin, Cyrillic and the
    // punctuation and arrows the interface uses, and hinted with ttfautohint for GDI. Each face is loaded twice:
    // into a PrivateFontCollection for GDI+ Font objects and with AddFontMemResourceEx for GDI, which
    // TextRenderer and the native controls draw with. If either fails the interface stays on Segoe UI.
    internal static class AppFonts {
        [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint count);
        static readonly PrivateFontCollection collection = new PrivateFontCollection();
        static readonly List<IntPtr> buffers = new List<IntPtr>();
        static FontFamily regular, semibold;
        static bool loaded;

        internal static bool Embedded { get { Load(); return regular != null; } }

        // GDI+ does not find a private face by its name: fonts in pixels are made from the family too.
        internal static Font CreatePixels(float size, bool strong) {
            Load();
            if (regular != null) return new Font(strong && semibold != null ? semibold : regular, size, FontStyle.Regular, GraphicsUnit.Pixel);
            return new Font("Segoe UI", size, strong ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
        internal static Font Create(float size, bool strong) {
            Load();
            if (regular != null) {
                if (strong && semibold != null) return new Font(semibold, size, FontStyle.Regular);
                return new Font(regular, size, strong ? FontStyle.Bold : FontStyle.Regular);
            }
            return new Font(strong ? "Segoe UI Semibold" : "Segoe UI", size);
        }

        static void Load() {
            if (loaded) return;
            loaded = true;
            // Mono (the Linux check build) keeps memory fonts only half-working: stay on the default face there.
            if (Type.GetType("Mono.Runtime") != null) return;
            try {
                if (!Add("font.Inter-Regular.ttf") || !Add("font.Inter-SemiBold.ttf")) return;
                foreach (FontFamily family in collection.Families) {
                    if (family.Name == "Inter") regular = family;
                    else if (family.Name == "Inter SemiBold") semibold = family;
                }
            } catch (Exception error) { regular = semibold = null; EngineLog.Write("fonts: " + error.Message); }
        }
        // The memory stays allocated for the life of the process: both GDI and GDI+ read from it.
        static bool Add(string resource) {
            byte[] data;
            using (Stream stream = typeof(AppFonts).Assembly.GetManifestResourceStream(resource)) {
                if (stream == null) return false;
                data = new byte[stream.Length]; int read = 0; while (read < data.Length) { int n = stream.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
            }
            IntPtr memory = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, memory, data.Length);
            buffers.Add(memory);
            collection.AddMemoryFont(memory, data.Length);
            try { uint count = 0; if (AddFontMemResourceEx(memory, (uint)data.Length, IntPtr.Zero, ref count) == IntPtr.Zero) EngineLog.Write("fonts: GDI refused " + resource); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            return true;
        }
    }
}
