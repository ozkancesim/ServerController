using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Models;
using ServerController.Services;

namespace ServerController.ViewModels;

public sealed record SenderStat(string Sender, int Count);

public sealed partial class MailViewModel : PageViewModel
{
    public MailViewModel(AppState app) : base(app) { }

    public override string Title => "E-posta Kuyruğu";
    public override string Icon => "📧";
    public override string Subtitle => "Gönderilmeyi bekleyen e-postalar ve spam kontrolü";

    private List<MailRow> _all = new();
    public ObservableCollection<MailRow> Items { get; } = new();
    public ObservableCollection<SenderStat> TopSenders { get; } = new();

    [ObservableProperty] private int _count;
    [ObservableProperty] private int _frozenCount;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _allChecked;
    [ObservableProperty] private string _hint = "";

    partial void OnFilterChanged(string value) => Apply();
    partial void OnAllCheckedChanged(bool value) { foreach (var i in Items) i.IsChecked = value; }

    public override Task RefreshAsync() => Busy(LoadAsync, "Mail kuyruğu okunuyor…");

    private async Task LoadAsync()
    {
        var (count, items) = await App.Mail.GetQueueAsync();
        Count = count;
        _all = items.Select(i => new MailRow(i)).ToList();
        FrozenCount = items.Count(i => i.Frozen);
        Replace(TopSenders, items.GroupBy(i => i.Sender).Select(g => new SenderStat(g.Key, g.Count())).OrderByDescending(s => s.Count).Take(8));
        Hint = count == 0 ? "Kuyruk boş, her şey yolunda 🎉"
            : count > 200 ? "⚠️ Kuyrukta çok fazla e-posta var. Bir site hacklenip spam gönderiyor olabilir; 'En çok gönderenler' listesine bakın."
            : $"Kuyrukta {count} e-posta bekliyor.";
        AllChecked = false;
        Apply();
    }

    private void Apply()
    {
        var f = Filter.Trim();
        Replace(Items, _all.Where(i => f.Length == 0 || i.Item.Sender.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Item.Recipients.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Item.Id.Contains(f)).Take(500));
    }

    [RelayCommand] private void FilterBySender(SenderStat s) => Filter = s.Sender;

    [RelayCommand]
    private async Task ViewAsync(MailRow m)
    {
        string text = "";
        if (await Busy(async () => text = await App.Mail.HeadersAsync(m.Item.Id), "Okunuyor…"))
            await App.Dialogs.ShowTextAsync($"E-posta başlıkları — {m.Item.Id}", text);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var ids = Items.Where(i => i.IsChecked).Select(i => i.Item.Id).ToList();
        if (ids.Count == 0) { App.Toasts.Info("Önce silinecek e-postaları seçin."); return; }
        if (!await App.Dialogs.ConfirmAsync("E-postaları sil", $"Seçilen {ids.Count} e-posta kuyruktan silinecek ve hiç gönderilmeyecek.", "Sil", danger: true)) return;
        await Busy(async () =>
        {
            foreach (var chunk in ids.Chunk(100)) await App.Mail.DeleteAsync(chunk);
            App.Log("Mail kuyruğu", $"{ids.Count} e-posta silindi");
            App.Toasts.Success($"{ids.Count} e-posta silindi.");
            await LoadAsync();
        }, "Siliniyor…");
    }

    [RelayCommand]
    private async Task DeleteFilteredAsync()
    {
        if (Filter.Trim().Length == 0) { App.Toasts.Info("Önce arama kutusuna bir gönderen yazın veya 'En çok gönderenler'den birine tıklayın."); return; }
        var ids = _all.Where(i => i.Item.Sender.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)).Select(i => i.Item.Id).ToList();
        if (ids.Count == 0) return;
        if (!await App.Dialogs.ConfirmAsync("Gönderenin tüm e-postalarını sil", $"\"{Filter}\" gönderenine ait {ids.Count} e-posta silinecek.", "Sil", danger: true)) return;
        await Busy(async () =>
        {
            foreach (var chunk in ids.Chunk(100)) await App.Mail.DeleteAsync(chunk);
            App.Log("Mail kuyruğu", $"{Filter}: {ids.Count} e-posta silindi");
            await LoadAsync();
        }, "Siliniyor…");
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("Tüm kuyruğu sil", $"Kuyruktaki TÜM e-postalar ({Count}) silinecek. Gerçek müşteri e-postaları da silinir!\n\nEmin misiniz?", "Hepsini sil", danger: true)) return;
        await App.RunInConsoleAsync("Tüm kuyruk siliniyor", MailService.DeleteAllCommand, "Mail kuyruğu tamamen silindi");
        await Busy(LoadAsync);
    }

    [RelayCommand]
    private async Task DeleteFrozenAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("Donmuş e-postaları sil", "Teslim edilemeyip 'donmuş' (frozen) duruma düşen e-postalar silinecek. Bunlar genelde geri dönen spam bildirimleridir.", "Sil")) return;
        await App.RunInConsoleAsync("Donmuş e-postalar siliniyor", MailService.DeleteFrozenCommand, "Donmuş e-postalar silindi");
        await Busy(LoadAsync);
    }

    [RelayCommand]
    private async Task FlushAsync()
    {
        await App.RunInConsoleAsync("Kuyruk yeniden gönderiliyor", MailService.FlushCommand, "Mail kuyruğu yeniden gönderildi");
        await Busy(LoadAsync);
    }
}

