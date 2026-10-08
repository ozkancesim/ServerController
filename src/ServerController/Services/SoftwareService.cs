using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ServerController.Services;

public sealed record PhpSlot(int Index, string Version, string Mode);
public sealed record PhpExtension(string Name, bool Enabled);
public sealed record DaPlugin(string Id, string Name, string Version, bool Active, string Author);
public sealed record CustomBuildInfo(string DaVersion, string WebServer, List<PhpSlot> Php, List<PhpExtension> Extensions, Dictionary<string, string> Options);

/// <summary>DirectAdmin CustomBuild ve eklentileri.</summary>
public sealed class CustomBuildService
{
    public const string Dir = "/usr/local/directadmin/custombuild";
    private readonly SshSession _ssh;
    public CustomBuildService(SshSession ssh) => _ssh = ssh;

    public static readonly string[] PhpVersions = { "8.4", "8.3", "8.2", "8.1", "8.0", "7.4", "7.3", "7.2", "5.6" };
    public static readonly string[] PhpModes = { "lsphp", "php-fpm", "fastcgi", "mod_php" };

    public static readonly string[] KnownExtensions =
    {
        "bcmath", "gmp", "igbinary", "imagick", "imap", "intl", "ioncube", "ldap", "opcache",
        "phalcon", "readline", "redis", "snuffleupagus", "sodium", "suhosin", "xmlrpc", "zend",
    };

