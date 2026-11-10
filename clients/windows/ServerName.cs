using System;

namespace Netlane {
    // Profile names from the panel look like "🇳🇱 Амстердам" (Happ style): a flag emoji, then the
    // name. Windows does not draw flag emoji, so the client keeps the country code separately.
    internal static class ServerName {
        const int RegionalA = 0x1F1E6, RegionalZ = 0x1F1FF;

        internal static void Split(string raw, out string country, out string name) {
            country = null; name = null;
            if (raw == null) return;
            string text = raw.Trim();
            int first, second;
            if (Regional(text, 0, out first) && Regional(text, 2, out second)) {
                country = new string(new[] { (char)('A' + first - RegionalA), (char)('A' + second - RegionalA) });
                text = text.Substring(4).Trim();
            }
            name = text.Length == 0 ? null : text;
        }

        // A Regional Indicator symbol is one surrogate pair in UTF-16.
        static bool Regional(string text, int index, out int codePoint) {
            codePoint = 0;
            if (text.Length < index + 2 || !Char.IsSurrogatePair(text[index], text[index + 1])) return false;
            codePoint = Char.ConvertToUtf32(text[index], text[index + 1]);
            return codePoint >= RegionalA && codePoint <= RegionalZ;
        }
    }
}
