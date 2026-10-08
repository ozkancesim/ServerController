using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerController.Models;

namespace ServerController.Services;

/// <summary>
/// Uygulamanın tüm verisini (sunucular, şifreler, komutlar, geçmiş) tek bir şifreli dosyada tutar.
/// Rastgele bir ana anahtar verileri AES-256-GCM ile şifreler; ana anahtar da
/// kullanıcı adı + uygulama şifresinden PBKDF2 ile türetilen anahtarla sarılır.
/// </summary>
public sealed class VaultService
{
    private const int Iterations = 310_000;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly object _lock = new();
    private byte[]? _key;
    private VaultHeader? _header;

    public VaultData Data { get; private set; } = new();
    public bool IsUnlocked => _key != null;

    public static string FilePath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("SERVERCONTROLLER_HOME");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServerController");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "vault.dat");
        }
    }

    public static bool Exists => File.Exists(FilePath);

    public void Create(string user, string password)
    {
        lock (_lock)
        {
            _key = RandomNumberGenerator.GetBytes(32);
            _header = WrapKey(_key, user, password);
            Data = VaultData.CreateDefault(user.Trim());
            WriteFile();
        }
    }

    public bool TryUnlock(string user, string password)
    {
        lock (_lock)
        {
            var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(FilePath))
                       ?? throw new InvalidDataException("Veri dosyası bozuk.");
            var kek = DeriveKek(user, password, Convert.FromBase64String(file.Salt), file.Iterations);
            byte[] key;
            try { key = Decrypt(kek, Convert.FromBase64String(file.WrappedKey)); }
            catch (CryptographicException) { return false; }

            var json = Decrypt(key, Convert.FromBase64String(file.Data));
            Data = JsonSerializer.Deserialize<VaultData>(json) ?? new VaultData();
            _key = key;
            _header = new VaultHeader(file.Salt, file.Iterations, file.WrappedKey);
            return true;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (_key == null) return;
            WriteFile();
        }
    }

    public void ChangePassword(string user, string newPassword)
    {
        lock (_lock)
        {
            if (_key == null) throw new InvalidOperationException("Kasa kilitli.");
            _header = WrapKey(_key, user, newPassword);
            Data.UserName = user.Trim();
            WriteFile();
        }
    }

    public void Lock()
    {
        lock (_lock)
        {
            if (_key != null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _header = null;
            Data = new VaultData();
        }
    }

    public static void Reset()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private void WriteFile()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(Data, JsonOpts);
        var file = new VaultFile
        {
            Version = 1,
            Salt = _header!.Salt,
            Iterations = _header.Iterations,
            WrappedKey = _header.WrappedKey,
            Data = Convert.ToBase64String(Encrypt(_key!, json)),
        };
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static VaultHeader WrapKey(byte[] key, string user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = DeriveKek(user, password, salt, Iterations);
        return new VaultHeader(Convert.ToBase64String(salt), Iterations, Convert.ToBase64String(Encrypt(kek, key)));
    }

    private static byte[] DeriveKek(string user, string password, byte[] salt, int iterations)
    {
        var secret = Encoding.UTF8.GetBytes(user.Trim().ToLowerInvariant() + "\n" + password);
        return Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    private static byte[] Encrypt(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var result = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        cipher.CopyTo(result, 28);
        return result;
    }

    private static byte[] Decrypt(byte[] key, byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException();
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return plain;
    }

    private sealed record VaultHeader(string Salt, int Iterations, string WrappedKey);

    private sealed class VaultFile
    {
        public int Version { get; set; }
        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string WrappedKey { get; set; } = "";
        public string Data { get; set; } = "";
    }
}
