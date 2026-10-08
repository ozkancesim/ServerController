using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerController.Services;

public sealed class DecisionInfo
{
    public long Id { get; init; }
    public string Value { get; init; } = "";
    public string Scope { get; init; } = "Ip";
    public string Type { get; init; } = "ban";
    public string Origin { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Country { get; init; } = "";
    public string AsName { get; init; } = "";
    public DateTime? CreatedAt { get; init; }
    public int Events { get; init; }

    public string ScenarioText => Scenarios.Describe(Scenario, Origin);
    public string DurationText => Durations.Humanize(Duration);
    public string OriginText => Origin switch
    {
        "cscli" => "Elle eklendi",
        "crowdsec" => "Otomatik (bu sunucu)",
        "CAPI" => "Topluluk listesi",
        "lists" => "Abonelik listesi",
        "console" => "CrowdSec konsolu",
        _ => Origin,
    };
    public string CreatedText => CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";
}

public sealed class AllowlistEntry
{
    public string Value { get; init; } = "";
    public string Description { get; init; } = "";
    public DateTime? CreatedAt { get; init; }
    public DateTime? Expiration { get; init; }

    public string CreatedText => CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";
    public string ExpirationText => Expiration is { } e ? e.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "Süresiz";
}

public sealed class AlertInfo
{
    public long Id { get; init; }
    public string Value { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string Country { get; init; } = "";
    public string AsName { get; init; } = "";
    public int Events { get; init; }
    public DateTime? CreatedAt { get; init; }
    public int DecisionCount { get; init; }

    public string ScenarioText => Scenarios.Describe(Scenario, "");
    public string CreatedText => CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";
}

/// <summary>CrowdSec'i cscli komutları üzerinden yönetir.</summary>
public sealed class CrowdSecService
{
    private readonly SshSession _ssh;

    public CrowdSecService(SshSession ssh) => _ssh = ssh;

    public async Task<string> GetVersionAsync()
    {
        var r = await _ssh.RunAsync("cscli version 2>&1 | head -n 3");
        if (!r.Ok || r.Output.Contains("not found"))
            throw new UserFacingException("Bu sunucuda CrowdSec (cscli) bulunamadı.");
        var line = r.Output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("version", StringComparison.OrdinalIgnoreCase)) ?? "";
        return line.Contains(':') ? line.Split(':', 2)[1].Trim() : "?";
    }

    public async Task<List<DecisionInfo>> GetDecisionsAsync(string? ip = null, bool includeCommunity = false)
    {
        var cmd = "cscli decisions list -o json --limit 0";
        if (includeCommunity) cmd += " -a";
        if (ip != null) cmd += (IpUtil.IsRange(ip) ? " --range " : " --ip ") + Shell.Q(ip);
        var r = (await _ssh.RunAsync(cmd, TimeSpan.FromMinutes(1))).EnsureOk("Blok listesi alma");
        var result = new List<DecisionInfo>();
        foreach (var alert in Json.Array(r.Output))
        {
            var src = alert.Prop("source");
            foreach (var d in alert.Prop("decisions").Items())
            {
                result.Add(new DecisionInfo
                {
                    Id = d.Long("id"),
                    Value = d.Str("value"),
                    Scope = d.Str("scope"),
                    Type = d.Str("type"),
                    Origin = d.Str("origin"),
                    Scenario = d.Str("scenario").Length > 0 ? d.Str("scenario") : alert.Str("scenario"),
                    Duration = d.Str("duration"),
                    Country = src.Str("cn"),
                    AsName = src.Str("as_name"),
                    CreatedAt = alert.Date("created_at"),
                    Events = (int)alert.Long("events_count"),
                });
            }
        }
        return result.OrderByDescending(d => d.CreatedAt ?? DateTime.MinValue).ToList();
    }

    public async Task DeleteDecisionAsync(string value)
    {
        var flag = IpUtil.IsRange(value) ? "--range" : "--ip";
        (await _ssh.RunAsync($"cscli decisions delete {flag} {Shell.Q(value)}")).EnsureOk("Blok kaldırma");
    }

    public async Task AddDecisionAsync(string value, string duration, string reason)
    {
        var flag = IpUtil.IsRange(value) ? "--range" : "--ip";
        var cmd = $"cscli decisions add {flag} {Shell.Q(value)} --duration {Shell.Token(duration, "süre")} --type ban";
        if (!string.IsNullOrWhiteSpace(reason)) cmd += " --reason " + Shell.Q(reason.Trim());
        (await _ssh.RunAsync(cmd)).EnsureOk("IP bloklama");
    }