    public async Task<CustomBuildInfo> GetInfoAsync()
    {
        var script = $"""
            if [ ! -d {Dir} ]; then echo "NOCB"; exit 0; fi
            echo "DAVER=$( (/usr/local/directadmin/directadmin version 2>/dev/null || /usr/local/directadmin/directadmin v 2>/dev/null) | head -n1)"
            sed -n 's/^\([a-z0-9_]*\)=\(.*\)$/OPT:\1=\2/p' {Dir}/options.conf
            [ -f {Dir}/php_extensions.conf ] && sed -n 's/^\([a-z0-9_]*\)=\(.*\)$/EXT:\1=\2/p' {Dir}/php_extensions.conf
            true
            """;
        var r = (await _ssh.RunAsync(script)).EnsureOk("CustomBuild bilgisi alma");
        if (r.Output.StartsWith("NOCB")) throw new UserFacingException("Bu sunucuda DirectAdmin CustomBuild bulunamadı.");

        var opts = new Dictionary<string, string>();
        var exts = new Dictionary<string, bool>();
        string da = "";
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("DAVER=")) da = line[6..].Replace("DirectAdmin", "").Trim();
            else if (line.StartsWith("OPT:")) { var kv = line[4..].Split('=', 2); opts[kv[0]] = kv.Length > 1 ? kv[1].Trim() : ""; }
            else if (line.StartsWith("EXT:")) { var kv = line[4..].Split('=', 2); exts[kv[0]] = kv.Length > 1 && kv[1].Trim() == "yes"; }
        }
        var slots = new List<PhpSlot>();
        for (int i = 1; i <= 4; i++)
        {
            var v = opts.GetValueOrDefault($"php{i}_release", "no");
            if (v != "no" && v.Length > 0) slots.Add(new PhpSlot(i, v, opts.GetValueOrDefault($"php{i}_mode", "")));
        }
        foreach (var k in KnownExtensions) exts.TryAdd(k, false);
        var extList = exts.Select(e => new PhpExtension(e.Key, e.Value)).OrderByDescending(e => e.Enabled).ThenBy(e => e.Name).ToList();
        return new CustomBuildInfo(da, opts.GetValueOrDefault("webserver", "?"), slots, extList, opts);
    }

    public static string SetPhpVersionCommand(int slot, string version, string mode)
    {
        if (!PhpVersions.Contains(version) || !PhpModes.Contains(mode) || slot is < 1 or > 4)
            throw new UserFacingException("Geçersiz PHP seçimi.");
        return $"cd {Dir} && ./build update && ./build set php{slot}_release {version} && ./build set php{slot}_mode {mode} && ./build php n && ./build rewrite_confs";
    }

    public static string RemovePhpSlotCommand(int slot) =>
        $"cd {Dir} && ./build set php{slot}_release no && ./build rewrite_confs";

    public static string ExtensionCommand(string ext, bool enable)
    {
        Shell.Token(ext, "eklenti adı");
        return $"cd {Dir} && ./build update && ./build set_php {ext} {(enable ? "yes" : "no")} && ./build php_{ext}";
    }

    /// <summary>(Başlık, komut, onay gerekir mi, açıklama)</summary>
    public static readonly (string Title, string Command, bool Confirm, string Info)[] Actions =
    {
        ("Güncellemeleri kontrol et", $"cd {Dir} && ./build update && ./build versions", false, "Hangi yazılımların güncel olmadığını listeler. Hiçbir şeyi değiştirmez."),
        ("Tüm yazılımları güncelle", $"cd {Dir} && ./build update && ./build update_versions", true, "Güncel olmayan her şeyi günceller. Uzun sürebilir (10-60 dk)."),
        ("DirectAdmin'i güncelle", "(command -v da >/dev/null 2>&1 && da update) || (cd " + Dir + " && ./build update && ./build update_da)", true, "DirectAdmin panelini en son sürüme günceller."),
        ("LiteSpeed'i güncelle", $"cd {Dir} && ./build update && ./build litespeed", true, "LiteSpeed web sunucusunu yeniden derler/günceller."),
        ("phpMyAdmin kur / güncelle", $"cd {Dir} && ./build update && ./build phpmyadmin", true, "Veritabanı yönetim aracı."),
        ("Roundcube kur / güncelle", $"cd {Dir} && ./build update && ./build roundcube", true, "Web tabanlı e-posta (webmail)."),
        ("Softaculous kur / güncelle", $"cd {Dir} && ./build update && ./build set softaculous yes && ./build softaculous", true, "Tek tıkla uygulama (WordPress vb.) kurucu."),
        ("Exim ayarlarını yenile", $"cd {Dir} && ./build update && ./build exim_conf", true, "E-posta sunucusu yapılandırmasını günceller."),
        ("Yapılandırmaları yeniden yaz", $"cd {Dir} && ./build rewrite_confs", true, "Web sunucu ayarlarını DirectAdmin şablonlarından yeniden oluşturur."),
    };

    public async Task<List<DaPlugin>> GetPluginsAsync()
    {
        const string script = """
            for d in /usr/local/directadmin/plugins/*/; do
              [ -f "$d/plugin.conf" ] || continue
              id=$(basename "$d")
              name=$(sed -n 's/^name=//p' "$d/plugin.conf" | head -n1)
              ver=$(sed -n 's/^version=//p' "$d/plugin.conf" | head -n1)
              act=$(sed -n 's/^active=//p' "$d/plugin.conf" | head -n1)
              auth=$(sed -n 's/^author=//p' "$d/plugin.conf" | head -n1)
              echo "$id|$name|$ver|$act|$auth"
            done
            true
            """;
        var r = (await _ssh.RunAsync(script)).EnsureOk("Eklenti listesini alma");
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 5)
            .Select(p => new DaPlugin(p[0], p[1].Length > 0 ? p[1] : p[0], p[2], p[3].Trim() == "yes", p[4]))
            .ToList();
    }

    public async Task SetPluginActiveAsync(string id, bool active)
    {
        Shell.Token(id, "eklenti");
        var f = $"/usr/local/directadmin/plugins/{id}/plugin.conf";
        var v = active ? "yes" : "no";
        (await _ssh.RunAsync($"grep -q '^active=' {f} && sed -i 's/^active=.*/active={v}/' {f} || echo 'active={v}' >> {f}"))
            .EnsureOk("Eklenti durumu değiştirme");
    }
}

public sealed record WpSite(string Owner, string Domain, string Path)
{
    public string Display => Path.Contains("/public_html/") && !Path.EndsWith("/public_html")
        ? Domain + Path[(Path.IndexOf("/public_html", StringComparison.Ordinal) + 12)..]
        : Domain;
}

public sealed record WpPlugin(string Name, string Status, string Version, string Update, string UpdateVersion, string Title)
{
    public bool IsActive => Status is "active" or "active-network";
    public bool HasUpdate => Update == "available";
    public string StatusText => Status switch
    {
        "active" => "Aktif",
        "active-network" => "Ağda aktif",
        "inactive" => "Pasif",
        "must-use" => "Zorunlu (MU)",
        "dropin" => "Drop-in",
        _ => Status,
    };
}

public sealed record WpSearchResult(string Slug, string Name, string Rating, string Installs);

