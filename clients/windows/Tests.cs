using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Netlane {
    // Engine stand-in for switching tests: records starts and stops, each takes a few milliseconds.
    sealed class FakeConnection : IConnection {
        public bool Active { get; set; }
        public int Starts, Stops; public List<string> Hosts = new List<string>(); public List<string> Sets = new List<string>();
        public event Action<string> Changed;
        public async Task StartAsync(Profile profile, bool elevate, int socksPort) { await Task.Delay(30); Starts++; Hosts.Add(profile.Hostname); Sets.Add(String.Join(",", profile.Routing.Destinations)); Active = true; if (Changed != null) { } }
        public async Task<bool> StopAsync() { await Task.Delay(30); Stops++; Active = false; return true; }
        public void Dispose() { }
    }
    sealed class FakeTraffic : ITrafficSource { public long Rx, Tx; public bool Ok = true; public bool TryRead(out long received, out long sent) { received = Rx; sent = Tx; return Ok; } }
    sealed class FakeFetcher : IFeedFetcher {
        public string Json; public Exception Fail; public int Calls;
        public Task<string> Fetch(string url, CancellationToken cancel) { Calls++; if (Fail != null) { TaskCompletionSource<string> t = new TaskCompletionSource<string>(); t.SetException(Fail); return t.Task; } return Task.FromResult(Json); }
    }
    sealed class GatedFetcher : IFeedFetcher {
        public string Json; public string LastUrl; public int Calls; public TaskCompletionSource<string> Gate = new TaskCompletionSource<string>(); public bool Hang;
        public Task<string> Fetch(string url, CancellationToken cancel) { LastUrl = url; Calls++; if (Hang) return new TaskCompletionSource<string>().Task; if (Calls == 1) return Gate.Task; return Task.FromResult(Json); }
    }
    // A pipe whose engine is gone: every write or flush fails like «Канал нарушен».
    sealed class BrokenPipeStream : Stream {
        public override bool CanRead { get { return false; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return true; } }
        public override long Length { get { return 0; } } public override long Position { get { return 0; } set { } }
        public override void Flush() { throw new IOException("Канал нарушен."); }
        public override int Read(byte[] buffer, int offset, int count) { return 0; }
        public override long Seek(long offset, SeekOrigin origin) { return 0; }
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { throw new IOException("Канал нарушен."); }
    }
    internal static class Tests {
        static readonly string Fixture = "hostname = \"vpn.example.com\"\naddresses = [\"vpn.example.com:443\"]\nhas_ipv6 = false\nusername = \"alice\"\npassword = \"unique-test-secret-123\"\nskip_verification = false\nupstream_protocol = \"http3\"\nanti_dpi = true\n";
        // The last check that passed: the watchdog names it when a run stops moving.
        static volatile string lastCheck = "(none)";
        static void Check(bool value, string message) { if (!value) throw new Exception(message); lastCheck = message; }
        static void RejectState(Action action) { try { action(); } catch (FormatException) { return; } throw new Exception("State rule violation was accepted."); }
        static Profile LegacyEquivalent(string text) { Profile p = Profile.Import(text); p.KillSwitch = true; p.AntiDpi = false; p.Routing = new Routing(); return p; }
        // Servers, rule sets and the client state (spec 2026-11-03 servers and rule sets).
        static void DnsTests() {
            foreach(string local in new[]{"192.168.1.1","10.0.0.1:53","127.0.0.1","172.16.5.5","169.254.1.1","tls://192.168.1.1","[fe80::1]:53","fd00::1","::1"}){
                string message=null;try{DnsSettings.NormalizeServer(local);}catch(FormatException error){message=error.Message;}
                Check(message!=null&&message.Contains("локальной сети"),"DNS from the local network is refused with an explanation: "+local+" → "+message);
            }
            Check(DnsSettings.NormalizeServer("1.1.1.1")=="1.1.1.1"&&DnsSettings.NormalizeServer("tls://8.8.8.8")=="tls://8.8.8.8","Public resolvers stay accepted.");
            {
                string stateDir=TempDir();
                ClientState stored=ClientState.CreateEmpty();stored.Servers.Add(Server.FromProfile(Profile.Import(Fixture)));stored.SelectServer(stored.Servers[0].Id);
                StateStore.Save(stateDir,stored);
                // A state written by a version with looser checks: one good and two bad DNS entries.
                string json=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(stored).Replace("\"DnsServers\":[]","\"DnsServers\":[\"1.1.1.1\",\"192.168.1.1\",\"bogus\"]");
                Check(json.Contains("192.168.1.1"),"Test state carries the bad DNS entries.");
                File.WriteAllBytes(Path.Combine(stateDir,"state.bin"),ProtectedData.Protect(Encoding.UTF8.GetBytes(json),Encoding.UTF8.GetBytes("Netlane.state.v2"),DataProtectionScope.CurrentUser));
                string notice;ClientState loaded=StateStore.LoadOrMigrate(stateDir,out notice);
                Check(loaded.Servers.Count==1&&loaded.DnsServers.SequenceEqual(new[]{"1.1.1.1"})&&!File.Exists(Path.Combine(stateDir,"state.bin.bad"))&&Directory.GetFiles(stateDir,"state.bin.bad-*").Length==0,"Bad stored DNS entries are dropped, the servers stay: "+String.Join(",",loaded.DnsServers));
                Check(notice!=null&&notice.Contains("192.168.1.1")&&notice.Contains("bogus"),"The user is told which DNS entries were dropped: "+notice);
            }
            Check(DnsSettings.Parse("").Count==0,"Empty DNS means engine defaults.");
            var values=DnsSettings.Parse(" 1.1.1.1 \r\n8.8.8.8:53\ntls://DNS.EXAMPLE.COM\nhttps://dns.example.com/dns-query\n[2001:db8::1]:53");
            Check(values.Count==5&&values[0]=="1.1.1.1"&&values[2]=="tls://dns.example.com"&&values[4]=="[2001:db8::1]:53","DNS address normalization.");
            foreach(string bad in new[]{"http://dns.example.com/dns-query","ftp://dns.example.com","dns.example.com","1","1:53","127.1:53","01.1.1.1:53","256.1.1.1","0.0.0.0","::","tls://user:pass@dns.example.com","tls://dns.example.com:0","tls://dns.example.com:65536","tls://dns.example.com/path","https://dns.example.com/#fragment","1.1.1.1\npassword = injected","1.1.1.1 bad"}){
                bool refused=false;try{DnsSettings.NormalizeServer(bad);}catch(FormatException){refused=true;}Check(refused,"Invalid DNS accepted: "+bad);
            }
            RejectState(delegate{DnsSettings.Parse("1.1.1.1\n1.1.1.1");});
            RejectState(delegate{DnsSettings.Normalize(Enumerable.Range(1,9).Select(i=>"192.0.2."+i));});
            Check(DnsSettings.NormalizeServer("tls://[2001:db8::1]:853")=="tls://[2001:db8::1]:853"&&DnsSettings.NormalizeServer("quic://dns.example.com:8853")=="quic://dns.example.com:8853","Encrypted resolver ports must be preserved.");
            ClientState state=ClientState.CreateEmpty();state.Servers.Add(Server.FromProfile(Profile.Import(Fixture)));state.SelectServer(state.Servers[0].Id);state.DnsServers=values;
            Profile session=state.SessionProfile();string config=session.EngineConfig(18090);
            Check(config.Contains("dns_upstreams = [\"1.1.1.1\", \"8.8.8.8:53\", \"tls://dns.example.com\"")&&config.IndexOf("dns_upstreams")>config.IndexOf("[endpoint]")&&config.IndexOf("dns_upstreams")<config.IndexOf("[listener.socks]"),"DNS must reach the endpoint section of the real engine config.");
            session.DnsServers[0]="9.9.9.9";Check(state.DnsServers[0]=="1.1.1.1","Session must not mutate stored DNS.");
            string dir=Path.Combine(Path.GetTempPath(),"netlane-dns-"+Guid.NewGuid().ToString("N"));
            try{StateStore.Save(dir,state);string notice;ClientState saved=StateStore.LoadOrMigrate(dir,out notice);Check(notice==null&&saved.DnsServers.SequenceEqual(values),"DNS survives encrypted storage round trip.");}
            finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
            FakeHost host=new FakeHost();host.Active=true;
            using(SettingsForm settings=new SettingsForm(host)){
                settings.Show();settings.ShowPage("connection");Application.DoEvents();
                TextBox input=AllControls(settings).OfType<TextBox>().First(c=>c.AccessibleName=="DNS-серверы для VPN");
                input.Text="1.1.1.1\r\ntls://1.0.0.1";Check(settings.SaveDns()&&host.Value.DnsServers.Count==2&&host.Active,"DNS saves while preserving the active connection.");
                input.Text="not a resolver";Check(!settings.SaveDns()&&host.Value.DnsServers[0]=="1.1.1.1","Invalid DNS leaves the previous setting intact.");
                host.FailDns=true;input.Text="9.9.9.9";Check(!settings.SaveDns()&&host.Value.DnsServers[0]=="1.1.1.1","Failed persistence preserves the previous DNS.");host.FailDns=false;
                input.Clear();Check(settings.SaveDns()&&host.Value.DnsServers.Count==0,"Clearing DNS restores the engine default.");
            }
        }

        static void StateTests() {
            ClientState s=ClientState.CreateEmpty();
            Check(s.RuleSets.Count==1&&s.RuleSets[0].Builtin&&s.RuleSets[0].Name=="Весь трафик"&&s.RuleSets[0].Routing.Mode=="general"&&s.SelectedRuleSet.Builtin,"Built-in set exists and is selected.");
            Check(s.KillSwitch&&!s.AntiDpi&&!s.StartWithWindows&&!s.AutoConnect&&s.CloseToTray,"Spec defaults.");
            int changes=0;s.Changed+=delegate{changes++;};
            Server a=Server.FromProfile(Profile.Import(Fixture));s.Servers.Add(a);s.SelectServer(a.Id);
            Profile session=s.SessionProfile();Check(session.EngineConfig(0)==LegacyEquivalent(Fixture).EngineConfig(0),"Engine config equals the single-profile config.");
            RuleSet work=s.AddRuleSet("Работа",new Routing{Mode="selective",Destinations=new List<string>{"mos.ru"}});s.SelectRuleSet(work.Id);
            Check(s.SessionProfile().Routing.Destinations.SequenceEqual(new[]{"mos.ru"})&&s.SessionProfile().Routing.Mode=="selective","Selected set routes the session.");
            RejectState(delegate{s.RemoveRuleSet(s.RuleSets[0].Id);});RejectState(delegate{s.RenameRuleSet(s.RuleSets[0].Id,"X");});RejectState(delegate{s.UpdateRuleSet(s.RuleSets[0].Id,new Routing());});
            RejectState(delegate{s.AddRuleSet("работа",new Routing());});RejectState(delegate{s.AddRuleSet("",new Routing());});RejectState(delegate{s.AddRuleSet(new string('x',41),new Routing());});
            for(int i=s.RuleSets.Count;i<20;i++)s.AddRuleSet("Набор "+i,new Routing());RejectState(delegate{s.AddRuleSet("Лишний",new Routing());});
            s.RemoveRuleSet(work.Id);Check(s.SelectedRuleSet.Builtin,"Removing the selected set falls back to the built-in one.");
            Check(changes>=3,"Mutators raise Changed.");
            ClientState none=ClientState.CreateEmpty();bool threw=false;try{none.SessionProfile();}catch(InvalidOperationException){threw=true;}Check(threw,"No session without a selected server.");
            StoreTests();
            ImportTests();
            SpeedTests();
        }
        static List<Control> AllControls(Control root) { List<Control> all=new List<Control>();Stack<Control> pending=new Stack<Control>();pending.Push(root);while(pending.Count>0){Control c=pending.Pop();all.Add(c);foreach(Control child in c.Controls)pending.Push(child);}return all; }
        // Main window (layout of 2026-11-08): status, ring, clock, speed graph, locations,
        // routing, kill switch, subscription and the footer. Error and first launch have their views.
        static void MainWindowTests() {
            using (MainForm f=new MainForm(true,null,ConnectionView.Connected)) {
                f.Show();Application.DoEvents();List<Control> all=AllControls(f);
                DarkButton action=(DarkButton)all.First(c=>c is DarkButton&&c.Visible&&c.Text=="Отключиться");
                Check(action.Kind==ButtonKind.Secondary&&action.Enabled,"Connected: the action is a quiet «Отключиться».");
                Check(all.OfType<StatusBlock>().Any(c=>c.Visible&&c.Title=="Защищено"),"Connected: «Защищено» in the page header.");
                Check(all.OfType<StatusBlock>().Any(c=>c.Visible&&c.Subtitle.Contains("HTTP/2")&&c.Subtitle.StartsWith("Амстердам")),"Connected: location and protocol under the status.");
                Check(all.Any(c=>c is Label&&c.Visible&&c.Text=="00:42:17"),"Connected: session time in the session rows.");
                Check(all.Any(c=>c is Label&&c.Visible&&c.Text=="↑ 210,0 МБ")&&all.Any(c=>c is Label&&c.Visible&&c.Text=="↓ 1,8 ГБ"),"Connected: sent and received in the session rows.");
                LocationsTable table=(LocationsTable)all.First(c=>c is LocationsTable);
                Check(table.Visible&&table.RowCount==2&&table.SelectedName=="Амстердам","Two locations: both are rows of the table: "+table.RowCount);
                if(Type.GetType("Mono.Runtime")==null)Check(f.PeakFitsForTests,"The peak line under the speed lanes fits its column.");
                Check(!all.Any(c=>c is LinkLabel&&c.Visible&&(c.Text=="Проверить пинг"||c.Text=="Добавить ссылку…")),"Ping check and adding live in the table, not as links under it.");
                Check(table.AddRowBounds.Bottom<=table.Height&&table.RefreshBounds.Right<=table.Width,"The add row and the refresh glyph fit the table: "+table.AddRowBounds+" / "+table.RefreshBounds);
                Check(f.Size.Width==920&&f.Size.Height==560,"Default window is 920 × 560: "+f.Size);
                List<NavItem> items=all.OfType<NavItem>().Where(c=>c.Visible).ToList();
                Check(items.Count==MainForm.NavIds.Length&&items.Single(c=>c.Selected).Text=="Подключение","Navigation: six pages, «Подключение» first: "+items.Count);
                Check(items.All(c=>c.Glyph==(string)c.Tag),"Every navigation item draws its own icon.");
                Size normal=f.ClientSize;
                foreach(Size size in new[]{normal,new Size(860,520),new Size(1200,760),normal}) {
                    if(size==normal)f.ClientSize=size;else f.Size=size;Application.DoEvents();
                    foreach(Control control in all) {
                        if(!control.Visible||control.Parent==null)continue;
                        Rectangle bounds=control.Bounds, client=control.Parent.ClientRectangle;
                        Check(client.Contains(bounds),"Control "+control.GetType().Name+" escapes its parent at "+f.ClientSize+": "+bounds+" in "+client);
                    }
                }
                using(ContextMenuStrip menu=f.BuildRoutingMenu()) {
                    menu.Show(f,new Point(220,250));Application.DoEvents();
                    bool mono=Type.GetType("Mono.Runtime")!=null;
                    if(!mono)foreach(ToolStripItem item in menu.Items)if(item is ToolStripMenuItem)
                        Check(item.Bounds.Right>=menu.ClientSize.Width-2,"Routing menu row must reach the right edge: "+item.Bounds+" / "+menu.ClientSize);
                    ToolStripItem manage=menu.Items[menu.Items.Count-1];
                    Check(manage.Text=="Настроить наборы…"&&manage.ForeColor==Theme.Muted&&menu.Items[menu.Items.Count-2] is ToolStripSeparator,"«Настроить наборы…» is a quiet action under a separator.");
                    menu.Close();Application.DoEvents();
                }
                DarkButton routing=(DarkButton)all.First(c=>c is DarkButton&&c.AccessibleName=="Набор правил");
                Check(routing.Text=="Мои правила"&&routing.Visible,"«Набор правил» shows the set in use: "+routing.Text);
                MiniSwitch kill=(MiniSwitch)all.First(c=>c is MiniSwitch);
                Check(kill.Visible&&kill.Checked==f.State.KillSwitch,"Kill Switch on the page mirrors the state.");
                kill.Checked=!kill.Checked;Application.DoEvents();
                Check(f.State.KillSwitch==kill.Checked,"Kill Switch on the page changes the state.");
                Label versionLabel=(Label)all.First(c=>c is Label&&c.Text.StartsWith("v"));Check(TextRenderer.MeasureText(versionLabel.Text,versionLabel.Font).Width<=versionLabel.Width+4,"Footer version fits: "+versionLabel.Text);
            }
            foreach(KeyValuePair<string,int> sample in new[]{new KeyValuePair<string,int>("one-server",1),new KeyValuePair<string,int>("three-servers",3),new KeyValuePair<string,int>("six-servers",6),new KeyValuePair<string,int>("ten-servers",10)})
            using (MainForm f=new MainForm(true,sample.Key,ConnectionView.Connected)) {
                f.Show();Application.DoEvents();
                LocationsTable table=(LocationsTable)AllControls(f).First(c=>c is LocationsTable);
                Check(table.Visible&&table.RowCount==sample.Value&&f.State.Servers.Count==sample.Value,sample.Key+": every location is a table row: "+table.RowCount);
            }
            using (MainForm f=new MainForm(true,null,ConnectionView.Idle)) {
                f.Show();Application.DoEvents();List<Control> all=AllControls(f);
                DarkButton action=(DarkButton)all.First(c=>c is DarkButton&&c.Visible&&c.Text=="Подключиться");
                Check(action.Kind==ButtonKind.Primary,"Idle: the action is the primary «Подключиться».");
                Check(all.OfType<StatusBlock>().Any(c=>c.Visible&&c.Title=="Не подключено"),"Idle status.");
                Check(!all.Any(c=>c is Label&&c.Visible&&(c.Text.StartsWith("↓ ")||c.Text.StartsWith("↑ "))),"Idle: no traffic in the session rows.");
                SpeedGraph lanes=new SpeedGraph(); lanes.Push(2048,100); lanes.Push(4096,300);
                Check(lanes.SampleCount==2&&lanes.Peak==4096&&lanes.UpPeak==300&&lanes.Last(true)==300&&lanes.Last(false)==4096,"Speed lanes keep receive and send apart.");
                lanes.Dispose();
            }
            using (MainForm f=new MainForm(true,null,ConnectionView.Idle)) {
                f.Show();Application.DoEvents();
                f.PingProbe=delegate(Server server){return Task.FromResult(server.Hostname.StartsWith("ams")?"31 мс":"нет ответа");};
                LocationsTable table=(LocationsTable)AllControls(f).First(c=>c is LocationsTable);
                string pickedId=null; table.Picked+=delegate(string id){pickedId=id;}; table.PickRow(1);
                Check(pickedId==f.State.Servers[1].Id,"A click on a row picks that location.");
                Pump(f.CheckPings());
                Check(table.PingFor(0)=="31 мс"&&f.PingsForTests.Count==2&&f.PingsForTests[f.State.Servers[1].Id]=="нет ответа","Ping check measures every server and updates the table.");
                Check(!table.Checking&&f.PingNoteForTests=="Пинг проверен только что","After a ping check the note under the table says when: "+f.PingNoteForTests);
                int pingAsks=0,addAsks=0; table.PingRequested+=delegate{pingAsks++;}; table.AddRequested+=delegate{addAsks++;};
                table.RequestPing(); table.RequestAdd(); table.Checking=true; table.RequestPing(); table.Checking=false;
                Check(pingAsks==1&&addAsks==1,"The refresh glyph and the add row raise their actions; a running check ignores another: "+pingAsks+"/"+addAsks);
                string error=f.PasteLink("not a link");Check(error=="В буфере обмена нет ссылки tt:// или ссылки подписки.","«Вставить» without a link explains: "+error);
            }
            using (MainForm f=new MainForm(true,"no-servers",null)) {
                f.Show();Application.DoEvents();List<Control> all=AllControls(f);
                Check(!all.Any(c=>(c is LocationsTable||c is StatusBlock)&&c.Visible)&&all.Any(c=>c is Label&&c.Visible&&c.Text=="Подключите первый сервер"),"No profile: the onboarding view instead of the columns.");
                DarkButton primary = (DarkButton)all.Single(c=>c is DarkButton&&c.Text=="Вставить ссылку"&&c.Visible);
                DarkButton secondary = (DarkButton)all.Single(c=>c is DarkButton&&c.Text=="Открыть файл TOML…"&&c.Visible);
                Check(primary.Kind==ButtonKind.Primary&&primary.Width==secondary.Width,"No profile: one primary action, equal buttons.");
                Check(secondary.Top-primary.Bottom>=8,"Buttons keep a visible gap: "+secondary.Top+" / "+primary.Bottom);
                foreach(Control control in all) {
                    if(!control.Visible||control.Parent==null)continue;
                    Check(control.Parent.ClientRectangle.Contains(control.Bounds),"First launch: "+control.GetType().Name+" escapes its parent: "+control.Bounds+" in "+control.Parent.ClientRectangle);
                }
            }
            using (MainForm f=new MainForm(true,null,ConnectionView.Error)) {
                f.Show();Application.DoEvents();List<Control> all=AllControls(f);
                Check(all.OfType<StatusBlock>().Any(c=>c.Visible&&c.Title=="Не удалось подключиться"),"Error: its own status.");
                Check(all.Any(c=>c is DarkButton&&c.Visible&&c.Text=="Повторить"&&((DarkButton)c).Kind==ButtonKind.Primary),"Error: the action retries.");
                Check(all.OfType<LogPreview>().Any(c=>c.Visible)&&all.OfType<LocationsTable>().Any(c=>c.Visible),"Error: the engine tail is on screen and another location can be picked.");
            }
            // One window: settings and the log are pages beside «Подключение».
            using (MainForm f=new MainForm(true,null,ConnectionView.Idle)) {
                f.Show();Application.DoEvents();
                f.Navigate("servers");Application.DoEvents();List<Control> all=AllControls(f);
                Check(f.PageForTests=="servers"&&all.OfType<SettingsForm>().Single().Visible&&!all.OfType<LocationsTable>().Any(c=>c.Visible),"«Серверы» opens inside the main window.");
                Check(all.OfType<NavItem>().Single(c=>c.Selected).Text=="Серверы","Navigation marks the open page.");
                f.Navigate("rules");Application.DoEvents();
                Check(!AllControls(f).OfType<SettingsForm>().Single().RulesDirtyForTests,"Opening «Правила» does not mark the set as changed.");
                Routing sample=new Routing{Mode="selective"};sample.Destinations.Add("mos.ru");
                using(RuleSetEditor managed=new RuleSetEditor(new RuleSet{Id="m",Name="Tutor",SubscriptionId="s",Routing=sample},true))
                using(RuleSetEditor own=new RuleSetEditor(new RuleSet{Id="o",Name="Мои",Routing=sample},false)) {
                    managed.Value.Destinations.Add("example.com");own.Value.Destinations.Add("example.com");
                    Check(!managed.Dirty&&own.Dirty,"A set from the subscription is never «unsaved»; a personal one is once it changes.");
                }
                Check(f.PageForTests=="rules"&&AllControls(f).OfType<SettingsForm>().Count()==1,"Settings pages share one embedded form.");
                f.Navigate("log");Application.DoEvents();all=AllControls(f);
                Check(f.PageForTests=="log"&&all.OfType<LogPage>().Single().Visible&&!all.OfType<SettingsForm>().Single().Visible,"«Журнал» replaces the settings page.");
                LogView log=all.OfType<LogView>().Single();
                log.SetLines(new[]{"short",new string('x',300)+" tail","   at Netlane.Something()"});
                Check(log.RowCountForTests>4&&log.Text.Contains("tail"),"The log wraps long lines instead of scrolling sideways: "+log.RowCountForTests);
                Check(!all.Any(c=>c is TextBox&&c.Visible&&c.Parent is Panel&&c.Parent.Parent is LogPage),"The log has no system text box.");
                f.Navigate("home");Application.DoEvents();
                Check(f.PageForTests=="home"&&AllControls(f).OfType<LocationsTable>().Single().Visible,"«Подключение» comes back.");
                f.OpenSettings(null);Application.DoEvents();
                Check(f.PageForTests=="servers","The tray «Настройки» opens «Серверы».");
                f.Navigate("no-such-page");Check(f.PageForTests=="home","An unknown page falls back to «Подключение».");
            }
            Check(PreviewRequest.Parse(new[]{"--render","x.png","--page=six-servers"}).Page=="six-servers"&&PreviewRequest.Parse(new[]{"--render","x.png","--page=no-servers"}).Page=="no-servers","New preview pages.");
        }
        static void Pump(Task task, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { DateTime until = DateTime.UtcNow.AddSeconds(10); while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); } if (!task.IsCompleted) throw new Exception("Switch did not finish (Tests.cs line " + line + ")."); if (task.IsFaulted) throw task.Exception; }
        // Quick switching (spec §2): reconnect when connected, coalesce bursts, only select when idle.
        static void SwitchTests() {
            FakeConnection fake = new FakeConnection { Active = true };
            using (MainForm f = new MainForm(true, null, ConnectionView.Connected, null, fake)) {
                f.Show(); Application.DoEvents();
                Server other = f.State.Servers[1]; RuleSet builtin = f.State.RuleSets[0];
                Pump(f.SwitchTo(other.Id, null));
                Check(fake.Stops == 1 && fake.Starts == 1 && fake.Hosts[0] == other.Hostname && f.State.SelectedServerId == other.Id, "Connected switch: one stop, one start with the new server.");
                Task first = f.SwitchTo(f.State.Servers[0].Id, null); f.SwitchTo(null, builtin.Id); Task last = f.SwitchTo(other.Id, builtin.Id);
                Pump(first); Pump(last);
                Check(fake.Starts == 2 && fake.Hosts[1] == other.Hostname && fake.Sets[1] == "" && f.State.SelectedRuleSet.Builtin, "A burst of switches ends in exactly one more start with the last target: " + fake.Starts);
                Check(f.ViewForTests == ConnectionView.Reconnecting || f.ViewForTests == ConnectionView.Connected, "No stuck state after switching.");
            }
            FakeConnection stopping = new FakeConnection { Active = true };
            using (MainForm f = new MainForm(true, null, ConnectionView.Connected, null, stopping)) {
                f.Show(); Application.DoEvents();
                Task off = f.Disconnect(); Task pick = f.SwitchTo(f.State.Servers[1].Id, null);
                Pump(off); Pump(pick);
                Check(stopping.Starts == 0 && !stopping.Active && f.State.SelectedServerId == f.State.Servers[1].Id, "A switch during «Отключить» only selects; the VPN stays off: starts=" + stopping.Starts);
            }
            FakeConnection starting = new FakeConnection();
            using (MainForm f = new MainForm(true, null, ConnectionView.Idle, null, starting)) {
                f.Show(); Application.DoEvents();
                Task on = f.Connect(); Task pick = f.SwitchTo(f.State.Servers[1].Id, null);
                Pump(on); Pump(pick); Pump(f.SwitchTo(null, null));
                Check(starting.Starts == 2 && starting.Hosts[1] == f.State.Servers[1].Hostname && starting.Active, "A switch during «Подключить» restarts once with the new server after the connect: " + String.Join(",", starting.Hosts));
            }
            FakeConnection idle = new FakeConnection();
            using (MainForm f = new MainForm(true, null, ConnectionView.Idle, null, idle)) {
                Pump(f.SwitchTo(f.State.Servers[1].Id, null));
                Check(idle.Starts == 0 && idle.Stops == 0 && f.State.SelectedServerId == f.State.Servers[1].Id, "Idle switch only selects.");
            }
            ClientState st = ClientState.CreateEmpty();
            foreach (string h in new[] { "a.example.com", "b.example.com" }) st.AddServer(Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com", h))), DuplicateChoice.AddCopy);
            string picked = null; bool add = false;
            using (ServerMenu menu = new ServerMenu(st, new Dictionary<string, string>())) {
                menu.Picked += delegate(string id) { picked = id; }; menu.AddRequested += delegate { add = true; };
                Check(menu.RowCount == 3, "Menu: one row per server plus «+ Добавить подписку или сервер…».");
                menu.PickRow(0); menu.PickRow(2);
                Check(picked == st.Servers[0].Id && add, "Menu rows pick a server or ask to add one.");
            }
            using (MainForm f = new MainForm(true, null, ConnectionView.Idle)) {
                Server existing = f.State.Servers[0]; existing.Name = "Дом";
                Check(MainForm.DuplicateText(existing) == "Сервер «Дом» уже есть. Обновить его или добавить копию?", "Duplicate question text.");
                Server same = Server.FromProfile(Profile.Import("hostname = \"" + existing.Hostname + "\"\naddresses = [\"" + existing.Hostname + ":443\"]\nusername = \"adm\"\npassword = \"rotated-password-12345\"\n"));
                Server asked = null; int count = f.State.Servers.Count;
                Check(f.ImportServer(same, delegate(Server d) { asked = d; return null; }) == null && asked == existing && f.State.Servers.Count == count, "Cancel keeps everything.");
                Server updated = f.ImportServer(same, delegate(Server d) { return DuplicateChoice.Update; });
                Check(updated.Id == existing.Id && updated.Password == "rotated-password-12345" && f.State.Servers.Count == count, "Update rewrites the existing server.");
                Server copy = f.ImportServer(Server.FromProfile(Profile.Import("hostname = \"" + existing.Hostname + "\"\naddresses = [\"" + existing.Hostname + ":443\"]\nusername = \"adm\"\npassword = \"rotated-password-12345\"\n")), delegate(Server d) { return DuplicateChoice.AddCopy; });
                Check(copy.Id != existing.Id && f.State.Servers.Count == count + 1 && f.State.SelectedServerId == copy.Id, "Add copy adds and selects.");
                bool askedNew = false; f.ImportServer(Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com", "new.example.com"))), delegate(Server d) { askedNew = true; return DuplicateChoice.Update; });
                Check(!askedNew && f.State.Servers.Count == count + 2, "A new server is added without a question.");
            }
            Check(MainForm.KillSwitchSwitchHint == "При переключении VPN перезапускается: на 1–3 с трафик может пойти напрямую.", "Kill Switch hint text.");
        }
        // Settings window, «Серверы» page (spec §3).
        static void SettingsServerTests() {
            using (MainForm main = new MainForm(true, null, ConnectionView.Idle))
            using (SettingsForm settings = new SettingsForm(main)) {
                main.Show(); settings.Show(); Application.DoEvents();
                StatusBlock status = (StatusBlock)AllControls(main).First(c => c is StatusBlock);
                Server selected = main.State.SelectedServer; int changed = 0; main.State.Changed += delegate { changed++; };
                settings.SetProtocol(selected.Id, "http3"); Application.DoEvents();
                Check(selected.Protocol == "http3" && changed > 0 && status.Subtitle.Contains("HTTP/3 · UDP"), "Protocol changed in settings reaches the state and the main window: " + status.Subtitle);
                Check(settings.RenameServer(selected.Id, "   ") != null && settings.RenameServer(selected.Id, new string('x', 65)) != null && selected.Name != "   ", "Empty or long names are rejected.");
                Check(settings.RenameServer(selected.Id, "Амстердам") == null && selected.Name == "Амстердам" && status.Subtitle.StartsWith("Амстердам"), "Rename reaches the card.");
                settings.SetCountry(selected.Id, "NL"); Check(selected.Country == "NL", "Flag change.");
                settings.SetCountry(selected.Id, null); Check(selected.Country == null, "«Без флага».");
                Check(settings.CanDelete(selected, false) && !settings.CanDelete(selected, true) && settings.CanDelete(main.State.Servers[1], true), "Selected server cannot be deleted while the VPN is on.");
                Check(AllControls(settings).Any(c => c is DarkButton && c.Text == "Добавить по ссылке…") && AllControls(settings).Any(c => c is DarkButton && c.Text == "Открыть TOML…"), "Import buttons on the servers page.");
                Check(SettingsForm.FastestLabel(new Dictionary<string, long> { { "a", 80 }, { "b", 21 }, { "c", -1 } }) == "b", "Fastest server among answered pings.");
            }
            PreviewRequest sr = PreviewRequest.Parse(new[] { "--render-settings", "x.png", "--page=rules" }); Check(sr.Settings && sr.Page == "rules" && PreviewRequest.Parse(new[] { "--render-settings", "x.png" }).Page == "servers", "Settings preview pages.");
            Check(CountryPicker.Search("нидер").Contains("NL") && CountryPicker.Search("герм").Contains("DE") && CountryPicker.Search("").Count() >= 240 && !CountryPicker.Search("qqqq").Any(), "Country search by Russian name.");
        }
        // «Правила» page and the editor fixes (spec §3).
        sealed class FakeHost : ISettingsHost {
            internal readonly ClientState Value = ClientState.CreateEmpty(); internal int Saves; internal bool Active; internal bool FailDns;
            public ClientState State { get { return Value; } }
            public void SaveState() { Saves++; }
            public bool SaveDnsServers(List<string> servers) { if(FailDns)return false;Value.DnsServers=DnsSettings.Normalize(servers);Saves++;Value.NotifyChanged();return true; }
            public Task SwitchTo(string serverId, string ruleSetId) { if (serverId != null) Value.SelectServer(serverId); if (ruleSetId != null) Value.SelectRuleSet(ruleSetId); return Task.FromResult(0); }
            public bool VpnActive { get { return Active; } }
            public void ImportLink() { } public void ImportFile() { }
            readonly Dictionary<string, string> pings = new Dictionary<string, string>();
            public Dictionary<string, string> Pings { get { return pings; } }
            public bool PreviewOnly { get { return false; } }
            public void RulesChanged(string ruleSetId) { }
            public void ImportTomlFile(string path) { }
            public Task RefreshSubscription(string id) { return Task.FromResult(0); }
            public void RemoveSubscription(string id) { Value.RemoveSubscription(id, null); }
            public Task<string> CheckForUpdates() { return Task.FromResult("Обновлений нет."); }
        }
        static void ConnectionAboutTests() {
            FakeHost host = new FakeHost();
            using (SettingsForm settings = new SettingsForm(host)) {
                settings.Show(); settings.ShowPage("connection"); Application.DoEvents();
                List<Control> all = AllControls(settings);
                var flags = new[] {
                    Tuple.Create("Kill Switch", (Func<bool>)(() => host.Value.KillSwitch)),
                    Tuple.Create("Anti-DPI", (Func<bool>)(() => host.Value.AntiDpi)),
                    Tuple.Create("Запускать вместе с Windows", (Func<bool>)(() => host.Value.StartWithWindows)),
                    Tuple.Create("Подключаться к последнему серверу при запуске", (Func<bool>)(() => host.Value.AutoConnect)),
                    Tuple.Create("Закрытие окна сворачивает в трей", (Func<bool>)(() => host.Value.CloseToTray)) };
                foreach (var flag in flags) {
                    ToggleSwitch toggle = all.OfType<ToggleSwitch>().FirstOrDefault(t => t.Text == flag.Item1);
                    Check(toggle != null && toggle.Checked == flag.Item2(), "Connection page shows «" + flag.Item1 + "» from the state.");
                    int saves = host.Saves; bool before = flag.Item2();
                    toggle.Checked = !toggle.Checked;
                    Check(flag.Item2() == !before && host.Saves == saves + 1, "«" + flag.Item1 + "» writes the state and saves.");
                }
                settings.ShowPage("about"); Application.DoEvents();
                all = AllControls(settings);
                Check(all.Any(c => c.Text.Contains(Program.Version)) && all.Any(c => c.Text.Contains(EngineSpec.Version)), "About: client and engine versions.");
                Check(all.OfType<LinkLabel>().Any(l => l.Text == "Журнал движка" && (string)l.Tag == EngineLog.Path), "About: engine log link.");
                Check(all.Any(c => c.Text.Contains("Apache-2.0")) && all.Any(c => c.Text.Contains("Wintun")), "About: licenses.");
            }
        }
        static void TrayTests() {
            ClientState s = ClientState.CreateEmpty();
            s.AddServer(Server.FromProfile(Profile.Import(Fixture)), DuplicateChoice.AddCopy);
            Server second = s.AddServer(Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com", "ams.example.com"))), DuplicateChoice.AddCopy);
            RuleSet mine = s.AddRuleSet("Мои правила", new Routing());
            bool connected = false, visible = true;
            using (NotifyIcon icon = new NotifyIcon())
            using (TrayController tray = new TrayController(icon, s, () => connected, () => visible)) {
                tray.Rebuild();
                Check(tray.Item("toggle").Text == "Подключить", "Tray: «Подключить» while idle.");
                connected = true; tray.Rebuild(); Check(tray.Item("toggle").Text == "Отключить", "Tray: «Отключить» while connected.");
                ToolStripMenuItem servers = tray.Item("servers"), rules = tray.Item("rules");
                Check(servers.DropDownItems.Count == 2 && servers.DropDownItems.Cast<ToolStripMenuItem>().Count(i => i.Checked) == 1 && ((ToolStripMenuItem)servers.DropDownItems[1]).Checked, "Tray: servers with a check on the selected one.");
                Check(rules.DropDownItems.Count == 2 && ((ToolStripMenuItem)rules.DropDownItems[0]).Text == ClientState.BuiltinName && ((ToolStripMenuItem)rules.DropDownItems[0]).Checked, "Tray: rule sets with a check on the selected one.");
                Check(new[] { "open", "settings", "exit" }.All(n => tray.Item(n) != null), "Tray: Открыть, Настройки, Выход.");
                string pickedServer = null, pickedSet = null; tray.ServerPicked += id => pickedServer = id; tray.RuleSetPicked += id => pickedSet = id;
                servers.DropDownItems[0].PerformClick(); rules.DropDownItems[1].PerformClick();
                Check(pickedServer == s.Servers[0].Id && pickedSet == mine.Id, "Tray: picking a server or a set raises the switch.");
                Check(!tray.Notify(Program.ProductName, "Подключено: X") && tray.Notifications == 0, "Notifications are suppressed while the window is visible.");
                visible = false;
                Check(tray.Notify(Program.ProductName, "Подключено: X") && tray.LastText == "Подключено: X" && tray.Notifications == 1, "Notification while hidden.");
                tray.WindowHidden(); tray.WindowHidden();
                Check(tray.Notifications == 2 && tray.LastText == Program.ProductName + " работает в трее", "First hide tells once that the app stays in the tray.");
            }
            Check(MainForm.CloseDecision(true, CloseReason.UserClosing) == CloseAction.Hide, "Close to tray hides the window.");
            Check(MainForm.CloseDecision(false, CloseReason.UserClosing) == CloseAction.Exit, "Without close to tray the cross exits (stopping the VPN).");
            Check(MainForm.CloseDecision(true, CloseReason.WindowsShutDown) == CloseAction.Exit, "Shutdown always exits.");
        }
        sealed class FakeRunKey : IRunKey {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string name) { string v; return Values.TryGetValue(name, out v) ? v : null; }
            public void Set(string name, string value) { Values[name] = value; }
            public void Delete(string name) { Values.Remove(name); }
        }
        static void AutostartTests() {
            string exe = @"C:\Program Files\Netlane\Netlane.exe";
            Check(Autostart.Command(exe) == "\"" + exe + "\" --tray", "Autostart command: quoted path and --tray.");
            FakeRunKey key = new FakeRunKey();
            key.Set("WinTrustTunnel", "\"C:\\Old\\WinTrustTunnel.exe\" --tray");
            Autostart.Apply(true, key, exe); Check(key.Get(Autostart.ValueName) == Autostart.Command(exe) && key.Get("WinTrustTunnel") == null, "Autostart on writes the Run value and removes the former name.");
            Autostart.Apply(false, key, exe); Check(key.Get(Autostart.ValueName) == null, "Autostart off deletes the Run value.");
            Autostart.Apply(false, key, exe); Check(key.Values.Count == 0, "Autostart off without a value is a no-op.");
            Check(PreviewRequest.Parse(new[] { "--tray" }) == null && Program.StartHidden(new[] { "--tray" }) && !Program.StartHidden(new string[0]), "--tray is a normal start, hidden in the tray.");
            FakeHost host = new FakeHost();
            string dir = TempDir(), file = Path.Combine(dir, "Office.netlanerules");
            File.WriteAllText(file, RulesFile.Export(new Routing { Mode = "selective", Destinations = new List<string> { "office.example.com" } }));
            using (SettingsForm settings = new SettingsForm(host)) {
                RuleSet first = settings.ImportRulesFile(file), second = settings.ImportRulesFile(file);
                Check(first != null && first.Name == "Office" && first.Routing.Destinations.SequenceEqual(new[] { "office.example.com" }) && second != null && second.Name == "Office (2)", "Dropping Office.netlanerules twice makes «Office» and «Office (2)».");
                Check(SettingsForm.DropKind(file) == "rules" && SettingsForm.DropKind(@"C:\x\p.TOML") == "server" && SettingsForm.DropKind(@"C:\x\a.wttrules") == "rules" && SettingsForm.DropKind("a.exe") == null, "Drop kinds by extension.");
            }
        }
        static void FlagTests() {
            Image nl = Flags.For("NL", 15), big = Flags.For("nl", 30);
            Check(nl != null && nl.Width == 20 && nl.Height == 20 && big != null && big.Width == 40 && big.Height == 40, "Twemoji flag images 20×20 up to 18 px, 40×40 above.");
            Check(Flags.For("ZZ", 15) == null && Flags.For(null, 15) == null && Flags.For("../x", 15) == null, "No image for unknown codes.");
            using (Bitmap canvas = new Bitmap(40, 40)) using (Graphics g = Graphics.FromImage(canvas)) {
                Flags.DrawBadge(g, new Rectangle(4, 4, 28, 28), null, "Амстердам"); Flags.DrawBadge(g, new Rectangle(4, 4, 28, 28), "ZZ", null); Flags.DrawBadge(g, new Rectangle(4, 4, 28, 28), "NL", "x");
                Check(canvas.GetPixel(18, 18).A > 0, "Badges draw without throwing.");
            }
            Check(Enumerable.Range(0, CountryNames.All.GetLength(0)).All(i => Flags.For(CountryNames.All[i, 0], 15) != null), "Every country in the picker has a flag.");
        }
        static void RulesPageTests() {
            RuleSet managed = new RuleSet { Name = "Tutor", SubscriptionId = "test-feed", RemoteId = "tutor", Routing = new Routing() };
            Check(managed.Managed && managed.DisplayName == "Tutor", "Managed set shows only its name.");
            for (int i = 0; i < 60; i++) managed.Routing.Destinations.Add("host" + i + ".example.com");
            using (Form frame = new Form { ClientSize = new Size(700, 520) })
            using (RuleSetEditor editor = new RuleSetEditor(managed, true) { Dock = DockStyle.Fill }) {
                frame.Controls.Add(editor); frame.Show(); Application.DoEvents();
                DarkRuleList list = AllControls(editor).OfType<DarkRuleList>().Single(c => c.Items.Count == 60);
                Check(list.Parent.Parent is DarkRuleListViewport && list.Right > list.Parent.Width, "Native list scrollbar is outside the clipped viewport.");
                list.SelectedIndex = 59; list.TopIndex = 59; Application.DoEvents();
                Check(list.SelectedIndex == 59 && list.TopIndex > 0, "Last managed rule remains selectable and scrollable.");
                Check(AllControls(editor).OfType<Label>().Any(c => c.Text.StartsWith("Правила обновляются через подписку.")), "Managed rules have the correct explanation.");
                Check(AllControls(editor).OfType<TextBox>().All(c => !c.Enabled), "Managed rules remain read-only.");
                frame.ClientSize = new Size(1000, 650); Application.DoEvents();
                Check(list.Right > list.Parent.Width && list.Parent.Width > 500, "Scrollbar remains clipped after resize.");
            }
            using (RuleSetEditor builtin = new RuleSetEditor(ClientState.CreateEmpty().RuleSets[0], true)) {
                List<Control> all = AllControls(builtin);
                Check(!all.Any(c => c is DarkButton && c.Text == "Сохранить" && c.Visible && builtin.Visible) && all.Where(c => c is TextBox).All(c => !c.Enabled) && !all.Any(c => c is DarkButton && c.Text == "Добавить" && c.Enabled), "Built-in set opens read-only.");
            }
            using (Form frame = new Form { ClientSize = new Size(640, 480) })
            using (RuleSetEditor editor = new RuleSetEditor(new RuleSet { Name = "Мои правила", Routing = new Routing() }, false)) {
                editor.Dock = DockStyle.Fill; frame.Controls.Add(editor); frame.Show();
                foreach (Size size in new[] { new Size(640, 480), new Size(900, 600), new Size(1400, 900) }) {
                    frame.ClientSize = size; Application.DoEvents();
                    Control save = AllControls(editor).First(c => c is DarkButton && c.Text == "Сохранить");
                    Rectangle box = editor.RectangleToClient(save.RectangleToScreen(save.ClientRectangle));
                    Check(box.Bottom == editor.ClientSize.Height && box.Right == editor.ClientSize.Width, "«Сохранить» sits in the footer corner at " + size + ": " + box);
                }
                Check(!editor.Dirty, "A fresh editor is not dirty.");
            }
            using (MainForm main = new MainForm(true, null, ConnectionView.Idle))
            using (SettingsForm settings = new SettingsForm(main)) {
                settings.Show(); settings.ShowPage("rules"); Application.DoEvents();
                RuleSet mine = main.State.RuleSets.First(r => r.Name == "Мои правила");
                settings.CopyRuleSet(mine.Id);
                Check(main.State.RuleSets.Any(r => r.Name == "Мои правила (копия)" && r.Routing.Destinations.SequenceEqual(mine.Routing.Destinations)), "«Копировать» makes «Мои правила (копия)».");
                settings.CopyRuleSet(mine.Id); Check(main.State.RuleSets.Any(r => r.Name == "Мои правила (копия 2)"), "A second copy gets a distinct name.");
                Check(settings.RenameRuleSet(main.State.RuleSets[0].Id, "X") != null && settings.RenameRuleSet(mine.Id, "Офис") == null && mine.Name == "Офис", "Built-in cannot be renamed; others can.");
                RuleSet added = settings.NewRuleSet(); Check(added != null && added.Name == "Новый набор" && !added.Builtin, "«+ Новый набор».");
                settings.ShowPage("rules"); Application.DoEvents();
                settings.UseRuleSet(main.State.RuleSets[0].Id); Pump(Task.Delay(50));
                Check(main.State.SelectedRuleSet.Builtin, "«Использовать» selects the set.");
                Check(AllControls(settings).Any(c => c is DarkButton && c.Text == "Используется" && !c.Enabled), "The set in use shows «Используется».");
            }
            Check(PreviewRequest.Parse(new[] { "--render-routing", "x.png" }).Settings && PreviewRequest.Parse(new[] { "--render-routing", "x.png" }).Page == "rules", "--render-routing shows the rules page.");
        }
        static void SpeedTests() {
            FakeTraffic f=new FakeTraffic();TrafficMeter meter=new TrafficMeter(f);meter.Start();DateTime t=new DateTime(2026,11,3,12,0,0,DateTimeKind.Utc);double d,u;
            Check(meter.TryGetRate(t,out d,out u)&&d==0&&u==0,"First reading has no rate yet.");
            f.Rx=15938355;f.Tx=1258291;Check(meter.TryGetRate(t.AddSeconds(1),out d,out u)&&SessionStats.SpeedLine(d,u)=="↓ 15,2 МБ/с · ↑ 1,2 МБ/с","Speed line from two readings: "+SessionStats.SpeedLine(d,u));
            f.Rx=99999999;Check(meter.TryGetRate(t.AddSeconds(1.2),out d,out u)&&SessionStats.FormatRate(d)=="15,2 МБ/с","Readings closer than 0.5 s keep the previous rate.");
            f.Rx=100;f.Tx=50;Check(meter.TryGetRate(t.AddSeconds(3),out d,out u)&&d>=0&&u>=0,"A recreated adapter never gives a negative rate.");
            f.Ok=false;Check(!meter.TryGetRate(t.AddSeconds(5),out d,out u),"No counters, no rate.");
            Check(SessionStats.FormatRate(512)=="512 Б/с","Small rates in bytes.");
        }
        static void ImportTests() {
            string c,n;ServerName.Split("\U0001F1F3\U0001F1F1 Амстердам",out c,out n);Check(c=="NL"&&n=="Амстердам","Flag split.");
            ServerName.Split("Амстердам",out c,out n);Check(c==null&&n=="Амстердам","No flag.");
            ServerName.Split("\U0001F1F3 Амстердам",out c,out n);Check(c==null&&n=="\U0001F1F3 Амстердам","Lone indicator kept.");
            ServerName.Split("\U0001F1FF\U0001F1FF Test",out c,out n);Check(c=="ZZ"&&n=="Test","Unknown code still parsed; drawing falls back.");
            ServerName.Split("\U0001F1F3\U0001F1F1\U0001F1E9\U0001F1EA Двойной",out c,out n);Check(c=="NL"&&n=="\U0001F1E9\U0001F1EA Двойной","Only the first flag is taken.");
            ServerName.Split("\U0001F1F3\U0001F1F1",out c,out n);Check(c=="NL"&&n==null,"Flag without a name.");
            ServerName.Split("   ",out c,out n);Check(c==null&&n==null,"Blank name.");ServerName.Split(null,out c,out n);Check(c==null&&n==null,"No name.");
            Profile named=Profile.Import(LinkWithName("\U0001F1F3\U0001F1F1 Амстердам"));Check(named.Name=="\U0001F1F3\U0001F1F1 Амстердам","Tag 12 read.");
            Server fromLink=Server.FromProfile(named);Check(fromLink.Name=="Амстердам"&&fromLink.Country=="NL","Server takes name and flag from the link.");
            Check(Server.FromProfile(Profile.Import(Link(1,false))).Name=="vpn.example.com"&&Profile.Import(Fixture).Name==null,"No name: the host is used; TOML has no name.");
            ClientState s=ClientState.CreateEmpty();Server first=s.AddServer(Server.FromProfile(Profile.Import(Fixture)),DuplicateChoice.AddCopy);
            Server other=s.AddServer(Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com","ams.example.com"))),DuplicateChoice.AddCopy);
            Check(s.Servers.Count==2&&s.SelectedServerId==other.Id&&s.Servers[0]==first,"Import keeps the old server and selects the new one.");
            first.Name="Дом";first.Country="DE";Server again=Server.FromProfile(Profile.Import(Fixture.Replace("unique-test-secret-123","another-long-secret-456")));
            Check(s.FindDuplicate(again)==first&&s.FindDuplicate(Server.FromProfile(Profile.Import(Fixture.Replace("alice","bob"))))==null,"Duplicate found by host and user.");
            Server updated=s.AddServer(again,DuplicateChoice.Update);
            Check(updated.Id==first.Id&&updated.Name=="Дом"&&updated.Country=="DE"&&updated.Password=="another-long-secret-456"&&s.Servers.Count==2&&s.SelectedServerId==first.Id,"Update keeps id, name and flag.");
            Server copy=s.AddServer(Server.FromProfile(Profile.Import(Fixture)),DuplicateChoice.AddCopy);Check(s.Servers.Count==3&&copy.Id!=first.Id,"Add copy adds.");
            Server renamed=Server.FromProfile(named);renamed.Hostname="vpn.example.com";renamed.Username="alice";Server kept=s.AddServer(renamed,DuplicateChoice.Update);Check(kept.Name=="Амстердам"&&kept.Country=="NL","Update takes a new name and flag when the link has them.");
            for(int i=s.Servers.Count;i<50;i++)s.AddServer(Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com","h"+i+".example.com"))),DuplicateChoice.AddCopy);
            RejectState(delegate{s.AddServer(Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com","h99.example.com"))),DuplicateChoice.AddCopy);});
        }
        static string TempDir() { string d=Path.Combine(Path.GetTempPath(),"Netlane-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(d);return d; }
        static void StoreTests() {
            string notice;
            string badDir=TempDir();File.WriteAllBytes(Path.Combine(badDir,"state.bin"),new byte[]{9,9,9});
            ClientState fresh=StateStore.LoadOrMigrate(badDir,out notice);
            Check(notice!=null&&notice.Contains("state.bin.bad-")&&fresh.Servers.Count==0&&!File.Exists(Path.Combine(badDir,"state.bin"))&&Directory.GetFiles(badDir,"state.bin.bad-*").Length==1,"An unreadable state.bin is set aside, not overwritten later.");
            Server longHost=Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com",new string('a',60)+"."+new string('b',20)+".example.com")));
            Check(longHost.Name.Length<=64&&!longHost.HasOwnName,"A host longer than 64 characters still imports: "+longHost.Name);
            // An old profile with rules becomes one server and «Мои правила»; flags carried; profile.bin renamed.
            string dir=TempDir();
            Profile old=Profile.Import(Fixture);old.AntiDpi=true;old.KillSwitch=false;old.Routing=new Routing{Mode="selective",Destinations=new List<string>{"mos.ru"}};
            ProfileStore.Save(Path.Combine(dir,"profile.bin"),old);
            ClientState m=StateStore.LoadOrMigrate(dir,out notice);
            Check(notice==null&&m.Servers.Count==1&&m.SelectedServer!=null&&m.SelectedServer.Name==old.Hostname&&m.SelectedServer.Country==null&&m.SelectedRuleSet.Name=="Мои правила"&&m.SelectedRuleSet.Routing.Destinations.SequenceEqual(new[]{"mos.ru"})&&m.AntiDpi&&!m.KillSwitch,"Migration with rules.");
            Check(!File.Exists(Path.Combine(dir,"profile.bin"))&&File.Exists(Path.Combine(dir,"profile.bin.migrated"))&&File.Exists(Path.Combine(dir,"state.bin")),"Migration files.");
            ClientState again=StateStore.LoadOrMigrate(dir,out notice);
            Check(again.Servers[0].Id==m.Servers[0].Id&&again.SelectedRuleSet.Name=="Мои правила"&&again.RuleSets[0].Builtin,"state.bin is read on the next start.");
            // Without rules the built-in set is selected and no «Мои правила» appears.
            dir=TempDir();ProfileStore.Save(Path.Combine(dir,"profile.bin"),Profile.Import(Fixture));
            m=StateStore.LoadOrMigrate(dir,out notice);Check(m.SelectedRuleSet.Builtin&&m.RuleSets.Count==1&&m.Servers.Count==1,"Migration without rules.");
            // A profile saved under the former WinTrustTunnel entropy still migrates.
            dir=TempDir();byte[] plain=Encoding.UTF8.GetBytes(Profile.Import(Fixture).Serialize());
            File.WriteAllBytes(Path.Combine(dir,"profile.bin"),ProtectedData.Protect(plain,ProfileStore.LegacyEntropy,DataProtectionScope.CurrentUser));
            m=StateStore.LoadOrMigrate(dir,out notice);Check(notice==null&&m.Servers.Count==1,"Legacy entropy migrates.");
            // A corrupted profile is left untouched and the client starts empty with a notice.
            dir=TempDir();File.WriteAllBytes(Path.Combine(dir,"profile.bin"),new byte[]{1,2,3,4});
            m=StateStore.LoadOrMigrate(dir,out notice);
            Check(notice=="Не удалось прочитать прежний профиль. Добавьте сервер заново; старый файл не тронут."&&m.Servers.Count==0&&File.Exists(Path.Combine(dir,"profile.bin"))&&!File.Exists(Path.Combine(dir,"state.bin")),"Corrupted old profile.");
            // No files at all: empty state, no notice.
            dir=TempDir();m=StateStore.LoadOrMigrate(dir,out notice);Check(notice==null&&m.Servers.Count==0&&m.RuleSets.Count==1,"Fresh start.");
            // Save and reload keeps everything; removing the selected or last server moves the selection.
            Server x=Server.FromProfile(Profile.Import(Fixture)),y=Server.FromProfile(Profile.Import(Fixture.Replace("vpn.example.com","ams.example.com")));
            m.Servers.Add(x);m.Servers.Add(y);m.SelectServer(y.Id);m.StartWithWindows=true;StateStore.Save(dir,m);
            again=StateStore.LoadOrMigrate(dir,out notice);Check(again.SelectedServer.Hostname=="ams.example.com"&&again.StartWithWindows&&again.Servers.Count==2,"Save round trip.");
            again.RemoveServer(y.Id);Check(again.SelectedServerId==x.Id,"Removing the selected server selects the first remaining.");
            again.RemoveServer(x.Id);Check(again.SelectedServerId==null&&again.SelectedServer==null,"Removing the last server selects none.");
            Check(new FileInfo(Path.Combine(dir,"state.bin")).Length<1048576,"State file stays small.");
        }
        static void Reject(string value) { try { Profile.Import(value); } catch (FormatException) { return; } throw new Exception("Unsafe or malformed profile was accepted."); }
        static string Link(byte version, bool skip) { return Link(version, skip, null); }
        static string LinkWithName(string name) { return Link(1, false, name); }
        static string Link(byte version, bool skip, string name) {
            List<byte> bytes = new List<byte>();
            Action<byte, byte[]> field = delegate(byte tag, byte[] value) { bytes.Add(tag); if (value.Length < 64) bytes.Add((byte)value.Length); else { bytes.Add((byte)((value.Length >> 8) | 64)); bytes.Add((byte)value.Length); } bytes.AddRange(value); };
            field(0,new byte[]{version});field(1,Encoding.UTF8.GetBytes("vpn.example.com"));field(2,Encoding.UTF8.GetBytes("vpn.example.com:443"));field(4,new byte[]{(byte)(skip?1:0)});field(5,Encoding.UTF8.GetBytes("alice"));field(6,Encoding.UTF8.GetBytes(new string('x',128)));field(9,new byte[]{2});field(10,new byte[]{1});if(name!=null)field(12,Encoding.UTF8.GetBytes(name));
            return "tt://?"+Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+','-').Replace('/','_');
        }
        // Subscriptions (spec 2026-11-04 nodes and subscription): parse, merge, orphans.
        static string NodeLink(string host, string password, string name) {
            List<byte> bytes = new List<byte>();
            Action<byte, byte[]> field = delegate(byte tag, byte[] value) { bytes.Add(tag); if (value.Length < 64) bytes.Add((byte)value.Length); else { bytes.Add((byte)((value.Length >> 8) | 64)); bytes.Add((byte)value.Length); } bytes.AddRange(value); };
            field(0,new byte[]{1});field(1,Encoding.UTF8.GetBytes(host));field(2,Encoding.UTF8.GetBytes(host+":443"));field(4,new byte[]{0});field(5,Encoding.UTF8.GetBytes("alice"));field(6,Encoding.UTF8.GetBytes(password));field(9,new byte[]{1});if(name!=null)field(12,Encoding.UTF8.GetBytes(name));
            return "tt://?"+Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+','-').Replace('/','_');
        }
        static string Feed(int hours, params string[][] servers) {
            return "{\"version\":1,\"name\":\"\uD83C\uDDF3\uD83C\uDDF1 Амстердам\",\"update_hours\":"+hours+",\"servers\":["+String.Join(",",servers.Select(x=>"{\"id\":\""+x[0]+"\",\"name\":\""+x[1]+"\",\"country\":\""+x[2]+"\",\"link\":\""+x[3]+"\"}"))+"]}";
        }
        static void RejectFeed(string json, string why) { try { SubscriptionFeed.Parse(json); } catch (FormatException) { return; } throw new Exception("Bad subscription accepted: "+why); }
        static void SharedSubscriptionSettingsTests() {
            string plain = Feed(6,new[]{"main","Main","NL",NodeLink("ams.example.com","password-12345",null)});
            string extra = @"{""version"":1,""revision"":1,""dns_servers"":[""1.1.1.1""],""rule_sets"":[{""id"":""work"",""name"":""Рабочие"",""routing"":{""Mode"":""selective"",""Destinations"":[""example.com""],""Applications"":[{""Name"":""telegram.exe"",""Action"":""vpn""}]}}]}";
            string json = plain.Substring(0,plain.Length-1)+",\"client_settings\":"+extra+"}";
            SubscriptionFeed feed = SubscriptionFeed.Parse(json);
            ClientState state = ClientState.CreateEmpty();
            state.DnsServers = new List<string>{"8.8.8.8"};
            RuleSet local = state.AddRuleSet("Рабочие",new Routing());
            Server manual = Server.FromProfile(Profile.Import(Fixture));
            state.AddServer(manual,DuplicateChoice.AddCopy);
            Subscription sub = state.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/settings");
            FeedResult result = state.ApplyFeed(sub.Id,feed,null);
            Check(result.SettingsChanged && state.RuleSets.Count==3 && state.RuleSets.Contains(local),"Shared rules added beside local rules of the same name.");
            RuleSet managed = state.RuleSets.Single(r=>r.Managed);
            Server remote = state.Servers.Single(r=>r.SubscriptionId==sub.Id);
            Check(state.AvailableRuleSets.All(r=>!r.Managed),"Remote rules are scoped to their subscription.");
            state.SelectServer(remote.Id);state.SelectRuleSet(managed.Id);
            Profile session = state.SessionProfile();
            Check(session.DnsServers.SequenceEqual(new[]{"1.1.1.1"})&&session.Routing.Mode=="selective"&&session.Routing.Applications[0].Name=="telegram.exe","Shared routes and DNS reach the session profile.");
            RejectState(()=>state.UpdateRuleSet(managed.Id,new Routing()));
            RejectState(()=>state.RemoveRuleSet(managed.Id));
            RejectState(()=>state.RenameRuleSet(managed.Id,"different"));
            Check(!state.ApplyFeed(sub.Id,SubscriptionFeed.Parse(json),remote.Id).SettingsChanged,"Identical settings cause no extra change.");
            SubscriptionFeed changed = SubscriptionFeed.Parse(json.Replace("\"revision\":1","\"revision\":2").Replace("example.com\"]","other.example.com\"]"));
            state.ApplyFeed(sub.Id,changed,remote.Id);
            Check(state.SelectedRuleSet.Id==managed.Id&&managed.Routing.Destinations[0]=="other.example.com","Remote update keeps local selection and id.");
            state.Validate();
            string dir=TempDir();
            try {
                StateStore.Save(dir,state);
                string notice; ClientState back=StateStore.LoadOrMigrate(dir,out notice);
                Check(back.SelectedRuleSet.Managed&&back.SessionProfile().DnsServers[0]=="1.1.1.1","Managed rules and DNS survive restart.");
            } finally {Directory.Delete(dir,true);}
            state.SelectServer(manual.Id);
            Check(state.SessionProfile().DnsServers[0]=="8.8.8.8"&&!state.SelectedRuleSet.Managed,"Manual server keeps personal DNS and routing.");
            string before=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state);
            RejectFeed(json.Replace("1.1.1.1","192.168.1.1"),"private subscription DNS");
            Check(before==new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state),"Invalid feed leaves state intact.");
            state.RemoveSubscription(sub.Id,null);
            Check(state.RuleSets.Count==2&&state.RuleSets.Contains(local)&&state.DnsServers[0]=="8.8.8.8","Removing a subscription preserves local rules and DNS.");
        }
        static void SubscriptionTests() {
            SharedSubscriptionSettingsTests();
            string ams=NodeLink("ams.example.com","alice-password-123","\uD83C\uDDF3\uD83C\uDDF1 Амстердам"), fra=NodeLink("fra.example.com","alice-password-123",null);
            SubscriptionFeed feed=SubscriptionFeed.Parse(Feed(6,new[]{"main","\uD83C\uDDF3\uD83C\uDDF1 Амстердам","NL",ams},new[]{"n1","\uD83C\uDDE9\uD83C\uDDEA Франкфурт","DE",fra}));
            Check(feed.Name=="\uD83C\uDDF3\uD83C\uDDF1 Амстердам"&&feed.UpdateHours==6&&feed.Servers.Count==2,"Feed parsed: "+feed.Name+" "+feed.Servers.Count);
            Check(feed.Servers[0].RemoteId=="main"&&feed.Servers[0].Name=="Амстердам"&&feed.Servers[0].Country=="NL","Main server from the feed.");
            Check(feed.Servers[1].RemoteId=="n1"&&feed.Servers[1].Name=="Франкфурт"&&feed.Servers[1].Country=="DE"&&feed.Servers[1].Hostname=="fra.example.com","Node from the feed takes the feed's name and country.");
            Check(SubscriptionFeed.Parse(Feed(0,new[]{"main","A","",ams})).UpdateHours==12&&SubscriptionFeed.Parse(Feed(100,new[]{"main","A","",ams})).UpdateHours==12&&SubscriptionFeed.Parse(Feed(1,new[]{"main","A","",ams})).UpdateHours==1,"update_hours outside 1..24 falls back to 12.");
            string withClient=Feed(6,new[]{"main","A","",ams}).Replace("\"servers\"","\"client\":{\"version\":\"9.9.9\",\"url\":\"https://example.com/netlane.zip\",\"notes\":\"новая версия\"},\"servers\"");
            SubscriptionFeed clientFeed=SubscriptionFeed.Parse(withClient);
            Check(clientFeed.ClientVersion=="9.9.9"&&clientFeed.ClientUrl=="https://example.com/netlane.zip"&&clientFeed.ClientNotes=="новая версия","Client update offer parsed.");
            ClientState offered=ClientState.CreateEmpty();Subscription offeredSub=offered.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/offer");
            offered.ApplyFeed(offeredSub.Id,clientFeed,null);
            Check(offeredSub.LatestClientVersion=="9.9.9"&&offeredSub.ClientDownloadUrl=="https://example.com/netlane.zip","A newer client is kept on the subscription.");
            string manifest="{\"version\":\"0.4.15\",\"url\":\"https://github.com/divezh/netlane-updates/releases/download/v0.4.15/Netlane-windows-x64.zip\",\"sha256\":\"2679abc3d2b95759b4d7814ae71d559f79afdb02de994572428828bc742d89f4\",\"notes\":\"Что нового\"}";
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls;Tls.Enable();
            Check((ServicePointManager.SecurityProtocol&SecurityProtocolType.Tls12)!=0,"HTTPS requests offer TLS 1.2 whatever runs first: "+ServicePointManager.SecurityProtocol);
            UpdateManifest parsed=UpdateFeed.Parse(manifest);
            Check(parsed.Version=="0.4.15"&&parsed.Url.EndsWith("Netlane-windows-x64.zip")&&parsed.Sha256.Length==64&&parsed.Notes=="Что нового","Public update manifest parsed: version, archive, checksum and notes.");
            Check(UpdateFeed.ManifestUrl=="https://raw.githubusercontent.com/divezh/netlane-windows/updates/latest.json"&&UpdateFeed.ReleaseUrl.Contains("/repos/divezh/netlane-windows/releases/latest"),"Updates come from the public netlane-windows repository.");
            Check(parsed.Mirror==null,"No mirror unless the channel names one.");
            UpdateManifest mirrored=UpdateFeed.Parse(manifest.Replace("\"notes\"","\"mirror\":\"https://github.com/divezh/netlane-windows/releases/download/v0.4.15/Netlane-windows-x64.zip\",\"notes\""));
            Check(mirrored.Mirror!=null&&mirrored.Mirror.StartsWith("https://github.com/"),"The channel's mirror is kept for a failed download.");
            Check(UpdateFeed.Parse(manifest.Replace("\"notes\"","\"mirror\":\"http://example.com/x.zip\",\"notes\"")).Mirror==null,"A non-HTTPS mirror is dropped.");
            string release="{\"tag_name\":\"v0.7.1\",\"draft\":false,\"prerelease\":false,\"body\":\"## длинные\\n заметки\",\"assets\":[{\"name\":\"Netlane-windows-x64.zip.sha256\",\"browser_download_url\":\"https://github.com/divezh/netlane-windows/releases/download/v0.7.1/Netlane-windows-x64.zip.sha256\",\"digest\":\"sha256:"+new string('b',64)+"\"},{\"name\":\"Netlane-windows-x64.zip\",\"browser_download_url\":\"https://github.com/divezh/netlane-windows/releases/download/v0.7.1/Netlane-windows-x64.zip\",\"digest\":\"sha256:"+new string('A',64)+"\"}]}";
            UpdateManifest fromRelease=UpdateFeed.ParseRelease(release);
            Check(fromRelease.Version=="0.7.1"&&fromRelease.Url.EndsWith("/v0.7.1/Netlane-windows-x64.zip")&&fromRelease.Sha256==new string('a',64)&&fromRelease.Notes==null,"The latest GitHub release becomes an update: tag, archive asset and its digest.");
            bool refused=false;try{UpdateFeed.ParseRelease(release.Replace("\"prerelease\":false","\"prerelease\":true"));}catch(FormatException){refused=true;}
            Check(refused,"A pre-release is not offered.");
            refused=false;try{UpdateFeed.ParseRelease(release.Replace(",\"digest\":\"sha256:"+new string('A',64)+"\"",""));}catch(FormatException){refused=true;}
            Check(refused,"A release asset without a checksum is not offered.");
            RejectFeed(Feed(6,new[]{"main","A","",ams}).Replace("\"version\":1","\"version\":2"),"version 2");
            RejectFeed(Feed(6,new[]{"main","A","",ams},new[]{"n1","B","",fra.Substring(0,fra.Length-6)+"!!!!!!"}),"a broken link in one server");
            RejectFeed(Feed(6,new[]{"n1","A","",ams},new[]{"n1","B","",fra}),"duplicate ids");
            RejectFeed(Feed(6,new[]{"bad id","A","",ams}),"bad id");
            RejectFeed("not json","not json");RejectFeed("{\"version\":1}","no servers");
            Check(SubscriptionFeed.Parse(Feed(6)).Servers.Count==0,"An empty server list is valid.");
            Check(Subscription.LooksLikeUrl("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/tok")&&!Subscription.LooksLikeUrl("http://ams.example.com/p/sub/tok")&&!Subscription.LooksLikeUrl("https://ams.example.com/page")&&!Subscription.LooksLikeUrl("tt://?AAA"),"Subscription URL detection.");

            ClientState s=ClientState.CreateEmpty();
            Server manual=Server.FromProfile(Profile.Import(Fixture));s.AddServer(manual,DuplicateChoice.AddCopy);
            Subscription sub=s.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/tok");
            Check(s.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/tok")==sub&&s.Subscriptions.Count==1,"Same URL reuses the subscription.");
            FeedResult r=s.ApplyFeed(sub.Id,feed,null);
            Check(r.Added==2&&s.Servers.Count==3&&sub.Name==feed.Name&&sub.UpdateHours==6&&sub.UpdatedAt!=null&&sub.LastError==null,"Feed adds its servers: "+r.Added);
            Server fraServer=s.Servers.First(x=>x.RemoteId=="n1");Check(fraServer.SubscriptionId==sub.Id&&manual.SubscriptionId==null,"Servers carry their subscription.");
            r=s.ApplyFeed(sub.Id,feed,fraServer.Id);Check(r.Added==0&&r.Updated==0&&r.Removed==0&&!r.CurrentChanged,"Same feed changes nothing.");
            SubscriptionFeed changed=SubscriptionFeed.Parse(Feed(12,new[]{"main","\uD83C\uDDF3\uD83C\uDDF1 Амстердам","NL",ams},new[]{"n1","\uD83C\uDDE9\uD83C\uDDEA Франкфурт","DE",NodeLink("fra.example.com","new-password-12345",null)}));
            r=s.ApplyFeed(sub.Id,changed,fraServer.Id);
            Check(r.Updated==1&&r.CurrentChanged&&fraServer.Password=="new-password-12345"&&sub.UpdateHours==12,"Changed password updates the server and flags the connected one.");
            SubscriptionFeed less=SubscriptionFeed.Parse(Feed(12,new[]{"main","\uD83C\uDDF3\uD83C\uDDF1 Амстердам","NL",ams}));
            s.SelectServer(fraServer.Id);
            r=s.ApplyFeed(sub.Id,less,fraServer.Id);
            Check(r.Removed==1&&fraServer.Orphaned&&s.Servers.Contains(fraServer)&&s.SelectedServerId==fraServer.Id,"A connected server that left the feed stays until disconnect.");
            s.DropOrphans();
            Check(!s.Servers.Contains(fraServer)&&s.SelectedServer!=null&&s.SelectedServer.RemoteId=="main","After disconnect the orphan goes and the subscription's first server is selected.");
            r=s.ApplyFeed(sub.Id,SubscriptionFeed.Parse(Feed(6)),null);
            Check(r.Removed==1&&s.Servers.Count==1&&s.Servers[0]==manual&&s.SelectedServerId==manual.Id,"An empty feed removes the subscription's servers.");
            s.ApplyFeed(sub.Id,feed,null);Server connected=s.Servers.First(x=>x.RemoteId=="main");
            s.RemoveSubscription(sub.Id,connected.Id);
            Check(s.Subscriptions.Count==0&&s.Servers.Count==2&&connected.Orphaned&&connected.SubscriptionId==null,"Removing a subscription keeps the connected server until disconnect.");
            s.DropOrphans();Check(s.Servers.Count==1&&s.Servers[0]==manual,"Orphan dropped after removing the subscription.");

            string dir=Path.Combine(Path.GetTempPath(),"Netlane-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try {
                Subscription saved=s.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/tok");s.ApplyFeed(saved.Id,feed,null);
                StateStore.Save(dir,s);string notice;ClientState back=StateStore.LoadOrMigrate(dir,out notice);
                Check(notice==null&&back.Subscriptions.Count==1&&back.Subscriptions[0].Url==saved.Url&&back.Subscriptions[0].UpdateHours==6,"Subscription survives save.");
                Check(back.Servers.Count(x=>x.SubscriptionId==saved.Id&&x.RemoteId!=null)==2,"Subscription servers survive save.");
            } finally { Directory.Delete(dir,true); }
        }
        static T Wait<T>(Task<T> task) { Pump(task); return task.Result; }
        // Closing the window after the engine exited must not throw (the pipe is already broken).
        static void EngineAvailabilityTests() {
            string directory=TempDir();
            try {
                bool missingDirectory=false;
                try {EngineSpec.CheckAvailability(Path.Combine(directory,"engine"));} catch(DirectoryNotFoundException error){missingDirectory=error.Message.Contains("ZIP");}
                Check(missingDirectory,"A missing engine folder explains how to unpack the bundle.");
                bool missingExe=false;
                try {EngineSpec.CheckAvailability(directory);} catch(FileNotFoundException error){missingExe=error.Message.Contains("trusttunnel_client.exe");}
                Check(missingExe,"The missing executable is named in the error.");
                File.WriteAllText(Path.Combine(directory,"trusttunnel_client.exe"),"test");
                bool missingDll=false;
                try {EngineSpec.CheckAvailability(directory);} catch(FileNotFoundException error){missingDll=error.Message.Contains("wintun.dll");}
                Check(missingDll,"The missing driver is named in the error.");
                File.WriteAllText(Path.Combine(directory,"wintun.dll"),"test");
                EngineSpec.CheckAvailability(directory);
            } finally {Directory.Delete(directory,true);}
        }
        static void ConnectionDisposeTests() {
            Connection c = new Connection();
            StreamWriter broken = new StreamWriter(new BrokenPipeStream(), new UTF8Encoding(false), 4096, false);
            broken.Write("STOP");
            typeof(Connection).GetField("writer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(c, broken);
            bool threw = false;
            try { c.Dispose(); c.Dispose(); } catch (Exception) { threw = true; }
            Check(!threw, "Dispose after the engine exited does not throw.");
            // A probe that stopped waiting must still observe the late failure.
            bool unobserved = false;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = delegate { unobserved = true; };
            TaskScheduler.UnobservedTaskException += handler;
            try {
                Func<Task> lateFailure = delegate { TaskCompletionSource<int> t = new TaskCompletionSource<int>(); Latency.Observe(t.Task); t.SetException(new IOException("host unknown")); return t.Task; };
                lateFailure();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Check(!unobserved, "A late failure of an abandoned task is observed.");
            } finally { TaskScheduler.UnobservedTaskException -= handler; }
        }
        // A server added by tt:// and a subscription already present do not block one more subscription
        // from another VPS; both keep their «main» locations apart.
        static void SecondSubscriptionTests() {
            string first="https://first.example.com/abcdefghijklmnopqrstuvwxyz/sub/one", other="https://other-vps.example.net/zyxwvutsrqponmlkjihgfedcba/sub/two";
            using(MainForm form=new MainForm(true)) {
                form.Show();Application.DoEvents();
                int before=form.State.Servers.Count, manual=form.State.Servers.Count(s=>s.SubscriptionId==null);
                Check(form.PasteLink(NodeLink("manual.example.org","password-12345",null))==null&&form.State.Servers.Count==before+1,"A tt:// server is added first: "+before+" -> "+form.State.Servers.Count);
                form.FeedFetcher=new FakeFetcher{Json=Feed(12,new[]{"main","First","NL",NodeLink("ams.example.com","password-12345",null)})};
                Check(Wait(form.AddSubscription(first))==null,"A subscription is added next to a tt:// server.");
                form.FeedFetcher=new FakeFetcher{Json=Feed(12,new[]{"main","Other","DE",NodeLink("fra.example.net","password-67890",null)},new[]{"wars","Other Warsaw","PL",NodeLink("wars.example.net","password-67890",null)})};
                Check(Wait(form.AddSubscription(other))==null,"A second subscription from another VPS is added.");
                Check(form.State.Subscriptions.Count==2&&form.State.Servers.Count==before+4&&form.State.Servers.Count(s=>s.SubscriptionId==null)==manual+1,"Both subscriptions and the manual server are kept: "+form.State.Servers.Count);
                Check(form.State.Servers.Any(s=>s.Name=="First")&&form.State.Servers.Any(s=>s.Name=="Other"),"Each subscription keeps its own «main» location.");
            }
            using(ImportForm dialog=new ImportForm(link=>Subscription.LooksLikeUrl(link)?null:"no")) {
                Check(dialog.Text.Contains("подписку")&&AllControls(dialog).Any(c=>c.Text.Contains("https://…/sub/…")),"The add dialog names subscription links.");
            }
        }
        // The panel's account block (0.5.18+): parsed defensively, carried into the subscription and
        // shown as a short line, as details on «Серверы» and as the reason when no servers come.
        static void AccountTests() {
            string plain=Feed(12,new[]{"main","Main","NL",NodeLink("ams.example.com","password-12345",null)});
            Func<string,string> with=block=>plain.Substring(0,plain.Length-1)+",\"account\":"+block+"}";
            DateTime now=DateTime.UtcNow, end=now.Date.AddDays(12);
            SubscriptionFeed feed=SubscriptionFeed.Parse(with("{\"status\":\"active\",\"expires_at\":\""+end.ToString("yyyy-MM-ddTHH:mm:ssZ")+"\",\"quota_bytes\":53687091200,\"quota_used_bytes\":13314398617,\"quota_month\":\"2026-11\"}"));
            Check(feed.AccountStatus=="active"&&feed.ExpiresAt!=null&&feed.QuotaBytes==50L<<30&&feed.QuotaUsedBytes==13314398617&&feed.QuotaMonth=="2026-11","Account block parsed: status, end date, quota and use.");
            Check(SubscriptionFeed.Parse(plain).AccountStatus==null,"A panel without the account block leaves it empty.");
            SubscriptionFeed odd=SubscriptionFeed.Parse(with("{\"status\":\"vip\",\"quota_bytes\":5}"));
            Check(odd.AccountStatus==null&&odd.QuotaBytes==0,"An unknown status is ignored, not a broken feed.");
            SubscriptionFeed bad=SubscriptionFeed.Parse(with("{\"status\":\"active\",\"expires_at\":\"soon\",\"quota_bytes\":-5,\"quota_used_bytes\":\"x\",\"quota_month\":\"11/2026\"}"));
            Check(bad.AccountStatus=="active"&&bad.ExpiresAt==null&&bad.QuotaBytes==0&&bad.QuotaMonth==null,"Bad account fields are dropped one by one.");
            ClientState state=ClientState.CreateEmpty();
            Subscription sub=state.AddSubscription("https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/account");
            state.ApplyFeed(sub.Id,feed,null);
            Check(sub.AccountStatus=="active"&&sub.QuotaBytes==50L<<30&&sub.ExpiresAt==feed.ExpiresAt,"The account reaches the subscription.");
            Check(Account.Summary(sub,now)=="до "+end.ToLocalTime().ToString(end.Year==now.Year?"dd.MM":"dd.MM.yyyy")+" · 12,4/50 ГБ","Short line: "+Account.Summary(sub,now));
            Check(Account.LevelOf(sub,now)==Account.Level.Normal,"Twelve days and a quarter of the quota are not a warning.");
            Check(Account.Details(sub).Contains("Трафик за ноябрь: 12,4 из 50 ГБ (25%)")&&Account.Details(sub).StartsWith("Доступ до "),"Details: "+Account.Details(sub));
            sub.QuotaUsedBytes=300L<<20; Check(Account.Usage(sub,"/")=="300 МБ/50 ГБ","Different units stay on both sizes."); sub.QuotaUsedBytes=13314398617;
            Check(Account.LevelOf(sub,end.AddDays(-2))==Account.Level.Warning,"Two days left is a warning.");
            sub.QuotaUsedBytes=46L<<30; Check(Account.LevelOf(sub,now)==Account.Level.Warning,"90% of the quota is a warning.");
            Check(Account.Size(512L<<20)=="512 МБ"&&Account.Size(3L<<30)=="3 ГБ"&&Account.Size(25L<<30)=="25 ГБ","Sizes in МБ and ГБ.");
            // Access ended: no servers, the reason instead of «add a server».
            DateTime ended=now.AddDays(-2);
            state.ApplyFeed(sub.Id,SubscriptionFeed.Parse("{\"version\":1,\"servers\":[],\"account\":{\"status\":\"expired\",\"expires_at\":\""+ended.ToString("yyyy-MM-ddTHH:mm:ssZ")+"\"}}"),null);
            Check(sub.AccountStatus=="expired"&&sub.QuotaBytes==0&&state.Servers.All(x=>x.SubscriptionId!=sub.Id),"An expired account keeps its status with no servers.");
            Check(Account.Blocked(sub)&&Account.Reason(sub)=="доступ истёк "+ended.ToLocalTime().ToString("dd.MM.yyyy")&&Account.LevelOf(sub,now)==Account.Level.Danger,"Reason: "+Account.Reason(sub));
            foreach (string[] c in new[]{new[]{"disabled","доступ отключён администратором"},new[]{"exhausted","трафик на месяц исчерпан"},new[]{"pending","доступ ещё не включён"}}) {
                sub.AccountStatus=c[0]; Check(Account.Reason(sub)==c[1]&&Account.Summary(sub,now)==c[1],"Reason for "+c[0]);
            }
            sub.AccountStatus=null; Check(Account.Summary(sub,now)==null&&Account.Details(sub)==null,"No account block, no line.");
            using(MainForm form=new MainForm(true,"expired",null)) {
                form.Show();Application.DoEvents();
                List<Control> all=AllControls(form);
                Check(all.Any(c=>c.Visible&&c.Text=="Подписка не даёт серверов")&&all.Any(c=>c.Visible&&c.Text.StartsWith("Доступ истёк ")&&c.Text.Contains("администратор")),"The empty window says why the subscription gives no servers.");
            }
            using(MainForm form=new MainForm(true,"account",null)) {
                form.Show();Application.DoEvents();
                SubscriptionRow row=AllControls(form).OfType<SubscriptionRow>().First();
                Check(row.Visible&&row.Height==70&&row.AccountText!=null&&row.AccountText.EndsWith("12,4/50 ГБ"),"The login row grows a line for the access: "+row.AccountText);
                Check(!AllControls(form).Any(c=>c.Visible&&c.Text=="Подписка не даёт серверов"),"A working subscription keeps the usual first-run text away.");
            }
        }
        static void EntrySubscriptionRefreshTests() {
            string url="https://first.example.com/abcdefghijklmnopqrstuvwxyz/sub/one";
            string json=Feed(12,new[]{"main","Main","NL",NodeLink("ams.example.com","password-12345",null)},new[]{"wars","Warsaw","PL",NodeLink("wars.example.com","password-12345",null)});
            using(MainForm form=new MainForm(true)) {
                form.Show();Application.DoEvents();form.FeedFetcher=new FakeFetcher{Json=json};
                Check(Wait(form.AddSubscription(url))==null,"Entry test subscription loads.");
                GatedFetcher gate=new GatedFetcher{Json=json};form.FeedFetcher=gate;
                Task first=form.RefreshOnEntry(),second=form.RefreshOnEntry();
                Check(Object.ReferenceEquals(first,second)&&gate.Calls==1,"Overlapping entries share one refresh even for a fresh subscription.");
                gate.Gate.SetResult(json);Pump(first);Pump(form.RefreshOnEntry());
                Check(gate.Calls==2&&form.State.Servers.Any(s=>s.Name=="Warsaw"),"A later entry fetches again without waiting for the automatic interval.");
                int count=form.State.Servers.Count;string updated=form.State.Subscriptions[0].UpdatedAt;
                form.FeedFetcher=new FakeFetcher{Fail=new FormatException("Offline")};Pump(form.RefreshOnEntry());
                Check(form.State.Servers.Count==count&&form.State.Subscriptions[0].UpdatedAt==updated&&form.State.Subscriptions[0].LastError!=null,"Offline entry preserves servers and records the failure.");
                gate=new GatedFetcher{Json=json};form.FeedFetcher=gate;
                Task manual=form.RefreshSubscriptions(true),entry=form.RefreshOnEntry();
                gate.Gate.SetResult(json);Pump(manual);Pump(entry);
                Check(gate.Calls==1&&form.State.Subscriptions[0].LastError==null,"Entry waits for an existing manual refresh without duplicating it; successful retry clears the error.");
            }
            FakeConnection connection=new FakeConnection();
            using(MainForm form=new MainForm(true,null,null,null,connection)) {
                form.Show();Application.DoEvents();form.FeedFetcher=new FakeFetcher{Json=json};Wait(form.AddSubscription(url));
                form.State.SelectServer(form.State.Servers.First(s=>s.RemoteId=="main").Id);form.State.AutoConnect=true;
                string changed=Feed(12,new[]{"main","Updated","NL",NodeLink("updated.example.com","password-12345",null)});
                GatedFetcher gate=new GatedFetcher{Json=changed};form.FeedFetcher=gate;
                Task startup=form.RefreshBeforeAutoConnect();
                Check(connection.Starts==0&&!startup.IsCompleted,"Autoconnect waits for the startup feed.");
                gate.Gate.SetResult(changed);Pump(startup);
                Check(connection.Starts==1&&connection.Hosts[0]=="updated.example.com","Autoconnect uses the updated server profile.");
            }
        }
        static void HomeSubscriptionRefreshTests() {
            string firstUrl = "https://first.example.com/abcdefghijklmnopqrstuvwxyz/sub/one";
            string secondUrl = "https://second.example.com/abcdefghijklmnopqrstuvwxyz/sub/two";
            string json = Feed(12,new[]{"main","Main","NL",NodeLink("ams.example.com","password-12345",null)});
            using (MainForm form = new MainForm(true)) {
                form.Show();Application.DoEvents();
                Server manual = form.State.SelectedServer;
                FakeFetcher instant = new FakeFetcher {Json=json};
                form.FeedFetcher=instant;
                Check(Wait(form.AddSubscription(firstUrl))==null&&Wait(form.AddSubscription(secondUrl))==null,"Two feeds added for the home refresh test.");
                SubscriptionRow row=AllControls(form).OfType<SubscriptionRow>().Single();
                Check(row.Visible&&row.Subscription==null&&row.AccountName==manual.Username&&!row.Enabled,"Manual server: account card without a feed.");
                Subscription second=form.State.Subscriptions.Single(sub=>sub.Url==secondUrl);
                form.State.SelectServer(form.State.Servers.First(server=>server.SubscriptionId==second.Id).Id);
                Application.DoEvents();
                Check(row.Visible&&row.Enabled&&row.AccessibleRole==AccessibleRole.PushButton,"A feed server exposes the keyboard accessible action.");
                Check(row.Parent!=null&&row.Parent.Controls.OfType<NavItem>().Any()&&row.StatusText.Contains("локац"),"The account line sits in the navigation and counts the locations: "+row.StatusText);
                GatedFetcher pending=new GatedFetcher {Json=json};
                form.FeedFetcher=pending;
                Task request=form.RefreshSelectedSubscription();
                Check(!row.Enabled&&row.ActionText=="Обновляем подписку…"&&second.PendingRefreshes==1,"Home action is disabled while the request runs.");
                Pump(form.RefreshSelectedSubscription());
                Check(pending.Calls==1&&pending.LastUrl==secondUrl,"Repeated presses do not duplicate requests and only the selected feed refreshes.");
                pending.Gate.SetResult(json);Pump(request);
                Check(row.Enabled&&second.PendingRefreshes==0&&second.LastError==null,"Successful refresh re-enables the line.");
                int events=0;
                row.SubscriptionRefreshRequested+=delegate {events++;};
                typeof(ThemedControl).GetMethod("OnKeyUp",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
                    .Invoke(row,new object[]{new KeyEventArgs(Keys.Space)});
                Check(events==1,"Space activates the subscription action.");
                int count=form.State.Servers.Count;
                form.FeedFetcher=new FakeFetcher {Fail=new FormatException("Нет связи с сервером подписки.")};
                Pump(form.RefreshSelectedSubscription());
                Check(row.Enabled&&row.ActionText.Contains("Нет связи")&&form.State.Servers.Count==count,"Failed refresh explains itself and keeps the existing profiles.");
                form.State.SelectServer(manual.Id);Application.DoEvents();
                Check(row.Visible&&row.Subscription==null&&row.AccountName==manual.Username,"Switching back to a manual server keeps the account card without a feed.");
            }
        }
        static void SubscriptionUiTests() {
            const string url = "https://ams.example.com/abcdefghijklmnopqrstuvwxyz/sub/tok";
            string ams=NodeLink("ams.example.com","alice-password-123",null), fra=NodeLink("fra.example.com","alice-password-123",null);
            FakeConnection fake = new FakeConnection { Active = true };
            FakeFetcher fetch = new FakeFetcher { Json = Feed(6,new[]{"main","Амстердам","NL",ams},new[]{"n1","Франкфурт","DE",fra}) };
            using (MainForm f = new MainForm(true, null, ConnectionView.Connected, null, fake)) {
                f.Show(); Application.DoEvents(); f.FeedFetcher = fetch;
                int before = f.State.Servers.Count;
                Check(Wait(f.AddSubscription(url)) == null && f.State.Subscriptions.Count == 1 && f.State.Servers.Count == before + 2, "Adding a subscription adds its servers.");
                Check(Wait(f.AddSubscription(url)) == null && f.State.Subscriptions.Count == 1 && f.State.Servers.Count == before + 2, "Adding the same link again only refreshes it.");
                Check(f.PasteLink("просто текст").Contains("ссылки подписки"), "Paste explains both link kinds.");
                Check(Wait(f.AddSubscription("https://ams.example.com/page")) != null, "A non-subscription URL is refused.");
                fetch.Fail = new FormatException("Сервер подписки недоступен."); int servers = f.State.Servers.Count;
                Check(Wait(f.AddSubscription("https://other.example.com/abcdefghijklmnopqrstuvwxyz/sub/x")) != null && f.State.Subscriptions.Count == 1, "A failed new subscription is not kept.");
                Pump(f.RefreshSubscriptions(true));
                Check(f.State.Subscriptions[0].LastError != null && f.State.Servers.Count == servers, "A failed refresh keeps the list and records the error.");
                fetch.Fail = null;
                Server node = f.State.Servers.First(x => x.RemoteId == "n1");
                Pump(f.SwitchTo(node.Id, null)); int starts = fake.Starts;
                fetch.Json = Feed(6,new[]{"main","Амстердам","NL",ams},new[]{"n1","Франкфурт","DE",NodeLink("fra.example.com","rotated-password-99",null)});
                Pump(f.RefreshSubscriptions(false)); Pump(f.SwitchTo(null, null));
                Check(fake.Starts == starts + 1 && fake.Hosts.Last() == "fra.example.com" && f.State.Subscriptions[0].LastError == null, "A changed password of the connected server reconnects once: " + (fake.Starts - starts));
                fetch.Json = Feed(6,new[]{"main","Амстердам","NL",ams});
                Pump(f.RefreshSubscriptions(false));
                Check(node.Orphaned && f.State.Servers.Contains(node), "The connected server that left the subscription stays while connected.");
                Pump(f.Disconnect());
                Check(!f.State.Servers.Contains(node) && f.State.SelectedServer != null, "After «Отключить» it is removed and another server is selected.");
                // «Обновить» while a background refresh runs: it waits for it, then fetches again.
                GatedFetcher gated = new GatedFetcher { Json = Feed(6,new[]{"main","Амстердам","NL",ams}) };
                f.FeedFetcher = gated;
                Task first = f.RefreshSubscriptions(false), second = f.RefreshSubscriptions(true);
                gated.Gate.SetResult(gated.Json); Pump(first); Pump(second);
                Check(gated.Calls == 2, "A manual refresh during a running one is not lost: " + gated.Calls);
                // A fetch that never answers ends with an error instead of blocking later refreshes.
                TimeSpan oldTimeout = MainForm.FeedTimeout; MainForm.FeedTimeout = TimeSpan.FromMilliseconds(200);
                try {
                    gated.Hang = true; Pump(f.RefreshSubscriptions(true));
                    Check(f.State.Subscriptions[0].LastError != null && f.State.Subscriptions[0].LastError.Contains("не ответил"), "A hung fetch times out: " + f.State.Subscriptions[0].LastError);
                    gated.Hang = false; Pump(f.RefreshSubscriptions(true));
                    Check(f.State.Subscriptions[0].LastError == null, "The next refresh works after a timeout.");
                } finally { MainForm.FeedTimeout = oldTimeout; }
                using (SettingsForm settings = new SettingsForm(f)) {
                    settings.Show(); Application.DoEvents();
                    List<Control> all = AllControls(settings);
                    Check(all.Where(c => c is DarkButton && (c.Text == "Обновить" || c.Text == "Удалить подписку…")).All(c => c.Height <= 34 && c.Width <= 170), "Subscription buttons are compact.");
                    Check(all.Any(c => c is Label && c.Text.StartsWith("Подписка «")) && all.Any(c => c is DarkButton && c.Text == "Обновить") && all.Any(c => c is DarkButton && c.Text == "Удалить подписку…"), "Settings show the subscription with its buttons.");
                    Server fromSub = f.State.Servers.First(x => x.SubscriptionId != null);
                    using (ContextMenuStrip menu = settings.ServerMenuForTests(fromSub)) Check(menu.Items.Cast<ToolStripItem>().All(i => i.Text != "Переименовать…" && i.Text != "Сменить флаг…"), "Subscription servers cannot be renamed or reflagged.");
                    using (ContextMenuStrip menu = settings.ServerMenuForTests(f.State.Servers.First(x => x.SubscriptionId == null))) Check(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "Переименовать…"), "Manual servers keep rename.");
                }
            }
            // The real fetcher: a response over 256 KB is refused.
            System.Net.HttpListener listener;
            try { listener = new System.Net.HttpListener(); } catch (PlatformNotSupportedException) { return; }
            int port = 18000 + new Random().Next(1000);
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            try {
                Task serve = Task.Run(delegate { System.Net.HttpListenerContext c = listener.GetContext(); byte[] big = new byte[300 * 1024]; c.Response.ContentLength64 = big.Length; try { c.Response.OutputStream.Write(big, 0, big.Length); c.Response.Close(); } catch (Exception) { } });
                bool refused = false;
                try { Wait(new HttpFeedFetcher().Fetch("http://127.0.0.1:" + port + "/sub/x", CancellationToken.None)); } catch (AggregateException error) { refused = error.InnerException is FormatException && error.InnerException.Message.Contains("слишком большой"); }
                Check(refused, "Oversized subscription response refused.");
            } finally { listener.Stop(); }
        }
        // WinForms drag and drop (AllowDrop) needs a single-threaded apartment, as in the app.
        [STAThread]
        static int Main(string[] args) {
            if (args.Length==3 && args[0]=="--worker") return Worker.Run(args[1],Int32.Parse(args[2]));
            // As under Application.Run: awaits resume on this UI thread. Without this, .NET drops the WinForms
            // context when a test form is disposed and continuations run on the thread pool.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            // An exception inside a WinForms event must fail the run, not open a modal error dialog that
            // nobody closes during a build.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            // A run that stops moving (a dialog, a deadlock) ends with the last passed check instead of hanging the build.
            new Thread(delegate() { Thread.Sleep(TimeSpan.FromMinutes(3)); Console.Error.WriteLine("Windows client test failed: timeout after 3 min; last passed check: " + lastCheck); Environment.Exit(3); }) { IsBackground = true }.Start();
            try {
                if(args.Length==2&&args[0]=="--latency") {Profile pingProfile=Profile.Import(Fixture.Replace("vpn.example.com",args[1]));LatencyResult result=Latency.Measure(pingProfile).GetAwaiter().GetResult();Console.WriteLine(result.Display+" · "+result.Detail);return result.Success?0:1;}
                if (args.Length==2 && args[0]=="--network") { Network(args[1]).GetAwaiter().GetResult();return 0; }
                Profile profile=Profile.Import(Fixture);
                Check(profile.Protocol=="http3"&&profile.AntiDpi&&profile.KillSwitch,"TOML import changed options.");
                Profile linked=Profile.Import(Link(1,false));Check(linked.Password.Length==128&&linked.Protocol=="http3"&&linked.AntiDpi,"Variable-length tt:// fields failed.");
                Reject(Link(2,false));Reject(Link(1,true));Reject("tt://?AAAA_");Reject(Fixture.Replace("skip_verification = false","skip_verification = true"));
                Reject(Fixture+"hostname = \"other.example.com\"\n");Reject(Fixture+"[listener.tun]\n");Reject(Fixture.Replace("http3","invalid"));Reject(Fixture.Replace("alice","bad name"));
                Reject(Fixture.Replace("[\"vpn.example.com:443\"]","[\"vpn.example.com:443\", \"other.example.com:443\"]"));
                Reject(Fixture.Replace("vpn.example.com:443","user@vpn.example.com:443"));Reject(new string('x',32769));
                string config=profile.EngineConfig(0);Check(config.Contains("[listener.tun]")&&config.Contains("skip_verification = false")&&config.Contains("killswitch_enabled = true"),"Unsafe TUN configuration.");
                Check(config.Contains("included_routes = [\"0.0.0.0/0\"]")&&config.Contains("192.168.0.0/16")&&config.Contains("224.0.0.0/3"),"TUN routes missing: the official CLI installs no routes when included_routes is absent.");
                config=profile.EngineConfig(18090);Check(config.Contains("127.0.0.1:18090")&&!config.Contains("[listener.tun]")&&config.Contains("killswitch_enabled = false"),"SOCKS test modifies system routing.");
                string dir=Path.Combine(Path.GetTempPath(),"Netlane-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
                try {
                    string path=Path.Combine(dir,"profile.bin");ProfileStore.Save(path,profile);
                    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(profile.Password),"DPAPI storage leaked password.");
                    Check(ProfileStore.Load(path).Password==profile.Password,"DPAPI round trip failed.");
                    string legacy=Path.Combine(dir,ProfileStore.LegacyDataFolder);Directory.CreateDirectory(legacy);File.WriteAllBytes(Path.Combine(legacy,"profile.bin"),ProtectedData.Protect(Encoding.UTF8.GetBytes(profile.Serialize()),ProfileStore.LegacyEntropy,DataProtectionScope.CurrentUser));
                    string migrated=ProfileStore.MigrateDataDirectory(dir);
                    Check(migrated==Path.Combine(dir,ProfileStore.DataFolder)&&!Directory.Exists(legacy)&&ProfileStore.Load(Path.Combine(migrated,"profile.bin")).Password==profile.Password,"Legacy profile folder was not migrated.");
                    ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(migrated,"profile.bin")),ProfileStore.Entropy,DataProtectionScope.CurrentUser);
                    Check(ProfileStore.MigrateDataDirectory(dir)==migrated,"Data folder migration is not idempotent.");
                    profile.Protocol="http2";ProfileStore.Save(path,profile);Check(ProfileStore.Load(path).Protocol=="http2","Atomic profile update failed.");
                    byte[] corrupted=File.ReadAllBytes(path);corrupted[corrupted.Length/2]^=1;File.WriteAllBytes(path,corrupted);
                    bool refused=false;try{ProfileStore.Load(path);}catch(CryptographicException){refused=true;}Check(refused,"Corrupt encrypted profile accepted.");
                    Check(Directory.GetFiles(dir).Length==1,"Temporary encrypted files left behind.");
                } finally { Directory.Delete(dir,true); }
                string logDump=EngineLog.Mask("loglevel = \"info\"\npassword = \"super-secret-password\"\nusername = \"alice\"\n");
                Check(!logDump.Contains("super-secret-password")&&logDump.Contains("username = \"alice\"")&&logDump.Contains("password = \"***\""),"Engine log would leak the VPN password.");
                // Working beside another VPN: the engine needs a /32 route to its endpoint through the physical gateway.
                Check(RouteException.AddArguments(IPAddress.Parse("203.0.113.10"),IPAddress.Parse("192.0.2.1"),7)=="add 203.0.113.10 mask 255.255.255.255 192.0.2.1 metric 1 if 7","Endpoint route must be a host route through the physical gateway with metric 1.");
                Check(RouteException.DeleteArguments(IPAddress.Parse("203.0.113.10"))=="delete 203.0.113.10","Removing the endpoint route names it without a mask.");
                Check(RouteException.ToUInt(IPAddress.Parse("1.2.3.4"))==BitConverter.ToUInt32(new byte[]{1,2,3,4},0),"Endpoint address must use the IP helper byte order.");
                Check(RouteException.Resolve(null).Count==0&&RouteException.Resolve("").Count==0&&RouteException.Resolve("localhost").Count==0,"Loopback and empty host names never get a route.");
                // Redesign: palette and state styles (docs/superpowers/specs/2026-11-03-windows-client-redesign-design.md).
                Check(Theme.Accent.ToArgb()==Color.FromArgb(0x22,0xD3,0xE6).ToArgb()&&Theme.Ground.ToArgb()==Color.FromArgb(0x10,0x16,0x24).ToArgb()&&Theme.Danger.ToArgb()==Color.FromArgb(0xF2,0x83,0x7A).ToArgb(),"Theme palette differs from the spec.");
                Check(Theme.AccentSoft.A==31&&Theme.AccentSoft.R==Theme.Accent.R&&Theme.Soft(Theme.Protected).A==31,"Soft colors must be 12% of the base color.");
                StateStyle connected=Theme.ForState(ConnectionView.Connected),idle=Theme.ForState(ConnectionView.Idle),none=Theme.ForState(ConnectionView.NoProfile);
                Check(connected.Dot==Theme.Protected&&connected.DotHalo&&connected.Ring==Theme.Accent&&connected.RingHalo,"Connected style.");
                Check(idle.Dot==Theme.Faint&&idle.Ring==Theme.LineStrong&&!idle.RingHalo&&idle.RingEnabled,"Idle style.");
                Check(none.Ring==Theme.Faint&&!none.RingEnabled,"No-profile ring must be inactive.");
                Check(!Theme.ForState(ConnectionView.Connecting).RingEnabled&&!Theme.ForState(ConnectionView.Connecting).RingHalo,"Connecting style.");
                Check(Theme.ForState(ConnectionView.Reconnecting).Ring==Theme.Warning&&Theme.ForState(ConnectionView.Reconnecting).Dot==Theme.Warning,"Reconnecting style.");
                Check(Theme.ForState(ConnectionView.KillSwitch).Ring==Theme.Danger&&Theme.ForState(ConnectionView.Error).Dot==Theme.Danger&&Theme.ForState(ConnectionView.Error).Ring==Theme.Danger,"Kill switch and error styles.");
                Check(SessionStats.FormatDuration(new TimeSpan(0,42,17))=="00:42:17"&&SessionStats.FormatDuration(new TimeSpan(1,1,3,0))=="25:03:00"&&SessionStats.FormatDuration(TimeSpan.FromSeconds(-5))=="00:00:00","Duration format.");
                Check(SessionStats.FormatBytes(1023)=="1023 Б"&&SessionStats.FormatBytes(1024)=="1,0 КБ"&&SessionStats.FormatBytes(220200960)=="210,0 МБ"&&SessionStats.FormatBytes(1932735283)=="1,8 ГБ","Byte format.");
                FakeTraffic fake=new FakeTraffic{Rx=1000,Tx=500};TrafficMeter meter=new TrafficMeter(fake);meter.Start();long down,up;
                fake.Rx=3000;fake.Tx=900;Check(meter.TryGet(out down,out up)&&down==2000&&up==400,"Traffic delta.");
                fake.Rx=100;fake.Tx=50;Check(meter.TryGet(out down,out up)&&down==2100&&up==450,"Adapter counter reset: keep the total and count the new adapter from zero.");
                fake.Rx=600;Check(meter.TryGet(out down,out up)&&down==2600,"Traffic continues after a counter reset.");
                fake.Ok=false;Check(!meter.TryGet(out down,out up),"Unavailable counters hide the traffic line.");
                SegmentedControl seg=new SegmentedControl{Items=new[]{"HTTP/2 · TCP","HTTP/3 · QUIC"}};int changes=0;seg.SelectedIndexChanged+=delegate{changes++;};
                seg.SelectedIndex=1;seg.SelectedIndex=1;Check(seg.SelectedIndex==1&&changes==1,"Segmented control raises one change per new value.");
                bool segRejected=false;try{seg.SelectedIndex=2;}catch(ArgumentOutOfRangeException){segRejected=true;}Check(segRejected,"Out-of-range segment accepted.");
                PowerRing ring=new PowerRing{Enabled=false};ring.View=ConnectionView.Idle;Check(!ring.Enabled,"Ring.View must not override Enabled; MainForm.ControlsEnabled owns it.");
                TabStrip tabs=new TabStrip{Tabs=new[]{"Домены и IP · 31","Приложения · 5"}};tabs.SelectedIndex=1;Check(tabs.SelectedIndex==1,"Tab selection.");
                DarkButton danger=new DarkButton{Kind=ButtonKind.Danger};Check(danger.Kind==ButtonKind.Danger,"Button kind.");
                PreviewRequest pr=PreviewRequest.Parse(new[]{"--render","out.png","--state=Connected","--page=settings"});Check(pr!=null&&pr.Render&&pr.Output=="out.png"&&pr.State==ConnectionView.Connected&&pr.Page=="settings","Render arguments.");
                pr=PreviewRequest.Parse(new[]{"--render-settings","o.png"});Check(pr!=null&&pr.Render&&pr.Settings&&pr.Page=="servers"&&pr.State==null,"--render-settings opens the settings window.");
                                pr=PreviewRequest.Parse(new[]{"--preview","--state=killswitch"});Check(pr!=null&&!pr.Render&&pr.Page=="home"&&pr.State==ConnectionView.KillSwitch,"Preview with state.");
                Check(PreviewRequest.Parse(new string[0])==null&&PreviewRequest.Parse(new[]{"--worker","x","1"})==null,"Normal start is not a preview.");
                bool badPreview=false;try{PreviewRequest.Parse(new[]{"--preview","--state=bogus"});}catch(ArgumentException){badPreview=true;}Check(badPreview,"Unknown state accepted.");
                badPreview=false;try{PreviewRequest.Parse(new[]{"--render"});}catch(ArgumentException){badPreview=true;}Check(badPreview,"Render without output accepted.");
                Check(MainForm.StatusLineFor(ConnectionView.Connected,false)=="Защищено · HTTP/2"&&MainForm.StatusLineFor(ConnectionView.Connected,true)=="Защищено · HTTP/3"&&MainForm.StatusLineFor(ConnectionView.NoProfile,false)=="Профиль не добавлен"&&MainForm.StatusLineFor(ConnectionView.Idle,false)=="Не подключено"&&MainForm.StatusLineFor(ConnectionView.Reconnecting,false)=="Переподключение"&&MainForm.StatusLineFor(ConnectionView.KillSwitch,false)=="Интернет заблокирован"&&MainForm.StatusLineFor(ConnectionView.Error,false)=="Требуется внимание"&&MainForm.StatusLineFor(ConnectionView.Connecting,false)=="Подключение…","Status line texts.");
                pr=PreviewRequest.Parse(new[]{"--preview","--host=very-long-hostname.example.com"});Check(pr!=null&&pr.Host=="very-long-hostname.example.com","Preview host override.");
                Check(SessionStats.SessionLine(new TimeSpan(0,42,17),true,1932735283,220200960)=="00:42:17 · ↓ 1,8 ГБ · ↑ 210,0 МБ"&&SessionStats.SessionLine(new TimeSpan(0,0,5),false,0,0)=="00:00:05","Session line.");
                Check(SessionStats.FormatBytes(1048575)=="1,0 МБ"&&SessionStats.FormatBytes(1048575L*1024)=="1,0 ГБ","Rounding must carry into the next unit.");
                using (RuleSetEditor routingForm = new RuleSetEditor(new RuleSet { Name = "Тест", Routing = new Routing { Destinations = new List<string> { "*.example.com", "10.0.0.0/8" } } }, false)) {
                    List<Control> all = AllControls(routingForm);
                    TabStrip routingTabs = (TabStrip)all.FirstOrDefault(c => c is TabStrip);
                    Check(routingTabs != null && routingTabs.Tabs[0] == "Домены и IP · 2" && routingTabs.Tabs[1] == "Приложения · 0", "Routing: tabs with counts.");
                    Check(all.Any(c => c is DarkButton && c.Text == "Сохранить" && ((DarkButton)c).Kind == ButtonKind.Primary) && all.Any(c => c is DarkButton && c.Text == "Добавить"), "Routing: Add and primary Save.");
                    Check(all.Any(c => c is ListBox && c.Font.Name == Theme.Mono(10).Name), "Routing: rules in a monospace font.");
                }
                string rejectedWith = null;
                using (ImportForm importForm = new ImportForm(delegate(string link) { return link.StartsWith("tt://") ? null : "Нужна ссылка tt://."; })) {
                    List<Control> all = new List<Control>(); Stack<Control> pending = new Stack<Control>(); pending.Push(importForm);
                    while (pending.Count > 0) { Control c = pending.Pop(); all.Add(c); foreach (Control child in c.Controls) pending.Push(child); }
                    Check(all.Any(c => c is DarkButton && c.Text == "Импортировать" && ((DarkButton)c).Kind == ButtonKind.Primary) && all.Any(c => c is DarkButton && c.Text == "Отмена") && all.Any(c => c is DarkButton && c.Text == "Открыть TOML…"), "Import: three buttons, Import is primary.");
                    TextBox linkBox = (TextBox)all.First(c => c is TextBox);
                    Check(linkBox.Multiline && linkBox.Font.Name == Theme.Mono(10).Name, "Import: multiline monospace field.");
                    linkBox.Text = "not a link"; Check(!importForm.TryAccept(out rejectedWith) && rejectedWith == "Нужна ссылка tt://.", "Import: invalid link is rejected with the validator message.");
                    linkBox.Text = "  tt://?AQEB  "; Check(importForm.TryAccept(out rejectedWith) && importForm.Link == "tt://?AQEB" && importForm.DialogResult == DialogResult.OK, "Import: valid link accepted and trimmed.");
                }
                // Final review fixes.
                Check(MainForm.StatusLineFor(ConnectionView.Disconnecting,false)=="Отключение…"&&!Theme.ForState(ConnectionView.Disconnecting).RingEnabled&&!Theme.ForState(ConnectionView.Disconnecting).RingHalo,"Disconnecting has its own status line.");
                Check(Theme.Selection.A==255&&Theme.Selection.ToArgb()==Theme.Blend(Theme.Accent,Theme.Card,31).ToArgb()&&Theme.Blend(Color.FromArgb(255,0,0),Color.FromArgb(0,0,255),128).R==128,"Opaque selection colour.");
                using (MainForm reviewForm = new MainForm(true)) {
                    List<Control> all = new List<Control>(); Stack<Control> pending = new Stack<Control>(); pending.Push(reviewForm);
                    while (pending.Count > 0) { Control c = pending.Pop(); all.Add(c); foreach (Control child in c.Controls) pending.Push(child); }
                    reviewForm.ShowPreviewState(ConnectionView.Reconnecting); reviewForm.OnTimerTick();
                    Check(all.OfType<StatusBlock>().Any(c => c.Subtitle.Contains("попытка 2")) && !all.Any(c => c is Label && c.Text.StartsWith("00:")), "Timer must not fill the session line or wipe the reconnect explanation outside Connected.");
                    reviewForm.ShowPreviewState(ConnectionView.NoProfile); Check(!all.OfType<StatusBlock>().Any(c => c.Visible), "No profile: no state header is shown.");
                }
                Check(MainForm.StatusLineFor(ConnectionView.Reconnecting,false,3)=="Переподключение: попытка 3"&&MainForm.StatusLineFor(ConnectionView.Reconnecting,false,0)=="Переподключение","Reconnect status line carries the attempt number.");
                Check(AdapterTraffic.Matches("Netlane","Wintun Userspace Tunnel",System.Net.NetworkInformation.OperationalStatus.Up)&&AdapterTraffic.Matches("Ethernet 3","Netlane",System.Net.NetworkInformation.OperationalStatus.Up)&&AdapterTraffic.Matches("WinTrustTunnel","Wintun Userspace Tunnel",System.Net.NetworkInformation.OperationalStatus.Up)&&!AdapterTraffic.Matches("Netlane","Wintun",System.Net.NetworkInformation.OperationalStatus.Down)&&!AdapterTraffic.Matches("Ethernet","Realtek",System.Net.NetworkInformation.OperationalStatus.Up),"Only the running VPN adapter is counted.");
                MainWindowTests();
                SwitchTests();
                SettingsServerTests();
                RulesPageTests();
                ConnectionAboutTests();
                TrayTests();
                AutostartTests();
                FlagTests();
                Check(!Worker.IsEngineFailure("03.11.2026 15:35:28.700781 INFO  [4432] DNS_HANDLER send_as_listener: [upstream] Failed to send UDP 127.0.0.11:1 (__vpn_dns_resolver__) -> 192.168.10.1:53: read disabled"), "Informational DNS line is not a connection failure.");
                Check(!Worker.IsEngineFailure("03.11.2026 15:35:28.561360 WARN  [4432] OS_TUNNEL_WIN vpn_win_socket_protect: Failed to protect socket"), "Warning is not a connection failure.");
                Check(Worker.IsEngineFailure("03.11.2026 15:35:28.561360 ERROR [4432] VPNCORE connect: Failed to connect to endpoint"), "Engine ERROR line is a failure.");
                Check(Worker.IsEngineFailure("Error: invalid config") && Worker.IsEngineFailure("Cannot create route to 10.0.0.0/8"), "Unprefixed engine errors are failures.");
                Check(!Worker.IsEngineFailure("03.11.2026 15:35:28.575811 INFO  [4432] TRUSTTUNNEL_CLIENT_APP operator (): Successfully connected to endpoint"), "Connected line is not a failure.");
                DnsTests();
                StateTests();
                SubscriptionTests();
                SubscriptionUiTests();
                HomeSubscriptionRefreshTests();
                EntrySubscriptionRefreshTests();
                SecondSubscriptionTests();
                AccountTests();
                EngineAvailabilityTests();
                ConnectionDisposeTests();
                EngineSpec.Verify();
                Routing routes=new Routing();routes.Destinations.Add("*.Example.COM");routes.Destinations.Add("10.0.0.0/8");routes.Validate();Check(routes.Destinations[0]=="*.example.com","Domain normalization failed.");
                foreach(string bad in new[]{"https://example.com","example.com:443","10.0.0.1/33","2001:db8::/129","2001:db8::1","a b.com","1","..example.com","010.0.0.1","256.1.1.1"}){bool rejected=false;try{Routing.NormalizeDestination(bad);}catch(FormatException){rejected=true;}Check(rejected,"Unsafe routing destination accepted.");}
                routes.Applications.Add(new AppRule{Executable=@"C:\Apps\browser.exe",Action="direct"});routes.Validate();Check(routes.EngineConfig().Contains("[[application_rules]]"),"Application rule not emitted.");
                routes.Applications.Add(new AppRule{Executable=@"c:\apps\BROWSER.EXE",Action="vpn"});bool duplicateRejected=false;try{routes.Validate();}catch(FormatException){duplicateRejected=true;}Check(duplicateRejected,"Duplicate app path accepted.");
                Routing nameRoutes=new Routing();nameRoutes.Applications.Add(new AppRule{Name="UIS Operator Workplace.exe",Action="vpn"});nameRoutes.Validate();Check(nameRoutes.EngineConfig().Contains("process_name = \"UIS Operator Workplace.exe\"")&&!nameRoutes.EngineConfig().Contains("executable ="),"Process-name rule not emitted.");Check(nameRoutes.Applications[0].Executable==null,"Process-name rule kept a path.");
                foreach(string badName in new[]{"","browser","C:\\Apps\\browser.exe","app/browser.exe","a\"b.exe",new string('x',256)+".exe"}){bool nameRejected=false;try{Routing bad=new Routing();bad.Applications.Add(new AppRule{Name=badName,Action="vpn"});bad.Validate();}catch(FormatException){nameRejected=true;}Check(nameRejected,"Unsafe process name accepted: "+badName);}
                Routing duplicateName=new Routing();duplicateName.Applications.Add(new AppRule{Name="Browser.EXE",Action="direct"});duplicateName.Applications.Add(new AppRule{Name="browser.exe",Action="vpn"});bool dupNameRejected=false;try{duplicateName.Validate();}catch(FormatException){dupNameRejected=true;}Check(dupNameRejected,"Duplicate process name accepted.");
                Routing bothKinds=new Routing();bothKinds.Applications.Add(new AppRule{Name="A.exe",Executable=@"C:\Apps\a.exe",Action="vpn"});bool bothRejected=false;try{bothKinds.Validate();}catch(FormatException){bothRejected=true;}Check(bothRejected,"Rule with both name and path accepted.");
                string nameFile=RulesFile.Export(nameRoutes);Check(nameFile.Contains("UIS Operator Workplace.exe"),"Rules file missing process name.");Check(RulesFile.Import(nameFile).Applications[0].Name=="UIS Operator Workplace.exe","Rules file name round trip failed.");
                Routing shared=Profile.Import(Fixture).Routing;shared.Mode="selective";shared.Destinations.Add("*.Example.COM");shared.Destinations.Add("10.0.0.0/8");shared.Validate();
                string rulesFile=RulesFile.Export(shared);
                Check(!rulesFile.Contains(profile.Password)&&!rulesFile.Contains(profile.Hostname),"Rules file leaks credentials.");
                Routing restored=RulesFile.Import(rulesFile);
                Check(rulesFile.Contains("\"netlane-rules\"")&&RulesFile.Import(rulesFile.Replace("\"netlane-rules\"","\"wintrusttunnel-rules\"")).Destinations.Count==restored.Destinations.Count,"Rules files from earlier versions are not importable.");
                Check(restored.Mode=="selective"&&restored.Destinations.Count==2&&restored.Destinations[0]=="*.example.com","Rules file round trip failed.");
                foreach(string malformed in new[]{"{\"format\":\"other\"}","{\"format\":\"netlane-rules\",\"version\":2,\"routing\":{}}","{\"format\":\"netlane-rules\",\"version\":1}","","["}){bool rulesRejected=false;try{RulesFile.Import(malformed);}catch(FormatException){rulesRejected=true;}Check(rulesRejected,"Malformed rules file accepted: "+malformed);}
                Profile oversized=Profile.Import(Fixture);for(int i=0;i<256;i++)oversized.Routing.Destinations.Add(new string('a',60)+"."+new string('b',60)+"."+new string('c',60)+".route"+i+".example.com");bool oversizedRejected=false;try{oversized.Serialize();}catch(FormatException){oversizedRejected=true;}Check(oversizedRejected,"Oversized profile would exceed worker or storage limits.");
                Console.WriteLine("Windows client tests passed: panel TOML, tt v1, long varints, TLS enforcement, malformed profiles, isolated SOCKS config, DPAPI, atomic writes, corruption and pinned engine hashes.");return 0;
            } catch(Exception error){Console.Error.WriteLine("Windows client test failed: "+error);return 1;}
        }
        static async Task Network(string outputPath) {
            string input=Console.In.ReadToEnd();
            Profile profile=input.TrimStart().StartsWith("{")?Profile.Deserialize(input):Profile.Import(input);
            using(Connection connection=new Connection()) {
                TaskCompletionSource<bool> connected=new TaskCompletionSource<bool>();
                connection.Changed+=delegate(string state){if(state=="CONNECTED")connected.TrySetResult(true);if(state=="CONNECTION_ERROR"||state=="WORKER_ERROR")connected.TrySetException(new Exception("Engine connection failed."));};
                try {
                    await connection.StartAsync(profile,false,18090);
                    if(await Task.WhenAny(connected.Task,Task.Delay(15000))!=connected.Task)throw new Exception("Connection event timeout.");
                    await connected.Task;
                    File.WriteAllText(outputPath,"CONNECTED");
                    // A test controller performs real SOCKS traffic, then creates the stop marker.
                    for(int i=0;i<300&&!File.Exists(outputPath+".stop");i++)await Task.Delay(100);
                    if(!await connection.StopAsync())throw new Exception("Engine did not stop gracefully.");
                    File.WriteAllText(outputPath,"STOPPED");
                } finally { if(connection.Active)connection.StopAsync().GetAwaiter().GetResult(); }
            }
        }
    }
}
