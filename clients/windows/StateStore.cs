using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Netlane {
    // state.bin: the whole ClientState, encrypted for the current Windows user (DPAPI).
    // On the first start after an update the single profile.bin is migrated into it.
    public static class StateStore {
        public const string FileName = "state.bin", LegacyFileName = "profile.bin", MigratedFileName = "profile.bin.migrated";
        internal const string LegacyUnreadable = "Не удалось прочитать прежний профиль. Добавьте сервер заново; старый файл не тронут.";
        internal const string MigratedRuleSetName = "Мои правила";
        const int MaxBytes = 1 << 20;
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Netlane.state.v2");
        // state.bin written by the previous product name must stay readable.
        static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("WinTrustTunnel.state.v2");
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaxBytes }; }

        public static ClientState LoadOrMigrate(string dataDirectory, out string notice) {
            notice = null;
            string path = Path.Combine(dataDirectory, FileName);
            if (File.Exists(path)) {
                try {
                    ClientState loaded = Load(path);
                    if (loaded.DroppedDns.Count > 0) notice = "DNS-серверы отключены, они не прошли проверку: " + String.Join(", ", loaded.DroppedDns) + ". Проверьте «Настройки → Подключение».";
                    return loaded;
                }
                catch (Exception error) {
                    if (!(error is CryptographicException || error is FormatException || error is IOException || error is ArgumentException || error is InvalidOperationException)) throw;
                    // Keep the unreadable file for recovery; the next save must not overwrite it.
                    string aside = FileName + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    try { File.Move(path, Path.Combine(dataDirectory, aside)); }
                    catch (IOException) { throw error; } catch (UnauthorizedAccessException) { throw error; }
                    notice = "Не удалось прочитать настройки. Добавьте сервер заново; прежний файл сохранён как " + aside + ".";
                    return ClientState.CreateEmpty();
                }
            }
            string legacy = Path.Combine(dataDirectory, LegacyFileName);
            if (!File.Exists(legacy)) return ClientState.CreateEmpty();
            Profile old;
            try { old = ProfileStore.Load(legacy); }
            catch (Exception error) {
                if (!(error is CryptographicException || error is FormatException || error is IOException || error is ArgumentException || error is InvalidOperationException)) throw;
                notice = LegacyUnreadable;
                return ClientState.CreateEmpty();
            }
            ClientState state = Migrate(old);
            Save(dataDirectory, state);
            try { File.Move(legacy, Path.Combine(dataDirectory, MigratedFileName)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return state;
        }

        // The old profile becomes the selected server; non-empty rules become «Мои правила».
        internal static ClientState Migrate(Profile old) {
            ClientState state = ClientState.CreateEmpty();
            state.KillSwitch = old.KillSwitch;
            state.AntiDpi = old.AntiDpi;
            Server server = Server.FromProfile(old);
            state.Servers.Add(server);
            state.SelectedServerId = server.Id;
            Routing rules = old.Routing ?? new Routing();
            if (rules.Destinations.Count > 0 || rules.Applications.Count > 0) {
                RuleSet set = state.AddRuleSet(MigratedRuleSetName, rules);
                state.SelectedRuleSetId = set.Id;
            }
            return state;
        }

        static ClientState Load(string path) {
            if (new FileInfo(path).Length > MaxBytes) throw new FormatException("Повреждённый файл настроек.");
            byte[] sealedBytes = File.ReadAllBytes(path), plain; bool legacy = false;
            try { plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { plain = ProtectedData.Unprotect(sealedBytes, LegacyEntropy, DataProtectionScope.CurrentUser); legacy = true; }
            try {
                ClientState state = Json().Deserialize<ClientState>(Encoding.UTF8.GetString(plain));
                if (state == null) throw new FormatException("Пустой файл настроек.");
                state.Validate();
                // Re-encrypt with the current entropy on the next save, keeping the file readable meanwhile.
                if (legacy) { try { Save(Path.GetDirectoryName(path), state); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                return state;
            } finally { Array.Clear(plain, 0, plain.Length); }
        }

        public static void Save(string dataDirectory, ClientState state) {
            state.Validate();
            byte[] plain = Encoding.UTF8.GetBytes(Json().Serialize(state));
            if (plain.Length > MaxBytes) { Array.Clear(plain, 0, plain.Length); throw new FormatException("Настройки слишком большие."); }
            byte[] sealedBytes;
            try { sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); } finally { Array.Clear(plain, 0, plain.Length); }
            Directory.CreateDirectory(dataDirectory);
            string path = Path.Combine(dataDirectory, FileName), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(sealedBytes, 0, sealedBytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