public sealed partial class DiskViewModel : PageViewModel
{
    public DiskViewModel(AppState app) : base(app) => _minSize = SizeOptions[1];

    public override string Title => "Disk Analizi";
    public override string Icon => "📁";
    public override string Subtitle => "Disk alanını ne kaplıyor?";

    public ObservableCollection<UsageItem> Users { get; } = new();
    public ObservableCollection<UsageItem> Folder { get; } = new();
    public ObservableCollection<UsageItem> BigFiles { get; } = new();
    public ObservableCollection<DiskInfo> Disks { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _currentPath = "/home";
    [ObservableProperty] private ChoiceOption _minSize;
    [ObservableProperty] private bool _usersLoaded;

    public static ChoiceOption[] SizeOptions { get; } = { new("50 MB üzeri", "50"), new("100 MB üzeri", "100"), new("500 MB üzeri", "500"), new("1 GB üzeri", "1024") };


    partial void OnSelectedTabChanged(int value)
    {
        if (App.Session == null) return;
        if (value == 1 && Folder.Count == 0) _ = Busy(() => LoadFolderAsync(CurrentPath), "Klasörler hesaplanıyor…");
    }

    public override Task RefreshAsync() => Busy(async () =>
    {
        UsersLoaded = false;
        Replace(Folder, Array.Empty<UsageItem>());
        Replace(BigFiles, Array.Empty<UsageItem>());
        var stats = await App.System.GetStatsAsync();
        Replace(Disks, stats.Disks);
        Replace(Users, await App.Disk.GetUserUsageAsync());
        UsersLoaded = true;
    }, "Kullanıcıların disk kullanımı hesaplanıyor (biraz sürebilir)…");

    private async Task LoadFolderAsync(string path)
    {
        Replace(Folder, await App.Disk.GetFolderAsync(path));
        CurrentPath = path;
    }

    [RelayCommand] private Task OpenFolderAsync(UsageItem i) => Busy(() => LoadFolderAsync(i.Path), "Hesaplanıyor…");
    [RelayCommand] private Task OpenUserAsync(UsageItem i) { SelectedTab = 1; return Busy(() => LoadFolderAsync(i.Path), "Hesaplanıyor…"); }

    [RelayCommand]
    private Task UpAsync()
    {
        var p = CurrentPath.TrimEnd('/');
        var parent = p.Contains('/') ? p[..p.LastIndexOf('/')] : "/";
        if (parent.Length == 0) parent = "/";
        return Busy(() => LoadFolderAsync(parent), "Hesaplanıyor…");
    }

    [RelayCommand] private Task GoPathAsync() => Busy(() => LoadFolderAsync(CurrentPath.Trim().Length == 0 ? "/" : CurrentPath.Trim()), "Hesaplanıyor…");

    [RelayCommand]
    private Task FindBigFilesAsync() => Busy(async () =>
    {
        Replace(BigFiles, await App.Disk.GetBigFilesAsync(int.Parse(MinSize.Value)));
        if (BigFiles.Count == 0) App.Toasts.Info("Bu boyutun üzerinde dosya bulunamadı.");
    }, "Büyük dosyalar aranıyor (birkaç dakika sürebilir)…");

    [RelayCommand]
    private async Task DeleteFileAsync(UsageItem f)
    {
        if (!await App.Dialogs.ConfirmAsync("Dosyayı sil", $"Bu dosya kalıcı olarak silinecek:\n\n{f.Path}\n({f.SizeText})\n\nGeri alınamaz!", "Kalıcı olarak sil", danger: true)) return;
        await Busy(async () =>
        {
            await App.Disk.DeleteFileAsync(f.Path);
            App.Log("Dosya silindi", $"{f.Path} ({f.SizeText})");
            Ui(() => BigFiles.Remove(f));
            App.Toasts.Success("Dosya silindi.");
        }, "Siliniyor…");
    }
}

public sealed partial class LogsViewModel : PageViewModel
{
    private CancellationTokenSource? _followCts;
    private readonly StringBuilder _followBuffer = new();
    private string _raw = "";

