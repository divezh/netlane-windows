using System;
using Microsoft.Win32;

namespace Netlane {
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run, behind an interface so tests never touch the registry.
    internal interface IRunKey {
        string Get(string name);
        void Set(string name, string value);
        void Delete(string name);
    }
    internal sealed class RegistryRunKey : IRunKey {
        const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public string Get(string name) { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Path, false)) return key == null ? null : key.GetValue(name) as string; }
        public void Set(string name, string value) { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Path)) key.SetValue(name, value, RegistryValueKind.String); }
        public void Delete(string name) { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Path, true)) if (key != null) key.DeleteValue(name, false); }
    }

    // «Запускать вместе с Windows» (spec §4): a per-user Run value that starts the app hidden in the tray.
    internal static class Autostart {
        internal const string ValueName = "Netlane";
        // The previous product name left its own Run value; remove it to avoid a duplicate autostart.
        internal const string LegacyValueName = "WinTrustTunnel";
        internal static string Command(string exePath) { return "\"" + exePath + "\" --tray"; }
        internal static void Apply(bool enabled, IRunKey key) { Apply(enabled, key, System.Windows.Forms.Application.ExecutablePath); }
        // Rewrites the value when the path changed (the app was moved); removes it when off.
        internal static void Apply(bool enabled, IRunKey key, string exePath) {
            string current = key.Get(ValueName), wanted = Command(exePath);
            if (enabled) { if (current != wanted) key.Set(ValueName, wanted); }
            else if (current != null) key.Delete(ValueName);
            if (key.Get(LegacyValueName) != null) key.Delete(LegacyValueName);
        }
    }
}
