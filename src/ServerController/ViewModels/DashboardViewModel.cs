using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Services;

namespace ServerController.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private bool _statsBusy;

    public DashboardViewModel(AppState app, MainViewModel main) : base(app)
    {
        _main = main;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(5, app.Settings.DashboardRefreshSeconds)), DispatcherPriority.Background,
            async (_, _) => await LoadStatsAsync(silent: true));
    }

    public override string Title => "Gösterge Paneli";
    public override string Icon => "📊";
    public override string Subtitle => "Sunucunuzun anlık durumu";

    [ObservableProperty] private string _hostname = "-";
    [ObservableProperty] private string _os = "-";
    [ObservableProperty] private string _kernel = "-";
    [ObservableProperty] private string _uptime = "-";
    [ObservableProperty] private string _load = "-";
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private string _cpuText = "-";
    [ObservableProperty] private double _mem;
    [ObservableProperty] private string _memText = "-";
    [ObservableProperty] private double _swap;
    [ObservableProperty] private string _swapText = "-";
    [ObservableProperty] private string _blockedCount = "-";
    [ObservableProperty] private string _allowCount = "-";
    [ObservableProperty] private string _alerts24h = "-";
    [ObservableProperty] private string _crowdSecVersion = "-";
    [ObservableProperty] private string _servicesSummary = "-";
    [ObservableProperty] private bool _hasFailedServices;
    [ObservableProperty] private bool _hasData;

    public ObservableCollection<DiskInfo> Disks { get; } = new();
    public ObservableCollection<ProcessInfo> Processes { get; } = new();
    public ObservableCollection<ServiceInfo> Services { get; } = new();

    public override async Task OnActivatedAsync()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(5, App.Settings.DashboardRefreshSeconds));
        _timer.Start();
        await base.OnActivatedAsync();
    }

    public override void OnDeactivated() => _timer.Stop();

    public override async Task RefreshAsync()
    {
        if (App.Session == null) return;
        await LoadStatsAsync(silent: false);
        await Busy(async () =>
        {
            var services = await App.System.GetServicesAsync();
            Replace(Services, services);
            var failed = services.Count(s => !s.IsActive);
            HasFailedServices = failed > 0;
            ServicesSummary = failed == 0 ? $"{services.Count} servisin tamamı çalışıyor" : $"{failed} servis çalışmıyor!";

            try
            {
                var cs = App.CrowdSec;
                CrowdSecVersion = await cs.GetVersionAsync();
                var decisions = await cs.GetDecisionsAsync();
                BlockedCount = decisions.Count.ToString();
                var alerts = await cs.GetAlertsAsync(limit: 500);
                Alerts24h = alerts.Count(a => a.CreatedAt > DateTime.UtcNow.AddDays(-1)).ToString();
                var name = App.CurrentServer?.AllowlistName;
                if (!string.IsNullOrEmpty(name))
                {
                    try { AllowCount = (await cs.GetAllowlistAsync(name)).Count.ToString(); }
                    catch { AllowCount = "-"; }
                }
            }
            catch (UserFacingException ex)
            {
                CrowdSecVersion = ex.Message;
            }
        }, "Sunucu bilgileri alınıyor…");
    }

    private async Task LoadStatsAsync(bool silent)
    {
        if (_statsBusy || App.Session == null || (silent && !App.IsConnected)) return;
        _statsBusy = true;
        try
        {
            var s = await App.System.GetStatsAsync();
            Hostname = s.Hostname;
            Os = s.Os;
            Kernel = s.Kernel;
            Uptime = s.Uptime.TotalDays >= 1 ? $"{(int)s.Uptime.TotalDays} gün {s.Uptime.Hours} saat" : $"{s.Uptime.Hours} saat {s.Uptime.Minutes} dk";
            Load = s.Load;
            Cpu = s.CpuPercent;
            CpuText = $"%{s.CpuPercent:0} · {s.Cpus} çekirdek";
            var used = s.MemTotal - s.MemAvailable;
            Mem = s.MemTotal > 0 ? used * 100.0 / s.MemTotal : 0;
            MemText = $"{Bytes.Format(used)} / {Bytes.Format(s.MemTotal)}";
            var swUsed = s.SwapTotal - s.SwapFree;
            Swap = s.SwapTotal > 0 ? swUsed * 100.0 / s.SwapTotal : 0;
            SwapText = s.SwapTotal > 0 ? $"{Bytes.Format(swUsed)} / {Bytes.Format(s.SwapTotal)}" : "Swap yok";
            Replace(Disks, s.Disks);
            Replace(Processes, s.Processes);
            HasData = true;
        }
        catch (Exception ex)
        {
            if (!silent) App.Toasts.Error(ErrorText.From(ex), ex);
        }
        finally
        {
            _statsBusy = false;
        }
    }

    [RelayCommand] private void GoIps() => _main.Navigate<CrowdSecViewModel>();
    [RelayCommand] private void GoServices() => _main.Navigate<ServicesViewModel>();
}