    public LogsViewModel(AppState app) : base(app)
    {
        _source = Sources[0];
        _lineCount = LineOptions[1];
    }

    public override string Title => "Loglar";
    public override string Icon => "📜";
    public override string Subtitle => "Sunucu kayıtlarını okuyun ve canlı takip edin";

    public LogSource[] Sources => LogSources.All;
    public static ChoiceOption[] LineOptions { get; } = { new("Son 200 satır", "200"), new("Son 500 satır", "500"), new("Son 2000 satır", "2000"), new("Son 5000 satır", "5000") };

    [ObservableProperty] private LogSource _source;
    [ObservableProperty] private ChoiceOption _lineCount;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private bool _isFollowing;
    [ObservableProperty] private bool _onlyErrors;

    partial void OnSourceChanged(LogSource value) { StopFollow(); _ = RefreshAsync(); }
    partial void OnLineCountChanged(ChoiceOption value) { if (!IsFollowing) _ = RefreshAsync(); }
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyErrorsChanged(bool value) => ApplyFilter();

    public override Task RefreshAsync() => Busy(async () =>
    {
        var cmd = Source.Command.Replace("{N}", LineCount.Value);
        var r = await App.RequireSession().RunAsync(cmd, TimeSpan.FromMinutes(1));
        _raw = r.Combined;
        ApplyFilter();
    }, "Log okunuyor…");

    private static readonly string[] ErrorWords = { "error", "fail", "denied", "critical", "fatal", "warn", "reject", "invalid", "ban" };

    private void ApplyFilter()
    {
        IEnumerable<string> lines = _raw.Split('\n');
        var f = Filter.Trim();
        if (f.Length > 0) lines = lines.Where(l => l.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (OnlyErrors) lines = lines.Where(l => ErrorWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)));
        Content = string.Join('\n', lines);
    }

    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (IsFollowing) { StopFollow(); return; }
        if (Source.FollowCommand == null) return;
        var session = App.RequireSession();
        _followCts = new CancellationTokenSource();
        var ct = _followCts.Token;
        IsFollowing = true;
        _raw = "";
        ApplyFilter();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => FlushFollow());
        timer.Start();
        try
        {
            await Task.Run(() => session.StreamAsync(Source.FollowCommand, s => { lock (_followBuffer) _followBuffer.Append(s); }, ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Toasts.Error(ErrorText.From(ex)); }
        finally
        {
            timer.Stop();
            FlushFollow();
            IsFollowing = false;
        }
    }

    private void FlushFollow()
    {
        string chunk;
        lock (_followBuffer)
        {
            if (_followBuffer.Length == 0) return;
            chunk = _followBuffer.ToString();
            _followBuffer.Clear();
        }
        _raw += chunk;
        if (_raw.Length > 400_000) _raw = _raw[^300_000..];
        ApplyFilter();
    }

    private void StopFollow()
    {
        _followCts?.Cancel();
        _followCts = null;
    }

    public override void OnDeactivated() => StopFollow();
}

