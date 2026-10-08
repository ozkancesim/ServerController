using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Services;

namespace ServerController.ViewModels;

public sealed record BuildAction(string Title, string Command, bool Confirm, string Info);

public sealed partial class SoftwareViewModel : PageViewModel
{
    public SoftwareViewModel(AppState app) : base(app)
    {
        foreach (var a in CustomBuildService.Actions) BuildActions.Add(new BuildAction(a.Title, a.Command, a.Confirm, a.Info));
    }

    public override string Title => "Yazılım & Eklentiler";
    public override string Icon => "🧩";
    public override string Subtitle => "PHP, CustomBuild, DirectAdmin eklentileri ve WordPress";

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _daVersion = "-";
    [ObservableProperty] private string _webServer = "-";
    [ObservableProperty] private string? _cbError;

    public ObservableCollection<PhpSlot> PhpSlots { get; } = new();
    public ObservableCollection<PhpExtension> Extensions { get; } = new();
    public ObservableCollection<BuildAction> BuildActions { get; } = new();
    public ObservableCollection<DaPlugin> Plugins { get; } = new();

    // WordPress
    public ObservableCollection<WpSite> Sites { get; } = new();
    public ObservableCollection<WpPlugin> WpPlugins { get; } = new();
    [ObservableProperty] private WpSite? _selectedSite;
    [ObservableProperty] private bool _hasWpCli = true;
    [ObservableProperty] private string _wpCoreVersion = "";
    [ObservableProperty] private string _wpSummary = "";
    [ObservableProperty] private bool _wpLoaded;

