using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Models;
using ServerController.Services;

namespace ServerController.ViewModels;

public abstract class ViewModelBase : ObservableObject { }

public enum ToastKind { Info, Success, Error }

public sealed partial class ToastItem : ViewModelBase
{
    public string Message { get; init; } = "";
    public ToastKind Kind { get; init; }
    public bool IsSuccess => Kind == ToastKind.Success;
    public bool IsError => Kind == ToastKind.Error;
    public bool IsInfo => Kind == ToastKind.Info;
    public string Icon => Kind switch { ToastKind.Success => "✅", ToastKind.Error => "⛔", _ => "ℹ️" };
}

public sealed class ToastService
{
    public ObservableCollection<ToastItem> Items { get; } = new();

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var t = new ToastItem { Message = message, Kind = kind };
            Items.Add(t);
            while (Items.Count > 4) Items.RemoveAt(0);
            await Task.Delay(kind == ToastKind.Error ? 7000 : 4000);
            Items.Remove(t);
        });
    }

    public void Success(string m) => Show(m, ToastKind.Success);
    public void Error(string m) => Show(m, ToastKind.Error);
    public void Info(string m) => Show(m, ToastKind.Info);
}

/// <summary>Uygulama genelinde paylaşılan durum: kasa, aktif sunucu ve bağlantı.</summary>
public sealed partial class AppState : ViewModelBase
{
    public AppState(VaultService vault, ToastService toasts, DialogHost dialogs)
    {
        Vault = vault;
        Toasts = toasts;
        Dialogs = dialogs;
    }

    public VaultService Vault { get; }
    public ToastService Toasts { get; }
    public DialogHost Dialogs { get; }

    public ObservableCollection<ServerProfile> Servers { get; } = new();

    [ObservableProperty] private ServerProfile? _currentServer;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionText = "Sunucu seçilmedi";
    [ObservableProperty] private string? _myIp;

    /// <summary>Sunucu her değiştiğinde artar; sayfalar verilerini yenilemek için kullanır.</summary>
    public int SessionVersion { get; private set; }

    public SshSession? Session { get; private set; }

    public event Action? SessionChanged;

    public AppSettings Settings => Vault.Data.Settings;

    public void LoadServers()
    {
        Servers.Clear();
        foreach (var s in Vault.Data.Servers) Servers.Add(s);
    }

    partial void OnCurrentServerChanged(ServerProfile? value)
    {
        Session?.Dispose();
        Session = null;
        IsConnected = false;
        MyIp = null;
        SessionVersion++;
        if (value != null)
        {
            Session = new SshSession(value) { HostKeyPrompt = PromptHostKeyAsync };
            Session.HostKeyAccepted += _ => Vault.Save();
            Vault.Data.LastServerId = value.Id;
            Vault.Save();
            ConnectionText = "Bağlanılıyor…";
        }
        else
        {
            ConnectionText = "Sunucu seçilmedi";
        }
        SessionChanged?.Invoke();
        if (value != null) _ = ConnectAsync();
    }