public sealed partial class TerminalEntry : ViewModelBase
{
    public string Command { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private int? _exitCode;
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed partial class CommandsViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    private readonly List<string> _typed = new();
    private int _historyIndex = -1;

    public CommandsViewModel(AppState app) : base(app)
    {
        Reload();
        SettingsViewModel.CommandsChanged += Reload;
    }

    private void Reload()
    {
        Library.Clear();
        foreach (var c in App.Vault.Data.Commands) Library.Add(c);
    }

    public override string Title => "Komutlar & Terminal";
    public override string Icon => "⭐";
    public override string Subtitle => "Kayıtlı komutlarınızı tek tıkla çalıştırın";
    public override bool RequiresServer => false;

    public ObservableCollection<SavedCommand> Library { get; } = new();
    public ObservableCollection<TerminalEntry> Terminal { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isRunning;

    [RelayCommand]
    private async Task RunSavedAsync(SavedCommand c)
    {
        if (c.Confirm && !await App.Dialogs.ConfirmAsync(c.Name, $"Bu komut çalıştırılacak:\n\n{c.Command}\n\nDevam edilsin mi?", "Çalıştır"))
            return;
        try { await App.RunInConsoleAsync(c.Name, c.Command, "Komut: " + c.Name); }
        catch (Exception ex) { App.Toasts.Error(ErrorText.From(ex)); }
    }

    private FormDialogViewModel CommandForm(SavedCommand? c) => new(
        FormField.TextField("name", "Komut adı", c?.Name ?? "", "Örn: Mail kuyruğunu temizle"),
        FormField.MultiLineField("cmd", "Komut", c?.Command ?? "", "Teknisyeninizin verdiği komutu buraya yapıştırın."),
        FormField.TextField("desc", "Açıklama (isteğe bağlı)", c?.Description ?? "", "Ne işe yaradığını yazın"),
        FormField.CheckField("confirm", "Çalıştırmadan önce onay iste", c?.Confirm ?? true))
    {
        Title = c == null ? "Yeni komut ekle" : "Komutu düzenle",
        Validate = f => f.Text("name").Length == 0 ? "Komuta bir ad verin." : f.Text("cmd").Length == 0 ? "Komut boş olamaz." : null,
    };

    [RelayCommand]
    private async Task AddAsync()
    {
        var form = CommandForm(null);
        if (!await App.Dialogs.FormAsync(form)) return;
        var c = new SavedCommand { Name = form.Text("name"), Command = form.Text("cmd"), Description = form.Text("desc"), Confirm = form.Checked("confirm") };
        App.Vault.Data.Commands.Add(c);
        App.Vault.Save();
        Library.Add(c);
        App.Toasts.Success("Komut kaydedildi.");
    }

    [RelayCommand]
    private async Task EditAsync(SavedCommand c)
    {
        var form = CommandForm(c);
        if (!await App.Dialogs.FormAsync(form)) return;
        c.Name = form.Text("name"); c.Command = form.Text("cmd"); c.Description = form.Text("desc"); c.Confirm = form.Checked("confirm");
        App.Vault.Save();
        var i = Library.IndexOf(c);
        Library.RemoveAt(i);
        Library.Insert(i, c);
    }

    [RelayCommand]
    private async Task DeleteAsync(SavedCommand c)
    {
        if (!await App.Dialogs.ConfirmAsync("Komutu sil", $"\"{c.Name}\" kütüphaneden silinsin mi?", "Sil", danger: true)) return;
        App.Vault.Data.Commands.Remove(c);
        App.Vault.Save();
        Library.Remove(c);
    }

    [RelayCommand]
    private async Task SaveFromTerminalAsync(TerminalEntry e)
    {
        var form = CommandForm(new SavedCommand { Command = e.Command, Confirm = true });
        if (!await App.Dialogs.FormAsync(form)) return;
        var c = new SavedCommand { Name = form.Text("name"), Command = form.Text("cmd"), Description = form.Text("desc"), Confirm = form.Checked("confirm") };
        App.Vault.Data.Commands.Add(c);
        App.Vault.Save();
        Library.Add(c);
        App.Toasts.Success("Komut kütüphaneye eklendi.");
    }

    [RelayCommand]
    private async Task RunTerminalAsync()
    {
        var cmd = Input.Trim();
        if (cmd.Length == 0 || IsRunning) return;
        SshSession session;
        try { session = App.RequireSession(); }
        catch (Exception ex) { App.Toasts.Error(ErrorText.From(ex)); return; }

        var first = cmd.Split(' ')[0];
        if (first is "top" or "htop" or "nano" or "vi" or "vim" or "less" or "more" or "mysql" or "watch")
        {
            App.Toasts.Error($"\"{first}\" etkileşimli bir programdır ve bu pencerede çalışmaz. Gerekirse PuTTY kullanın.");
            return;
        }
        if (_typed.LastOrDefault() != cmd) _typed.Add(cmd);
        _historyIndex = -1;
        Input = "";
        var entry = new TerminalEntry { Command = cmd };
        Terminal.Add(entry);
        while (Terminal.Count > 50) Terminal.RemoveAt(0);
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var sb = new StringBuilder();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            lock (sb) entry.Output = sb.ToString();
        });
        timer.Start();
        try
        {
            entry.ExitCode = await Task.Run(() => session.StreamAsync(cmd, s => { lock (sb) { sb.Append(s); if (sb.Length > 200_000) sb.Remove(0, sb.Length - 150_000); } }, _cts.Token));
            App.Log("Terminal", cmd, entry.ExitCode == 0);
        }
        catch (OperationCanceledException) { lock (sb) sb.Append("\n[iptal edildi]"); }
        catch (Exception ex) { lock (sb) sb.Append("\n" + ErrorText.From(ex)); entry.ExitCode = -1; }
        finally
        {
            timer.Stop();
            lock (sb) entry.Output = sb.ToString().TrimEnd();
            entry.IsRunning = false;
            IsRunning = false;
        }
    }

