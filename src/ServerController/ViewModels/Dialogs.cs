using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerController.Services;

namespace ServerController.ViewModels;

public abstract partial class DialogViewModel : ViewModelBase
{
    public DialogHost? Host { get; set; }
    public string Title { get; init; } = "";
    protected void Close() => Host?.Remove(this);
}

/// <summary>Pencere içinde üst üste açılabilen cam görünümlü diyalogları yönetir.</summary>
public sealed class DialogHost : ViewModelBase
{
    public ObservableCollection<DialogViewModel> Items { get; } = new();

    public bool HasDialog => Items.Count > 0;

    public DialogHost() => Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDialog));

    public void Show(DialogViewModel d)
    {
        d.Host = this;
        Items.Add(d);
    }

    public void Remove(DialogViewModel d) => Items.Remove(d);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Evet", bool danger = false, string? cancelText = "Vazgeç")
    {
        var d = new ConfirmDialogViewModel { Title = title, Message = message, ConfirmText = confirmText, IsDanger = danger, CancelText = cancelText };
        Show(d);
        return d.Result.Task;
    }

    public Task AlertAsync(string title, string message) => ConfirmAsync(title, message, "Tamam", cancelText: null);

    public Task<bool> FormAsync(FormDialogViewModel form)
    {
        Show(form);
        return form.Result.Task;
    }

    public Task ShowTextAsync(string title, string text)
    {
        var d = new TextDialogViewModel { Title = title, Text = text };
        Show(d);
        return d.Result.Task;
    }

    /// <summary>Komutu canlı çıktı penceresinde çalıştırır. İptal edilirse null döner.</summary>
    public async Task<int?> RunStreamingAsync(string title, Func<Action<string>, CancellationToken, Task<int>> run)
    {
        var d = new OutputDialogViewModel { Title = title };
        Show(d);
        return await d.RunAsync(run);
    }
}

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    public string Message { get; init; } = "";
    public string ConfirmText { get; init; } = "Evet";
    public string? CancelText { get; init; }
    public bool HasCancel => CancelText != null;
    public bool IsDanger { get; init; }
    public bool IsNormal => !IsDanger;
    public TaskCompletionSource<bool> Result { get; } = new();

    [RelayCommand] private void Confirm() { Close(); Result.TrySetResult(true); }
    [RelayCommand] private void Cancel() { Close(); Result.TrySetResult(false); }
}

public sealed partial class TextDialogViewModel : DialogViewModel
{
    public string Text { get; init; } = "";
    public TaskCompletionSource<bool> Result { get; } = new();
    [RelayCommand] private void Ok() { Close(); Result.TrySetResult(true); }
    [RelayCommand] private Task CopyAsync() => Clip.SetAsync(Text);
}

public static class Clip
{
    public static async Task SetAsync(string text)
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;
        if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(text);
    }
}

public enum FieldKind { Text, Password, Choice, Check, MultiLine }

public sealed record ChoiceOption(string Label, string Value)
{
    public override string ToString() => Label;
}

public sealed partial class FormField : ViewModelBase
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Watermark { get; init; }
    public string? Hint { get; init; }
    public FieldKind Kind { get; init; }
    public List<ChoiceOption> Options { get; init; } = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _checked;
    [ObservableProperty] private ChoiceOption? _selected;

    public bool IsTextLike => Kind is FieldKind.Text or FieldKind.Password or FieldKind.MultiLine;
    public bool IsChoice => Kind == FieldKind.Choice;
    public bool IsCheck => Kind == FieldKind.Check;
    public bool HasHint => !string.IsNullOrEmpty(Hint);
    public char PasswordChar => Kind == FieldKind.Password ? '●' : '\0';
    public bool AcceptsReturn => Kind == FieldKind.MultiLine;
    public double MinHeight => Kind == FieldKind.MultiLine ? 90 : 0;
    public bool ShowLabel => Kind != FieldKind.Check;

    public static FormField TextField(string key, string label, string text = "", string? watermark = null, string? hint = null) =>
        new() { Key = key, Label = label, Text = text, Watermark = watermark, Hint = hint, Kind = FieldKind.Text };

    public static FormField PasswordField(string key, string label, string? hint = null) =>
        new() { Key = key, Label = label, Hint = hint, Kind = FieldKind.Password };

    public static FormField MultiLineField(string key, string label, string text = "", string? hint = null) =>
        new() { Key = key, Label = label, Text = text, Hint = hint, Kind = FieldKind.MultiLine };

    public static FormField CheckField(string key, string label, bool value, string? hint = null) =>
        new() { Key = key, Label = label, Checked = value, Hint = hint, Kind = FieldKind.Check };

    public static FormField ChoiceField(string key, string label, IEnumerable<ChoiceOption> options, string? selectedValue = null, string? hint = null)
    {
        var list = options.ToList();
        return new()
        {
            Key = key, Label = label, Hint = hint, Kind = FieldKind.Choice, Options = list,
            Selected = list.FirstOrDefault(o => o.Value == selectedValue) ?? list.FirstOrDefault(),
        };
    }
}

