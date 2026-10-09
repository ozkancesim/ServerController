using System;
using System.Collections.Generic;

namespace ServerController.Models;

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    public string AllowlistName { get; set; } = "my_allowlist";
    public string? HostKeyFingerprint { get; set; }
    public string Notes { get; set; } = "";

    public string Display => string.IsNullOrWhiteSpace(Name) ? Host : Name;
    public string Address => $"{Username}@{Host}:{Port}";
}

public sealed class SavedCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Confirm { get; set; }
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Server { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Success { get; set; } = true;
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public int AutoLockMinutes { get; set; } = 15;
    public string DefaultBanDuration { get; set; } = "24h";
    public int DashboardRefreshSeconds { get; set; } = 15;
    public bool AutoCheckUpdates { get; set; } = true;
}

public sealed class VaultData
{
    public string UserName { get; set; } = "";
    public List<ServerProfile> Servers { get; set; } = new();
    public List<SavedCommand> Commands { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public string? LastServerId { get; set; }

    public static VaultData CreateDefault(string user) => new()
    {
        UserName = user,
        Commands = DefaultCommands.Create(),
    };
}

public static class DefaultCommands
{
    public static List<SavedCommand> Create() => new()
    {
        new() { Name = "CrowdSec durumu", Command = "systemctl status crowdsec --no-pager -l | head -n 20", Description = "CrowdSec servisinin çalışıp çalışmadığını gösterir." },
        new() { Name = "CrowdSec metrikleri", Command = "cscli metrics", Description = "CrowdSec'in kaç saldırı yakaladığına dair istatistikler." },
        new() { Name = "Disk doluluğu", Command = "df -h", Description = "Disklerin doluluk oranları." },
        new() { Name = "Bellek kullanımı", Command = "free -h", Description = "RAM ve swap kullanımı." },
        new() { Name = "En çok işlemci kullananlar", Command = "ps -eo pid,user,pcpu,pmem,comm --sort=-pcpu | head -n 15", Description = "Sunucuyu en çok yoran işlemler." },
        new() { Name = "Mail kuyruğu sayısı", Command = "exim -bpc", Description = "Gönderilmeyi bekleyen e-posta sayısı." },
        new() { Name = "LiteSpeed yeniden başlat (nazik)", Command = "/usr/local/lsws/bin/lswsctrl restart", Description = "LiteSpeed'i ziyaretçileri kesmeden yeniden başlatır.", Confirm = true },
        new() { Name = "Son SSH girişleri", Command = "last -n 20 -a", Description = "Sunucuya en son kimlerin giriş yaptığı." },
    };
}
