using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ServerController.Models;

namespace ServerController.Services;

/// <summary>
/// Uygulama verilerini SQLite veritabanında tutar (portable: exe'nin yanındaki "data" klasörü).
/// - Uygulama şifresi: PBKDF2-SHA256 ile tuzlanmış HASH olarak saklanır (geri çözülemez).
/// - Sunucu şifreleri: SSH ile bağlanırken gerektiği için hash'lenemez; rastgele bir ana anahtarla
///   AES-256-GCM ile ŞİFRELENİR. Ana anahtar da uygulama şifresinden türetilen anahtarla sarılıdır,
///   yani doğru uygulama şifresi olmadan sunucu şifreleri okunamaz.
/// </summary>
public sealed class VaultService
{
    private const int Iterations = 310_000;
    private const int MaxHistory = 10_000;

    private readonly object _lock = new();
    private byte[]? _key;

    public VaultData Data { get; private set; } = new();
    public bool IsUnlocked => _key != null;

    public static string DataDirectory { get; } = ResolveDataDirectory();
    public static string FilePath => Path.Combine(DataDirectory, "servercontroller.db");

    private static string ResolveDataDirectory()
    {
        var env = Environment.GetEnvironmentVariable("SERVERCONTROLLER_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }
        // Portable kullanım: verileri programın yanındaki "data" klasöründe tut.
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".yazma-testi");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch
        {
            // Program yazılamayan bir yerdeyse (örn. Program Files) kullanıcı klasörüne düş.
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServerController");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        c.Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS servers (
                id TEXT PRIMARY KEY, sort INTEGER NOT NULL, name TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL,
                username TEXT NOT NULL, password_enc BLOB NOT NULL, allowlist TEXT NOT NULL, host_key TEXT, notes TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commands (
                id TEXT PRIMARY KEY, sort INTEGER NOT NULL, name TEXT NOT NULL, command TEXT NOT NULL,
                description TEXT NOT NULL, confirm INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, server TEXT NOT NULL,
                action TEXT NOT NULL, detail TEXT NOT NULL, success INTEGER NOT NULL);
            """);
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static Dictionary<string, string> ReadMeta(SqliteConnection c)
    {
        var d = new Dictionary<string, string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public static bool Exists
    {
        get
        {
            if (!File.Exists(FilePath)) return false;
            using var c = Open();
            return ReadMeta(c).ContainsKey("pwd_hash");
        }
    }

    // ------------------------------------------------------------------ hesap

    public void Create(string user, string password)
    {
        lock (_lock)
        {
            _key = RandomNumberGenerator.GetBytes(32);
            Data = VaultData.CreateDefault(user.Trim());
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var t in new[] { "meta", "servers", "commands", "settings", "history" })
                Exec(c, $"DELETE FROM {t}", tx);
            WriteCredentials(c, tx, user, password);
            WriteData(c, tx);
            tx.Commit();
        }
    }

    public bool TryUnlock(string user, string password)
    {
        lock (_lock)
        {
            using var c = Open();
            var meta = ReadMeta(c);
            var iterations = int.Parse(meta["pwd_iterations"]);
            var hash = Derive(user, password, Convert.FromBase64String(meta["pwd_salt"]), iterations);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(meta["pwd_hash"])))
                return false;

            var kek = Derive(user, password, Convert.FromBase64String(meta["kek_salt"]), iterations);
            byte[] key;
            try { key = Decrypt(kek, Convert.FromBase64String(meta["wrapped_key"]), "master"); }
            catch (CryptographicException) { return false; }

            _key = key;
            Data = ReadData(c, meta.GetValueOrDefault("user_name", user.Trim()));
            return true;
        }
    }

    public void ChangePassword(string user, string newPassword)
    {
        lock (_lock)
        {
            if (_key == null) throw new InvalidOperationException("Veritabanı kilitli.");
            Data.UserName = user.Trim();
            using var c = Open();
            using var tx = c.BeginTransaction();
            WriteCredentials(c, tx, user, newPassword);
            tx.Commit();
        }
    }

    private void WriteCredentials(SqliteConnection c, SqliteTransaction tx, string user, string password)
    {
        var pwdSalt = RandomNumberGenerator.GetBytes(16);
        var kekSalt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(user, password, pwdSalt, Iterations);
        var kek = Derive(user, password, kekSalt, Iterations);
        var values = new Dictionary<string, string>
        {
            ["schema_version"] = "1",
            ["user_name"] = user.Trim(),
            ["pwd_iterations"] = Iterations.ToString(),
            ["pwd_salt"] = Convert.ToBase64String(pwdSalt),
            ["pwd_hash"] = Convert.ToBase64String(hash),
            ["kek_salt"] = Convert.ToBase64String(kekSalt),
            ["wrapped_key"] = Convert.ToBase64String(Encrypt(kek, _key!, "master")),
        };
        foreach (var (k, v) in values)
            Exec(c, "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)", tx, ("$k", k), ("$v", v));
    }

    public void Lock()
    {
        lock (_lock)
        {
            if (_key != null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
            Data = new VaultData();
        }
    }

    public static void Reset()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    // ------------------------------------------------------------------ veri

    /// <summary>Sunucuları, komutları ve ayarları veritabanına yazar.</summary>
    public void Save()
    {
        lock (_lock)
        {
            if (_key == null) return;
            using var c = Open();
            using var tx = c.BeginTransaction();
            WriteData(c, tx);
            tx.Commit();
        }
    }

    private void WriteData(SqliteConnection c, SqliteTransaction tx)
    {
        Exec(c, "DELETE FROM servers", tx);
        for (int i = 0; i < Data.Servers.Count; i++)
        {
            var s = Data.Servers[i];
            Exec(c, """
                INSERT INTO servers (id, sort, name, host, port, username, password_enc, allowlist, host_key, notes)
                VALUES ($id, $sort, $name, $host, $port, $user, $pass, $allow, $hk, $notes)
                """, tx,
                ("$id", s.Id), ("$sort", i), ("$name", s.Name), ("$host", s.Host), ("$port", s.Port), ("$user", s.Username),
                ("$pass", Encrypt(_key!, Encoding.UTF8.GetBytes(s.Password), s.Id)),
                ("$allow", s.AllowlistName), ("$hk", s.HostKeyFingerprint), ("$notes", s.Notes));
        }

        Exec(c, "DELETE FROM commands", tx);
        for (int i = 0; i < Data.Commands.Count; i++)
        {
            var m = Data.Commands[i];
            Exec(c, "INSERT INTO commands (id, sort, name, command, description, confirm) VALUES ($id, $sort, $n, $c, $d, $f)", tx,
                ("$id", m.Id), ("$sort", i), ("$n", m.Name), ("$c", m.Command), ("$d", m.Description), ("$f", m.Confirm ? 1 : 0));
        }

        Exec(c, "INSERT OR REPLACE INTO settings (key, value) VALUES ('app', $v)", tx, ("$v", JsonSerializer.Serialize(Data.Settings)));
        Exec(c, "INSERT OR REPLACE INTO settings (key, value) VALUES ('last_server', $v)", tx, ("$v", Data.LastServerId ?? ""));
    }

    private VaultData ReadData(SqliteConnection c, string userName)
    {
        var data = new VaultData { UserName = userName };
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, host, port, username, password_enc, allowlist, host_key, notes FROM servers ORDER BY sort";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                data.Servers.Add(new ServerProfile
                {
                    Id = id,
                    Name = r.GetString(1),
                    Host = r.GetString(2),
                    Port = r.GetInt32(3),
                    Username = r.GetString(4),
                    Password = Encoding.UTF8.GetString(Decrypt(_key!, (byte[])r[5], id)),
                    AllowlistName = r.GetString(6),
                    HostKeyFingerprint = r.IsDBNull(7) ? null : r.GetString(7),
                    Notes = r.GetString(8),
                });
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, command, description, confirm FROM commands ORDER BY sort";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                data.Commands.Add(new SavedCommand
                {
                    Id = r.GetString(0), Name = r.GetString(1), Command = r.GetString(2), Description = r.GetString(3), Confirm = r.GetInt32(4) != 0,
                });
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(0) == "app") data.Settings = JsonSerializer.Deserialize<AppSettings>(r.GetString(1)) ?? new AppSettings();
                if (r.GetString(0) == "last_server" && r.GetString(1).Length > 0) data.LastServerId = r.GetString(1);
            }
        }
        return data;
    }

    // ------------------------------------------------------------------ işlem geçmişi

    public void AddHistory(HistoryEntry e)
    {
        lock (_lock)
        {
            if (_key == null) return;
            using var c = Open();
            Exec(c, "INSERT INTO history (time, server, action, detail, success) VALUES ($t, $s, $a, $d, $ok)", null,
                ("$t", e.Time.ToString("o")), ("$s", e.Server), ("$a", e.Action), ("$d", e.Detail), ("$ok", e.Success ? 1 : 0));
            Exec(c, $"DELETE FROM history WHERE id <= (SELECT id FROM history ORDER BY id DESC LIMIT 1 OFFSET {MaxHistory})");
        }
    }

    public List<HistoryEntry> GetHistory(string filter = "", int limit = 1000)
    {
        lock (_lock)
        {
            var list = new List<HistoryEntry>();
            if (_key == null) return list;
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT time, server, action, detail, success FROM history
                WHERE $f = '' OR action LIKE $like OR detail LIKE $like OR server LIKE $like
                ORDER BY id DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$f", filter);
            cmd.Parameters.AddWithValue("$like", "%" + filter + "%");
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HistoryEntry
                {
                    Time = DateTime.TryParse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue,
                    Server = r.GetString(1), Action = r.GetString(2), Detail = r.GetString(3), Success = r.GetInt32(4) != 0,
                });
            return list;
        }
    }

    public int CountHistory()
    {
        lock (_lock)
        {
            if (_key == null) return 0;
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM history";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            if (_key == null) return;
            using var c = Open();
            Exec(c, "DELETE FROM history");
        }
    }

    // ------------------------------------------------------------------ kripto

    private static byte[] Derive(string user, string password, byte[] salt, int iterations)
    {
        var secret = Encoding.UTF8.GetBytes(user.Trim().ToLowerInvariant() + "\n" + password);
        return Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>AES-256-GCM. "context" (örn. sunucu kimliği) ek doğrulama verisi olarak bağlanır.</summary>
    private static byte[] Encrypt(byte[] key, byte[] plain, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        var result = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        cipher.CopyTo(result, 28);
        return result;
    }

    private static byte[] Decrypt(byte[] key, byte[] blob, string context)
    {
        if (blob.Length < 28) throw new CryptographicException();
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(context));
        return plain;
    }
}