public sealed partial class ServicesViewModel : PageViewModel
{
    public ServicesViewModel(AppState app) : base(app) { }

    public override string Title => "Servisler";
    public override string Icon => "🔄";
    public override string Subtitle => "Web, veritabanı, e-posta ve diğer servisleri yönetin";

    public ObservableCollection<ServiceInfo> Services { get; } = new();

    public override Task RefreshAsync() => Busy(async () =>
    {
        Replace(Services, await App.System.GetServicesAsync());
    }, "Servisler kontrol ediliyor…");

    [RelayCommand] private Task RestartAsync(ServiceInfo s) => ActionAsync(s, "restart", "yeniden başlatılsın mı", "yeniden başlatıldı");
    [RelayCommand] private Task StartAsync(ServiceInfo s) => ActionAsync(s, "start", "başlatılsın mı", "başlatıldı");
    [RelayCommand] private Task StopAsync(ServiceInfo s) => ActionAsync(s, "stop", "DURDURULSUN mu", "durduruldu");

    private async Task ActionAsync(ServiceInfo s, string action, string question, string done)
    {
        var warn = s.Unit is "sshd" && action == "stop"
            ? "\n\n⚠️ SSH'ı durdurursanız bu uygulama dahil hiçbir şekilde sunucuya bağlanamazsınız!"
            : s.Unit is "crowdsec" or "crowdsec-firewall-bouncer" && action == "stop"
                ? "\n\nDurdurulursa sunucunuz saldırılara karşı korumasız kalır."
                : "";
        if (!await App.Dialogs.ConfirmAsync(s.Name, $"{s.Name} ({s.Unit}) {question}?{warn}", "Evet", danger: action == "stop"))
            return;
        await Busy(async () =>
        {
            try
            {
                await App.System.ServiceActionAsync(s.Unit, action);
                App.Log("Servis", $"{s.Name} {done}");
                App.Toasts.Success($"{s.Name} {done}.");
            }
            catch (Exception ex)
            {
                App.Log("Servis", $"{s.Name} {action} hatası", false);
                App.Toasts.Error(ErrorText.From(ex), ex);
            }
            Replace(Services, await App.System.GetServicesAsync());
        }, $"{s.Name} işleniyor…");
    }

    [RelayCommand]
    private async Task StatusAsync(ServiceInfo s)
    {
        string text = "";
        if (await Busy(async () => text = await App.System.ServiceStatusAsync(s.Unit), "Durum alınıyor…"))
            await App.Dialogs.ShowTextAsync($"{s.Name} durumu", text);
    }

    [RelayCommand]
    private async Task PurgeCacheAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("LiteSpeed önbelleğini temizle",
                "Tüm sitelerin LiteSpeed önbelleği silinecek ve LiteSpeed nazikçe yeniden başlatılacak. Siteler birkaç dakika biraz daha yavaş açılabilir.\n\nDevam edilsin mi?", "Temizle"))
            return;
        string text = "";
        if (await Busy(async () => text = await App.System.PurgeLiteSpeedCacheAsync(), "Önbellek temizleniyor…"))
        {
            App.Log("Önbellek", "LiteSpeed önbelleği temizlendi");
            await App.Dialogs.AlertAsync("Önbellek temizlendi", text);
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("Sunucuyu yeniden başlat",
                "Sunucu yeniden başlatılacak. Tüm siteler ve e-postalar 1-5 dakika erişilemez olacak.\n\nEmin misiniz?", "Yeniden başlat", danger: true))
            return;
        var form = new FormDialogViewModel(FormField.TextField("c", "Onaylamak için YENIDEN BASLAT yazın"))
        {
            Title = "Son onay",
            ConfirmText = "Sunucuyu yeniden başlat",
            IsDanger = true,
            Validate = f => f.Text("c").ToUpperInvariant() is "YENIDEN BASLAT" or "YENİDEN BAŞLAT" ? null : "Lütfen tam olarak YENIDEN BASLAT yazın.",
        };
        if (!await App.Dialogs.FormAsync(form)) return;
        if (await Busy(() => App.System.RebootAsync(), "Yeniden başlatma komutu gönderiliyor…"))
        {
            App.Log("Sunucu", "Sunucu yeniden başlatıldı");
            App.Toasts.Success("Sunucu yeniden başlatılıyor. Birkaç dakika sonra tekrar bağlanın.");
        }
    }
}
