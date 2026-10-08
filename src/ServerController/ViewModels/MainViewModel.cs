using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Models;
using ServerController.Services;

namespace ServerController.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly VaultService _vault = new();
    private DateTime _lastActivity = DateTime.Now;
    private readonly DispatcherTimer _lockTimer;

    public MainViewModel()
    {
        Toasts = new ToastService();
        Dialogs = new DialogHost();
        App = new AppState(_vault, Toasts, Dialogs);
        Login = new LoginViewModel(_vault, OnLoggedIn, Dialogs);
        _lockTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => CheckAutoLock());
        App.SessionChanged += () => Dispatcher.UIThread.Post(() => { CurrentPage?.Invalidate(); _ = CurrentPage?.OnActivatedAsync(); });
        App.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsConnected) && App.IsConnected)
                Dispatcher.UIThread.Post(() => { CurrentPage?.Invalidate(); _ = CurrentPage?.OnActivatedAsync(); });
        };
    }

    public AppState App { get; }
    public ToastService Toasts { get; }
    public DialogHost Dialogs { get; }
    public LoginViewModel Login { get; }

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private bool _isDark = true;

    public string UserName => _vault.Data.UserName;

    private void OnLoggedIn()
    {
        Pages.Clear();
        var crowdsec = new CrowdSecViewModel(App);
        Pages.Add(new DashboardViewModel(App, this));
        Pages.Add(crowdsec);
        Pages.Add(new ServicesViewModel(App));
        Pages.Add(new SoftwareViewModel(App));
        Pages.Add(new MailViewModel(App));
        Pages.Add(new DiskViewModel(App));
        Pages.Add(new LogsViewModel(App));
        Pages.Add(new CommandsViewModel(App));
        Pages.Add(new HistoryViewModel(App));
        Pages.Add(new ServersViewModel(App));
        Pages.Add(new SettingsViewModel(App, this));

        ApplyTheme(App.Settings.Theme);
        App.LoadServers();
        OnPropertyChanged(nameof(UserName));
        IsLoggedIn = true;

        if (App.Servers.Count == 0)
        {
            CurrentPage = Pages.OfType<ServersViewModel>().First();
            Toasts.Info("Hoş geldiniz! Başlamak için ilk sunucunuzu ekleyin.");
        }
        else
        {
            CurrentPage = Pages[0];
            App.CurrentServer = App.Servers.FirstOrDefault(s => s.Id == _vault.Data.LastServerId) ?? App.Servers[0];
        }
        _lastActivity = DateTime.Now;
        _lockTimer.Start();
    }

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        oldValue?.OnDeactivated();
        if (newValue != null) _ = newValue.OnActivatedAsync();
    }

    public void Navigate<T>() where T : PageViewModel
    {
        CurrentPage = Pages.OfType<T>().FirstOrDefault();
    }

    public void ApplyTheme(string theme)
    {
        IsDark = theme != "Light";
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        App.Settings.Theme = IsDark ? "Light" : "Dark";
        ApplyTheme(App.Settings.Theme);
        _vault.Save();
    }

    [RelayCommand]
    private void LockApp()
    {
        _lockTimer.Stop();
        CurrentPage?.OnDeactivated();
        App.CurrentServer = null;
        Pages.Clear();
        CurrentPage = null;
        Dialogs.Items.Clear();
        _vault.Lock();
        IsLoggedIn = false;
        Login.Reset();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (App.Session == null) return;
        if (await App.ConnectAsync()) Toasts.Success("Sunucuya bağlanıldı.");
    }

    public void NotifyActivity() => _lastActivity = DateTime.Now;

    private void CheckAutoLock()
    {
        var mins = App.Settings.AutoLockMinutes;
        if (!IsLoggedIn || mins <= 0) return;
        if (Dialogs.Items.OfType<OutputDialogViewModel>().Any(o => o.IsRunning)) return;
        if (DateTime.Now - _lastActivity > TimeSpan.FromMinutes(mins))
        {
            LockApp();
            Toasts.Info("Uzun süre işlem yapılmadığı için uygulama kilitlendi.");
        }
    }
}

public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly VaultService _vault;
    private readonly Action _onSuccess;
    private readonly DialogHost _dialogs;
    private int _failures;

    public LoginViewModel(VaultService vault, Action onSuccess, DialogHost dialogs)
    {
        _vault = vault;
        _onSuccess = onSuccess;
        _dialogs = dialogs;
        IsSetup = !VaultService.Exists;
    }

    [ObservableProperty] private bool _isSetup;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _password2 = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isWorking;

    public string Heading => IsSetup ? "Hoş geldiniz" : "Tekrar hoş geldiniz";
    public string SubHeading => IsSetup
        ? "Uygulamayı korumak için bir kullanıcı adı ve şifre belirleyin. Sunucu şifreleriniz bu şifreyle şifrelenerek saklanır."
        : "Devam etmek için uygulama kullanıcı adı ve şifrenizi girin.";
    public string ButtonText => IsSetup ? "Hesabı oluştur" : "Giriş yap";

    partial void OnIsSetupChanged(bool value)
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(SubHeading));
        OnPropertyChanged(nameof(ButtonText));
    }

    public void Reset()
    {
        Password = "";
        Password2 = "";
        Error = null;
        IsSetup = !VaultService.Exists;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsWorking) return;
        Error = null;
        var user = UserName.Trim();
        if (user.Length < 3) { Error = "Kullanıcı adı en az 3 karakter olmalı."; return; }
        if (Password.Length < 6) { Error = "Şifre en az 6 karakter olmalı."; return; }
        if (IsSetup && Password != Password2) { Error = "Şifreler birbiriyle uyuşmuyor."; return; }

        IsWorking = true;
        try
        {
            var pass = Password;
            if (IsSetup)
            {
                await Task.Run(() => _vault.Create(user, pass));
            }
            else
            {
                var ok = await Task.Run(() => _vault.TryUnlock(user, pass));
                if (!ok)
                {
                    _failures++;
                    await Task.Delay(Math.Min(_failures, 5) * 600);
                    Error = "Kullanıcı adı veya şifre hatalı.";
                    return;
                }
            }
            _failures = 0;
            Password = "";
            Password2 = "";
            _onSuccess();
        }
        catch (Exception ex)
        {
            Error = ErrorText.From(ex);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task ForgotAsync()
    {
        var ok = await _dialogs.ConfirmAsync("Şifremi unuttum",
            "Güvenlik nedeniyle şifre kurtarılamaz; çünkü tüm verileriniz bu şifreyle şifrelenmiştir.\n\n" +
            "Sıfırlarsanız kayıtlı sunucularınız, komutlarınız ve işlem geçmişiniz SİLİNİR ve yeni bir hesap oluşturursunuz. " +
            "Sunucunun kendisinde hiçbir şey değişmez.",
            "Her şeyi sil ve sıfırla", danger: true);
        if (!ok) return;
        VaultService.Reset();
        Reset();
    }
}
