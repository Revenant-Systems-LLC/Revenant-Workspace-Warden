using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevenantWorkspaceWarden.Providers
{
    // HMAC-SHA256 local license validation.
    // Key format: WARD-XXXXXXXX-XXXXXXXX-XXXXXXXX (24 hex chars = 12 bytes)
    //   Byte[0]     = tier (0x01 = Pro)
    //   Bytes[1-5]  = random (5 bytes, ensures uniqueness)
    //   Bytes[6-11] = first 6 bytes of HMAC-SHA256(bytes[0..5], _secret)
    // Stored encrypted via AES-CBC with a machine-derived key.
    // Swap ValidateAndActivate for a server call later — nothing else changes.
    internal static class LicenseManager
    {
        private static readonly byte[] _secret =
        {
            0xCA, 0x28, 0xC1, 0xF9, 0x0A, 0xF8, 0x68, 0xCA,
            0xE3, 0x93, 0x65, 0x55, 0xB9, 0xE2, 0x70, 0xD2,
            0x04, 0x04, 0x0E, 0xE2, 0x2F, 0x37, 0x4A, 0x31,
            0x6C, 0x30, 0x5F, 0x6B, 0x1B, 0xF3, 0x5E, 0xD2
        };

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
            if (string.IsNullOrWhiteSpace(rawKey)) return false;

            var cleaned = rawKey.Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (cleaned.StartsWith("WARD")) cleaned = cleaned[4..];
            if (cleaned.Length != 24) return false;

            try
            {
                var bytes    = Convert.FromHexString(cleaned);
                var payload  = bytes[..6];
                var checksum = bytes[6..];

                using var hmac = new HMACSHA256(_secret);
                var computed = hmac.ComputeHash(payload);
                if (!computed.Take(6).SequenceEqual(checksum)) return false;
                if (bytes[0] != 0x01) return false;

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

        /// <summary>
        /// Dev tool — generate a valid Pro key for distribution.
        /// </summary>
        public static string GenerateProKey()
        {
            var random = new byte[5];
            RandomNumberGenerator.Fill(random);
            var payload = new byte[] { 0x01 }.Concat(random).ToArray();
            using var hmac = new HMACSHA256(_secret);
            var sig = hmac.ComputeHash(payload).Take(6).ToArray();
            var hex = Convert.ToHexString(payload.Concat(sig).ToArray());
            return $"WARD-{hex[..8]}-{hex[8..16]}-{hex[16..24]}";
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
