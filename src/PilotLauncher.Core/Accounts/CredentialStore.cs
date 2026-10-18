using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PilotLauncher.Core.Accounts;

public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI, bound to the current Windows user: other users and other machines cannot decrypt it.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "PilotLauncher.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

public static class PasswordGenerator
{
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+=?@";

    public static string Create(int length = 32)
    {
        if (length < 8)
            throw new ArgumentOutOfRangeException(nameof(length));
        var all = Lower + Upper + Digits + Symbols;
        var chars = new char[length];
        chars[0] = Pick(Lower);
        chars[1] = Pick(Upper);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++)
            chars[i] = Pick(all);
        RandomNumberGenerator.Shuffle<char>(chars);
        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}

/// <summary>Passwords of the isolated accounts, generated once and kept protected on disk. Nobody has to know them.</summary>
public sealed class CredentialStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    public CredentialStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public string? Get(string userName)
    {
        lock (_gate)
        {
            return Load().TryGetValue(userName, out var protectedValue)
                ? Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(protectedValue)))
                : null;
        }
    }

    public string GetOrCreate(string userName)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.TryGetValue(userName, out var existing))
                return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(existing)));
            var password = PasswordGenerator.Create();
            all[userName] = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(password)));
            Save(all);
            return password;
        }
    }

    public void Remove(string userName)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(userName))
                Save(all);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = File.OpenRead(_path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Json)
                     ?? throw new InvalidDataException($"Empty credential file: {_path}");
        return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
