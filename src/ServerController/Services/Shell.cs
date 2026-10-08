using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ServerController.Services;

public static partial class Shell
{
    /// <summary>Bir metni bash için güvenli şekilde tek tırnak içine alır.</summary>
    public static string Q(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    [GeneratedRegex(@"^[A-Za-z0-9@._\-]+$")]
    private static partial Regex SafeTokenRegex();

    public static string Token(string value, string what = "değer")
    {
        if (!SafeTokenRegex().IsMatch(value))
            throw new UserFacingException($"Geçersiz {what}: {value}");
        return value;
    }
}

public static class IpUtil
{
    /// <summary>IP veya CIDR aralığını doğrular ve normalize eder. Geçersizse null.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        var slash = s.IndexOf('/');
        if (slash < 0)
            return IPAddress.TryParse(s, out var ip) && IsPlain(s) ? ip.ToString() : null;

        if (!IPAddress.TryParse(s[..slash], out var net) || !int.TryParse(s[(slash + 1)..], out var bits)) return null;
        var max = net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (bits < 0 || bits > max) return null;
        if (bits == max) return net.ToString();
        return $"{net}/{bits}";
    }

    // "1" gibi kısaltılmış yazımları ("0.0.0.1") kabul etmeyelim.
    private static bool IsPlain(string s) => s.Contains(':') || s.Count(c => c == '.') == 3;

    public static bool IsRange(string value) => value.Contains('/');

    /// <summary>ip, ipOrRange içinde mi? (eşitlik veya CIDR kapsaması)</summary>
    public static bool Contains(string ipOrRange, string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var slash = ipOrRange.IndexOf('/');
        if (slash < 0)
            return IPAddress.TryParse(ipOrRange, out var other) && other.Equals(addr);
        if (!IPAddress.TryParse(ipOrRange[..slash], out var net) || !int.TryParse(ipOrRange[(slash + 1)..], out var bits))
            return false;
        if (net.AddressFamily != addr.AddressFamily) return false;
        var a = addr.GetAddressBytes();
        var n = net.GetAddressBytes();
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>Bloklanacak değer verilen IP'yi kapsıyor mu (kendini kilitleme kontrolü)?</summary>
    public static bool Covers(string value, string? ip) =>
        !string.IsNullOrEmpty(ip) && Contains(value, ip);
}
