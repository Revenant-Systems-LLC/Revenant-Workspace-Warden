using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RevenantWorkspaceWarden.Providers;

namespace WardenKeyMint
{
    // Mints Warden Pro license keys (format v2, see Providers/LicenseKey.cs).
    //   keygen <out.pem>          new P-256 signing key; prints the line to paste into LicenseKey.PublicKeys
    //   mint <key.pem> [count]    prints count new Pro keys (default 1), each with its serial
    //   verify <license-key>      checks a key against the public keys the client ships with
    // Run with: dotnet run --project tools/WardenKeyMint -- <command> ...
    // The private key stays outside every repo, backed up offline. Never paste it anywhere.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return args switch
                {
                    ["keygen", var outPath]                => KeyGen(outPath),
                    ["mint", var keyPath]                  => Mint(keyPath, 1),
                    ["mint", var keyPath, var count]
                        when int.TryParse(count, out var n) && n > 0 => Mint(keyPath, n),
                    ["verify", .. var parts] when parts.Length > 0 => Verify(string.Concat(parts)),
                    _                                      => Usage(),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static int KeyGen(string outPath)
        {
            var fullPath = Path.GetFullPath(outPath);
            if (InsideGitWorkTree(fullPath))
            {
                Console.Error.WriteLine($"error: {fullPath} is inside a git work tree. Keep the private key outside every repo.");
                return 1;
            }

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };   // never overwrite a key
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new StreamWriter(fullPath, options))
                file.Write(key.ExportPkcs8PrivateKeyPem());

            var nextId = (byte)(LicenseKey.PublicKeys.Keys.DefaultIfEmpty((byte)0).Max() + 1);
            Console.WriteLine($"Private key written to {fullPath}. Back it up offline; it never goes in a repo.");
            Console.WriteLine("Paste this line into LicenseKey.PublicKeys:");
            Console.WriteLine($"    [0x{nextId:X2}] = \"{Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}\",");
            return 0;
        }

        private static int Mint(string keyPath, int count)
        {
            using var key = LoadSigningKey(keyPath);
            var keyId = LicenseMinter.FindKeyId(key, LicenseKey.PublicKeys);
            if (keyId == null)
            {
                Console.Error.WriteLine("error: this key's public half is not in LicenseKey.PublicKeys, so the client would reject every key it signs.");
                return 1;
            }

            for (int i = 0; i < count; i++)
            {
                var licenseKey = LicenseMinter.MintPro(key, keyId.Value);
                if (!LicenseKey.IsValidPro(licenseKey))
                    throw new CryptographicException("A minted key failed the client's own check.");
                Console.WriteLine(licenseKey);
                Console.WriteLine($"  serial {Serial(licenseKey)}");
            }
            return 0;
        }

        private static int Verify(string licenseKey)
        {
            if (!LicenseKey.IsValidPro(licenseKey))
            {
                Console.WriteLine("INVALID: not a Pro key signed by any key in LicenseKey.PublicKeys.");
                return 1;
            }
            Console.WriteLine($"VALID: Pro, key id 0x{LicenseKey.Decode(licenseKey)![1]:X2}, serial {Serial(licenseKey)}");
            return 0;
        }

        private static int Usage()
        {
            Console.Error.WriteLine("usage: WardenKeyMint keygen <out.pem> | mint <key.pem> [count] | verify <license-key>");
            return 2;
        }

        private static ECDsa LoadSigningKey(string keyPath)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(keyPath));
            if (key.KeySize != 256)
            {
                key.Dispose();
                throw new CryptographicException("The signing key must be ECDSA P-256.");
            }
            return key;
        }

        private static string Serial(string licenseKey) =>
            Convert.ToHexString(LicenseKey.Decode(licenseKey)!.AsSpan(3, LicenseKey.SerialLength));

        // True if path is inside a git work tree: any folder above it holds a .git directory or file.
        private static bool InsideGitWorkTree(string path)
        {
            for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                    return true;
            return false;
        }
    }
}
