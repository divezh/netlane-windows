using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Netlane {
    internal sealed partial class SettingsForm {
        TextBox dnsInput;
        Label dnsNotice;
        Button dnsSave;
        bool updatingDns;
        string savedDns="";
        bool DnsDirty { get { return dnsInput!=null&&dnsInput.Text.Replace("\r\n","\n")!=savedDns.Replace("\r\n","\n"); } }
        Control BuildDnsSettings() {
            TableLayoutPanel box=new TableLayoutPanel{Height=230,ColumnCount=1,RowCount=4,BackColor=Theme.Ground,Margin=new Padding(0)};
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{44,88,46,52})box.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            Label hint=Hint("По одному адресу на строку. Пустой список — стандартный DNS движка. Только для запросов через VPN. Примеры: 1.1.1.1, tls://1.1.1.1, https://dns.example.com/dns-query.");hint.ForeColor=Theme.Muted;box.Controls.Add(hint,0,0);
            dnsInput=new TextBox{Multiline=true,AcceptsReturn=true,ScrollBars=ScrollBars.None,MaxLength=4200,Dock=DockStyle.Fill,BackColor=Theme.Card,ForeColor=Theme.Ink,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(0,0,0,8),AccessibleName="DNS-серверы для VPN"};
            tips.SetToolTip(dnsInput,"Примеры: 1.1.1.1, 8.8.8.8:53, tls://1.1.1.1, https://dns.example.com/dns-query");box.Controls.Add(dnsInput,0,1);
            FlowLayoutPanel actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0),BackColor=Theme.Ground};
            dnsSave=MainForm.Button("Сохранить DNS",true);dnsSave.Dock=DockStyle.None;dnsSave.Size=new Size(156,36);dnsSave.Margin=new Padding(0,0,10,0);
            Button reset=MainForm.Button("По умолчанию",false);reset.Dock=DockStyle.None;reset.Size=new Size(150,36);reset.Margin=new Padding(0);
            actions.Controls.Add(dnsSave);actions.Controls.Add(reset);box.Controls.Add(actions,0,2);
            dnsNotice=Hint("Изменения применяются при следующем подключении.");dnsNotice.ForeColor=Theme.Muted;box.Controls.Add(dnsNotice,0,3);
            dnsInput.TextChanged+=delegate{if(!updatingDns){dnsSave.Enabled=DnsDirty;dnsNotice.ForeColor=Theme.Muted;dnsNotice.Text=DnsDirty?"Есть несохранённые изменения. Нажмите «Сохранить DNS».":"Изменения применяются при следующем подключении.";}};
            dnsSave.Click+=delegate{SaveDns();};reset.Click+=delegate{dnsInput.Clear();};
            RefreshDns();return box;
        }
        void RefreshDns() {
            if(dnsInput==null||DnsDirty)return;
            updatingDns=true;
            try{savedDns=String.Join(Environment.NewLine,DnsSettings.Normalize(state.DnsServers));dnsInput.Text=savedDns;dnsSave.Enabled=false;ShowDnsSource();}
            finally{updatingDns=false;}
        }
        void ShowDnsSource() {
            Server server = state.SelectedServer;
            Subscription source = server == null ? null : state.Subscriptions.FirstOrDefault(sub=>sub.Id == server.SubscriptionId);
            if (source == null || source.ManagedDns == null) return;
            string servers = source.ManagedDns.Count == 0 ? "по умолчанию" : String.Join(", ",source.ManagedDns);
            dnsNotice.Text = "Для этой подписки DNS задаёт администратор: " + servers + ". Здесь редактируются личные DNS.";
            tips.SetToolTip(dnsNotice,dnsNotice.Text);
        }
        internal bool SaveDns() {
            try {
                var servers=DnsSettings.Parse(dnsInput.Text);
                if(host.PreviewOnly){dnsNotice.Text="Демонстрационный режим: настройки не сохраняются.";return false;}
                if(!host.SaveDnsServers(servers)){dnsNotice.ForeColor=Theme.Danger;dnsNotice.Text="Не удалось сохранить DNS. Прежние настройки сохранены.";return false;}
                updatingDns=true;
                try{savedDns=String.Join(Environment.NewLine,servers);dnsInput.Text=savedDns;dnsSave.Enabled=false;}
                finally{updatingDns=false;}
                dnsNotice.ForeColor=Theme.Muted;dnsNotice.Text="Личный DNS сохранён. Применится при следующем подключении.";ShowDnsSource();return true;
            } catch(FormatException error){dnsNotice.ForeColor=Theme.Danger;dnsNotice.Text=error.Message;return false;}
        }
        bool ConfirmDnsChanges() {
            if(current!="connection"||!DnsDirty)return true;
            DialogResult answer=MessageBox.Show(this,"Сохранить изменения DNS?","DNS",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
            if(answer==DialogResult.Cancel)return false;
            if(answer==DialogResult.Yes)return SaveDns();
            updatingDns=true;try{dnsInput.Text=savedDns;}finally{updatingDns=false;}return true;
        }
    }
}