    [RelayCommand] private void CancelTerminal() => _cts?.Cancel();
    [RelayCommand] private void ClearTerminal() => Terminal.Clear();

    public void HistoryUp()
    {
        if (_typed.Count == 0) return;
        _historyIndex = _historyIndex < 0 ? _typed.Count - 1 : Math.Max(0, _historyIndex - 1);
        Input = _typed[_historyIndex];
    }

    public void HistoryDown()
    {
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= _typed.Count) { _historyIndex = -1; Input = ""; }
        else Input = _typed[_historyIndex];
    }
}

public sealed partial class HistoryViewModel : PageViewModel
{
    public HistoryViewModel(AppState app) : base(app)
    {
        app.HistoryChanged += () => { if (_active) Apply(); };
    }

    private bool _active;

    public override string Title => "İşlem Geçmişi";
    public override string Icon => "🕘";
    public override string Subtitle => "Uygulamadan yaptığınız tüm işlemler";
    public override bool RequiresServer => false;

    public ObservableCollection<HistoryEntry> Items { get; } = new();
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";

    partial void OnFilterChanged(string value) => Apply();

    public override Task OnActivatedAsync()
    {
        _active = true;
        Apply();
        return Task.CompletedTask;
    }

    public override void OnDeactivated() => _active = false;

    public override Task RefreshAsync() { Apply(); return Task.CompletedTask; }

    private void Apply()
    {
        var f = Filter.Trim();
        var all = App.Vault.Data.History;
        Replace(Items, all.Where(h => f.Length == 0 || h.Action.Contains(f, StringComparison.OrdinalIgnoreCase) || h.Detail.Contains(f, StringComparison.OrdinalIgnoreCase) || h.Server.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(1000));
        Summary = $"{all.Count} kayıt";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(dir, $"ServerController-gecmis-{DateTime.Now:yyyyMMdd-HHmm}.csv");
            var sb = new StringBuilder("Tarih;Sunucu;İşlem;Detay;Başarılı\n");
            static string E(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            foreach (var h in App.Vault.Data.History)
                sb.Append($"{h.Time:dd.MM.yyyy HH:mm:ss};{E(h.Server)};{E(h.Action)};{E(h.Detail)};{(h.Success ? "Evet" : "Hayır")}\n");
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
            await App.Dialogs.AlertAsync("Dışa aktarıldı", $"Geçmiş Excel'de açılabilecek şekilde kaydedildi:\n\n{path}");
        }
        catch (Exception ex) { App.Toasts.Error(ErrorText.From(ex)); }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("Geçmişi temizle", "Tüm işlem geçmişi silinsin mi?", "Temizle", danger: true)) return;
        App.Vault.Data.History.Clear();
        App.Vault.Save();
        Apply();
    }
}

public sealed partial class ServersViewModel : PageViewModel
{
    public ServersViewModel(AppState app) : base(app) { }

    public override string Title => "Sunucular";
    public override string Icon => "🖥️";
    public override string Subtitle => "Yönettiğiniz sunucuları ekleyin ve düzenleyin";
    public override bool RequiresServer => false;

    public override Task OnActivatedAsync() => Task.CompletedTask;

