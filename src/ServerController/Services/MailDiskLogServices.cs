using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ServerController.Services;

public sealed record MailItem(string Id, string Age, string Size, string Sender, string Recipients, bool Frozen);

public sealed partial class MailService
{
    private readonly SshSession _ssh;
    public MailService(SshSession ssh) => _ssh = ssh;

    // " 25m  2.9K 1t5Xyz-000Abc-12 <a@b.com> *** frozen ***"
    [GeneratedRegex(@"^\s*(\S+)\s+(\S+)\s+([A-Za-z0-9]{6}-[A-Za-z0-9]{6,11}-[A-Za-z0-9]{2,4})\s+(<[^>]*>)(.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-]+$")]
    private static partial Regex IdRegex();

    public async Task<(int Count, List<MailItem> Items)> GetQueueAsync(int max = 1000)
    {
        var r = await _ssh.RunAsync($"command -v exim >/dev/null || {{ echo NOEXIM; exit 0; }}; echo \"COUNT=$(exim -bpc)\"; exim -bp | head -n {max * 4}", TimeSpan.FromMinutes(1));
        r.EnsureOk("Mail kuyruğunu okuma");
        if (r.Output.StartsWith("NOEXIM")) throw new UserFacingException("Bu sunucuda Exim bulunamadı.");
        int count = 0;
        var items = new List<MailItem>();
        string? id = null, age = null, size = null, sender = null; bool frozen = false;
        var rcpts = new List<string>();
        void Flush()
        {
            if (id != null && items.Count < max)
                items.Add(new MailItem(id, age!, size!, sender!, string.Join(", ", rcpts), frozen));
            id = null; rcpts.Clear();
        }
        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("COUNT=")) { int.TryParse(line[6..], out count); continue; }
            var m = HeaderRegex().Match(line);
            if (m.Success)
            {
                Flush();
                age = m.Groups[1].Value; size = m.Groups[2].Value; id = m.Groups[3].Value;
                sender = m.Groups[4].Value.Trim('<', '>');
                if (sender.Length == 0) sender = "(sistem bildirimi)";
                frozen = m.Groups[5].Value.Contains("frozen");
            }
            else if (id != null && line.Trim().Length > 0)
            {
                var t = line.Trim();
                if (t.StartsWith("D ")) continue; // teslim edilmiş alıcı
                rcpts.Add(t);
            }
        }
        Flush();
        return (count, items);
    }

    private static string Ids(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Any(i => !IdRegex().IsMatch(i))) throw new UserFacingException("Geçersiz mail kimliği.");
        return string.Join(' ', list);
    }

    public async Task DeleteAsync(IEnumerable<string> ids) =>
        (await _ssh.RunAsync($"exim -Mrm {Ids(ids)}", TimeSpan.FromMinutes(2))).EnsureOk("Mail silme");

    public async Task<string> HeadersAsync(string id) =>
        (await _ssh.RunAsync($"exim -Mvh {Ids(new[] { id })}")).Combined;

    public const string DeleteAllCommand = "exim -bp | exiqgrep -i | xargs -r exim -Mrm; echo \"Kalan: $(exim -bpc)\"";
    public const string DeleteFrozenCommand = "exiqgrep -z -i | xargs -r exim -Mrm; echo \"Kalan: $(exim -bpc)\"";
    public const string FlushCommand = "exim -qff -v 2>&1 | tail -n 50; echo \"Kalan: $(exim -bpc)\"";
}

public sealed record UsageItem(string Name, long Bytes, string Path)
{
    public string SizeText => ServerController.Services.Bytes.Format(Bytes);
}

public sealed class DiskService
{
    private readonly SshSession _ssh;
    public DiskService(SshSession ssh) => _ssh = ssh;

    public async Task<List<UsageItem>> GetUserUsageAsync()
    {
        const string script = """
            for u in $(ls /usr/local/directadmin/data/users 2>/dev/null); do
              s=$(du -sb "/home/$u" 2>/dev/null | cut -f1); echo "$u|${s:-0}"
            done
            true
            """;
        var r = (await _ssh.RunAsync(script, TimeSpan.FromMinutes(10))).EnsureOk("Kullanıcı disk kullanımı");
        return Parse(r.Output, u => "/home/" + u);
    }