    public async Task<List<AlertInfo>> GetAlertsAsync(string? ip = null, int limit = 200)
    {
        var cmd = $"cscli alerts list -o json --limit {limit}";
        if (ip != null) cmd += (IpUtil.IsRange(ip) ? " --range " : " --ip ") + Shell.Q(ip);
        var r = (await _ssh.RunAsync(cmd, TimeSpan.FromMinutes(1))).EnsureOk("Saldırı uyarılarını alma");
        return Json.Array(r.Output).Select(a =>
        {
            var src = a.Prop("source");
            return new AlertInfo
            {
                Id = a.Long("id"),
                Value = src.Str("value").Length > 0 ? src.Str("value") : src.Str("ip"),
                Scenario = a.Str("scenario"),
                Country = src.Str("cn"),
                AsName = src.Str("as_name"),
                Events = (int)a.Long("events_count"),
                CreatedAt = a.Date("created_at"),
                DecisionCount = a.Prop("decisions").Items().Count(),
            };
        }).ToList();
    }

    // ---------- Beyaz liste (allowlist) — CrowdSec 1.6.8+ ----------

    public async Task<List<string>> GetAllowlistNamesAsync()
    {
        var r = await _ssh.RunAsync("cscli allowlists list -o json");
        if (!r.Ok)
        {
            if (r.Combined.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("Sunucudaki CrowdSec sürümü beyaz listeleri (allowlists) desteklemiyor. En az 1.6.8 gerekir.");
            r.EnsureOk("Beyaz listeleri alma");
        }
        return Json.Array(r.Output).Select(e => e.Str("name")).Where(n => n.Length > 0).ToList();
    }

    public async Task CreateAllowlistAsync(string name, string description)
    {
        (await _ssh.RunAsync($"cscli allowlists create {Shell.Token(name, "liste adı")} -d {Shell.Q(description)}"))
            .EnsureOk("Beyaz liste oluşturma");
    }

    public async Task<List<AllowlistEntry>> GetAllowlistAsync(string name)
    {
        var r = (await _ssh.RunAsync($"cscli allowlists inspect {Shell.Token(name, "liste adı")} -o json")).EnsureOk("Beyaz listeyi okuma");
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Output) ? "{}" : r.Output);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array) root = root.Items().FirstOrDefault();
        return root.Prop("items").Items().Select(i => new AllowlistEntry
        {
            Value = i.Str("value"),
            Description = i.Str("description"),
            CreatedAt = i.Date("created_at"),
            Expiration = i.Date("expiration"),
        }).Where(e => e.Value.Length > 0).OrderByDescending(e => e.CreatedAt ?? DateTime.MinValue).ToList();
    }

    public async Task AddToAllowlistAsync(string name, string value, string description, string? expiration)
    {
        var cmd = $"cscli allowlists add {Shell.Token(name, "liste adı")} {Shell.Q(value)}";
        if (!string.IsNullOrWhiteSpace(description)) cmd += " -d " + Shell.Q(description.Trim());
        if (!string.IsNullOrWhiteSpace(expiration)) cmd += " -e " + Shell.Token(expiration, "süre");
        (await _ssh.RunAsync(cmd)).EnsureOk("Beyaz listeye ekleme");
    }

    public async Task RemoveFromAllowlistAsync(string name, string value)
    {
        (await _ssh.RunAsync($"cscli allowlists remove {Shell.Token(name, "liste adı")} {Shell.Q(value)}"))
            .EnsureOk("Beyaz listeden çıkarma");
    }
}

