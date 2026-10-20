using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using RevenantWorkspaceWarden.Providers;
using WardenKeyMint;

namespace RevenantWorkspaceWarden.Tests
{
    // xUnit builds a new instance per test, so every test signs with its own fresh P-256 key.
    // The private half never leaves memory.
    public sealed class LicenseKeyTests : IDisposable
    {
        private const byte KeyId = 0x01;

        private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly IReadOnlyDictionary<byte, string> _publicKeys;

        public LicenseKeyTests()
        {
            _publicKeys = new Dictionary<byte, string> { [KeyId] = Spki(_signingKey) };
        }

        public void Dispose() => _signingKey.Dispose();

        // ── Valid ────────────────────────────────────────────────────────────────

        [Fact]
        public void MintedKeyIsValid()
        {
            var key = LicenseMinter.MintPro(_signingKey, KeyId);

            Assert.True(LicenseKey.IsValidPro(key, _publicKeys));
            Assert.StartsWith("WARD-", key);
            Assert.Equal(139, key.Length);
        }

        [Fact]
        public void CaseDashesWhitespaceAndPrefixDoNotMatter()
        {
            var key  = LicenseMinter.MintPro(_signingKey, KeyId);
            var body = key[(LicenseKey.Prefix.Length + 1)..].Replace("-", "");

            Assert.True(LicenseKey.IsValidPro(key.ToLowerInvariant(), _publicKeys));
            Assert.True(LicenseKey.IsValidPro(body, _publicKeys));
            Assert.True(LicenseKey.IsValidPro("  " + key.Replace("-", " - ") + "\r\n", _publicKeys));
            Assert.True(LicenseKey.IsValidPro(key[..70] + "\n" + key[70..], _publicKeys));    // wrapped by a mail client
            Assert.True(LicenseKey.IsValidPro(key.Replace('-', '–'), _publicKeys));      // en dashes
        }

        // Minted by the ship plan's Step 10 PHP code (openssl_sign, rww_p1363, rww_base32) under key
        // id 2 with a throwaway key whose private half was deleted. Both r and s of this signature
        // start with a zero byte, so it covers rww_p1363's padding. Keeps the client in step with PHP.
        private const string PhpPublicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELO7oMovN1qf99vHrNj1s2GW02y0O68soVSE9H3JDRtSz+oM2xSbA6Hi+/NfCiRnB2I3LQLZeIjJIkBMHAEKf5A==";
        private const string PhpMintedKey =
            "WARD-AIBADGPD-GB2PFIH7-EUAMSGL2-VN3JT5M3-M753B3DT-5XLZFOE6-I26TJYQS-5URL2RFM-36PUGAAI-HCS4AHOI-JBUYETNR-FS332ASF-V3YFV7IH-VFBOJLQM-ZEXU3VU4";

        [Fact]
        public void KeyMintedByStep10PhpIsValid()
        {
            var publicKeys = new Dictionary<byte, string> { [0x02] = PhpPublicKey };

            Assert.True(LicenseKey.IsValidPro(PhpMintedKey, publicKeys));
            Assert.False(LicenseKey.IsValidPro(PhpMintedKey, _publicKeys));
        }

        // ── Tampered ─────────────────────────────────────────────────────────────

        [Fact]
        public void EveryBitFlipIsRejected()
        {
            var bytes = LicenseKey.Decode(LicenseMinter.MintPro(_signingKey, KeyId))!;

            for (int i = 0; i < bytes.Length; i++)
                for (int bit = 0; bit < 8; bit++)
                {
                    var tampered = (byte[])bytes.Clone();
                    tampered[i] ^= (byte)(1 << bit);
                    Assert.False(LicenseKey.IsValidPro(LicenseKey.Encode(tampered), _publicKeys), $"byte {i}, bit {bit}");
                }
        }

        [Fact]
        public void EveryChangedCharacterIsRejected()
        {
            var key = LicenseMinter.MintPro(_signingKey, KeyId);

            for (int i = LicenseKey.Prefix.Length; i < key.Length; i++)
            {
                if (key[i] == '-') continue;
                var changed = key[..i] + (key[i] == 'A' ? 'B' : 'A') + key[(i + 1)..];
                Assert.False(LicenseKey.IsValidPro(changed, _publicKeys), $"character {i}");
            }
        }