    partial void OnSelectedSiteChanged(WpSite? value)
    {
        if (value != null) _ = Busy(() => LoadWpPluginsAsync(value), "Eklentiler alınıyor…");
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 2 && !WpLoaded && App.Session != null) _ = Busy(LoadWordPressAsync, "WordPress siteleri aranıyor…");
    }

    public override Task RefreshAsync() => Busy(async () =>
    {
        WpLoaded = false;
        Replace(Sites, Array.Empty<WpSite>());
        Replace(WpPlugins, Array.Empty<WpPlugin>());
        await LoadCustomBuildAsync();
        try { Replace(Plugins, await App.CustomBuild.GetPluginsAsync()); } catch { Replace(Plugins, Array.Empty<DaPlugin>()); }
        if (SelectedTab == 2) await LoadWordPressAsync();
    }, "Yazılım bilgileri alınıyor…");

    private async Task LoadCustomBuildAsync()
    {
        try
        {
            var info = await App.CustomBuild.GetInfoAsync();
            CbError = null;
            DaVersion = info.DaVersion.Length > 0 ? info.DaVersion : "?";
            WebServer = info.WebServer;
            Replace(PhpSlots, info.Php);
            Replace(Extensions, info.Extensions);
        }
        catch (UserFacingException ex)
        {
            CbError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunBuildActionAsync(BuildAction a)
    {
        if (a.Confirm && !await App.Dialogs.ConfirmAsync(a.Title, a.Info + "\n\nBu işlem sırasında ilgili servisler kısa süreliğine yeniden başlatılabilir. Devam edilsin mi?", "Başlat"))
            return;
        await App.RunInConsoleAsync(a.Title, a.Command, "CustomBuild: " + a.Title);
        if (a.Confirm) await Busy(LoadCustomBuildAsync, "Yenileniyor…");
    }

    [RelayCommand]
    private async Task ChangePhpAsync(PhpSlot slot)
    {
        var form = new FormDialogViewModel(
            FormField.ChoiceField("ver", "PHP sürümü", CustomBuildService.PhpVersions.Select(v => new ChoiceOption("PHP " + v, v)), slot.Version),
            FormField.ChoiceField("mode", "Çalışma modu", CustomBuildService.PhpModes.Select(m => new ChoiceOption(m, m)), slot.Mode,
                "LiteSpeed kullanıyorsanız 'lsphp' önerilir."))
        {
            Title = $"PHP {slot.Index}. slot",
            Message = "PHP yeniden derlenecek. Bu işlem 10-30 dakika sürebilir; bu sürede siteler çalışmaya devam eder.",
            ConfirmText = "Kur / Değiştir",
        };
        if (!await App.Dialogs.FormAsync(form)) return;
        await App.RunInConsoleAsync($"PHP {form.Choice("ver")} kuruluyor",
            CustomBuildService.SetPhpVersionCommand(slot.Index, form.Choice("ver"), form.Choice("mode")),
            $"PHP slot {slot.Index} → {form.Choice("ver")} ({form.Choice("mode")})");
        await Busy(LoadCustomBuildAsync, "Yenileniyor…");
    }

    [RelayCommand]
    private async Task AddPhpSlotAsync()
    {
        var free = Enumerable.Range(1, 4).FirstOrDefault(i => PhpSlots.All(s => s.Index != i));
        if (free == 0) { App.Toasts.Info("En fazla 4 PHP sürümü kurulabilir."); return; }
        await ChangePhpAsync(new PhpSlot(free, "8.3", PhpSlots.FirstOrDefault()?.Mode ?? "lsphp"));
    }

    [RelayCommand]
    private async Task RemovePhpSlotAsync(PhpSlot slot)
    {
        if (slot.Index == 1) { App.Toasts.Error("1. PHP slotu varsayılan sürümdür, kaldırılamaz. Sürümünü değiştirebilirsiniz."); return; }
        if (!await App.Dialogs.ConfirmAsync("PHP sürümünü kaldır", $"PHP {slot.Version} ({slot.Index}. slot) kaldırılsın mı?\n\nBu sürümü kullanan siteler varsayılan PHP sürümüne geçer.", "Kaldır", danger: true))
            return;
        await App.RunInConsoleAsync($"PHP {slot.Version} kaldırılıyor", CustomBuildService.RemovePhpSlotCommand(slot.Index), $"PHP slot {slot.Index} kaldırıldı");
        await Busy(LoadCustomBuildAsync, "Yenileniyor…");
    }

    [RelayCommand]
    private async Task ToggleExtensionAsync(PhpExtension ext)
    {
        var enable = !ext.Enabled;
        if (!await App.Dialogs.ConfirmAsync($"PHP eklentisi: {ext.Name}",
                enable ? $"{ext.Name} eklentisi tüm PHP sürümleri için kurulacak. Birkaç dakika sürebilir." : $"{ext.Name} eklentisi kaldırılacak. Bu eklentiye ihtiyaç duyan siteler bozulabilir.",
                enable ? "Kur" : "Kaldır", danger: !enable))
            return;
        await App.RunInConsoleAsync($"{ext.Name} {(enable ? "kuruluyor" : "kaldırılıyor")}",
            CustomBuildService.ExtensionCommand(ext.Name, enable), $"PHP eklentisi {ext.Name} {(enable ? "kuruldu" : "kaldırıldı")}");
        await Busy(LoadCustomBuildAsync, "Yenileniyor…");
    }

    [RelayCommand]
    private async Task TogglePluginAsync(DaPlugin p)
    {
        var activate = !p.Active;
        if (!await App.Dialogs.ConfirmAsync(p.Name, $"{p.Name} eklentisi {(activate ? "aktifleştirilsin" : "devre dışı bırakılsın")} mı?", "Evet"))
            return;
        await Busy(async () =>
        {
            await App.CustomBuild.SetPluginActiveAsync(p.Id, activate);
            App.Log("DirectAdmin eklentisi", $"{p.Name} {(activate ? "aktif" : "pasif")}");
            Replace(Plugins, await App.CustomBuild.GetPluginsAsync());
        }, "Uygulanıyor…");
    }

    // ---------------- WordPress ----------------

    private async Task LoadWordPressAsync()
    {
        HasWpCli = await App.WordPress.HasWpCliAsync();
        var sites = await App.WordPress.FindSitesAsync();
        Replace(Sites, sites);
        WpSummary = sites.Count == 0 ? "Sunucuda WordPress sitesi bulunamadı." : $"{sites.Count} WordPress sitesi bulundu";
        WpLoaded = true;
    }

    private async Task LoadWpPluginsAsync(WpSite site)
    {
        if (!HasWpCli) return;
        WpCoreVersion = "WordPress " + await App.WordPress.GetCoreVersionAsync(site);
        Replace(WpPlugins, await App.WordPress.GetPluginsAsync(site));
    }

    [RelayCommand]
    private Task RefreshWordPressAsync() => Busy(async () =>
    {
        await LoadWordPressAsync();
        if (SelectedSite != null) await LoadWpPluginsAsync(SelectedSite);
    }, "WordPress siteleri aranıyor…");

    [RelayCommand]
    private async Task InstallWpCliAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("WP-CLI kur", "WordPress yönetimi için resmi WP-CLI aracı sunucuya kurulacak (/usr/local/bin/wp).", "Kur"))
            return;
        await App.RunInConsoleAsync("WP-CLI kuruluyor", WordPressService.InstallWpCliCommand);
        await Busy(LoadWordPressAsync, "Kontrol ediliyor…");
    }

    private WpSite? RequireSite()
    {
        if (SelectedSite == null) App.Toasts.Info("Önce soldan bir WordPress sitesi seçin.");
        return SelectedSite;
    }

    private async Task WpRunAsync(string title, string command, string log)
    {
        var site = SelectedSite!;
        await App.RunInConsoleAsync(title, command, $"WordPress {site.Display}: {log}");
        await Busy(() => LoadWpPluginsAsync(site), "Yenileniyor…");
    }

    [RelayCommand]
    private async Task WpInstallAsync()
    {
        var site = RequireSite();
        if (site == null) return;
        var form = new FormDialogViewModel(
            FormField.TextField("slug", "Eklenti adı (slug) veya arama kelimesi", "", "Örn: litespeed-cache, wordfence, yoast",
                "WordPress.org adresindeki adını yazın. Emin değilseniz önce 'Ara' seçeneğini işaretleyin."),
            FormField.CheckField("search", "Önce WordPress.org'da ara", false))
        {
            Title = $"Eklenti kur — {site.Display}",
            ConfirmText = "Devam",
            Validate = f => f.Text("slug").Length < 2 ? "Bir eklenti adı yazın." : null,
        };
        if (!await App.Dialogs.FormAsync(form)) return;
        var slug = form.Text("slug");
        if (form.Checked("search"))
        {
            System.Collections.Generic.List<WpSearchResult> results = new();
            if (!await Busy(async () => results = await App.WordPress.SearchAsync(site, slug), "Aranıyor…")) return;
            if (results.Count == 0) { App.Toasts.Info("Sonuç bulunamadı."); return; }
            var pick = new FormDialogViewModel(FormField.ChoiceField("p", "Kurulacak eklenti",
                results.Select(r => new ChoiceOption($"{r.Name}  ({r.Slug} · {r.Installs} kurulum)", r.Slug))))
            {
                Title = "Arama sonuçları",
                ConfirmText = "Kur ve aktifleştir",
            };
            if (!await App.Dialogs.FormAsync(pick)) return;
            slug = pick.Choice("p");
        }
        string cmd;
        try { cmd = WordPressService.PluginCommand(site, "install", slug); }
        catch (UserFacingException ex) { App.Toasts.Error(ex.Message); return; }
        await WpRunAsync($"{slug} kuruluyor", cmd, $"{slug} kuruldu");
    }

    [RelayCommand]
    private async Task WpToggleAsync(WpPlugin p)
    {
        var site = RequireSite();
        if (site == null) return;
        var action = p.IsActive ? "deactivate" : "activate";
        await WpRunAsync($"{p.Name} {(p.IsActive ? "devre dışı bırakılıyor" : "aktifleştiriliyor")}",
            WordPressService.PluginCommand(site, action, p.Name), $"{p.Name} {(p.IsActive ? "pasif" : "aktif")}");
    }

    [RelayCommand]
    private async Task WpUpdateAsync(WpPlugin p)
    {
        var site = RequireSite();
        if (site == null) return;
        await WpRunAsync($"{p.Name} güncelleniyor", WordPressService.PluginCommand(site, "update", p.Name), $"{p.Name} güncellendi");
    }

    [RelayCommand]
    private async Task WpDeleteAsync(WpPlugin p)
    {
        var site = RequireSite();
        if (site == null) return;
        if (!await App.Dialogs.ConfirmAsync("Eklentiyi sil", $"{p.Title} ({p.Name}) eklentisi {site.Display} sitesinden tamamen silinsin mi?", "Sil", danger: true))
            return;
        await WpRunAsync($"{p.Name} siliniyor", WordPressService.PluginCommand(site, "delete", p.Name), $"{p.Name} silindi");
    }

    [RelayCommand]
    private async Task WpUpdateAllAsync()
    {
        var site = RequireSite();
        if (site == null) return;
        if (!await App.Dialogs.ConfirmAsync("Tüm eklentileri güncelle", $"{site.Display} sitesindeki tüm eklentiler güncellenecek.", "Güncelle"))
            return;
        await WpRunAsync("Tüm eklentiler güncelleniyor", WordPressService.UpdateAllCommand(site), "tüm eklentiler güncellendi");
    }

    [RelayCommand]
    private async Task WpCoreUpdateAsync()
    {
        var site = RequireSite();
        if (site == null) return;
        if (!await App.Dialogs.ConfirmAsync("WordPress'i güncelle", $"{site.Display} sitesinin WordPress çekirdeği en son sürüme güncellenecek. Öncesinde yedek almanız önerilir.", "Güncelle"))
            return;
        await WpRunAsync("WordPress güncelleniyor", WordPressService.CoreUpdateCommand(site), "çekirdek güncellendi");
    }

    [RelayCommand]
    private async Task WpCacheFlushAsync()
    {
        var site = RequireSite();
        if (site == null) return;
        await WpRunAsync("Önbellek temizleniyor", WordPressService.CacheFlushCommand(site), "önbellek temizlendi");
    }
}
