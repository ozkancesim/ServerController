using System;
using System.Net.Sockets;
using Renci.SshNet.Common;

namespace ServerController.Services;

/// <summary>Kullanıcıya doğrudan gösterilebilecek, Türkçe açıklamalı hata.</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ErrorText
{
    public static string From(Exception ex) => ex switch
    {
        UserFacingException u => u.Message,
        OperationCanceledException => "İşlem iptal edildi veya zaman aşımına uğradı.",
        SshAuthenticationException => "Sunucu kullanıcı adı veya şifresi hatalı.",
        SshOperationTimeoutException => "Sunucu zamanında yanıt vermedi.",
        SshConnectionException => "Sunucu bağlantısı koptu. Tekrar deneyin.",
        SocketException s => s.SocketErrorCode switch
        {
            SocketError.HostNotFound => "Sunucu adresi bulunamadı. Adresi kontrol edin.",
            SocketError.ConnectionRefused => "Sunucu bağlantıyı reddetti. Port numarasını kontrol edin.",
            SocketError.TimedOut => "Sunucuya ulaşılamadı (zaman aşımı). IP'niz bloklanmış olabilir.",
            _ => "Ağ hatası: " + s.Message,
        },
        AggregateException a when a.InnerException != null => From(a.InnerException),
        _ => ex.Message,
    };
}
