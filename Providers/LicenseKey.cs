using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RevenantWorkspaceWarden.Providers
{
    // Signed license keys, format v2. Design: docs/notes/license-signing-2026-10-20.md
    // Key format: WARD- + RFC 4648 base32 (no padding) of 75 bytes, a dash every 8 chars.
    //   Byte[0]      = format version (0x02)
    //   Byte[1]      = signing key id (selects a key in PublicKeys)
    //   Byte[2]      = tier (0x01 = Pro)
    //   Bytes[3-10]  = serial (8 random bytes, unique per issued key)
    //   Bytes[11-74] = ECDSA P-256 / SHA-256 signature, IEEE P1363 r||s,
    //                  over "RWW-LICENSE-V2" || bytes[0-10]
    // The client holds public keys only, so nothing in this binary can mint a key.
    // The private key stays with tools/WardenKeyMint, outside the repo.
    internal static class LicenseKey
    {
        public const string Prefix          = "WARD";
        public const byte   FormatVersion   = 0x02;
        public const byte   TierPro         = 0x01;
        public const int    PayloadLength   = 11;
        public const int    SerialLength    = 8;
        public const int    SignatureLength = 64;
        public const int    KeyLength       = PayloadLength + SignatureLength;   // 75 bytes = 120 base32 chars

        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        // Signing key id -> SubjectPublicKeyInfo (DER, base64) of a P-256 key.
        // `WardenKeyMint keygen` prints the line to paste here.
        internal static readonly IReadOnlyDictionary<byte, string> PublicKeys = new Dictionary<byte, string>
        {
            // [0x01] = Yggdrasil manual-minting key. Pending: Dave runs keygen (see the design note).
        };

        public static bool IsValidPro(string? rawKey) => IsValidPro(rawKey, PublicKeys);

        // Seam for tests: the same check against a caller-supplied key table.
        internal static bool IsValidPro(string? rawKey, IReadOnlyDictionary<byte, string> publicKeys)
        {
            var bytes = Decode(rawKey);
            if (bytes == null || bytes[0] != FormatVersion || bytes[2] != TierPro) return false;
            if (!publicKeys.TryGetValue(bytes[1], out var spki)) return false;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            return ecdsa.VerifyData(SignedMessage(bytes.AsSpan(0, PayloadLength)), bytes.AsSpan(PayloadLength),
                                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        // The bytes a signature covers. The context string keeps a license signature
        // from being valid for anything else signed with the same key.
        internal static byte[] SignedMessage(ReadOnlySpan<byte> payload)
        {
            ReadOnlySpan<byte> context = "RWW-LICENSE-V2"u8;
            var message = new byte[context.Length + payload.Length];
            context.CopyTo(message);
            payload.CopyTo(message.AsSpan(context.Length));
            return message;
        }

        // 75 key bytes -> "WARD-XXXXXXXX-...-XXXXXXXX".
        internal static string Encode(ReadOnlySpan<byte> keyBytes)
        {
            if (keyBytes.Length != KeyLength)
                throw new ArgumentException($"A license key is {KeyLength} bytes.", nameof(keyBytes));

            var text = new StringBuilder(Prefix, Prefix.Length + KeyLength * 2);
            int buffer = 0, bits = 0, chars = 0;
            foreach (var b in keyBytes)
            {
                buffer = ((buffer << 8) | b) & 0xFFF;   // never more than 12 live bits
                bits  += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    if (chars++ % 8 == 0) text.Append('-');
                    text.Append(Base32Alphabet[(buffer >> bits) & 31]);
                }
            }
            return text.ToString();
        }

        // Text -> the 75 key bytes, or null if it is not a v2-shaped key. Ignores case,
        // whitespace and any kind of dash (pasted keys pick up en dashes). WARD is optional.
        internal static byte[]? Decode(string? rawKey)
        {
            if (rawKey == null) return null;

            var text = new StringBuilder(rawKey.Length);
            foreach (var c in rawKey)
            {
                if (char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation) continue;
                text.Append(char.ToUpperInvariant(c));
            }

            const int bodyLength = KeyLength * 8 / 5;
            var body = text.ToString();
            if (body.Length == Prefix.Length + bodyLength && body.StartsWith(Prefix, StringComparison.Ordinal))
                body = body[Prefix.Length..];
            if (body.Length != bodyLength) return null;

            var bytes = new byte[KeyLength];
            int buffer = 0, bits = 0, n = 0;
            foreach (var c in body)
            {
                int value = Base32Alphabet.IndexOf(c);
                if (value < 0) return null;
                buffer = ((buffer << 5) | value) & 0xFFF;
                bits  += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes[n++] = (byte)(buffer >> bits);
                }
            }
            return bytes;
        }
    }
}
