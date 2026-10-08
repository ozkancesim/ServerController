using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Services;

namespace ServerController.ViewModels;

public sealed partial class CrowdSecViewModel : PageViewModel
{
    public CrowdSecViewModel(AppState app) : base(app) { }

    public override string Title => "IP Yönetimi";
    public override string Icon => "🛡️";
    public override string Subtitle => "CrowdSec blokları, beyaz liste ve saldırı uyarıları";

    private List<DecisionRow> _allDecisions = new();
    private List<AllowRow> _allAllow = new();
    private List<AlertInfo> _allAlerts = new();

    public ObservableCollection<DecisionRow> Decisions { get; } = new();
    public ObservableCollection<AllowRow> Allow { get; } = new();
    public ObservableCollection<AlertInfo> Alerts { get; } = new();
    public ObservableCollection<string> AllowlistNames { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _decisionFilter = "";
    [ObservableProperty] private string _allowFilter = "";
    [ObservableProperty] private string _alertFilter = "";
    [ObservableProperty] private string _decisionSummary = "";
    [ObservableProperty] private string _allowSummary = "";
    [ObservableProperty] private string? _selectedAllowlist;
    [ObservableProperty] private string? _allowError;
    [ObservableProperty] private bool _allChecked;
    [ObservableProperty] private bool _allAllowChecked;

    // IP sorgulama
    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private bool _hasQueryResult;
    [ObservableProperty] private string _queryIp = "";
    [ObservableProperty] private bool _queryBlocked;
    [ObservableProperty] private bool _queryAllowed;
    [ObservableProperty] private bool _queryClean;
    [ObservableProperty] private string _queryTitle = "";
    [ObservableProperty] private string _queryDetail = "";
    [ObservableProperty] private bool _queryIsMine;
    public ObservableCollection<DecisionInfo> QueryDecisions { get; } = new();
    public ObservableCollection<AlertInfo> QueryAlerts { get; } = new();

    public string MyIpText => App.MyIp is { } ip ? $"Sizin IP adresiniz: {ip}" : "IP adresiniz bağlandıktan sonra görünür";

    partial void OnDecisionFilterChanged(string value) => ApplyDecisionFilter();
    partial void OnAllowFilterChanged(string value) => ApplyAllowFilter();
    partial void OnAlertFilterChanged(string value) => ApplyAlertFilter();

    partial void OnAllCheckedChanged(bool value)
    {
        foreach (var d in Decisions) d.IsChecked = value;
    }

    partial void OnAllAllowCheckedChanged(bool value)
    {
        foreach (var d in Allow) d.IsChecked = value;
    }

    partial void OnSelectedAllowlistChanged(string? value)
    {
        if (value != null && App.CurrentServer is { } s && s.AllowlistName != value)
        {
            s.AllowlistName = value;
            App.Vault.Save();
        }
        if (value != null && !IsBusy) _ = Busy(LoadAllowAsync, "Beyaz liste yükleniyor…");
    }

    public override Task RefreshAsync() => Busy(async () =>
    {
        OnPropertyChanged(nameof(MyIpText));
        await LoadDecisionsAsync();
        await LoadAllowlistNamesAsync();
        await LoadAllowAsync();
        await LoadAlertsAsync();
        OnPropertyChanged(nameof(MyIpText));
    }, "CrowdSec verileri alınıyor…");

    private static bool Match(string filter, params string[] fields) =>
        filter.Length == 0 || fields.Any(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));

    // ---------------- Bloklananlar ----------------

    private async Task LoadDecisionsAsync()
    {
        var list = await App.CrowdSec.GetDecisionsAsync();
        _allDecisions = list.Select(d => new DecisionRow(d)).ToList();
        AllChecked = false;
        ApplyDecisionFilter();
    }

    private void ApplyDecisionFilter()
    {
        var f = DecisionFilter.Trim();
        Replace(Decisions, _allDecisions.Where(d => Match(f, d.Item.Value, d.Item.Country, d.Item.ScenarioText, d.Item.AsName, d.Item.Scenario)));
        DecisionSummary = _allDecisions.Count == 0
            ? "Şu anda bloklu IP yok 🎉"
            : f.Length > 0 ? $"{Decisions.Count} / {_allDecisions.Count} bloklu IP gösteriliyor" : $"Toplam {_allDecisions.Count} bloklu IP";
    }

    [RelayCommand]
    private Task RefreshDecisionsAsync() => Busy(LoadDecisionsAsync, "Blok listesi alınıyor…");

