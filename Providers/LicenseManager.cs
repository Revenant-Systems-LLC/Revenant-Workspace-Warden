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
            0x5C, 0x8E, 0x3A, 0xF1, 0x72, 0xB4, 0x9D, 0x06,
            0xC8, 0x41, 0xE7, 0x2D, 0x94, 0xF0, 0x5B, 0x38,
            0x1E, 0xA9, 0x67, 0xC3, 0x80, 0x4F, 0xD2, 0xEB,
            0x15, 0x73, 0xBC, 0x29, 0x86, 0x4C, 0xD8, 0x07
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
