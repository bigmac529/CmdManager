using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Client.Services;

/// <summary>Persists the session (access + refresh token) encrypted with DPAPI for the current Windows user.</summary>
public static class TokenStore
{
    private static readonly byte[] Entropy = "CmdManager.Session.v1"u8.ToArray();

    public static void Save(AuthResponse? session)
    {
        try
        {
            if (session is null)
            {
                Clear();
                return;
            }
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.SerializeToUtf8Bytes(session, CmdManagerJson.Options);
            var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppPaths.SessionFile, protectedBytes);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not save session: " + ex.Message);
        }
    }

    public static AuthResponse? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionFile))
                return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.SessionFile), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthResponse>(json, CmdManagerJson.Options);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not load session: " + ex.Message);
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                File.Delete(AppPaths.SessionFile);
        }
        catch (IOException)
        {
        }
    }
}
