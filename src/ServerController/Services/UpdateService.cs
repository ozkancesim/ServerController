using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ServerController.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string? Sha256, long Size, string PageUrl);

/// <summary>
/// GitHub Releases üzerinden güncelleme denetler, indirir ve kurar.
/// Çalışan exe kendi dosyalarının üzerine yazamadığı için kurulumu küçük bir PowerShell betiği yapar:
/// uygulama kapanınca yeni dosyaları kopyalar ("data" klasörüne dokunmaz) ve uygulamayı yeniden açar.
/// </summary>
public static partial class UpdateService
{
    public const string Owner = "ozkancesim";
    public const string Repo = "ServerController";
    private const string AssetName = "ServerController-win-x64.zip";

    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases/latest";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ServerController", Current.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Daha yeni bir sürüm varsa bilgisini döner, yoksa null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        if ((int)resp.StatusCode == 404)
            throw new UserFacingException("Güncelleme bilgisi alınamadı. GitHub deposu gizli olabilir veya henüz sürüm yayınlanmamış.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.Str("tag_name");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= Current) return null;

        var asset = root.Prop("assets").Items().FirstOrDefault(a => a.Str("name") == AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        var digest = asset.Str("digest");
        return new UpdateInfo(
            latest, tag, CleanNotes(root.Str("body")), asset.Str("browser_download_url"),
            digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null,
            asset.Long("size"), root.Str("html_url"));
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    /// <summary>Markdown sürüm notlarını düz metne yakın hale getirir.</summary>
    private static string CleanNotes(string md)
    {
        var lines = md.Replace("\r", "").Split('\n')
            .SkipWhile(l => !l.Contains("ile gelenler") && !l.Contains("Bu sürümde"))
            .Select(l => LinkRegex().Replace(l, "$1").Replace("**", "").Replace("`", "").TrimStart('#', ' '))
            .Where(l => !l.StartsWith("Ayrıntılar ve ekran görüntüleri"));
        var text = string.Join("\n", lines).Trim();
        if (text.Length == 0) text = LinkRegex().Replace(md, "$1").Replace("**", "");
        return text.Length > 1500 ? text[..1500] + "…" : text;
    }

    /// <summary>Uygulama klasörüne yazılabiliyor mu (Program Files gibi yerlerde güncelleme yapılamaz)?</summary>
    public static bool CanSelfUpdate(out string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "Otomatik güncelleme yalnızca Windows'ta çalışır.";
            return false;
        }
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".guncelleme-testi");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch
        {
            reason = "Uygulama klasörüne yazılamıyor (ör. Program Files içinde). Uygulamayı Belgeler gibi bir klasöre taşıyın veya yeni sürümü elle indirin.";
            return false;
        }
    }

    /// <summary>Zip'i indirir, SHA-256 özetini doğrular ve geçici bir klasöre çıkarır. Yeni sürüm klasörünü döner.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, Action<string> log, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "ServerController-guncelleme-" + info.Version);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, AssetName);

        log($"{info.Tag} indiriliyor ({Bytes.Format(info.Size)})…\n");
        using (var http = CreateClient())
        using (var resp = await http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int lastPct = -1;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                var pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct / 10 != lastPct / 10)
                {
                    lastPct = pct;
                    log($"  %{pct}  ({Bytes.Format(done)})\n");
                }
            }
        }

        if (info.Sha256 != null)
        {
            log("Dosya bütünlüğü doğrulanıyor (SHA-256)…\n");
            await using var fs = File.OpenRead(zip);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (hash != info.Sha256)
                throw new UserFacingException("İndirilen dosya doğrulanamadı (özet uyuşmuyor). Güncelleme iptal edildi; tekrar deneyin.");
            log("  ✓ Doğrulandı\n");
        }

        log("Arşiv açılıyor…\n");
        var extract = Path.Combine(work, "yeni");
        ZipFile.ExtractToDirectory(zip, extract);
        var exe = Directory.GetFiles(extract, "ServerController.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new UserFacingException("İndirilen pakette ServerController.exe bulunamadı.");
        log("  ✓ Hazır\n");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>Kurulum betiğini başlatır. Çağıran taraf hemen ardından uygulamayı kapatmalıdır.</summary>
    public static void LaunchInstaller(string newDir)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exe = Path.Combine(appDir, "ServerController.exe");
        var log = Path.Combine(Path.GetTempPath(), "ServerController-guncelleme.log");
        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $src = {{Q(newDir)}}
            $dst = {{Q(appDir)}}
            $exe = {{Q(exe)}}
            try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 60 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 700
            $ok = $false
            for ($i = 0; $i -lt 10 -and -not $ok; $i++) {
              robocopy $src $dst /E /XD data /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-File -FilePath {{Q(log)}} -Append
              if ($LASTEXITCODE -lt 8) { $ok = $true } else { Start-Sleep -Seconds 2 }
            }
            if (-not $ok) {
              Add-Type -AssemblyName PresentationFramework
              [System.Windows.MessageBox]::Show("Güncelleme dosyaları kopyalanamadı. Lütfen yeni sürümü GitHub'dan elle indirin.`n`nAyrıntılar: {{log}}", "Server Controller") | Out-Null
            }
            Start-Process -FilePath $exe
            """;
        var ps1 = Path.Combine(Path.GetTempPath(), "ServerController-guncelle.ps1");
        File.WriteAllText(ps1, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
