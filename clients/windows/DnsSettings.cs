using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Netlane {
    internal static class DnsSettings {
        internal const int MaximumServers = 8;
        static FormatException Invalid() { return new FormatException("Укажите IP DNS-сервера (например, 1.1.1.1), IP:порт, tls://имя или https://имя/dns-query."); }
        internal static List<string> Parse(string text) {
            return Normalize((text ?? "").Split(new[]{"\r\n","\n","\r"},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim()).Where(s=>s.Length>0));
        }
        internal static List<string> Normalize(IEnumerable<string> entries) {
            List<string> result=new List<string>();
            if(entries==null)return result;
            foreach(string entry in entries){
                if(result.Count>=MaximumServers)throw new FormatException("Не больше восьми DNS-серверов.");
                string value=NormalizeServer(entry);
                if(result.Contains(value))throw new FormatException("DNS-сервер указан повторно: "+value);
                result.Add(value);
            }
            return result;
        }
        static bool IsIp(string text,out IPAddress ip) {
            ip=null;
            if(text.Contains("%")||text.Contains("[")||text.Contains("]")||!IPAddress.TryParse(text,out ip))return false;
            if(ip.AddressFamily==AddressFamily.InterNetwork&&!Regex.IsMatch(text,@"\A(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}\z"))return false;
            return true;
        }
        static void CheckAddress(IPAddress ip) {
            if(ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||ip.Equals(IPAddress.Broadcast))throw Invalid();
            // Queries go through the VPN server, which does not reach private networks
            // (allow_private_network_connections = false): such a resolver would break every name.
            if(IsLocal(ip))throw new FormatException("DNS-сервер из локальной сети ("+ip+") недоступен через VPN: запросы DNS идут через сервер. Укажите публичный DNS, например 1.1.1.1.");
        }
        internal static bool IsLocal(IPAddress ip) {
            if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
            if(IPAddress.IsLoopback(ip))return true;
            byte[] b=ip.GetAddressBytes();
            if(ip.AddressFamily==AddressFamily.InterNetwork)
                return b[0]==10||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168)||(b[0]==169&&b[1]==254)||(b[0]==100&&b[1]>=64&&b[1]<=127);
            return ip.IsIPv6LinkLocal||ip.IsIPv6SiteLocal||(b[0]&0xFE)==0xFC;
        }
        // For stored settings: keeps the entries that pass today's checks and lists the rest, so one
        // bad entry (for example from an older version) never makes the whole state unreadable.
        internal static List<string> Sanitize(IEnumerable<string> entries,List<string> dropped) {
            List<string> result=new List<string>();
            if(entries==null)return result;
            foreach(string entry in entries){
                string value;
                try{value=NormalizeServer(entry);}catch(FormatException){dropped.Add(entry??"");continue;}
                if(result.Contains(value)||result.Count>=MaximumServers){dropped.Add(entry);continue;}
                result.Add(value);
            }
            return result;
        }
        internal static string NormalizeServer(string input) {
            if(input==null||input.Length>512||input.Any(Char.IsControl))throw Invalid();
            string value=input.Trim();
            if(value.Length==0||value.Any(Char.IsWhiteSpace)||value.Contains("\\")||value.EndsWith(":"))throw Invalid();
            IPAddress ip;
            if(IsIp(value,out ip)){CheckAddress(ip);return ip.ToString();}
            bool plain=!value.Contains("://");
            Uri uri;
            if(!Uri.TryCreate(plain?"tcp://"+value:value,UriKind.Absolute,out uri)||String.IsNullOrEmpty(uri.Host)||uri.UserInfo!=""||uri.Fragment!=""||uri.Port==0||uri.Port>65535)throw Invalid();
            string scheme=uri.Scheme.ToLowerInvariant();
            if(scheme!="https"&&scheme!="tls"&&scheme!="tcp"&&scheme!="quic")throw Invalid();
            string authority=(plain?value:value.Substring(value.IndexOf("://",StringComparison.Ordinal)+3)).Split('/','?','#')[0];
            string rawHost,tail;
            if(authority.StartsWith("[")){int end=authority.IndexOf(']');if(end<2)throw Invalid();rawHost=authority.Substring(1,end-1);tail=authority.Substring(end+1);}
            else {int colon=authority.LastIndexOf(':');rawHost=colon<0?authority:authority.Substring(0,colon);tail=colon<0?"":authority.Substring(colon);}
            int explicitPort=0;
            if(tail!=""&&(!tail.StartsWith(":")||!Int32.TryParse(tail.Substring(1),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out explicitPort)||explicitPort<1||explicitPort>65535))throw Invalid();
            if(plain&&tail.Length==0)throw Invalid();
            string host=rawHost;
            bool literal=IsIp(host,out ip);
            if(literal)CheckAddress(ip);
            else if(plain||uri.HostNameType!=UriHostNameType.Dns||Uri.CheckHostName(host)==UriHostNameType.Unknown||host.All(c=>Char.IsDigit(c)||c=='.')||host.Contains("%"))throw Invalid();
            if(scheme=="https"){string address=uri.AbsoluteUri;if(address.Length>512)throw Invalid();return address;}
            if((uri.AbsolutePath!=""&&uri.AbsolutePath!="/")||uri.Query!="")throw Invalid();
            host=literal?(ip.AddressFamily==AddressFamily.InterNetworkV6?"["+ip+"]":ip.ToString()):uri.IdnHost.ToLowerInvariant();
            string port=explicitPort>0?":"+explicitPort:"";
            if(plain&&port.Length==0)throw Invalid();
            return (plain?"":scheme+"://")+host+port;
        }
    }
}
