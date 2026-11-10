using System;
using System.Drawing;

namespace Netlane {
    internal enum ConnectionView { NoProfile, Idle, Connecting, Connected, Reconnecting, KillSwitch, Error, Disconnecting }

    internal struct StateStyle {
        public Color Dot; public bool DotHalo; public Color Ring; public bool RingHalo; public bool RingEnabled;
        public StateStyle(Color dot, bool dotHalo, Color ring, bool ringHalo, bool ringEnabled) { Dot = dot; DotHalo = dotHalo; Ring = ring; RingHalo = ringHalo; RingEnabled = ringEnabled; }
    }

    // The one palette and scale of the client (docs/superpowers/specs/2026-11-03-windows-client-redesign-design.md).
    // Accent is reserved for the main action and active elements; Protected only for "connected".
    internal static class Theme {
        internal static readonly Color Ground = Color.FromArgb(0x10, 0x16, 0x24);
        internal static readonly Color Card = Color.FromArgb(0x13, 0x1B, 0x2B);
        internal static readonly Color Raised = Color.FromArgb(0x1A, 0x24, 0x37);
        internal static readonly Color Line = Color.FromArgb(0x27, 0x31, 0x49);
        internal static readonly Color LineStrong = Color.FromArgb(0x36, 0x41, 0x5A);
        internal static readonly Color Ink = Color.FromArgb(0xF1, 0xF3, 0xFB);
        internal static readonly Color Muted = Color.FromArgb(0xAB, 0xB6, 0xCC);
        internal static readonly Color Faint = Color.FromArgb(0x8E, 0x9A, 0xB1);
        internal static readonly Color Accent = Color.FromArgb(0x22, 0xD3, 0xE6);
        internal static readonly Color AccentInk = Color.FromArgb(0x06, 0x22, 0x2A);
        internal static readonly Color Protected = Color.FromArgb(0x62, 0xDB, 0xB0);
        internal static readonly Color Warning = Color.FromArgb(0xFF, 0xBF, 0x87);
        // Sending, beside the accent for receiving: the arrows ↑ and the lower speed lane.
        internal static readonly Color Upload = Color.FromArgb(0xFF, 0xBF, 0x87);
        internal static readonly Color Danger = Color.FromArgb(0xF2, 0x83, 0x7A);
        // Colors of the drawn fallback app icon (used only when the embedded .ico is missing).
        internal static readonly Color IconNavy = Color.FromArgb(0x09, 0x1A, 0x52), IconCyan = Color.FromArgb(0x00, 0xE5, 0xF5);
        internal static readonly Color AccentSoft = Soft(Accent);
        // Opaque selected-row colour (12% accent over a card): owner-drawn lists repaint rows without erasing them.
        internal static readonly Color Selection = Blend(Accent, Card, 31);
        internal static readonly Color ProtectedSoft = Soft(Protected);

        internal const int Gutter = 20, Gap = 12, RadiusCard = 14, RadiusControl = 10;
        // Teal scale: 20 px state, 16 px brand, 15 px server, 13 px body.
        internal const float StateSize = 20f, HeadingSize = 15f, BrandSize = 16f, ServerSize = 11.75f, BodySize = 10f, SmallSize = 9f, CaptionSize = 8.25f;

        // 12% opacity of a base color, for halos around the ring and the status dot.
        internal static Color Soft(Color color) { return Color.FromArgb(31, color.R, color.G, color.B); }

        // Opaque result of drawing `over` with the given alpha on top of `under`.
        internal static Color Blend(Color over, Color under, int alpha) { return Color.FromArgb(255, (over.R * alpha + under.R * (255 - alpha) + 127) / 255, (over.G * alpha + under.G * (255 - alpha) + 127) / 255, (over.B * alpha + under.B * (255 - alpha) + 127) / 255); }
        internal static Color Fade(Color color, int alpha) { return Color.FromArgb(alpha, color.R, color.G, color.B); }
        internal static Color Darken(Color color, int amount) { return Color.FromArgb(color.A, Math.Max(0, color.R - amount), Math.Max(0, color.G - amount), Math.Max(0, color.B - amount)); }

        // Onest from AppFonts, Segoe UI if it could not be loaded.
        internal static Font Font(float size, bool semibold = false) { return AppFonts.Create(size, semibold); }
        internal static Font Mono(float size) { return new Font("Consolas", size); }

        // Spec table "Состояния подключения": dot, ring and whether the ring can be pressed.
        // The ring speaks for the state, not for the action: when the tunnel is up it is Accent
        // with a halo, and red is left to the kill switch, to stopping and to failures. A red
        // ring beside a green «Защищено» read as a fault in the very state that means "all good".
        internal static StateStyle ForState(ConnectionView view) {
            switch (view) {
                case ConnectionView.NoProfile: return new StateStyle(Faint, false, Faint, false, false);
                case ConnectionView.Connecting: return new StateStyle(Accent, false, Accent, false, false);
                case ConnectionView.Disconnecting: return new StateStyle(Danger, false, LineStrong, false, false);
                case ConnectionView.Connected: return new StateStyle(Protected, true, Accent, true, true);
                case ConnectionView.Reconnecting: return new StateStyle(Warning, false, Warning, false, true);
                case ConnectionView.KillSwitch: return new StateStyle(Danger, false, Danger, true, true);
                case ConnectionView.Error: return new StateStyle(Danger, false, Danger, true, true);
                default: return new StateStyle(Faint, false, LineStrong, false, true);
            }
        }

        // Logical pixels at 96 DPI to device pixels for owner-drawn controls.
        internal static float Px(Graphics graphics, float logical) { return logical * graphics.DpiX / 96f; }
    }
}
