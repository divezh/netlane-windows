using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Netlane {
    // One VPN server the user added. Connection fields are those of the imported Profile;
    // routing and Kill Switch live in the rule set and the client state instead.
    public sealed class Server {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public string Hostname { get; set; }
        public string Address { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        // The transport the panel or the profile gives; a subscription refresh overwrites it.
        public string Protocol { get; set; }
        // The user's choice, kept across refreshes: null (follow Protocol), "auto", "http2" or "http3".
        public string Transport { get; set; }
        // In "auto": the transport that last connected (null → HTTP/3 first).
        public string AutoProtocol { get; set; }
        [ScriptIgnore] public bool AutoTransport { get { return Transport == "auto"; } }
        // What the engine is started with.
        [ScriptIgnore] public string EffectiveProtocol { get { return Transport == "auto" ? (AutoProtocol ?? "http3") : Transport ?? Protocol; } }
        // Servers from a subscription: its Id and the node id in its feed; null when added by hand.
        public string SubscriptionId { get; set; }
        public string RemoteId { get; set; }
        // Left its subscription while connected; removed when the session ends.
        public bool Orphaned { get; set; }
        public Server() { Id = Guid.NewGuid().ToString("N"); Protocol = "http2"; }

        public void Validate() {
            if (String.IsNullOrEmpty(Id) || !Regex.IsMatch(Id, @"\A[0-9a-f]{32}\z")) throw new FormatException("Повреждённый идентификатор сервера.");
            ValidateName(Name);
            if (Country != null && !Regex.IsMatch(Country, @"\A[A-Z]{2}\z")) throw new FormatException("Страна сервера — две латинские буквы.");
            if (Transport != null && Transport != "auto" && Transport != "http2" && Transport != "http3") Transport = null;
            if (AutoProtocol != null && AutoProtocol != "http2" && AutoProtocol != "http3") AutoProtocol = null;
            if (RemoteId != null && !Regex.IsMatch(RemoteId, @"\A[A-Za-z0-9_-]{1,64}\z")) throw new FormatException("Повреждённый идентификатор сервера подписки.");
            ToProfile().Validate();
        }
        internal static void ValidateName(string name) {
            if (String.IsNullOrEmpty(name) || name.Trim().Length == 0 || name.Length > 64 || name.Any(Char.IsControl)) throw new FormatException("Название сервера — от 1 до 64 символов.");
        }
        internal Profile ToProfile() {
            return new Profile { Hostname = Hostname, Address = Address, Username = Username, Password = Password, Protocol = EffectiveProtocol, Name = Name };
        }
        public static Server FromProfile(Profile profile) {
            Server server = new Server { Hostname = profile.Hostname, Address = profile.Address, Username = profile.Username, Password = profile.Password, Protocol = profile.Protocol, Name = DefaultName(profile.Hostname) };
            string country, name;
            ServerName.Split(profile.Name, out country, out name);
            if (name != null && name.Length <= 64 && !name.Any(Char.IsControl)) server.Name = name;
            server.Country = country;
            return server;
        }
        // A profile without its own name or flag got the host name: an update must not overwrite a name the user chose.
        internal bool HasOwnName { get { return Country != null || Name != DefaultName(Hostname); } }
        // A server without its own name shows the host, shortened to the 64-character name limit.
        internal static string DefaultName(string hostname) { return hostname == null || hostname.Length <= 64 ? hostname : hostname.Substring(0, 63) + "…"; }
    }

    public enum DuplicateChoice { Update, AddCopy }

    public sealed class RuleSet {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Builtin { get; set; }
        public string SubscriptionId { get; set; }
        public string RemoteId { get; set; }
        [ScriptIgnore] public bool Managed { get { return SubscriptionId != null; } }
        [ScriptIgnore] public string DisplayName { get { return Name; } }
        public Routing Routing { get; set; }
        public RuleSet() { Id = Guid.NewGuid().ToString("N"); Routing = new Routing(); }
        public RuleSet Copy(string newName) { return new RuleSet { Name = newName, Routing = Routing.Copy() }; }
    }

    // Everything the client keeps between runs: servers, rule sets, selections and shared options.
    public sealed partial class ClientState {
        public const int CurrentVersion = 2, MaxServers = 50, MaxRuleSets = 20;
        public const string BuiltinName = "Весь трафик";
        public int Version { get; set; }
        public List<Server> Servers { get; set; }
        public string SelectedServerId { get; set; }
        public List<RuleSet> RuleSets { get; set; }
        public string SelectedRuleSetId { get; set; }
        public List<string> DnsServers { get; set; }
        public List<Subscription> Subscriptions { get; set; }
        // DNS entries left out by the last Validate (they no longer pass the checks); not stored.
        [ScriptIgnore] internal List<string> DroppedDns { get; private set; }
        public bool KillSwitch { get; set; }
        public bool AntiDpi { get; set; }
        public bool StartWithWindows { get; set; }
        public bool AutoConnect { get; set; }
        public bool CloseToTray { get; set; }
        // Off by default: the public IP is checked through cloudflare.com/cdn-cgi/trace unless the user turns it off.
        public bool HidePublicIp { get; set; }
        // The last finished session, shown while disconnected.
        public string LastSessionEnd { get; set; }
        public long LastSessionSeconds { get; set; }
        public long LastSessionDown { get; set; }
        public long LastSessionUp { get; set; }
        public string LastSessionServer { get; set; }
        // The version the user chose to postpone; the startup dialog stays quiet until the next one.
        public string SkippedUpdateVersion { get; set; }
        public event Action Changed;

        public static ClientState CreateEmpty() {
            ClientState state = new ClientState { Version = CurrentVersion, Servers = new List<Server>(), RuleSets = new List<RuleSet>(), KillSwitch = true, CloseToTray = true };
            state.EnsureBuiltin();
            return state;
        }

        [ScriptIgnore] public Server SelectedServer { get { return Servers.FirstOrDefault(s => s.Id == SelectedServerId); } }
        [ScriptIgnore] public RuleSet SelectedRuleSet { get { return AvailableRuleSets.FirstOrDefault(r => r.Id == SelectedRuleSetId) ?? RuleSets.First(r => r.Builtin); } }

        [ScriptIgnore] public IEnumerable<RuleSet> AvailableRuleSets {
            get { string source = SelectedServer == null ? null : SelectedServer.SubscriptionId;
                return RuleSets.Where(r => !r.Managed || r.SubscriptionId == source); }
        }

        // Keeps the built-in set present, first and unchanged, whatever was stored.
        internal void EnsureBuiltin() {
            if (Servers == null) Servers = new List<Server>();
            if (RuleSets == null) RuleSets = new List<RuleSet>();
            if (Subscriptions == null) Subscriptions = new List<Subscription>();
            RuleSet builtin = RuleSets.FirstOrDefault(r => r.Builtin);
            if (builtin == null) builtin = new RuleSet { Builtin = true };
            RuleSets.Remove(builtin);
            builtin.Name = BuiltinName; builtin.Routing = new Routing();
            RuleSets.Insert(0, builtin);
        }

        public void Validate() {
            if (Version != CurrentVersion) throw new FormatException("Неизвестный формат настроек.");
            System.Version skipped;
            if (SkippedUpdateVersion != null && (SkippedUpdateVersion.Length > 32 || !System.Version.TryParse(SkippedUpdateVersion, out skipped))) SkippedUpdateVersion = null;
            DroppedDns = new List<string>();
            DnsServers = DnsSettings.Sanitize(DnsServers, DroppedDns);
            EnsureBuiltin();
            if (Servers.Count > MaxServers) throw new FormatException("Не больше 50 серверов.");
            if (RuleSets.Count > MaxRuleSets) throw new FormatException("Не больше 20 наборов правил.");
            if (Servers.Select(s => s.Id).Distinct().Count() != Servers.Count || RuleSets.Select(r => r.Id).Distinct().Count() != RuleSets.Count) throw new FormatException("Повторяющиеся записи в настройках.");
            if (Subscriptions.Count > MaxSubscriptions || Subscriptions.Select(x => x.Id).Distinct().Count() != Subscriptions.Count) throw new FormatException("Повторяющиеся записи в настройках.");
            foreach (Subscription sub in Subscriptions) sub.Validate();
            RuleSets.RemoveAll(r => r.Managed && Subscriptions.All(sub => sub.Id != r.SubscriptionId));
            foreach (Server server in Servers) {
                server.Validate();
                // A server whose subscription is gone becomes an ordinary one.
                if (server.SubscriptionId != null && Subscriptions.All(x => x.Id != server.SubscriptionId)) { server.SubscriptionId = null; server.RemoteId = null; }
            }
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RuleSet set in RuleSets) {
                ValidateRuleSetName(set.Name);
                if (!set.Managed && !names.Add(set.Name.Trim())) throw new FormatException("Набор правил с таким названием уже есть.");
                if (set.Routing == null) set.Routing = new Routing();
                set.Routing.Validate();
            }
            if (SelectedServerId != null && SelectedServer == null) SelectedServerId = Servers.Count > 0 ? Servers[0].Id : null;
            if (RuleSets.All(r => r.Id != SelectedRuleSetId)) SelectedRuleSetId = RuleSets[0].Id;
        }

        static void ValidateRuleSetName(string name) {
            if (String.IsNullOrEmpty(name) || name.Trim().Length == 0 || name.Length > 40 || name.Any(Char.IsControl)) throw new FormatException("Название набора — от 1 до 40 символов.");
        }

        // The single profile the engine runs: selected server + selected set + shared options.
        public Profile SessionProfile() {
            Server server = SelectedServer;
            if (server == null) throw new InvalidOperationException("Сервер не выбран.");
            Profile profile = server.ToProfile();
            profile.Routing = SelectedRuleSet.Routing.Copy();
            Subscription source = Subscriptions.FirstOrDefault(sub => sub.Id == server.SubscriptionId);
            profile.DnsServers = DnsSettings.Normalize(source != null && source.ManagedDns != null ? source.ManagedDns : DnsServers);
            profile.KillSwitch = KillSwitch;
            profile.AntiDpi = AntiDpi;
            profile.Validate();
            return profile;
        }

        void Raise() { Action handler = Changed; if (handler != null) handler(); }
        internal void NotifyChanged() { Raise(); }

        public void SelectServer(string id) {
            if (id != null && Servers.All(s => s.Id != id)) throw new FormatException("Сервер не найден.");
            SelectedServerId = id; Raise();
        }
        public void SelectRuleSet(string id) {
            if (AvailableRuleSets.All(r => r.Id != id)) throw new FormatException("Набор правил не найден.");
            SelectedRuleSetId = id; Raise();
        }
        // Same server = same host (any case) and the same VPN user.
        public Server FindDuplicate(Server incoming) {
            return Servers.FirstOrDefault(s => String.Equals(s.Hostname, incoming.Hostname, StringComparison.OrdinalIgnoreCase) && s.Username == incoming.Username);
        }
        // Import: a new server goes to the end and is selected; Update rewrites the duplicate in place.
        public Server AddServer(Server incoming, DuplicateChoice choice) {
            incoming.Validate();
            Server existing = choice == DuplicateChoice.Update ? FindDuplicate(incoming) : null;
            if (existing != null) {
                existing.Hostname = incoming.Hostname; existing.Address = incoming.Address; existing.Username = incoming.Username;
                existing.Password = incoming.Password; existing.Protocol = incoming.Protocol;
                if (incoming.HasOwnName) { existing.Name = incoming.Name; existing.Country = incoming.Country; }
                SelectedServerId = existing.Id; Raise();
                return existing;
            }
            if (Servers.Count >= MaxServers) throw new FormatException("Не больше 50 серверов.");
            if (Servers.Any(s => s.Id == incoming.Id)) incoming.Id = Guid.NewGuid().ToString("N");
            Servers.Add(incoming);
            SelectedServerId = incoming.Id; Raise();
            return incoming;
        }
        // Removing the selected server selects the first remaining one, or none.
        public void RemoveServer(string id) {
            Server server = Servers.FirstOrDefault(s => s.Id == id);
            if (server == null) throw new FormatException("Сервер не найден.");
            Servers.Remove(server);
            if (SelectedServerId == id) SelectedServerId = Servers.Count > 0 ? Servers[0].Id : null;
            Raise();
        }

        RuleSet EditableSet(string id) {
            RuleSet set = RuleSets.FirstOrDefault(r => r.Id == id);
            if (set == null) throw new FormatException("Набор правил не найден.");
            if (set.Managed) throw new FormatException("Набор управляется администратором. Можно создать личную копию.");
            if (set.Builtin) throw new FormatException("Встроенный набор «Весь трафик» нельзя изменить.");
            return set;
        }
        void CheckUniqueName(string name, string exceptId) {
            ValidateRuleSetName(name);
            if (RuleSets.Any(r => !r.Managed && r.Id != exceptId && String.Equals(r.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new FormatException("Набор правил с таким названием уже есть.");
        }
        public RuleSet AddRuleSet(string name, Routing routing) {
            if (RuleSets.Count >= MaxRuleSets) throw new FormatException("Не больше 20 наборов правил.");
            CheckUniqueName(name, null);
            Routing value = (routing ?? new Routing()).Copy();
            value.Validate();
            RuleSet set = new RuleSet { Name = name.Trim(), Routing = value };
            RuleSets.Add(set); Raise();
            return set;
        }
        public void RenameRuleSet(string id, string name) {
            RuleSet set = EditableSet(id);
            CheckUniqueName(name, id);
            set.Name = name.Trim(); Raise();
        }
        public void RemoveRuleSet(string id) {
            RuleSet set = EditableSet(id);
            RuleSets.Remove(set);
            if (SelectedRuleSetId == id) SelectedRuleSetId = RuleSets[0].Id;
            Raise();
        }
        public void UpdateRuleSet(string id, Routing routing) {
            RuleSet set = EditableSet(id);
            Routing value = routing.Copy();
            value.Validate();
            set.Routing = value; Raise();
        }
    }
}
