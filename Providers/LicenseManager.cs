using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevenantWorkspaceWarden.Providers
{
    // Local license activation. Keys are ECDSA-signed (format v2, see LicenseKey.cs) and
    // checked against embedded public keys, so nothing in the client can mint one.
    // Stored encrypted via AES-CBC with a machine-derived key.
    // Swap ValidateAndActivate for a server call later — nothing else changes.
    internal static class LicenseManager
    {
        private static readonly string _keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevenantWarden",
            "license.dat");

        private static bool _activated;
        private static bool _loaded;

        public static bool IsActivated
        {
            get
            {
                if (!_loaded) TryLoadSavedKey();
                return _activated;
            }
        }

        public static bool ValidateAndActivate(string rawKey)
        {
            try
            {
                if (!LicenseKey.IsValidPro(rawKey)) return false;

                _activated = true;
                _loaded    = true;
                SaveKey(rawKey);
                return true;
            }
            catch { return false; }
        }

        public static void Deactivate()
        {
            _activated = false;
            try { if (File.Exists(_keyFilePath)) File.Delete(_keyFilePath); } catch { }
        }

        private static void TryLoadSavedKey()
        {
            _loaded = true;
            try
            {
                if (!File.Exists(_keyFilePath)) return;
                var stored  = File.ReadAllBytes(_keyFilePath);
                var iv      = stored[..16];
                var payload = stored[16..];
                using var aes = Aes.Create();
                aes.Key = GetMachineKey();
                var decrypted = aes.DecryptCbc(payload, iv);
                ValidateAndActivate(Encoding.UTF8.GetString(decrypted));
            }
            catch { /* corrupted / different machine — stay free */ }
        }

        private static void SaveKey(string rawKey)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_keyFilePath)!);
                using var aes = Aes.Create();
                aes.Key = GetMachineKey();
                aes.GenerateIV();
                var encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(rawKey), aes.IV);
                File.WriteAllBytes(_keyFilePath, aes.IV.Concat(encrypted).ToArray());
            }
            catch { /* non-fatal — user re-enters key next launch */ }
        }

        private static byte[] GetMachineKey()
        {
            var entropy = $"RWW-{Environment.MachineName}-v1";
            return SHA256.HashData(Encoding.UTF8.GetBytes(entropy));
        }
    }
}