    public async Task<bool> ConnectAsync()
    {
        var s = Session;
        if (s == null) return false;
        IsConnecting = true;
        ConnectionText = "Bağlanılıyor…";
        try
        {
            await Task.Run(() => s.ConnectAsync());
            if (s != Session) return false;
            IsConnected = true;
            MyIp = s.ClientIp;
            ConnectionText = "Bağlı";
            return true;
        }
        catch (Exception ex)
        {
            if (s != Session) return false;
            IsConnected = false;
            ConnectionText = "Bağlantı yok";
            Toasts.Error(ErrorText.From(ex));
            return false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private Task<bool> PromptHostKeyAsync(string fingerprint, string? old)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var name = CurrentServer?.Display ?? "sunucu";
            return old == null
                ? Dialogs.ConfirmAsync("Yeni sunucu kimliği",
                    $"\"{name}\" sunucusuna ilk kez bağlanılıyor.\n\nSunucu parmak izi:\n{fingerprint}\n\nBu sunucuya güveniyor musunuz? Bu kimlik kaydedilecek ve sonraki bağlantılarda kontrol edilecek.",
                    "Güven ve bağlan")
                : Dialogs.ConfirmAsync("⚠️ Sunucu kimliği DEĞİŞTİ",
                    $"\"{name}\" sunucusunun kimliği daha önce kaydedilenden farklı!\n\nEski: {old}\nYeni: {fingerprint}\n\nSunucu yeniden kurulduysa bu normaldir. Aksi halde bağlantınız dinleniyor olabilir. Yine de devam edilsin mi?",
                    "Yine de bağlan", danger: true);
        });
    }

    public SshSession RequireSession() =>
        Session ?? throw new UserFacingException("Önce üst menüden bir sunucu seçin.");

    public CrowdSecService CrowdSec => new(RequireSession());
    public SystemService System => new(RequireSession());
    public CustomBuildService CustomBuild => new(RequireSession());
    public WordPressService WordPress => new(RequireSession());
    public MailService Mail => new(RequireSession());
    public DiskService Disk => new(RequireSession());

    public void Log(string action, string detail, bool success = true)
    {
        Vault.Data.History.Insert(0, new HistoryEntry
        {
            Server = CurrentServer?.Display ?? "-",
            Action = action,
            Detail = detail,
            Success = success,
        });
        if (Vault.Data.History.Count > 5000) Vault.Data.History.RemoveRange(5000, Vault.Data.History.Count - 5000);
        Vault.Save();
        HistoryChanged?.Invoke();
    }

    public event Action? HistoryChanged;

    /// <summary>Uzun süren bir komutu çıktı penceresinde canlı çalıştırır ve geçmişe yazar.</summary>
    public async Task<bool> RunInConsoleAsync(string title, string command, string? historyAction = null)
    {
        var session = RequireSession();
        var code = await Dialogs.RunStreamingAsync(title, (write, ct) => session.StreamAsync(command, write, ct));
        var ok = code == 0;
        Log(historyAction ?? title, code == null ? "İptal edildi" : ok ? "Tamamlandı" : $"Hata kodu {code}", ok);
        return ok;
    }
}

/// <summary>Sol menüdeki her sayfanın temel sınıfı.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    protected PageViewModel(AppState app) => App = app;

    public AppState App { get; }
    public abstract string Title { get; }
    public abstract string Icon { get; }
    public virtual string Subtitle => "";
    public virtual bool RequiresServer => true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "Yükleniyor…";

    private int _loadedVersion = -1;

    public virtual async Task OnActivatedAsync()
    {
        if (RequiresServer && !App.IsConnected) return;
        if (_loadedVersion == App.SessionVersion) return;
        _loadedVersion = App.SessionVersion;
        await RefreshAsync();
    }

    public virtual void OnDeactivated() { }

    public void Invalidate() => _loadedVersion = -1;

    [RelayCommand]
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>İşlemi meşgul göstergesiyle çalıştırır, hataları bildirim olarak gösterir.</summary>
    protected async Task<bool> Busy(Func<Task> action, string text = "Yükleniyor…")
    {
        if (IsBusy) return false;
        BusyText = text;
        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            App.Toasts.Error(ErrorText.From(ex));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        Dispatcher.UIThread.Invoke(() =>
        {
            target.Clear();
            foreach (var i in list) target.Add(i);
        });
    }

    protected static void Ui(Action a) => Dispatcher.UIThread.Invoke(a);
}

/// <summary>Seçim kutusu (checkbox) olan liste satırı.</summary>
public partial class Selectable<T> : ViewModelBase
{
    public Selectable(T item) => Item = item;
    public T Item { get; }
    [ObservableProperty] private bool _isChecked;
}

public sealed class DecisionRow : Selectable<DecisionInfo> { public DecisionRow(DecisionInfo i) : base(i) { } }
public sealed class AllowRow : Selectable<AllowlistEntry> { public AllowRow(AllowlistEntry i) : base(i) { } }
public sealed class MailRow : Selectable<MailItem> { public MailRow(MailItem i) : base(i) { } }