        [Theory]
        [InlineData((byte)0x01, LicenseKey.TierPro)]          // format versions this build doesn't know
        [InlineData((byte)0x03, LicenseKey.TierPro)]
        [InlineData(LicenseKey.FormatVersion, (byte)0x00)]    // tiers other than Pro
        [InlineData(LicenseKey.FormatVersion, (byte)0x02)]
        public void SignedKeyWithWrongVersionOrTierIsRejected(byte version, byte tier)
        {
            var payload = new byte[LicenseKey.PayloadLength];
            payload[0] = version;
            payload[1] = KeyId;
            payload[2] = tier;

            Assert.False(LicenseKey.IsValidPro(LicenseMinter.Sign(_signingKey, payload), _publicKeys));
        }

        // ── Wrong key ────────────────────────────────────────────────────────────

        [Fact]
        public void KeySignedByAnotherKeyIsRejected()
        {
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            Assert.False(LicenseKey.IsValidPro(LicenseMinter.MintPro(otherKey, KeyId), _publicKeys));
        }

        [Fact]
        public void KeyIdMustMatchTheSigningKey()
        {
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKeys = new Dictionary<byte, string> { [0x01] = Spki(_signingKey), [0x02] = Spki(otherKey) };

            Assert.True(LicenseKey.IsValidPro(LicenseMinter.MintPro(otherKey, 0x02), publicKeys));
            Assert.False(LicenseKey.IsValidPro(LicenseMinter.MintPro(otherKey, 0x01), publicKeys));    // claims the wrong id
            Assert.False(LicenseKey.IsValidPro(LicenseMinter.MintPro(_signingKey, 0x03), publicKeys)); // id the client lacks
        }

        [Fact]
        public void MinterFindsOnlyKnownKeys()
        {
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            Assert.Equal(KeyId, LicenseMinter.FindKeyId(_signingKey, _publicKeys));
            Assert.Null(LicenseMinter.FindKeyId(otherKey, _publicKeys));
        }

        // ── Old and malformed keys ───────────────────────────────────────────────

        [Fact]
        public void OldHmacKeyIsRejected()
        {
            // Format v1: WARD- + 24 hex chars = tier || 5 random bytes || first 6 bytes of HMAC-SHA256.
            var payload = new byte[] { 0x01, 0x11, 0x22, 0x33, 0x44, 0x55 };
            var tag     = HMACSHA256.HashData(new byte[32], payload).Take(6);
            var hex     = Convert.ToHexString(payload.Concat(tag).ToArray());

            Assert.False(LicenseKey.IsValidPro($"WARD-{hex[..8]}-{hex[8..16]}-{hex[16..]}", _publicKeys));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("WARD")]
        [InlineData("WARD-")]
        [InlineData("not a license key")]
        public void MalformedInputIsRejected(string? input)
        {
            Assert.False(LicenseKey.IsValidPro(input, _publicKeys));
        }

        [Fact]
        public void WrongLengthOrAlphabetIsRejected()
        {
            var key = LicenseMinter.MintPro(_signingKey, KeyId);

            Assert.False(LicenseKey.IsValidPro(key[..^1], _publicKeys));          // one character short
            Assert.False(LicenseKey.IsValidPro(key + "A", _publicKeys));          // one character long
            Assert.False(LicenseKey.IsValidPro(key[..^1] + "1", _publicKeys));    // '1' is not base32
            Assert.False(LicenseKey.IsValidPro(key[..^1] + "=", _publicKeys));    // nor is padding
        }

        // ── Production table ─────────────────────────────────────────────────────

        [Fact]
        public void ProductionPublicKeysAreP256()
        {
            foreach (var spki in LicenseKey.PublicKeys.Values)
            {
                var der = Convert.FromBase64String(spki);
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(der, out var read);

                Assert.Equal(der.Length, read);
                Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, key.ExportParameters(false).Curve.Oid.Value);
            }
        }

        private static string Spki(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
