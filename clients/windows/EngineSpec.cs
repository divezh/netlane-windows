using System;
using System.IO;
using System.Security.Cryptography;

namespace Netlane {
    internal static class EngineSpec {
        internal const string Version = "1.1.11";
        internal static readonly bool SupportsApplications = false;
        // Bare process-name rules (Clash PROCESS-NAME) need the routed engine built from the routing patch.
        internal static readonly bool SupportsApplicationNames = false;
        internal static string DirectoryPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine"); } }
        internal static string Executable { get { return Path.Combine(DirectoryPath, "trusttunnel_client.exe"); } }
        internal static void CheckAvailability() { CheckAvailability(DirectoryPath); }
        internal static void CheckAvailability(string directory) {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("Нет папки engine рядом с Netlane.exe. Распакуйте полный ZIP-комплект в одну папку.");
            foreach (string name in new[] { "trusttunnel_client.exe", "wintun.dll" }) {
                string path = Path.Combine(directory,name);
                if (!File.Exists(path))
                    throw new FileNotFoundException("Не найден "+Path.Combine("engine",name)+". Распакуйте полный ZIP-комплект заново.",path);
            }
        }
        internal static void Verify() {
            CheckAvailability();
            VerifyFile(Executable, "2572f6397ec4a39e018858da593ab278facf9b2e86c7e276753f4845ed78968d");
            VerifyFile(Path.Combine(DirectoryPath, "wintun.dll"), "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce");
        }
        static void VerifyFile(string path, string expected) {
            using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) {
                string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new IOException("Проверка целостности движка не пройдена. Распакуйте приложение заново.");
            }
        }
    }
}