/// <summary>WordPress sitelerini WP-CLI ile yönetir (komutlar site sahibi kullanıcısıyla çalışır).</summary>
public sealed partial class WordPressService
{
    private readonly SshSession _ssh;
    public WordPressService(SshSession ssh) => _ssh = ssh;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-_.]*$")]
    private static partial Regex SlugRegex();

    public async Task<bool> HasWpCliAsync() =>
        (await _ssh.RunAsync("command -v wp >/dev/null 2>&1")).Ok;

    public const string InstallWpCliCommand =
        "curl -fsSL -o /usr/local/bin/wp https://raw.githubusercontent.com/wp-cli/builds/gh-pages/phar/wp-cli.phar && chmod +x /usr/local/bin/wp && wp --allow-root --version";

    public async Task<List<WpSite>> FindSitesAsync()
    {
        const string script = """
            for f in /home/*/domains/*/public_html/wp-config.php /home/*/domains/*/public_html/*/wp-config.php; do
              [ -f "$f" ] && echo "$(dirname "$f")"
            done
            true
            """;
        var r = (await _ssh.RunAsync(script, TimeSpan.FromMinutes(1))).EnsureOk("WordPress sitelerini arama");
        var list = new List<WpSite>();
        foreach (var path in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = path.Trim().Split('/');
            // /home/USER/domains/DOMAIN/public_html[/alt]
            if (parts.Length < 6) continue;
            list.Add(new WpSite(parts[2], parts[4], path.Trim()));
        }
        return list.OrderBy(s => s.Domain).ThenBy(s => s.Path).ToList();
    }

    public static string Wp(WpSite site, string args)
    {
        Shell.Token(site.Owner, "kullanıcı");
        return $"cd {Shell.Q(site.Path)} && runuser -u {site.Owner} -- env PATH=\"$PATH\" HOME=/home/{site.Owner} wp --path={Shell.Q(site.Path)} {args}";
    }

    public async Task<List<WpPlugin>> GetPluginsAsync(WpSite site)
    {
        var r = (await _ssh.RunAsync(Wp(site, "plugin list --format=json --fields=name,status,version,update,update_version,title --skip-plugins --skip-themes"), TimeSpan.FromMinutes(1)))
            .EnsureOk("Eklenti listesini alma");
        var json = r.Output[Math.Max(0, r.Output.IndexOf('['))..];
        return Json.Array(json).Select(p => new WpPlugin(p.Str("name"), p.Str("status"), p.Str("version"), p.Str("update"), p.Str("update_version"), p.Str("title")))
            .OrderBy(p => p.Name).ToList();
    }

    public async Task<string> GetCoreVersionAsync(WpSite site)
    {
        var r = await _ssh.RunAsync(Wp(site, "core version --skip-plugins --skip-themes"));
        return r.Ok ? r.Output.Trim() : "?";
    }

    public async Task<List<WpSearchResult>> SearchAsync(WpSite site, string term)
    {
        var r = (await _ssh.RunAsync(Wp(site, $"plugin search {Shell.Q(term)} --per-page=20 --format=json --fields=slug,name,rating,active_installs --skip-plugins --skip-themes"), TimeSpan.FromMinutes(1)))
            .EnsureOk("Eklenti arama");
        var json = r.Output[Math.Max(0, r.Output.IndexOf('['))..];
        return Json.Array(json).Select(p => new WpSearchResult(p.Str("slug"), System.Net.WebUtility.HtmlDecode(p.Str("name")), p.Str("rating"), FormatInstalls(p.Long("active_installs")))).ToList();
    }

    private static string FormatInstalls(long n) => n >= 1_000_000 ? $"{n / 1_000_000}M+" : n >= 1000 ? $"{n / 1000}B+" : n.ToString();

    public static string Slug(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        if (!SlugRegex().IsMatch(slug)) throw new UserFacingException("Geçersiz eklenti adı. Örnek: litespeed-cache");
        return slug;
    }

    public static string PluginCommand(WpSite site, string action, string slug) => action switch
    {
        "install" => Wp(site, $"plugin install {Slug(slug)} --activate"),
        "activate" => Wp(site, $"plugin activate {Slug(slug)}"),
        "deactivate" => Wp(site, $"plugin deactivate {Slug(slug)}"),
        "update" => Wp(site, $"plugin update {Slug(slug)}"),
        "delete" => Wp(site, $"plugin deactivate {Slug(slug)}") + " ; " + Wp(site, $"plugin delete {Slug(slug)}"),
        _ => throw new ArgumentException(action),
    };

    public static string UpdateAllCommand(WpSite site) => Wp(site, "plugin update --all");
    public static string CoreUpdateCommand(WpSite site) => Wp(site, "core update") + " && " + Wp(site, "core update-db");

    public static string CacheFlushCommand(WpSite site) =>
        Wp(site, "cache flush") + " ; " + Wp(site, "plugin is-active litespeed-cache") + " && " + Wp(site, "litespeed-purge all") + " ; true";
}
