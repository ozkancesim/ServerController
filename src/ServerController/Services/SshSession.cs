using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerController.Models;

namespace ServerController.Services;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : (Output + "\n" + Error).Trim();

    public CommandResult EnsureOk(string what)
    {
        if (!Ok)
        {
            var detail = Combined.Trim();
            if (detail.Length > 400) detail = detail[..400] + "…";
            throw new UserFacingException($"{what} başarısız oldu (kod {ExitCode}).\n{detail}");
        }
        return this;
    }
}

/// <summary>Tek bir sunucuya SSH bağlantısı. Komutlar root olarak tek tek çalıştırılır.</summary>
public sealed class SshSession : IDisposable
{
    private const string Prelude =
        "export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH; export LC_ALL=C; ";

    private readonly ServerProfile _profile;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private SshClient? _client;
    private bool _disposed;

    public SshSession(ServerProfile profile) => _profile = profile;

    public ServerProfile Profile => _profile;

    /// <summary>(yeni parmak izi, eski parmak izi) → kullanıcı onayladı mı?</summary>
    public Func<string, string?, Task<bool>>? HostKeyPrompt { get; set; }

    /// <summary>Kullanıcı yeni bir sunucu kimliğini onayladığında tetiklenir.</summary>
    public event Action<string>? HostKeyAccepted;

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>Sunucunun gördüğü istemci IP'si (bağlandıktan sonra dolar).</summary>
    public string? ClientIp { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected) return;
            _client?.Dispose();
            _client = null;

            var pass = _profile.Password;
            var kbd = new KeyboardInteractiveAuthenticationMethod(_profile.Username);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = pass;
            };
            var info = new ConnectionInfo(_profile.Host.Trim(), _profile.Port, _profile.Username,
                new PasswordAuthenticationMethod(_profile.Username, pass), kbd)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            bool rejected = false;
            string? accepted = null;
            client.HostKeyReceived += (_, e) =>
            {
                var fp = e.FingerPrintSHA256;
                if (string.Equals(_profile.HostKeyFingerprint, fp, StringComparison.Ordinal))
                {
                    e.CanTrust = true;
                    return;
                }
                var ok = HostKeyPrompt != null && HostKeyPrompt(fp, _profile.HostKeyFingerprint).GetAwaiter().GetResult();
                e.CanTrust = ok;
                if (ok) accepted = fp; else rejected = true;
            };

            try
            {
                // Sunucu kimliği onayı diyalog beklerken arayüz iş parçacığını kilitlememek için arka planda bağlan.
                await Task.Run(() => client.ConnectAsync(ct), ct);
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (rejected)
                    throw new UserFacingException("Sunucu kimliği onaylanmadığı için bağlantı iptal edildi.", ex);
                if (ex is SshAuthenticationException)
                    throw new UserFacingException("Sunucu kullanıcı adı veya şifresi hatalı.", ex);
                if (ex is SocketException or SshOperationTimeoutException or SshConnectionException or OperationCanceledException)
                    throw new UserFacingException(
                        $"{_profile.Host}:{_profile.Port} adresine bağlanılamadı. {ErrorText.From(ex)}", ex);
                throw;
            }

            _client = client;
            if (accepted != null)
            {
                _profile.HostKeyFingerprint = accepted;
                HostKeyAccepted?.Invoke(accepted);
            }

            var who = await RunCoreAsync("echo \"${SSH_CLIENT%% *}\"", TimeSpan.FromSeconds(15), ct);
            ClientIp = IpUtil.Normalize(who.Output.Trim());
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<CommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        return await RunCoreAsync(command, timeout ?? TimeSpan.FromMinutes(2), ct);
    }

    private async Task<CommandResult> RunCoreAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var cmd = _client!.CreateCommand(Prelude + Normalize(command));
        try
        {
            await cmd.ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException($"Komut {timeout.TotalSeconds:0} saniyede tamamlanmadı (zaman aşımı).");
        }
        return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result ?? "", cmd.Error ?? "");
    }

    /// <summary>Uzun süren komutların çıktısını canlı olarak aktarır. Çıkış kodunu döner.</summary>
    public async Task<int> StreamAsync(string command, Action<string> onText, CancellationToken ct)
    {
        await ConnectAsync(ct);
        using var cmd = _client!.CreateCommand(Prelude + "exec 2>&1; " + Normalize(command));
        var exec = cmd.ExecuteAsync(ct);
        var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8);
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(), ct);
                if (n <= 0) break;
                onText(new string(buffer, 0, n));
            }
            await exec;
        }
        catch (OperationCanceledException)
        {
            try { cmd.CancelAsync(); } catch { /* yoksay */ }
            throw;
        }
        return cmd.ExitStatus ?? -1;
    }

    /// <summary>
    /// Windows satır sonlarını (CRLF) Linux'a uygun hale getirir. Kaynak kod Windows'ta derlendiğinde çok satırlı
    /// betikler ve kullanıcının kopyalayıp yapıştırdığı komutlar "\r" içerir; bash bunları sözdizimi hatası sayar.
    /// </summary>
    public static string Normalize(string command) => command.Replace("\r\n", "\n").Replace('\r', '\n');

    public void Dispose()
    {
        _disposed = true;
        try { _client?.Disconnect(); } catch { /* yoksay */ }
        _client?.Dispose();
        _client = null;
    }
}
