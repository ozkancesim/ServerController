using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace ServerController.Services;

public sealed class SystemStats
{
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string Kernel { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    public string Load { get; set; } = "";
    public int Cpus { get; set; }
    public double CpuPercent { get; set; }
    public long MemTotal { get; set; }
    public long MemAvailable { get; set; }
    public long SwapTotal { get; set; }
    public long SwapFree { get; set; }
    public List<DiskInfo> Disks { get; } = new();
    public List<ProcessInfo> Processes { get; } = new();
}

public sealed record DiskInfo(string Mount, long Size, long Used)
{
    public double Percent => Size > 0 ? Used * 100.0 / Size : 0;
    public string Text => $"{Bytes.Format(Used)} / {Bytes.Format(Size)}";
}

public sealed record ProcessInfo(string Pid, string User, double Cpu, double Mem, string Command);

public sealed record ServiceInfo(string Unit, string Name, string State, string SubState, string Description)
{
    public bool IsActive => State == "active";
    public bool IsFailed => State == "failed";
}

public sealed class SystemService
{
    private readonly SshSession _ssh;
    public SystemService(SshSession ssh) => _ssh = ssh;

    private const string StatsScript = """
        echo "HOST=$(hostname)"
        echo "OS=$( (. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME") )"
        echo "KERNEL=$(uname -r)"
        echo "UPTIME=$(cut -d' ' -f1 /proc/uptime)"
        echo "LOAD=$(cut -d' ' -f1-3 /proc/loadavg)"
        echo "CPUS=$(nproc)"
        echo "CPU1=$(head -n1 /proc/stat)"; sleep 1; echo "CPU2=$(head -n1 /proc/stat)"
        awk '/^(MemTotal|MemAvailable|SwapTotal|SwapFree):/ {gsub(":","",$1); print "MEM_"$1"="$2}' /proc/meminfo
        df -P -B1 -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null | awk 'NR>1 {print "DISK="$6"|"$2"|"$3}'
        ps -eo pid=,user=,pcpu=,pmem=,comm= --sort=-pcpu | head -n 12 | awk '{print "PROC="$1"|"$2"|"$3"|"$4"|"$5}'
        """;