    private FormDialogViewModel ServerForm(ServerProfile? s) => new(
        FormField.TextField("name", "Sunucu adı", s?.Name ?? "", "Örn: Ana sunucu"),
        FormField.TextField("host", "Sunucu adresi (IP veya alan adı)", s?.Host ?? "", "Örn: 192.0.2.10"),
        FormField.TextField("port", "SSH portu", (s?.Port ?? 22).ToString(), "22", "PuTTY'de kullandığınız port numarası."),
        FormField.TextField("user", "Kullanıcı adı", s?.Username ?? "root"),
        FormField.PasswordField("pass", s == null ? "Sunucu (root) şifresi" : "Sunucu şifresi (değiştirmeyecekseniz boş bırakın)"),
        FormField.TextField("allow", "CrowdSec beyaz liste adı", s?.AllowlistName ?? "my_allowlist", hint: "Teknisyeninizin verdiği komuttaki liste adı (örn: my_allowlist)."))
    {
        Title = s == null ? "Yeni sunucu ekle" : $"{s.Display} — düzenle",
        Message = "Bu bilgiler bilgisayarınızda, uygulama şifrenizle şifrelenmiş olarak saklanır.",
        Validate = f =>
        {
            if (f.Text("host").Length == 0) return "Sunucu adresini girin.";
            if (!int.TryParse(f.Text("port"), out var p) || p is < 1 or > 65535) return "Port 1-65535 arasında bir sayı olmalı.";
            if (f.Text("user").Length == 0) return "Kullanıcı adını girin.";
            if (s == null && f.RawText("pass").Length == 0) return "Sunucu şifresini girin.";
            if (!System.Text.RegularExpressions.Regex.IsMatch(f.Text("allow"), "^[A-Za-z0-9_-]+$")) return "Beyaz liste adı geçersiz.";
            return null;
        },
    };

    private static void Fill(ServerProfile s, FormDialogViewModel f)
    {
        s.Name = f.Text("name");
        if (s.Host != f.Text("host")) s.HostKeyFingerprint = null;
        s.Host = f.Text("host");
        s.Port = int.Parse(f.Text("port"));
        s.Username = f.Text("user");
        if (f.RawText("pass").Length > 0) s.Password = f.RawText("pass");
        s.AllowlistName = f.Text("allow");
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var form = ServerForm(null);
        if (!await App.Dialogs.FormAsync(form)) return;
        var s = new ServerProfile();
        Fill(s, form);
        App.Vault.Data.Servers.Add(s);
        App.Vault.Save();
        App.Servers.Add(s);
        App.Log("Sunucu eklendi", s.Display);
        App.CurrentServer = s;
        App.Toasts.Success($"{s.Display} eklendi, bağlanılıyor…");
    }

    [RelayCommand]
    private async Task EditAsync(ServerProfile s)
    {
        var form = ServerForm(s);
        if (!await App.Dialogs.FormAsync(form)) return;
        Fill(s, form);
        App.Vault.Save();
        var i = App.Servers.IndexOf(s);
        var wasCurrent = App.CurrentServer == s;
        App.Servers[i] = s;
        if (wasCurrent)
        {
            App.CurrentServer = null;
            App.CurrentServer = s;
        }
        App.Toasts.Success("Sunucu bilgileri güncellendi.");
    }

    [RelayCommand]
    private async Task DeleteAsync(ServerProfile s)
    {
        if (!await App.Dialogs.ConfirmAsync("Sunucuyu kaldır", $"\"{s.Display}\" bu uygulamadan kaldırılsın mı?\n\n(Sunucunun kendisine hiçbir şey olmaz, sadece listeden çıkar.)", "Kaldır", danger: true)) return;
        if (App.CurrentServer == s) App.CurrentServer = null;
        App.Vault.Data.Servers.Remove(s);
        App.Vault.Save();
        App.Servers.Remove(s);
        App.Log("Sunucu kaldırıldı", s.Display);
    }

    [RelayCommand]
    private void Select(ServerProfile s) => App.CurrentServer = s;