    [RelayCommand]
    private async Task UnblockAsync(DecisionRow row)
    {
        var d = row.Item;
        if (!await App.Dialogs.ConfirmAsync("Bloğu kaldır", $"{d.Value} adresinin bloğu kaldırılsın mı?\n\nSebep: {d.ScenarioText}", "Bloğu kaldır"))
            return;
        await Busy(async () =>
        {
            await App.CrowdSec.DeleteDecisionAsync(d.Value);
            App.Log("Blok kaldırıldı", d.Value);
            App.Toasts.Success($"{d.Value} bloğu kaldırıldı.");
            await LoadDecisionsAsync();
        }, "Blok kaldırılıyor…");
    }

    [RelayCommand]
    private async Task UnblockSelectedAsync()
    {
        var sel = Decisions.Where(d => d.IsChecked).Select(d => d.Item.Value).Distinct().ToList();
        if (sel.Count == 0) { App.Toasts.Info("Önce listeden IP seçin (soldaki kutucuklar)."); return; }
        if (!await App.Dialogs.ConfirmAsync("Seçilenlerin bloğunu kaldır", $"{sel.Count} IP adresinin bloğu kaldırılacak:\n\n{Preview(sel)}", "Hepsinin bloğunu kaldır"))
            return;
        await Busy(async () =>
        {
            int ok = 0;
            foreach (var ip in sel)
            {
                try { await App.CrowdSec.DeleteDecisionAsync(ip); ok++; App.Log("Blok kaldırıldı", ip); }
                catch (Exception ex) { App.Toasts.Error($"{ip}: {ErrorText.From(ex)}"); }
            }
            App.Toasts.Success($"{ok} IP adresinin bloğu kaldırıldı.");
            await LoadDecisionsAsync();
        }, "Bloklar kaldırılıyor…");
    }

    [RelayCommand]
    private async Task AllowFromDecisionAsync(DecisionRow row) => await AddAllowAsync(row.Item.Value);

    private static string Preview(List<string> items) =>
        string.Join("\n", items.Take(12)) + (items.Count > 12 ? $"\n… ve {items.Count - 12} tane daha" : "");

    public static readonly ChoiceOption[] BanDurations =
    {
        new("1 saat", "1h"), new("4 saat", "4h"), new("24 saat", "24h"), new("7 gün", "168h"),
        new("30 gün", "720h"), new("1 yıl", "8760h"), new("Kalıcı (10 yıl)", "87600h"),
    };

    [RelayCommand]
    private Task BlockNewAsync() => BlockAsync(null);

    [RelayCommand]
    private Task BlockFromAlertAsync(AlertInfo a) => BlockAsync(a.Value);

    public async Task BlockAsync(string? ip)
    {
        var form = new FormDialogViewModel(
            FormField.TextField("ip", "IP adresi veya aralık", ip ?? "", "Örn: 1.2.3.4 veya 1.2.3.0/24"),
            FormField.ChoiceField("dur", "Blok süresi", BanDurations, App.Settings.DefaultBanDuration),
            FormField.TextField("reason", "Sebep (isteğe bağlı)", "", "Örn: Spam gönderiyor"))
        {
            Title = "IP blokla",
            ConfirmText = "Blokla",
            IsDanger = true,
            Validate = f =>
            {
                var v = IpUtil.Normalize(f.Text("ip"));
                if (v == null) return "Geçerli bir IP adresi veya aralık girin.";
                if (IpUtil.Covers(v, App.MyIp))
                    return $"🔒 Bu işlem sizin kendi IP adresinizi ({App.MyIp}) bloklar ve sunucuya erişiminizi keser. Güvenlik için engellendi.";
                if (v.EndsWith("/0")) return "Tüm interneti bloklayamazsınız.";
                return null;
            },
        };
        if (!await App.Dialogs.FormAsync(form)) return;
        var value = IpUtil.Normalize(form.Text("ip"))!;
        var allowed = _allAllow.FirstOrDefault(a => IpUtil.Contains(a.Item.Value, value.Split('/')[0]));
        if (allowed != null && !await App.Dialogs.ConfirmAsync("Bu IP beyaz listede",
                $"{value} beyaz listede ({allowed.Item.Value}) bulunuyor. CrowdSec beyaz listedeki IP'leri bloklamaz.\n\nYine de blok eklensin mi?", "Yine de ekle"))
            return;
        await Busy(async () =>
        {
            await App.CrowdSec.AddDecisionAsync(value, form.Choice("dur"), form.Text("reason"));
            App.Log("IP bloklandı", $"{value} ({form["dur"].Selected?.Label}) {form.Text("reason")}");
            App.Toasts.Success($"{value} bloklandı.");
            await LoadDecisionsAsync();
        }, "IP bloklanıyor…");
    }

