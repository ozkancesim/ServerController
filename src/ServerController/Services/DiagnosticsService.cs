using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace ServerController.Services;

public enum DiagLevel { Ok, Warn, Fail, Info }

public sealed record DiagItem(DiagLevel Level, string Title, string Detail)
{
    public string Icon => Level switch { DiagLevel.Ok => "✅", DiagLevel.Warn => "⚠️", DiagLevel.Fail => "⛔", _ => "ℹ️" };
    public bool IsOk => Level == DiagLevel.Ok;
    public bool IsWarn => Level == DiagLevel.Warn;
    public bool IsFail => Level == DiagLevel.Fail;
    public bool IsInfo => Level == DiagLevel.Info;
}

public sealed record Visitor(string Ip, DateTime? LastSeen, string Domain, int Hits, bool IsCloudflare, bool IsBlocked)
{
    public string LastSeenText => LastSeen?.ToString("dd.MM HH:mm:ss") ?? "";
    public bool IsIpv6 => Ip.Contains(':');
    public string Note => IsCloudflare ? "Cloudflare sunucusu (gerçek ziyaretçi değil)" : IsIpv6 ? "IPv6 adresi" : "";
}

/// <summary>"Blokladım ama hâlâ girebiliyor" sorununun nedenlerini sunucuda kontrol eder.</summary>
public sealed class DiagnosticsService
{
    private readonly SshSession _ssh;
    public DiagnosticsService(SshSession ssh) => _ssh = ssh;