public sealed partial class FormDialogViewModel : DialogViewModel
{
    public string Message { get; init; } = "";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string ConfirmText { get; init; } = "Kaydet";
    public bool IsDanger { get; init; }
    public ObservableCollection<FormField> Fields { get; } = new();

    /// <summary>Hata metni döndürürse diyalog kapanmaz.</summary>
    public Func<FormDialogViewModel, string?>? Validate { get; init; }

    [ObservableProperty] private string? _error;

    public TaskCompletionSource<bool> Result { get; } = new();

    public FormDialogViewModel(params FormField[] fields)
    {
        foreach (var f in fields) Fields.Add(f);
    }

    public FormField this[string key] => Fields.First(f => f.Key == key);
    public string Text(string key) => this[key].Text.Trim();
    public string RawText(string key) => this[key].Text;
    public bool Checked(string key) => this[key].Checked;
    public string Choice(string key) => this[key].Selected?.Value ?? "";

    [RelayCommand]
    private void Confirm()
    {
        var err = Validate?.Invoke(this);
        if (err != null) { Error = err; return; }
        Close();
        Result.TrySetResult(true);
    }

    [RelayCommand] private void Cancel() { Close(); Result.TrySetResult(false); }
}

public sealed partial class OutputDialogViewModel : DialogViewModel
{
    private const int MaxChars = 300_000;
    private readonly StringBuilder _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private string _status = "Çalışıyor…";
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private bool _failed;

    public async Task<int?> RunAsync(Func<Action<string>, CancellationToken, Task<int>> run)
    {
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushPending());
        timer.Start();
        int? code = null;
        try
        {
            code = await Task.Run(() => run(Append, _cts.Token));
            Status = code == 0 ? "✅ Tamamlandı" : $"⚠️ İşlem hata koduyla bitti ({code})";
            Succeeded = code == 0;
            Failed = code != 0;
        }
        catch (OperationCanceledException)
        {
            Status = "⏹ İptal edildi";
            Failed = true;
        }
        catch (Exception ex)
        {
            Append("\n" + ErrorText.From(ex) + "\n");
            Status = "⛔ Hata";
            Failed = true;
            code = -1;
        }
        finally
        {
            timer.Stop();
            FlushPending();
            IsRunning = false;
        }
        await _closed.Task;
        return _cts.IsCancellationRequested ? null : code;
    }

    private void Append(string s)
    {
        lock (_pending) _pending.Append(s.Replace("\r\n", "\n"));
    }

    private void FlushPending()
    {
        string chunk;
        lock (_pending)
        {
            if (_pending.Length == 0) return;
            chunk = _pending.ToString();
            _pending.Clear();
        }
        var t = Text + StripAnsi(chunk);
        if (t.Length > MaxChars) t = "…(eski çıktı kısaltıldı)…\n" + t[^MaxChars..];
        Text = t;
    }

    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-9;?]*[A-Za-z]|\r(?!\n)", "");

    [RelayCommand]
    private void Cancel()
    {
        if (IsRunning) _cts.Cancel();
    }

    [RelayCommand]
    private void CloseDialog()
    {
        if (IsRunning) return;
        Close();
        _closed.TrySetResult();
    }

    [RelayCommand]
    private Task CopyAsync() => Clip.SetAsync(Text);
}
