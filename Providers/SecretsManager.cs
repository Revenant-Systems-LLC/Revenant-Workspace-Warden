using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden.Providers
{
    public class HotkeyBinding
    {
        public uint Modifiers  { get; set; }
        public uint VirtualKey { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public class AppConfig
    {
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string MegaLlmBaseUrl { get; set; } = "https://ai.megallm.io";
        public string SelectedProvider { get; set; } = "Ollama";
        public string SelectedModel { get; set; } = "revenant/axiom-14b";
        public bool HasCompletedTutorial { get; set; } = false;

        // Ctrl+Alt defaults — safe, no collisions with browsers or IDEs
        public HotkeyBinding ToggleHotkey { get; set; } = new()
            { Modifiers = 0x0003, VirtualKey = 0x74, DisplayName = "Ctrl+Alt+F5" };
        public HotkeyBinding ClipboardHotkey { get; set; } = new()
            { Modifiers = 0x0003, VirtualKey = 0x6F, DisplayName = "Ctrl+Alt+Num/" };
        public HotkeyBinding ScreenCaptureHotkey { get; set; } = new()
            { Modifiers = 0x0003, VirtualKey = 0x71, DisplayName = "Ctrl+Alt+F2" };
    }

    internal static class SecretsManager
    {
        private static readonly string CredentialsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevenantWarden");
        private static readonly string SettingsFile = Path.Combine(CredentialsDir, "settings.json");
        // Older builds stored settings next to the .exe, which silently fails to persist when the
        // app runs from a protected location (e.g. Program Files). Kept only to migrate once.
        private static readonly string LegacySettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppConfig LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(CredentialsDir);

                // One-time migration: pull settings from the legacy exe-directory location into
                // AppData so they persist regardless of where the app is installed.
                if (!File.Exists(SettingsFile) && File.Exists(LegacySettingsFile))
                {
                    try { File.Copy(LegacySettingsFile, SettingsFile); } catch { /* best effort */ }
                }

                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null) return config;
                }
            }
            catch { /* Ignore and return default */ }
            return new AppConfig();
        }

        public static void SaveConfig(AppConfig config)
        {
            try
            {
                Directory.CreateDirectory(CredentialsDir);
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { /* Ignore */ }
        }

        private static string GetKeyFilePath(string keyName)
        {
            if (!Directory.Exists(CredentialsDir))
            {
                Directory.CreateDirectory(CredentialsDir);
            }
            // Hash the keyname so the filename isn't blatantly obvious
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(keyName));
            string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return Path.Combine(CredentialsDir, $"{hex}.dat");
        }

        public static bool HasEncryptedKey(string keyName)
        {
            return File.Exists(GetKeyFilePath(keyName));
        }

        public static void DeleteEncryptedKey(string keyName)
        {
            string path = GetKeyFilePath(keyName);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { DebugLog($"Failed to delete key {keyName}: {ex.Message}"); }
            }
        }

        public static void SaveEncryptedKey(string keyName, string plainTextKey)
        {
            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainTextKey);
                // Encrypt using the current Windows user's DPAPI scope
                byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(GetKeyFilePath(keyName), encryptedBytes);
                
                // Clear plain text bytes from memory
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to encrypt and save key: {ex.Message}");
            }
        }

        public static async Task<SecureString?> GetApiKeyAsync(string keyName, Action<string>? logMessage = null)
        {
            string path = GetKeyFilePath(keyName);
            if (!File.Exists(path))
            {
                logMessage?.Invoke($"No DPAPI key found for {keyName}. Please configure it in Engine Settings -> Manage API Keys.");
                return null;
            }

            try
            {
                byte[] encryptedBytes = await File.ReadAllBytesAsync(path);
                byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                
                string plainText = Encoding.UTF8.GetString(plainBytes);
                var secureKey = new SecureString();
                foreach (char c in plainText) secureKey.AppendChar(c);
                secureKey.MakeReadOnly();

                // Clear plain text from memory immediately
                Array.Clear(plainBytes, 0, plainBytes.Length);
                plainText = new string('\0', plainText.Length); // weak clear, but better than nothing

                return secureKey;
            }
            catch (Exception ex)
            {
                logMessage?.Invoke($"Failed to decrypt {keyName}. The Windows credential vault might have changed, or the file is corrupted. Error: {ex.Message}");
                return null;
            }
        }

        public static string? UnsecureKey(SecureString? secureKey)
        {
            if (secureKey == null || secureKey.Length == 0) return null;

            IntPtr bstr = Marshal.SecureStringToBSTR(secureKey);
            try
            {
                return Marshal.PtrToStringBSTR(bstr);
            }
            finally
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
        }

        private static void DebugLog(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[SecretsManager] {message}");
        }
    }
}