    [RelayCommand]
    private async Task TestAsync(ServerProfile s)
    {
        using var session = new SshSession(new ServerProfile
        {
            Host = s.Host, Port = s.Port, Username = s.Username, Password = s.Password, HostKeyFingerprint = s.HostKeyFingerprint,
        })
        {
            HostKeyPrompt = (fp, old) => Task.FromResult(old == null || old == fp),
        };
        await Busy(async () =>
        {
            var r = await session.RunAsync("echo \"$(hostname) · $( (. /etc/os-release; echo $PRETTY_NAME) ) · cscli: $(command -v cscli >/dev/null && echo var || echo YOK)\"");
            await App.Dialogs.AlertAsync("✅ Bağlantı başarılı", r.Output.Trim() + (session.ClientIp != null ? $"\n\nSunucunun gördüğü IP adresiniz: {session.ClientIp}" : ""));
        }, $"{s.Display} test ediliyor…");
    }
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    public SettingsViewModel(AppState app, MainViewModel main) : base(app)
    {
        _main = main;
        _autoLock = LockOptions.FirstOrDefault(o => o.Value == app.Settings.AutoLockMinutes.ToString()) ?? LockOptions[2];
        _defaultBan = CrowdSecViewModel.BanDurations.FirstOrDefault(o => o.Value == app.Settings.DefaultBanDuration) ?? CrowdSecViewModel.BanDurations[2];
        _refresh = RefreshOptions.FirstOrDefault(o => o.Value == app.Settings.DashboardRefreshSeconds.ToString()) ?? RefreshOptions[1];
    }

    public override string Title => "Ayarlar";
    public override string Icon => "⚙️";
    public override string Subtitle => "Uygulama tercihleri ve güvenlik";
    public override bool RequiresServer => false;

    public override Task OnActivatedAsync() => Task.CompletedTask;

    public static ChoiceOption[] LockOptions { get; } = { new("5 dakika", "5"), new("10 dakika", "10"), new("15 dakika", "15"), new("30 dakika", "30"), new("1 saat", "60"), new("Asla", "0") };
    public static ChoiceOption[] RefreshOptions { get; } = { new("5 saniye", "5"), new("15 saniye", "15"), new("30 saniye", "30"), new("1 dakika", "60") };
    public ChoiceOption[] BanOptions => CrowdSecViewModel.BanDurations;

    [ObservableProperty] private ChoiceOption _autoLock;
    [ObservableProperty] private ChoiceOption _defaultBan;
    [ObservableProperty] private ChoiceOption _refresh;

    public string VaultPath => VaultService.FilePath;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public bool IsDark { get => _main.IsDark; set { if (value != _main.IsDark) _main.ToggleThemeCommand.Execute(null); OnPropertyChanged(); } }

    partial void OnAutoLockChanged(ChoiceOption value) { App.Settings.AutoLockMinutes = int.Parse(value.Value); App.Vault.Save(); }
    partial void OnDefaultBanChanged(ChoiceOption value) { App.Settings.DefaultBanDuration = value.Value; App.Vault.Save(); }
    partial void OnRefreshChanged(ChoiceOption value) { App.Settings.DashboardRefreshSeconds = int.Parse(value.Value); App.Vault.Save(); }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        var form = new FormDialogViewModel(
            FormField.TextField("user", "Kullanıcı adı", App.Vault.Data.UserName),
            FormField.PasswordField("p1", "Yeni şifre"),
            FormField.PasswordField("p2", "Yeni şifre (tekrar)"))
        {
            Title = "Uygulama şifresini değiştir",
            ConfirmText = "Değiştir",
            Validate = f =>
                f.Text("user").Length < 3 ? "Kullanıcı adı en az 3 karakter olmalı." :
                f.RawText("p1").Length < 6 ? "Şifre en az 6 karakter olmalı." :
                f.RawText("p1") != f.RawText("p2") ? "Şifreler uyuşmuyor." : null,
        };
        if (!await App.Dialogs.FormAsync(form)) return;
        await Busy(async () =>
        {
            var user = form.Text("user");
            var pass = form.RawText("p1");
            await Task.Run(() => App.Vault.ChangePassword(user, pass));
            App.Log("Ayarlar", "Uygulama şifresi değiştirildi");
            App.Toasts.Success("Şifre değiştirildi. Bir sonraki girişte yeni şifrenizi kullanın.");
        }, "Şifre değiştiriliyor…");
    }

    public static event Action? CommandsChanged;

    [RelayCommand]
    private async Task RestoreDefaultCommandsAsync()
    {
        if (!await App.Dialogs.ConfirmAsync("Hazır komutlar", "Hazır komutlar kütüphanenize tekrar eklensin mi? (Mevcut komutlarınız silinmez.)", "Ekle")) return;
        foreach (var c in DefaultCommands.Create().Where(d => App.Vault.Data.Commands.All(x => x.Command != d.Command)))
            App.Vault.Data.Commands.Add(c);
        App.Vault.Save();
        CommandsChanged?.Invoke();
        App.Toasts.Success("Hazır komutlar eklendi.");
    }
}
