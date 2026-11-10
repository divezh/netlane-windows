using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Netlane {
    // Country flags from flag-icons (MIT), embedded as flag.<cc>.20.png and flag.<cc>.40.png.
    internal static partial class Flags {
        static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>();
        // Twemoji flags: 20×20 up to 18 px, 40×40 above; null when the code has no image.
        internal static Image For(string country, int height) {
            if (country == null || country.Length != 2 || !Char.IsLetter(country[0]) || !Char.IsLetter(country[1])) return null;
            string key = "flag." + country.ToLowerInvariant() + (height <= 18 ? ".20" : ".40") + ".png";
            lock (cache) {
                Image image;
                if (cache.TryGetValue(key, out image)) return image;
                using (Stream stream = typeof(Flags).Assembly.GetManifestResourceStream(key)) {
                    if (stream != null) using (MemoryStream copy = new MemoryStream()) { stream.CopyTo(copy); copy.Position = 0; image = new Bitmap(Image.FromStream(copy)); }
                }
                cache[key] = image;
                return image;
            }
        }
        // Twemoji flags are square; centre the emoji in the badge instead of stretching it.
        static partial void DrawImageCore(Graphics g, Rectangle bounds, string country, ref bool drawn) {
            int side = Math.Min(bounds.Width, bounds.Height);
            Image image = For(country, side);
            if (image == null) return;
            Rectangle box = new Rectangle(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2, side, side);
            float radius = Math.Max(2f, side / 9f);
            GraphicsState saved = g.Save();
            try {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Shapes.Round(box, radius)) {
                    g.SetClip(path, CombineMode.Intersect);
                    g.DrawImage(image, box);
                    g.ResetClip();
                    using (Pen edge = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawPath(edge, path);
                }
            } finally { g.Restore(saved); }
            drawn = true;
        }
    }
}
