using System.Security.Cryptography;
using System.Text;

namespace Poltergeist;

/// <summary>The Haunt API key, encrypted for this Windows user (DPAPI) so it never sits on disk as plain text.</summary>
static class ApiKeyStore
{
    public static string Load()
    {
        try
        {
            if (!File.Exists(Paths.ApiKey)) return "";
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Paths.ApiKey), Encoding.UTF8.GetBytes("Poltergeist"), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }
    }

    public static void Save(string key)
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            if (string.IsNullOrWhiteSpace(key)) { if (File.Exists(Paths.ApiKey)) File.Delete(Paths.ApiKey); return; }
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Encoding.UTF8.GetBytes("Poltergeist"), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Paths.ApiKey, enc);
        }
        catch { }
    }
}