    // ---------------- Beyaz liste ----------------

    private async Task LoadAllowlistNamesAsync()
    {
        try
        {
            var names = await App.CrowdSec.GetAllowlistNamesAsync();
            AllowError = null;
            var want = App.CurrentServer?.AllowlistName;
            Replace(AllowlistNames, names);
            if (names.Count == 0)
            {
                AllowError = "Sunucuda hiç beyaz liste yok. \"Liste oluştur\" ile bir tane oluşturabilirsiniz.";
                SelectedAllowlist = null;
            }
            else
            {
                SetSelectedAllowlistSilently(names.Contains(want ?? "") ? want : names[0]);
            }
        }
        catch (UserFacingException ex)
        {
            AllowError = ex.Message;
            Replace(AllowlistNames, Array.Empty<string>());
        }
    }

    private void SetSelectedAllowlistSilently(string? name)
    {
#pragma warning disable MVVMTK0034
        _selectedAllowlist = name;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedAllowlist));
        if (name != null && App.CurrentServer is { } s && s.AllowlistName != name)
        {
            s.AllowlistName = name;
            App.Vault.Save();
        }
    }

    private async Task LoadAllowAsync()
    {
        if (SelectedAllowlist == null)
        {
            _allAllow = new();
        }
        else
        {
            var list = await App.CrowdSec.GetAllowlistAsync(SelectedAllowlist);
            _allAllow = list.Select(a => new AllowRow(a)).ToList();
        }
        AllAllowChecked = false;
        ApplyAllowFilter();
    }

    private void ApplyAllowFilter()
    {
        var f = AllowFilter.Trim();
        Replace(Allow, _allAllow.Where(a => Match(f, a.Item.Value, a.Item.Description)));
        AllowSummary = SelectedAllowlist == null ? "" :
            _allAllow.Count == 0 ? "Beyaz liste boş" : $"\"{SelectedAllowlist}\" listesinde {_allAllow.Count} kayıt";
    }

    [RelayCommand]
    private Task RefreshAllowAsync() => Busy(async () =>
    {
        await LoadAllowlistNamesAsync();
        await LoadAllowAsync();
    }, "Beyaz liste yükleniyor…");

    public static readonly ChoiceOption[] AllowDurations =
    {
        new("Süresiz", ""), new("1 gün", "24h"), new("7 gün", "168h"), new("30 gün", "720h"), new("1 yıl", "8760h"),
    };

    [RelayCommand]
    private Task AddAllowNewAsync() => AddAllowAsync(null);

    [RelayCommand]
    private Task AddMyIpAsync()
    {
        if (App.MyIp == null) { App.Toasts.Error("IP adresiniz henüz tespit edilemedi. Sunucuya bağlanın."); return Task.CompletedTask; }
        return AddAllowAsync(App.MyIp, "Benim IP adresim (" + DateTime.Now.ToString("dd.MM.yyyy") + ")");
    }

    [RelayCommand]
    private Task AllowFromAlertAsync(AlertInfo a) => AddAllowAsync(a.Value);

    public async Task AddAllowAsync(string? ip, string description = "")
    {
        if (SelectedAllowlist == null)
        {
            if (!await CreateAllowlistInternalAsync()) return;
        }
        var form = new FormDialogViewModel(
            FormField.TextField("ip", "IP adresi veya aralık", ip ?? "", "Örn: 1.2.3.4 veya 1.2.3.0/24"),
            FormField.TextField("desc", "Açıklama", description, "Örn: Ofis IP, Ahmet Bey ev",
                "Bu IP'nin kime ait olduğunu yazın; ileride çok işinize yarar."),
            FormField.ChoiceField("exp", "Geçerlilik süresi", AllowDurations, ""),
            FormField.CheckField("unblock", "Bu IP şu an bloklu ise bloğunu da kaldır", true))
        {
            Title = $"Beyaz listeye ekle ({SelectedAllowlist})",
            ConfirmText = "Beyaz listeye ekle",
            Validate = f =>
            {
                if (IpUtil.Normalize(f.Text("ip")) == null) return "Geçerli bir IP adresi veya aralık girin.";
                if (f.Text("desc").Length == 0) return "Lütfen bir açıklama yazın (kime ait olduğu).";
                return null;
            },
        };
        if (!await App.Dialogs.FormAsync(form)) return;
        var value = IpUtil.Normalize(form.Text("ip"))!;
        await Busy(async () =>
        {
            await App.CrowdSec.AddToAllowlistAsync(SelectedAllowlist!, value, form.Text("desc"), form.Choice("exp"));
            App.Log("Beyaz listeye eklendi", $"{value} — {form.Text("desc")}");
            if (form.Checked("unblock"))
            {
                try { await App.CrowdSec.DeleteDecisionAsync(value); } catch { /* bloklu değilse sorun yok */ }
            }
            App.Toasts.Success($"{value} beyaz listeye eklendi.");
            await LoadAllowAsync();
            await LoadDecisionsAsync();
        }, "Beyaz listeye ekleniyor…");
    }

    [RelayCommand]
    private async Task RemoveAllowAsync(AllowRow row)
    {
        var a = row.Item;
        var mine = App.MyIp != null && IpUtil.Contains(a.Value, App.MyIp) ? "\n\n⚠️ Bu kayıt sizin şu anki IP adresinizi kapsıyor." : "";
        if (!await App.Dialogs.ConfirmAsync("Beyaz listeden çıkar", $"{a.Value} ({a.Description}) beyaz listeden çıkarılsın mı?{mine}", "Çıkar", danger: true))
            return;
        await Busy(async () =>
        {
            await App.CrowdSec.RemoveFromAllowlistAsync(SelectedAllowlist!, a.Value);
            App.Log("Beyaz listeden çıkarıldı", $"{a.Value} — {a.Description}");
            App.Toasts.Success($"{a.Value} beyaz listeden çıkarıldı.");
            await LoadAllowAsync();
        }, "Çıkarılıyor…");
    }

    [RelayCommand]
    private async Task RemoveAllowSelectedAsync()
    {
        var sel = Allow.Where(a => a.IsChecked).Select(a => a.Item.Value).ToList();
        if (sel.Count == 0) { App.Toasts.Info("Önce listeden kayıt seçin."); return; }
        if (!await App.Dialogs.ConfirmAsync("Seçilenleri çıkar", $"{sel.Count} kayıt beyaz listeden çıkarılacak:\n\n{Preview(sel)}", "Hepsini çıkar", danger: true))
            return;
        await Busy(async () =>
        {
            foreach (var v in sel)
            {
                try { await App.CrowdSec.RemoveFromAllowlistAsync(SelectedAllowlist!, v); App.Log("Beyaz listeden çıkarıldı", v); }
                catch (Exception ex) { App.Toasts.Error($"{v}: {ErrorText.From(ex)}"); }
            }
            await LoadAllowAsync();
        }, "Çıkarılıyor…");
    }

    [RelayCommand]
    private async Task CreateAllowlistAsync()
    {
        if (await CreateAllowlistInternalAsync())
            await Busy(LoadAllowAsync, "Beyaz liste yükleniyor…");
    }

    private async Task<bool> CreateAllowlistInternalAsync()
    {
        var form = new FormDialogViewModel(
            FormField.TextField("name", "Liste adı", AllowlistNames.Count == 0 ? "my_allowlist" : "", "Örn: my_allowlist", "Sadece harf, rakam, - ve _ kullanın."),
            FormField.TextField("desc", "Açıklama", "Güvenilir IP adresleri"))
        {
            Title = "Yeni beyaz liste oluştur",
            ConfirmText = "Oluştur",
            Validate = f => System.Text.RegularExpressions.Regex.IsMatch(f.Text("name"), "^[A-Za-z0-9_-]+$") ? null : "Liste adı geçersiz.",
        };
        if (!await App.Dialogs.FormAsync(form)) return false;
        var ok = await Busy(async () =>
        {
            await App.CrowdSec.CreateAllowlistAsync(form.Text("name"), form.Text("desc"));
            App.Log("Beyaz liste oluşturuldu", form.Text("name"));
            await LoadAllowlistNamesAsync();
            SetSelectedAllowlistSilently(form.Text("name"));
        }, "Liste oluşturuluyor…");
        return ok && SelectedAllowlist != null;
    }

    // ---------------- Saldırı uyarıları ----------------

    private async Task LoadAlertsAsync()
    {
        _allAlerts = await App.CrowdSec.GetAlertsAsync(limit: 300);
        ApplyAlertFilter();
    }

    private void ApplyAlertFilter()
    {
        var f = AlertFilter.Trim();
        Replace(Alerts, _allAlerts.Where(a => Match(f, a.Value, a.Country, a.ScenarioText, a.Scenario, a.AsName)));
    }

    [RelayCommand]
    private Task RefreshAlertsAsync() => Busy(LoadAlertsAsync, "Uyarılar alınıyor…");

    [RelayCommand]
    private Task QueryFromAlertAsync(AlertInfo a)
    {
        QueryText = a.Value;
        SelectedTab = 3;
        return QueryAsync();
    }

    [RelayCommand]
    private Task QueryFromDecisionAsync(DecisionRow d)
    {
        QueryText = d.Item.Value;
        SelectedTab = 3;
        return QueryAsync();
    }

    // ---------------- IP sorgulama ----------------

    [RelayCommand]
    private async Task QueryAsync()
    {
        var ip = IpUtil.Normalize(QueryText);
        if (ip == null || IpUtil.IsRange(ip)) { App.Toasts.Error("Sorgulamak için geçerli tek bir IP adresi girin."); return; }
        await Busy(async () =>
        {
            var cs = App.CrowdSec;
            var decisions = await cs.GetDecisionsAsync(ip, includeCommunity: true);
            var alerts = await cs.GetAlertsAsync(ip, 50);
            if (_allAllow.Count == 0 && SelectedAllowlist != null) await LoadAllowAsync();
            var allowed = _allAllow.Where(a => IpUtil.Contains(a.Item.Value, ip)).Select(a => a.Item).ToList();

            QueryIp = ip;
            QueryIsMine = ip == App.MyIp;
            QueryBlocked = decisions.Count > 0;
            QueryAllowed = allowed.Count > 0;
            QueryClean = !QueryBlocked && !QueryAllowed;
            Replace(QueryDecisions, decisions);
            Replace(QueryAlerts, alerts);
            QueryTitle = QueryBlocked ? "⛔ Bu IP şu anda BLOKLU" : QueryAllowed ? "✅ Bu IP beyaz listede (güvenilir)" : "🟢 Bu IP bloklu değil";
            var lines = new List<string>();
            if (QueryBlocked) lines.Add("Sebep: " + string.Join(", ", decisions.Select(d => d.ScenarioText).Distinct()) + " · Kalan süre: " + decisions[0].DurationText);
            if (QueryAllowed) lines.Add("Beyaz liste kaydı: " + string.Join(", ", allowed.Select(a => $"{a.Value} ({a.Description})")));
            lines.Add(alerts.Count == 0 ? "Bu IP için kayıtlı saldırı uyarısı yok." : $"Bu IP için {alerts.Count} saldırı uyarısı kayıtlı (son: {alerts[0].CreatedText}).");
            var cn = decisions.Select(d => d.Country).Concat(alerts.Select(a => a.Country)).FirstOrDefault(c => c.Length > 0);
            var asn = decisions.Select(d => d.AsName).Concat(alerts.Select(a => a.AsName)).FirstOrDefault(c => c.Length > 0);
            if (cn != null || asn != null) lines.Add($"Konum / ağ: {cn ?? "?"} · {asn ?? "?"}");
            if (QueryIsMine) lines.Add("ℹ️ Bu sizin şu anki IP adresiniz.");
            QueryDetail = string.Join("\n", lines);
            HasQueryResult = true;
        }, "IP sorgulanıyor…");
    }

    [RelayCommand]
    private Task QueryMyIpAsync()
    {
        if (App.MyIp == null) { App.Toasts.Error("IP adresiniz henüz tespit edilemedi."); return Task.CompletedTask; }
        QueryText = App.MyIp;
        return QueryAsync();
    }

    [RelayCommand]
    private async Task QueryUnblockAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("Bloğu kaldır", $"{QueryIp} adresinin tüm blokları kaldırılsın mı?", "Bloğu kaldır")) return;
        await Busy(async () =>
        {
            await App.CrowdSec.DeleteDecisionAsync(QueryIp);
            App.Log("Blok kaldırıldı", QueryIp);
            App.Toasts.Success($"{QueryIp} bloğu kaldırıldı.");
            await LoadDecisionsAsync();
        }, "Blok kaldırılıyor…");
        await QueryAsync();
    }

    [RelayCommand]
    private async Task QueryAllowAsync()
    {
        await AddAllowAsync(QueryIp);
        await QueryAsync();
    }

    [RelayCommand]
    private async Task QueryBlockAsync()
    {
        await BlockAsync(QueryIp);
        await QueryAsync();
    }
}