    public async Task<SystemStats> GetStatsAsync()
    {
        var r = (await _ssh.RunAsync(StatsScript, TimeSpan.FromSeconds(30))).EnsureOk("Sunucu bilgilerini alma");
        var s = new SystemStats();
        string? cpu1 = null, cpu2 = null;
        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq];
            var v = line[(eq + 1)..];
            switch (k)
            {
                case "HOST": s.Hostname = v; break;
                case "OS": s.Os = v; break;
                case "KERNEL": s.Kernel = v; break;
                case "UPTIME": s.Uptime = TimeSpan.FromSeconds(D(v)); break;
                case "LOAD": s.Load = v; break;
                case "CPUS": s.Cpus = (int)D(v); break;
                case "CPU1": cpu1 = v; break;
                case "CPU2": cpu2 = v; break;
                case "MEM_MemTotal": s.MemTotal = (long)D(v) * 1024; break;
                case "MEM_MemAvailable": s.MemAvailable = (long)D(v) * 1024; break;
                case "MEM_SwapTotal": s.SwapTotal = (long)D(v) * 1024; break;
                case "MEM_SwapFree": s.SwapFree = (long)D(v) * 1024; break;
                case "DISK":
                    var d = v.Split('|');
                    if (d.Length == 3 && !s.Disks.Any(x => x.Mount == d[0]))
                        s.Disks.Add(new DiskInfo(d[0], (long)D(d[1]), (long)D(d[2])));
                    break;
                case "PROC":
                    var p = v.Split('|');
                    if (p.Length >= 5) s.Processes.Add(new ProcessInfo(p[0], p[1], D(p[2]), D(p[3]), p[4]));
                    break;
            }
        }
        s.CpuPercent = CpuUsage(cpu1, cpu2);
        s.Disks.RemoveAll(x => x.Size < 1L << 30 && x.Mount != "/");
        return s;
    }

    private static double CpuUsage(string? a, string? b)
    {
        static long[] Parse(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
        if (a == null || b == null) return 0;
        var x = Parse(a); var y = Parse(b);
        if (x.Length < 4 || y.Length < 4) return 0;
        long idle(long[] v) => v[3] + (v.Length > 4 ? v[4] : 0);
        var total = y.Sum() - x.Sum();
        var idleD = idle(y) - idle(x);
        return total <= 0 ? 0 : Math.Clamp((total - idleD) * 100.0 / total, 0, 100);
    }

    private static double D(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    // ---------- Servisler ----------

    private static readonly (string Unit, string Name)[] KnownServices =
    {
        ("lsws", "LiteSpeed Web Sunucusu"),
        ("litespeed", "LiteSpeed Web Sunucusu"),
        ("openlitespeed", "OpenLiteSpeed"),
        ("httpd", "Apache Web Sunucusu"),
        ("nginx", "Nginx"),
        ("mariadb", "MariaDB Veritabanı"),
        ("mysqld", "MySQL Veritabanı"),
        ("mysql", "MySQL Veritabanı"),
        ("exim", "Exim (E-posta gönderimi)"),
        ("dovecot", "Dovecot (E-posta kutuları)"),
        ("pure-ftpd", "Pure-FTPd (FTP)"),
        ("proftpd", "ProFTPD (FTP)"),
        ("named", "DNS Sunucusu (BIND)"),
        ("directadmin", "DirectAdmin Paneli"),
        ("crowdsec", "CrowdSec Güvenlik"),
        ("crowdsec-firewall-bouncer", "CrowdSec Güvenlik Duvarı"),
        ("lfd", "CSF / LFD Güvenlik Duvarı"),
        ("redis", "Redis Önbellek"),
        ("memcached", "Memcached Önbellek"),
        ("crond", "Zamanlanmış Görevler (cron)"),
        ("cron", "Zamanlanmış Görevler (cron)"),
        ("sshd", "SSH Sunucusu"),
    };

    public async Task<List<ServiceInfo>> GetServicesAsync()
    {
        var units = string.Join(" ", KnownServices.Select(k => k.Unit));
        var script = $$"""
            for u in {{units}} $(systemctl list-unit-files --no-legend 'php-fpm*.service' 2>/dev/null | awk '{sub(".service","",$1); print $1}'); do
              if systemctl cat "$u.service" >/dev/null 2>&1; then
                echo "$u|$(systemctl show -p Id --value "$u")|$(systemctl show -p ActiveState --value "$u")|$(systemctl show -p SubState --value "$u")|$(systemctl show -p Description --value "$u")"
              fi
            done
            """;
        var r = (await _ssh.RunAsync(script, TimeSpan.FromSeconds(45))).EnsureOk("Servis listesini alma");
        var list = new List<ServiceInfo>();
        var seen = new HashSet<string>();
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('|');
            if (p.Length < 5 || !seen.Add(p[1])) continue;
            var name = KnownServices.FirstOrDefault(k => k.Unit == p[0]).Name
                       ?? (p[0].StartsWith("php-fpm") ? "PHP-FPM " + p[0]["php-fpm".Length..] : p[0]);
            list.Add(new ServiceInfo(p[0], name, p[2], p[3], p[4]));
        }
        return list;
    }

    public async Task ServiceActionAsync(string unit, string action)
    {
        if (action is not ("restart" or "start" or "stop" or "reload")) throw new ArgumentException(action);
        (await _ssh.RunAsync($"systemctl {action} {Shell.Q(unit)}", TimeSpan.FromMinutes(3))).EnsureOk($"{unit} {action}");
    }

    public async Task<string> ServiceStatusAsync(string unit)
    {
        var r = await _ssh.RunAsync($"systemctl status {Shell.Q(unit)} --no-pager -l -n 40");
        return r.Combined;
    }

    public async Task<string> PurgeLiteSpeedCacheAsync()
    {
        const string script = """
            n=0
            for d in /home/lscache /usr/local/lsws/cachedata /var/lscache /tmp/lshttpd/cache; do
              if [ -d "$d" ]; then find "$d" -mindepth 1 -delete 2>/dev/null; echo "Temizlendi: $d"; n=$((n+1)); fi
            done
            for f in /home/*/lscache; do [ -d "$f" ] && find "$f" -mindepth 1 -delete 2>/dev/null && echo "Temizlendi: $f" && n=$((n+1)); done
            [ -x /usr/local/lsws/bin/lswsctrl ] && /usr/local/lsws/bin/lswsctrl restart >/dev/null 2>&1 && echo "LiteSpeed nazikçe yeniden başlatıldı."
            [ "$n" = 0 ] && echo "Önbellek klasörü bulunamadı (LiteSpeed yine de yenilendi)."
            true
            """;
        return (await _ssh.RunAsync(script, TimeSpan.FromMinutes(5))).EnsureOk("Önbellek temizleme").Output.Trim();
    }

    public Task RebootAsync() =>
        _ssh.RunAsync("nohup sh -c 'sleep 2; systemctl reboot' >/dev/null 2>&1 &", TimeSpan.FromSeconds(20));
}

public static class Bytes
{
    public static string Format(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }
}