    private static readonly string[] CloudflareRanges =
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
    };

    public static bool IsCloudflare(string ip) => CloudflareRanges.Any(r => IpUtil.Contains(r, ip));

    public async Task<List<DiagItem>> RunAsync(string? ip, string? domain, IReadOnlyList<AllowlistEntry> allow)
    {
        var ipArg = ip ?? "";
        var domArg = domain ?? "";
        var script = $$"""
            IP={{Shell.Q(ipArg)}}; DOM={{Shell.Q(domArg)}}
            echo "CS=$(systemctl is-active crowdsec 2>/dev/null)"
            for u in $(systemctl list-unit-files --no-legend 'crowdsec*bouncer*' 2>/dev/null | awk '{sub(".service","",$1); print $1}'); do
              echo "BU=$u|$(systemctl is-active "$u" 2>/dev/null)"
            done
            echo "BJSON_BEGIN"; cscli bouncers list -o json 2>/dev/null; echo; echo "BJSON_END"
            if command -v nft >/dev/null 2>&1 && nft list tables 2>/dev/null | grep -q crowdsec; then
              echo "FW=nftables"
              [ -n "$IP" ] && { nft list ruleset 2>/dev/null | grep -qwF "$IP" && echo "INFW=yes" || echo "INFW=no"; }
            elif command -v ipset >/dev/null 2>&1 && ipset list -n 2>/dev/null | grep -q crowdsec; then
              echo "FW=ipset"
              if [ -n "$IP" ]; then f=no; for s in $(ipset list -n | grep crowdsec); do ipset test "$s" "$IP" >/dev/null 2>&1 && f=yes; done; echo "INFW=$f"; fi
            else
              echo "FW=none"
            fi
            echo "V6=$(ip -6 addr show scope global 2>/dev/null | grep -c inet6)"
            echo "MYIPS=$(hostname -I 2>/dev/null)"
            [ -n "$DOM" ] && echo "DNS=$(getent ahosts "$DOM" 2>/dev/null | awk '{print $1}' | sort -u | tr '\n' ' ')"
            true
            """;
        var r = (await _ssh.RunAsync(script, TimeSpan.FromMinutes(1))).EnsureOk("Teşhis");
        var lines = r.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        string Val(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "="))?[(key.Length + 1)..].Trim() ?? "";

        var items = new List<DiagItem>();

        // 1) CrowdSec motoru
        var cs = Val("CS");
        items.Add(cs == "active"
            ? new DiagItem(DiagLevel.Ok, "CrowdSec çalışıyor", "Saldırı tespit motoru aktif.")
            : new DiagItem(DiagLevel.Fail, "CrowdSec çalışmıyor", $"Servis durumu: {(cs.Length > 0 ? cs : "bulunamadı")}. Servisler sayfasından başlatın."));

        // 2) Bouncer'lar (blokları gerçekten uygulayan bileşenler)
        var start = lines.IndexOf("BJSON_BEGIN");
        var end = lines.IndexOf("BJSON_END");
        var bouncers = start >= 0 && end > start ? Json.Array(string.Join("\n", lines.Skip(start + 1).Take(end - start - 1))) : new();
        if (bouncers.Count == 0)
        {
            items.Add(new DiagItem(DiagLevel.Fail, "Blok uygulayıcı (bouncer) bulunamadı",
                "CrowdSec saldırganları TESPİT EDİYOR ama ENGELLEYEN bir bileşen (örn. crowdsec-firewall-bouncer) kayıtlı değil. " +
                "Bu durumda listeye eklenen IP'ler hiçbir yerde engellenmez. Sunucu teknisyeninizden firewall bouncer kurmasını isteyin."));
        }
        foreach (var b in bouncers)
        {
            var name = b.Str("name");
            var type = b.Str("type");
            var lastPull = b.Date("last_pull");
            var revoked = b.Str("revoked") == "true" || b.Str("valid") == "false";
            var age = lastPull is { } lp ? DateTime.UtcNow - lp : (TimeSpan?)null;
            var desc = $"{name} {(type.Length > 0 ? "(" + type + ")" : "")} · son güncelleme: {(age == null ? "hiç" : Ago(age.Value))}";
            if (revoked)
                items.Add(new DiagItem(DiagLevel.Fail, "Bouncer yetkisi iptal edilmiş", desc));
            else if (age == null || age > TimeSpan.FromMinutes(5))
                items.Add(new DiagItem(DiagLevel.Fail, "Bouncer güncel blok listesini almıyor", desc + ". Bouncer servisi durmuş veya CrowdSec'e bağlanamıyor olabilir; yeni bloklar uygulanmaz."));
            else
                items.Add(new DiagItem(DiagLevel.Ok, "Bouncer çalışıyor ve blok listesini alıyor", desc));
        }

        // 3) Bouncer servisleri
        foreach (var u in lines.Where(l => l.StartsWith("BU=") && l.Contains("bouncer")).Select(l => l[3..].Split('|')))
        {
            var unit = u[0];
            var st = u.Length > 1 ? u[1] : "";
            items.Add(st == "active"
                ? new DiagItem(DiagLevel.Ok, $"{unit} servisi çalışıyor", "")
                : new DiagItem(DiagLevel.Fail, $"{unit} servisi çalışmıyor ({st})", "Servisler sayfasından başlatın; bu servis durursa bloklar uygulanmaz."));
        }

        // 4) IP özelinde kontroller
        var fw = Val("FW");
        if (!string.IsNullOrEmpty(ip))
        {
            var decisions = await new CrowdSecService(_ssh).GetDecisionsAsync(ip, includeCommunity: true);
            items.Add(decisions.Count > 0
                ? new DiagItem(DiagLevel.Ok, $"{ip} için CrowdSec'te aktif blok var", $"Kalan süre: {decisions[0].DurationText}")
                : new DiagItem(DiagLevel.Warn, $"{ip} için aktif blok YOK", "Blok süresi dolmuş, kaldırılmış ya da hiç eklenmemiş olabilir."));

            var allowed = allow.FirstOrDefault(a => IpUtil.Contains(a.Value, ip));
            if (allowed != null)
                items.Add(new DiagItem(DiagLevel.Fail, $"{ip} beyaz listede", $"Kayıt: {allowed.Value} ({allowed.Description}). CrowdSec beyaz listedeki IP'leri asla bloklamaz."));

            var infw = Val("INFW");
            if (fw == "none")
                items.Add(new DiagItem(DiagLevel.Warn, "Güvenlik duvarında CrowdSec kuralı bulunamadı",
                    "nftables / ipset içinde CrowdSec tablosu yok. Firewall bouncer kurulu değilse bloklar sunucu güvenlik duvarına işlenmez."));
            else if (infw == "yes")
                items.Add(new DiagItem(DiagLevel.Ok, $"{ip} güvenlik duvarına işlenmiş ({fw})", "Bu IP'den sunucuya gelen tüm bağlantılar (web, mail, SSH) engelleniyor."));
            else if (decisions.Count > 0)
                items.Add(new DiagItem(DiagLevel.Fail, $"{ip} blok listesinde ama güvenlik duvarında YOK ({fw})",
                    "Bouncer bu bloğu henüz uygulamamış. Genelde 10-60 saniye sürer; düzelmezse bouncer servisini yeniden başlatın."));
        }

        // 5) IPv6
        if (int.TryParse(Val("V6"), out var v6) && v6 > 0)
            items.Add(new DiagItem(DiagLevel.Warn, "Sunucunuzda IPv6 adresi var",
                "Cep telefonları mobil veride çoğunlukla IPv6 ile bağlanır. whatismyip.com'un gösterdiği IPv4 adresini bloklamak, " +
                "telefonun IPv6 ile siteye girmesini engellemez. Telefondan siteye girip hemen ardından aşağıdaki 'Son ziyaretçiler' listesine bakın: " +
                "telefonunuzun sunucuya hangi adresle geldiğini orada görürsünüz."));

        // 6) Cloudflare
        var dns = Val("DNS").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!string.IsNullOrEmpty(domain))
        {
            if (dns.Length == 0)
                items.Add(new DiagItem(DiagLevel.Warn, $"{domain} çözümlenemedi", "Alan adını doğru yazdığınızdan emin olun."));
            else if (dns.Any(IsCloudflare))
                items.Add(new DiagItem(DiagLevel.Fail, $"{domain} Cloudflare arkasında",
                    "Siteye gelen ziyaretçiler önce Cloudflare'e bağlanıyor; sunucu ziyaretçinin değil Cloudflare'in IP'sini görüyor. " +
                    "Bu yüzden sunucu güvenlik duvarındaki bir IP bloğu web sitesine erişimi ENGELLEMEZ (SSH, FTP ve mail için yine çalışır). " +
                    "Web sitesi için bloklamak isterseniz IP'yi Cloudflare panelinde (Security → WAF → Tools) engelleyin veya CrowdSec Cloudflare bouncer kurdurun."));
            else
                items.Add(new DiagItem(DiagLevel.Ok, $"{domain} doğrudan bu sunucuya geliyor", "Adres(ler): " + string.Join(", ", dns)));
        }

        items.Add(new DiagItem(DiagLevel.Info, "iPhone kullanıyorsanız: iCloud Özel Geçiş (Private Relay)",
            "iCloud+ 'Özel Geçiş' açıksa Safari her siteye farklı bir Apple sunucusu üzerinden bağlanır; whatismyip'te gördüğünüz IP ile " +
            "sitenize giden IP farklı olur. Test için Ayarlar → [adınız] → iCloud → Özel Geçiş'i kapatın veya başka bir tarayıcı kullanın."));
        items.Add(new DiagItem(DiagLevel.Info, "Mobil operatörler IP adresini sık değiştirir",
            "Mobil veride IP adresiniz birkaç dakikada bir değişebilir ve aynı anda birden çok kişi aynı IP'yi paylaşabilir (CGNAT). " +
            "Doğru test için blokladıktan sonra telefondan siteye girip 'Son ziyaretçiler' listesinde telefonun gerçek adresini kontrol edin."));
        return items;
    }

    public async Task<List<Visitor>> RecentVisitorsAsync(string? domain, IReadOnlyCollection<string> blocked)
    {
        var dom = string.IsNullOrWhiteSpace(domain) ? "" : Shell.Token(domain.Trim().ToLowerInvariant(), "alan adı");
        var script = $$"""
            D=/var/log/httpd/domains
            if [ -n '{{dom}}' ]; then FILES="$D/{{dom}}.log"; else FILES=$(ls -t $D/*.log 2>/dev/null | grep -v -E '\.(error|bytes)\.log$' | head -n 10); fi
            [ -z "$FILES" ] && FILES=/usr/local/lsws/logs/access.log
            for f in $FILES; do
              [ -f "$f" ] || continue
              d=$(basename "$f" .log)
              tail -n 400 "$f" | awk -v d="$d" '{print $1 "|" $4 "|" d}'
            done
            true
            """;
        var r = (await _ssh.RunAsync(script, TimeSpan.FromMinutes(1))).EnsureOk("Son ziyaretçileri okuma");
        var rows = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 3 && IpUtil.Normalize(p[0]) != null)
            .Select(p => (Ip: p[0], Time: ParseLogTime(p[1]), Domain: p[2]));
        return rows.GroupBy(x => x.Ip)
            .Select(g =>
            {
                var last = g.OrderByDescending(x => x.Time ?? DateTime.MinValue).First();
                return new Visitor(g.Key, last.Time, last.Domain, g.Count(), IsCloudflare(g.Key), blocked.Any(b => IpUtil.Contains(b, g.Key)));
            })
            .OrderByDescending(v => v.LastSeen ?? DateTime.MinValue)
            .Take(40)
            .ToList();
    }

    private static DateTime? ParseLogTime(string s) =>
        DateTime.TryParseExact(s.TrimStart('['), "dd/MMM/yyyy:HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{(int)t.TotalSeconds} sn önce" :
        t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} dk önce" :
        t.TotalHours < 48 ? $"{(int)t.TotalHours} saat önce" : $"{(int)t.TotalDays} gün önce";
}