    public async Task<List<UsageItem>> GetFolderAsync(string path)
    {
        var r = await _ssh.RunAsync($"du -xb --max-depth=1 {Shell.Q(path)} 2>/dev/null | sort -nr | head -n 60 | awk -F'\\t' '{{print $2\"|\"$1}}'", TimeSpan.FromMinutes(10));
        var items = Parse(r.Output, p => p).Where(i => i.Path.TrimEnd('/') != path.TrimEnd('/')).ToList();
        foreach (var i in items.ToList())
            items[items.IndexOf(i)] = i with { Name = i.Path.Split('/').Last() };
        return items;
    }

    public async Task<List<UsageItem>> GetBigFilesAsync(int minMb)
    {
        var r = (await _ssh.RunAsync($"find /home /var /usr/local -xdev -type f -size +{minMb}M -printf '%p|%s\\n' 2>/dev/null | sort -t'|' -k2 -nr | head -n 100", TimeSpan.FromMinutes(10)));
        return Parse(r.Output, p => p).Select(i => i with { Name = i.Path.Split('/').Last() }).ToList();
    }

    public async Task DeleteFileAsync(string path)
    {
        if (!path.StartsWith('/') || path.Contains("/../") || path.Count(c => c == '/') < 3)
            throw new UserFacingException("Bu dosya güvenlik nedeniyle buradan silinemez.");
        (await _ssh.RunAsync($"rm -f -- {Shell.Q(path)}")).EnsureOk("Dosya silme");
    }

    private static List<UsageItem> Parse(string output, Func<string, string> path) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => { var i = l.LastIndexOf('|'); return i > 0 ? (l[..i], l[(i + 1)..]) : (l, "0"); })
            .Select(p => new UsageItem(p.Item1, long.TryParse(p.Item2.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0, path(p.Item1)))
            .OrderByDescending(i => i.Bytes)
            .ToList();
}

public sealed record LogSource(string Name, string Command, string? FollowCommand, string Info);

public static class LogSources
{
    private static LogSource File(string name, string path, string info) =>
        new(name, $"[ -f {path} ] && tail -n {{N}} {path} || echo 'Log dosyası bulunamadı: {path}'", $"tail -n 50 -F {path}", info);

    private static LogSource Journal(string name, string unit, string info) =>
        new(name, $"journalctl -u {unit} -n {{N}} --no-pager", $"journalctl -u {unit} -n 50 -f --no-pager", info);

    public static readonly LogSource[] All =
    {
        File("LiteSpeed hata logu", "/usr/local/lsws/logs/error.log", "Web sunucusu hataları"),
        File("LiteSpeed erişim logu", "/usr/local/lsws/logs/access.log", "Siteye gelen istekler"),
        File("Exim ana log", "/var/log/exim/mainlog", "Gönderilen / alınan e-postalar"),
        File("Exim reddedilenler", "/var/log/exim/rejectlog", "Reddedilen e-postalar"),
        File("Dovecot / mail girişleri", "/var/log/maillog", "E-posta kutusu girişleri"),
        File("CrowdSec logu", "/var/log/crowdsec.log", "CrowdSec'in tespit ettikleri"),
        File("CrowdSec güvenlik duvarı", "/var/log/crowdsec-firewall-bouncer.log", "Blok uygulayıcı"),
        File("SSH / güvenlik logu", "/var/log/secure", "SSH giriş denemeleri"),
        File("DirectAdmin hata logu", "/var/log/directadmin/error.log", "Panel hataları"),
        File("DirectAdmin giriş logu", "/var/log/directadmin/login.log", "Panel girişleri"),
        File("Sistem mesajları", "/var/log/messages", "Genel sistem logu"),
        Journal("MariaDB / MySQL", "mariadb", "Veritabanı servis logu"),
        File("Pure-FTPd", "/var/log/pureftpd.log", "FTP bağlantıları"),
    };
}
