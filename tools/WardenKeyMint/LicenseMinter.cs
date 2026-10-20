using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using RevenantWorkspaceWarden.Providers;

namespace WardenKeyMint
{
    // The signing half of LicenseKey. It lives here and in the tests, never in the client.
    internal static class LicenseMinter
    {
        // A new Pro key with a random serial, signed by signingKey under keyId.
        public static string MintPro(ECDsa signingKey, byte keyId)
        {
            var payload = new byte[LicenseKey.PayloadLength];
            payload[0] = LicenseKey.FormatVersion;
            payload[1] = keyId;
            payload[2] = LicenseKey.TierPro;
            RandomNumberGenerator.Fill(payload.AsSpan(3, LicenseKey.SerialLength));
            return Sign(signingKey, payload);
        }

        // Signs any 11-byte payload. Tests use it to build properly signed keys with bad fields.
        public static string Sign(ECDsa signingKey, byte[] payload)
        {
            if (payload.Length != LicenseKey.PayloadLength)
                throw new ArgumentException($"A payload is {LicenseKey.PayloadLength} bytes.", nameof(payload));

            var signature = signingKey.SignData(LicenseKey.SignedMessage(payload), HashAlgorithmName.SHA256,
                                                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var key = new byte[LicenseKey.KeyLength];
            payload.CopyTo(key, 0);
            signature.CopyTo(key, LicenseKey.PayloadLength);
            return LicenseKey.Encode(key);
        }

        // The key id the client knows signingKey's public half by, or null if it doesn't.
        public static byte? FindKeyId(ECDsa signingKey, IReadOnlyDictionary<byte, string> publicKeys)
        {
            var spki = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo());
            foreach (var (id, known) in publicKeys)
                if (known == spki) return id;
            return null;
        }
    }
}
