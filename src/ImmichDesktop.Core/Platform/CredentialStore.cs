using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.Core.Platform;

/// <summary>
/// Stores the access token / API key.
/// Windows: encrypted with DPAPI (only this Windows user can decrypt it).
/// Linux: a file readable only by the current user (0600). A libsecret backend can slot in here later.
/// </summary>
public static class CredentialStore
{
    static readonly byte[] Entropy = "ImmichDesktop.Credentials.v1"u8.ToArray();

    public static AuthCredential? Load(string? path = null)
    {
        path ??= AppPaths.CredentialsFile;
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<AuthCredential>(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            Log.Warn("Stored credentials could not be read", ex);
            return null;
        }
    }

    public static void Save(AuthCredential credential, string? path = null)
    {
        path ??= AppPaths.CredentialsFile;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credential));
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string? path = null)
    {
        path ??= AppPaths.CredentialsFile;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not delete stored credentials", ex);
        }
    }
}
