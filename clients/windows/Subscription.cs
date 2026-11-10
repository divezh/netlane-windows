using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Netlane {
    // A subscription link from the panel: one URL that lists all locations of the user
    // (spec 2026-11-04 nodes and subscription). Its servers carry its Id and their node id.
    public sealed class Subscription {
        public const int DefaultHours = 12;
        public string Id { get; set; }
        public string Url { get; set; }
        public string Name { get; set; }
        public int UpdateHours { get; set; }
        public string UpdatedAt { get; set; }
        public string LastError { get; set; }
        public List<string> ManagedDns { get; set; }
        public long SettingsRevision { get; set; }
        // The feed may advertise a newer client: version, download page and short notes.
        public string LatestClientVersion { get; set; }
        public string ClientDownloadUrl { get; set; }
        public string ClientNotes { get; set; }
        internal int PendingRefreshes { get; set; }
        public Subscription() { Id = Guid.NewGuid().ToString("N"); UpdateHours = DefaultHours; }

        public void Validate() {
            if (String.IsNullOrEmpty(Id) || !Regex.IsMatch(Id, @"\A[0-9a-f]{32}\z")) throw new FormatException("Повреждённый идентификатор подписки.");
            if (!LooksLikeUrl(Url) || Url.Length > 2048) throw new FormatException("Адрес подписки должен начинаться с https:// и вести на /sub/.");
            ManagedDns = ManagedDns == null ? null : DnsSettings.Normalize(ManagedDns);
            if (UpdateHours < 1 || UpdateHours > 24) UpdateHours = DefaultHours;
            if (Name != null && (Name.Length > 64 || Name.Any(Char.IsControl))) Name = null;
            Version offered;
            if (LatestClientVersion != null && (LatestClientVersion.Length > 32 || !Version.TryParse(LatestClientVersion, out offered))) LatestClientVersion = null;
            if (ClientDownloadUrl != null && (!ClientDownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || ClientDownloadUrl.Length > 2048)) ClientDownloadUrl = null;
            if (ClientNotes != null && (ClientNotes.Length > 500 || ClientNotes.Any(Char.IsControl))) ClientNotes = null;
        }
        // https://<host>/…/sub/<token>, as the panel issues them.
        public static bool LooksLikeUrl(string text) {
            Uri uri;
            if (text == null || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && Regex.IsMatch(uri.AbsolutePath, @"/sub/[A-Za-z0-9_-]+\z") && String.IsNullOrEmpty(uri.UserInfo);
        }
        // What the settings show under the subscription's name.
        internal string DisplayName { get { return String.IsNullOrEmpty(Name) ? new Uri(Url).Host : Name; } }
    }

    // A parsed subscription response; every server is checked like an imported tt:// link.
    internal sealed class SubscriptionFeed {
        internal string Name;
        internal int UpdateHours;
        internal List<Server> Servers = new List<Server>();
        internal List<RuleSet> RuleSets = new List<RuleSet>();
        internal List<string> Dns;
        internal long SettingsRevision;
        internal string ClientVersion, ClientUrl, ClientNotes;

        internal static SubscriptionFeed Parse(string json) {
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = 1 << 20 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { root = null; } catch (InvalidOperationException) { root = null; }
            if (root == null) throw Broken("ответ не похож на подписку");
            object version, list;
            if (!root.TryGetValue("version", out version) || !(version is int) || (int)version != 1) throw Broken("неизвестная версия формата, обновите программу");
            if (!root.TryGetValue("servers", out list) || !(list is object[])) throw Broken("нет списка серверов");
            object[] items = (object[])list;
            if (items.Length > ClientState.MaxServers) throw Broken("больше 50 серверов");
            SubscriptionFeed feed = new SubscriptionFeed { UpdateHours = Subscription.DefaultHours };
            object hours;
            if (root.TryGetValue("update_hours", out hours) && hours is int && (int)hours >= 1 && (int)hours <= 24) feed.UpdateHours = (int)hours;
            string name = Text(root, "name");
            if (name != null && name.Length <= 64 && !name.Any(Char.IsControl) && name.Trim().Length > 0) feed.Name = name.Trim();
            object client;
            if (root.TryGetValue("client", out client)) {
                Dictionary<string, object> entry = client as Dictionary<string, object>;
                if (entry != null) {
                    string clientVersion = Text(entry, "version");
                    Version parsed;
                    if (clientVersion != null && clientVersion.Length <= 32 && Version.TryParse(clientVersion, out parsed)) feed.ClientVersion = clientVersion;
                    string url = Text(entry, "url");
                    if (url != null && url.Length <= 2048 && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) feed.ClientUrl = url;
                    string notes = Text(entry, "notes");
                    if (notes != null && notes.Length <= 500 && !notes.Any(Char.IsControl)) feed.ClientNotes = notes;
                }
            }
            HashSet<string> ids = new HashSet<string>();
            foreach (object item in items) {
                Dictionary<string, object> entry = item as Dictionary<string, object>;
                if (entry == null) throw Broken("неверная запись сервера");
                string id = Text(entry, "id"), link = Text(entry, "link");
                if (id == null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,64}\z") || !ids.Add(id)) throw Broken("неверный или повторяющийся id сервера");
                if (link == null || !link.StartsWith("tt://?", StringComparison.Ordinal)) throw Broken("у сервера нет ссылки tt://");
                Server server;
                try { server = Server.FromProfile(Profile.Import(link)); }
                catch (FormatException error) { throw Broken("сервер «" + id + "»: " + error.Message); }
                string country, display;
                ServerName.Split(Text(entry, "name"), out country, out display);
                if (display != null && display.Length <= 64 && !display.Any(Char.IsControl)) server.Name = display;
                string code = Text(entry, "country");
                if (code != null && Regex.IsMatch(code, @"\A[A-Z]{2}\z")) server.Country = code;
                else if (country != null) server.Country = country;
                server.RemoteId = id;
                server.Validate();
                feed.Servers.Add(server);
            }
            object settings;
            if (root.TryGetValue("client_settings", out settings)) {
                Dictionary<string,object> config = settings as Dictionary<string,object>;
                object configVersion, revision, sets, dns;
                if (config == null || !config.TryGetValue("version",out configVersion) || !(configVersion is int) || (int)configVersion != 1
                    || !config.TryGetValue("revision",out revision) || (!(revision is int) && !(revision is long))
                    || Convert.ToInt64(revision) < 0 || !config.TryGetValue("rule_sets",out sets) || !(sets is object[]) || ((object[])sets).Length > 10)
                    throw Broken("неверные настройки маршрутизации");
                feed.SettingsRevision = Convert.ToInt64(revision);
                if (config.TryGetValue("dns_servers",out dns) && dns != null) {
                    object[] entries = dns as object[];
                    if (entries == null || entries.Any(d => !(d is string))) throw Broken("неверные DNS-серверы");
                    feed.Dns = DnsSettings.Normalize(entries.Cast<string>());
                }
                HashSet<string> setIds = new HashSet<string>(), names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object raw in (object[])sets) {
                    Dictionary<string,object> entry = raw as Dictionary<string,object>;
                    object routing;
                    if (entry == null || !entry.TryGetValue("routing",out routing)) throw Broken("нет правил набора");
                    string id = Text(entry,"id"), title = Text(entry,"name");
                    if (id == null || !Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{1,64}\z") || !setIds.Add(id)
                        || String.IsNullOrWhiteSpace(title) || title.Length > 40 || title.Any(Char.IsControl) || !names.Add(title.Trim()))
                        throw Broken("неверное название или id набора");
                    Routing rules;
                    try { rules = new JavaScriptSerializer().ConvertToType<Routing>(routing); }
                    catch (Exception error) {
                        if (!(error is ArgumentException || error is InvalidOperationException)) throw;
                        throw Broken("неверные правила набора");
                    }
                    if (rules == null) throw Broken("нет правил набора");
                    rules.Validate();
                    feed.RuleSets.Add(new RuleSet { RemoteId=id, Name=title.Trim(), Routing=rules });
                }
            }
            return feed;
        }
        static string Text(Dictionary<string, object> map, string key) { object value; return map.TryGetValue(key, out value) ? value as string : null; }
        static FormatException Broken(string why) { return new FormatException("Подписка повреждена: " + why + "."); }
    }

    internal interface IFeedFetcher { Task<string> Fetch(string url, CancellationToken cancel); }

    // GET of a subscription: Windows certificate checks, 15 s, at most 256 KB, no redirects.
    internal sealed class HttpFeedFetcher : IFeedFetcher {
        internal const int MaxBytes = 256 * 1024;
        public async Task<string> Fetch(string url, CancellationToken cancel) {
            Tls.Enable();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.AllowAutoRedirect = false; request.Accept = "application/json";
            request.UserAgent = "Netlane/" + Program.Version; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (timeout.Token.Register(request.Abort)) {
                timeout.CancelAfter(15000);
                try {
                    using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                    using (Stream body = response.GetResponseStream()) {
                        if (response.StatusCode != HttpStatusCode.OK) throw new FormatException("Сервер подписки ответил " + (int)response.StatusCode + ".");
                        MemoryStream data = new MemoryStream();
                        byte[] buffer = new byte[16384]; int read;
                        while ((read = await body.ReadAsync(buffer, 0, buffer.Length)) > 0) {
                            data.Write(buffer, 0, read);
                            if (data.Length > MaxBytes) throw new FormatException("Ответ подписки слишком большой.");
                        }
                        return Encoding.UTF8.GetString(data.ToArray());
                    }
                } catch (WebException error) {
                    if (timeout.IsCancellationRequested && !cancel.IsCancellationRequested) throw new FormatException("Сервер подписки не ответил за 15 секунд.");
                    HttpWebResponse failed = error.Response as HttpWebResponse;
                    if (failed != null && failed.StatusCode == HttpStatusCode.NotFound) throw new FormatException("Подписка не найдена: ссылку перевыпустили в панели или она неверна.");
                    if (failed != null) throw new FormatException("Сервер подписки ответил " + (int)failed.StatusCode + ".");
                    throw new FormatException("Сервер подписки недоступен: " + error.Message);
                } catch (IOException error) { throw new FormatException("Сервер подписки недоступен: " + error.Message); }
            }
        }
    }

    internal struct FeedResult { internal int Added, Updated, Removed; internal bool CurrentChanged, SettingsChanged; }

    public sealed partial class ClientState {
        public const int MaxSubscriptions = 10;

        // Adds a subscription by URL, or returns the one that already has it.
        public Subscription AddSubscription(string url) {
            url = url == null ? null : url.Trim();
            Subscription existing = Subscriptions.FirstOrDefault(x => x.Url == url);
            if (existing != null) return existing;
            if (Subscriptions.Count >= MaxSubscriptions) throw new FormatException("Не больше 10 подписок.");
            Subscription created = new Subscription { Url = url };
            created.Validate();
            Subscriptions.Add(created);
            Raise();
            return created;
        }

        // Merges a feed into the subscription's servers by node id. A connected server that left
        // the feed is kept (Orphaned) until the session ends; see DropOrphans.
        internal FeedResult ApplyFeed(string subscriptionId, SubscriptionFeed feed, string connectedServerId) {
            Subscription sub = Subscriptions.FirstOrDefault(x => x.Id == subscriptionId);
            if (sub == null) throw new FormatException("Подписка не найдена.");
            List<Server> mine = Servers.Where(x => x.SubscriptionId == subscriptionId).ToList();
            int added = feed.Servers.Count(f => mine.All(m => m.RemoteId != f.RemoteId));
            int leaving = mine.Count(m => feed.Servers.All(f => f.RemoteId != m.RemoteId) && m.Id != connectedServerId);
            if (Servers.Count - leaving + added > MaxServers) throw new FormatException("Не больше 50 серверов: удалите лишние, чтобы добавить серверы подписки.");
            List<RuleSet> oldSets = RuleSets.Where(r => r.SubscriptionId == subscriptionId).ToList();
            if (RuleSets.Count - oldSets.Count + feed.RuleSets.Count > MaxRuleSets)
                throw new FormatException("Не больше 20 наборов: удалите лишние наборы перед обновлением подписки.");
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1 << 20 };
            try {
                string rules = json.Serialize(RuleSets.Where(r=>r.SubscriptionId != subscriptionId).Concat(feed.RuleSets).ToArray());
                if (Encoding.UTF8.GetByteCount(rules) > 512 * 1024)
                    throw new FormatException("Наборы правил занимают слишком много места. Удалите лишние наборы перед обновлением.");
            } catch (InvalidOperationException) {
                throw new FormatException("Наборы правил занимают слишком много места. Удалите лишние наборы перед обновлением.");
            }
            FeedResult result = new FeedResult();
            result.SettingsChanged = sub.SettingsRevision != feed.SettingsRevision ||
                json.Serialize(sub.ManagedDns) != json.Serialize(feed.Dns) ||
                json.Serialize(oldSets.OrderBy(r=>r.RemoteId).Select(r=>new {r.RemoteId,r.Name,r.Routing}).ToArray()) !=
                json.Serialize(feed.RuleSets.OrderBy(r=>r.RemoteId).Select(r=>new {r.RemoteId,r.Name,r.Routing}).ToArray());
            foreach (RuleSet incoming in feed.RuleSets) {
                RuleSet existing = oldSets.FirstOrDefault(r=>r.RemoteId == incoming.RemoteId);
                if (existing == null) {
                    RuleSets.Add(new RuleSet {
                        SubscriptionId = subscriptionId, RemoteId = incoming.RemoteId,
                        Name = incoming.Name, Routing = incoming.Routing.Copy()
                    });
                } else {
                    existing.Name = incoming.Name;
                    existing.Routing = incoming.Routing.Copy();
                }
            }
            RuleSets.RemoveAll(r=>r.SubscriptionId == subscriptionId && feed.RuleSets.All(f=>f.RemoteId != r.RemoteId));
            sub.ManagedDns = feed.Dns == null ? null : new List<string>(feed.Dns);
            sub.SettingsRevision = feed.SettingsRevision;
            sub.LatestClientVersion = feed.ClientVersion;
            sub.ClientDownloadUrl = feed.ClientUrl;
            sub.ClientNotes = feed.ClientNotes;
            if (RuleSets.All(r=>r.Id != SelectedRuleSetId)) SelectedRuleSetId = RuleSets.First(r=>r.Builtin).Id;
            foreach (Server incoming in feed.Servers) {
                Server existing = mine.FirstOrDefault(m => m.RemoteId == incoming.RemoteId);
                if (existing == null) {
                    incoming.SubscriptionId = subscriptionId;
                    Servers.Add(incoming);
                    result.Added++;
                    continue;
                }
                bool access = existing.Hostname != incoming.Hostname || existing.Address != incoming.Address || existing.Username != incoming.Username || existing.Password != incoming.Password;
                bool look = existing.Protocol != incoming.Protocol || existing.Name != incoming.Name || existing.Country != incoming.Country || existing.Orphaned;
                if (access || look) {
                    existing.Hostname = incoming.Hostname; existing.Address = incoming.Address; existing.Username = incoming.Username;
                    existing.Password = incoming.Password; existing.Protocol = incoming.Protocol; existing.Name = incoming.Name;
                    existing.Country = incoming.Country; existing.Orphaned = false;
                    result.Updated++;
                    if (access && existing.Id == connectedServerId) result.CurrentChanged = true;
                }
            }
            foreach (Server gone in mine.Where(m => feed.Servers.All(f => f.RemoteId != m.RemoteId))) {
                result.Removed++;
                if (gone.Id == connectedServerId) gone.Orphaned = true;
                else Servers.Remove(gone);
            }
            sub.Name = feed.Name; sub.UpdateHours = feed.UpdateHours;
            sub.UpdatedAt = DateTime.UtcNow.ToString("o"); sub.LastError = null;
            FixSelection(subscriptionId);
            Raise();
            return result;
        }

        // After the session ends: servers that left their subscription go away.
        internal void DropOrphans() {
            List<Server> orphans = Servers.Where(x => x.Orphaned).ToList();
            if (orphans.Count == 0) return;
            string from = orphans.Select(x => x.SubscriptionId).FirstOrDefault(x => x != null);
            foreach (Server orphan in orphans) Servers.Remove(orphan);
            FixSelection(from);
            Raise();
        }

        // Removes the subscription and its servers; a connected one stays as an orphan.
        internal void RemoveSubscription(string id, string connectedServerId) {
            Subscription sub = Subscriptions.FirstOrDefault(x => x.Id == id);
            if (sub == null) throw new FormatException("Подписка не найдена.");
            Subscriptions.Remove(sub);
            RuleSets.RemoveAll(r=>r.SubscriptionId == id);
            if (RuleSets.All(r=>r.Id != SelectedRuleSetId)) SelectedRuleSetId = RuleSets.First(r=>r.Builtin).Id;
            foreach (Server server in Servers.Where(x => x.SubscriptionId == id).ToList()) {
                if (server.Id == connectedServerId) { server.Orphaned = true; server.SubscriptionId = null; }
                else Servers.Remove(server);
            }
            FixSelection(null);
            Raise();
        }

        // A selected server that is gone: the subscription's first live server, else the first one.
        void FixSelection(string preferSubscription) {
            if (SelectedServerId != null && Servers.Any(x => x.Id == SelectedServerId)) return;
            Server next = Servers.FirstOrDefault(x => !x.Orphaned && preferSubscription != null && x.SubscriptionId == preferSubscription) ?? Servers.FirstOrDefault(x => !x.Orphaned);
            SelectedServerId = next == null ? null : next.Id;
        }
    }
}