public static class Scenarios
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh-bf"] = "SSH şifre deneme saldırısı",
        ["ssh-slow-bf"] = "Yavaş SSH şifre denemesi",
        ["ssh-cve-2024-6387"] = "SSH açığı istismar denemesi",
        ["ssh-refused-conn"] = "Reddedilen SSH bağlantıları",
        ["http-probing"] = "Site taraması (açık arama)",
        ["http-crawl-non_statics"] = "Agresif site gezinme (bot)",
        ["http-bad-user-agent"] = "Kötü amaçlı bot / tarayıcı",
        ["http-sensitive-files"] = "Gizli dosyalara erişim denemesi",
        ["http-path-traversal-probing"] = "Dizin atlama saldırısı",
        ["http-sqli-probing"] = "SQL enjeksiyon denemesi",
        ["http-xss-probing"] = "XSS saldırı denemesi",
        ["http-backdoors-attempts"] = "Arka kapı (backdoor) arama",
        ["http-admin-interface-probing"] = "Yönetim paneli arama",
        ["http-open-proxy"] = "Açık proxy denemesi",
        ["http-generic-bf"] = "Giriş sayfasına şifre denemesi",
        ["http-wordpress-scan"] = "WordPress taraması",
        ["http-wordpress_wpconfig"] = "wp-config.php erişim denemesi",
        ["http-bf-wordpress_bf"] = "WordPress şifre deneme saldırısı",
        ["http-bf-wordpress_bf_xmlrpc"] = "WordPress XML-RPC saldırısı",
        ["http-wordpress_user-enum"] = "WordPress kullanıcı adı toplama",
        ["http-cve-probing"] = "Bilinen açık taraması",
        ["http-dos-swithcing-ua"] = "DoS denemesi",
        ["exim-bf"] = "E-posta şifre deneme saldırısı",
        ["exim-spam"] = "Spam gönderme denemesi",
        ["dovecot-spam"] = "E-posta (IMAP/POP) şifre denemesi",
        ["postfix-spam"] = "Spam gönderme denemesi",
        ["pure-ftpd-bf"] = "FTP şifre deneme saldırısı",
        ["proftpd-bf"] = "FTP şifre deneme saldırısı",
        ["directadmin-bf"] = "DirectAdmin şifre deneme saldırısı",
        ["mysql-bf"] = "Veritabanı şifre denemesi",
        ["port-scan"] = "Port taraması",
        ["iptables-scan-multi_ports"] = "Port taraması",
        ["modsecurity"] = "Web güvenlik duvarı (ModSecurity) ihlali",
        ["appsec-vpatch"] = "Web saldırısı (sanal yama)",
    };

    public static string Describe(string scenario, string origin)
    {
        if (string.IsNullOrWhiteSpace(scenario)) return origin == "cscli" ? "Elle bloklandı" : "-";
        if (scenario.StartsWith("manual", StringComparison.OrdinalIgnoreCase)) return "Elle bloklandı";
        if (origin == "CAPI" || origin == "lists") return "Topluluk kara listesi";
        var key = scenario.Contains('/') ? scenario[(scenario.LastIndexOf('/') + 1)..] : scenario;
        foreach (var (k, v) in Known)
            if (key.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return v;
        if (key.Contains("bf", StringComparison.OrdinalIgnoreCase)) return "Şifre deneme saldırısı (" + key + ")";
        return scenario.StartsWith("crowdsecurity/") ? key : scenario;
    }
}

public static class Durations
{
    /// <summary>CrowdSec süre metnini ("3h59m12.5s") Türkçe kısa metne çevirir.</summary>
    public static string Humanize(string d)
    {
        if (string.IsNullOrWhiteSpace(d)) return "-";
        var neg = d.StartsWith('-');
        double total = 0;
        var num = "";
        foreach (var c in d.TrimStart('-'))
        {
            if (char.IsDigit(c) || c == '.') { num += c; continue; }
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { num = ""; continue; }
            total += c switch { 'h' => v * 3600, 'm' => v * 60, 's' => v, _ => 0 };
            num = "";
        }
        if (neg || total <= 0) return "Süresi doldu";
        var ts = TimeSpan.FromSeconds(total);
        if (ts.TotalDays >= 3650) return "Kalıcı";
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays} gün {ts.Hours} sa";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} sa {ts.Minutes} dk";
        return $"{Math.Max(1, ts.Minutes)} dk";
    }
}

/// <summary>Esnek JSON okuma yardımcıları (alan yoksa boş değer döner).</summary>
public static class Json
{
    public static List<JsonElement> Array(string json)
    {
        json = json.Trim();
        if (json.Length == 0 || json == "null") return new();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.Clone()).ToList() : Enumerable.Empty<JsonElement>();

    public static string Str(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    public static long Long(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return long.TryParse(e.Str(name), out var p) ? p : 0;
    }

    public static DateTime? Date(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (s.Length == 0 || s.StartsWith("0001-")) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
    }
}
