# Signed license keys: design note (2026-10-20)

**Status:** approved by Dave on 2026-10-20 as written, with a hard cutover and no grace period.
Implemented on branch `yggdrasil/confident-clarke-yotywd`. The production public key is still
pending (see "The production key pair"). Answers the reopened review thread
`PRRT_kwDOSs9a7M6YHcy_` on [PR 5](https://github.com/Revenant-Systems-LLC/Revenant-Workspace-Warden/pull/5).

## Problem

`LicenseManager` checks keys with HMAC-SHA256 under a 32-byte secret compiled into the client
(`Providers/LicenseManager.cs:18-24`, used at `:57`). HMAC is symmetric: the bytes that verify a
key are the bytes that mint one. Moving `GenerateProKey()` behind `#if DEBUG` (0b3d813) removed the
convenience method, not the capability.

Checked on `main` (3135761) on 2026-10-20:

- The 32 secret bytes are in both the Debug and the Release `RevenantWorkspaceWarden.dll` (exact
  byte search), and both reference `HMACSHA256`.
- This repository is **public**. The secret, and the algorithm that turns it into a key, are
  readable on GitHub. Forging a key does not even need the exe.

## Decision: ECDSA P-256 signatures

The client holds a public key; the private key stays with whoever mints keys.

- **Not Ed25519.** .NET has no Ed25519 API: the net10.0 reference assembly has no EdDSA type, and
  Microsoft's docs state that Windows does not support EdDSA. Using it would put a third-party
  crypto library (NSec or BouncyCastle) into the client.
- **ECDSA P-256** is in `System.Security.Cryptography` on Windows (CNG) and Linux (OpenSSL), and
  PHP's bundled `openssl` extension can sign with it for the automated Step 10 path. The signature
  is 64 bytes, the same size as Ed25519's.

## Key format v2

| Bytes | Field | Value |
|---|---|---|
| 0 | format version | `0x02` |
| 1 | signing key id | `0x01`; selects a public key in the client |
| 2 | tier | `0x01` = Pro |
| 3-10 | serial | 8 random bytes, unique per issued key |
| 11-74 | signature | ECDSA P-256 / SHA-256, raw `r‖s` (IEEE P1363), over `"RWW-LICENSE-V2"` ‖ bytes 0-10 |

The text form is `WARD-` plus RFC 4648 base32 (no padding) of the 75 bytes, in 15 dash-separated
groups of 8: `WARD-XXXXXXXX-…-XXXXXXXX`, 139 characters. Old keys were 31.

- **Base32, not base64 or base58.** The activation box forces upper case
  (`CharacterCasing="Upper"`), which would corrupt a case-sensitive encoding. Base32 is
  case-insensitive, and coreutils `base32 -d` can decode a key during support.
- The verifier ignores case, dashes and whitespace. The `WARD` prefix is optional.
- The context string `RWW-LICENSE-V2` stops a license signature from being valid for anything else
  signed with the same key.
- A license is identified by its **serial**, not by the full string: ECDSA signatures are
  malleable, so one license has more than one valid encoding. If a key is ever posted publicly, a
  later build can carry a denylist of serials. Nothing is built for that now.
- The **key id** makes rotation possible. Id 1 is the manual-minting key on Yggdrasil. If
  `thanks.php` is ever built, the web host gets its own id 2, so a compromised host means dropping
  id 2 and re-issuing only the keys that host minted.

## Code

- New `Providers/LicenseKey.cs` holds decode, verify and the public key table (SPKI, base64). It
  has no secret and no signing code, and nothing WPF, so tests can compile it on Linux.
- `LicenseManager` keeps its public surface (`IsActivated`, `ValidateAndActivate`, `Deactivate`)
  and its `license.dat` storage. `_secret`, the HMAC check and `GenerateProKey()` are deleted.
- New `tools/WardenKeyMint` (console app, net10.0, never shipped):
  - `keygen <out.pem>` writes a PKCS#8 private key that only the current user can read (0600 on
    Unix, a one-rule ACL on Windows). It refuses to overwrite a file or to write inside a git work
    tree, and prints the public key line for `LicenseKey.cs`.
  - `mint <key.pem> [count]` refuses a private key whose public half is not in the client's table,
    and checks every key it prints with the client's own verifier.
  - `verify <key>` is for support.
- `RevenantWorkspaceWarden.csproj` gets one `DefaultItemExcludes` line for `tests\**;tools\**`.
  Without it the SDK's `**/*.cs` glob would compile the tool and the tests into the app.

## The production key pair

You generate it on Yggdrasil, never in an agent session; Step 1 of the ship plan rotated the HMAC
secret because it had been through agent context. Keep the private key outside any repo and back it
up offline. Lose it, and nobody can mint keys that shipped builds accept. If it leaks, rotate to a
new key id and re-issue. Only the public half comes back into the repo.

Run this from the repo, replacing the quoted placeholder, then paste the line it prints into
`LicenseKey.PublicKeys`. Until then the table is empty and no key validates.

```
dotnet run --project tools/WardenKeyMint -- keygen "<outside-any-repo>\warden-license-1.pem"
```

OpenSSL makes an equivalent key if you prefer (Git for Windows ships it):

```
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "<outside-any-repo>\warden-license-1.pem"
openssl pkey -in "<outside-any-repo>\warden-license-1.pem" -pubout
```

The second command prints the public key. Its base64 body is what goes into `LicenseKey.cs`.

## Migration: hard cutover and re-issue, no HMAC grace period

A grace period that still accepts HMAC keys has to ship the secret. This secret is public, so
anyone could unlock Pro for as long as the grace period lasts. As of 2026-07-26 no key had been
sold (ship plan §1.7, fixes-applied §2), so the only HMAC keys are your dev keys; mint v2
replacements. An install whose `license.dat` holds an old key drops to Free on its next launch,
the same as for any invalid stored key today.

On 2026-10-20 Dave confirmed that no key has gone to anyone else, so no allowlist was built. Had
keys reached buyers, the fallback was to accept exactly those keys until a fixed date, matched
against an embedded list of their SHA-256 hashes (no secret), while the buyers got v2 keys.

Once no client accepts HMAC keys, the old secrets in git history stop mattering, so history needs
no rewrite. A build with HMAC validation that has already been handed out will accept forged keys
forever, and nothing offline can fix that. Per the ship plan, no Release build had been distributed
as of 2026-07-26.

## Ship plan Step 10

- Manual path, still the launch recommendation: `dotnet run --project tools/WardenKeyMint -- mint <key.pem>`.
- Automated `thanks.php`: the `hash_hmac` snippet becomes `openssl_sign`, a DER-to-`r‖s`
  conversion and base32, signing as key id 2. The config holds the PEM's path instead of
  `warden_secret_hex`. Each key carries its serial, so the existing order log of keys is enough.
- Short "superseded" notes went on §1.6, the rotation paragraph in Step 1, and `fixes-applied` §2.
  That last section told readers to copy the secret bytes into PHP.

## Tests and verification

`tests/RevenantWorkspaceWarden.Tests` (xUnit, net10.0, runs on Linux) covers:

- valid keys, including lowercase, missing dashes, missing prefix and pasted whitespace;
- every single-bit flip of payload or signature, all rejected;
- a wrong public key and an unknown key id, rejected;
- a wrong version or tier, rejected even when properly signed;
- old HMAC-format keys, rejected;
- malformed input, rejected without throwing;
- the production key, which must be P-256;
- a key minted by the Step 10 PHP code, accepted as a known-answer vector.

Debug and Release builds stay at 0 warnings and 0 errors. In the Release DLL the old secret bytes
must be gone and `HMACSHA256` must be absent. The secret check is an exact byte search, because
`strings` does not reliably show binary data.

## Implemented and verified (2026-10-20)

- The app, the tool and the tests build in Debug and Release with 0 warnings and 0 errors.
- `dotnet test tests/RevenantWorkspaceWarden.Tests`: 21 passed. Four deliberate breaks of the
  verifier each fail the suite: skipping the signature check, dropping the tier check, changing the
  context string, and swapping two base32 letters. Only the PHP vector catches the last two.
- The Step 10 PHP code minted 3000 keys under a throwaway key, covering r and s of 31, 32 and 33
  DER bytes. The client's verifier accepted all 3000, and none under the wrong key id. The ship
  plan's PHP block, run verbatim, is idempotent per session and fails closed without its PEM. The
  throwaway private keys were shredded afterwards.
- Release and Debug `RevenantWorkspaceWarden.dll`: the old secret bytes are gone, and the binary
  has no `HMACSHA256`, `GenerateProKey`, `SignData` or minting code. The same scan on `main`
  finds the secret.
- `WardenKeyMint`: `keygen` refuses an existing file and a path in a git work tree, and writes the
  PEM as 0600 on Unix. On Windows it replaces the inherited ACL with one rule for the current user;
  that path compiles but has not run, so check it with `icacls` after the first `keygen`. `mint`
  refuses a key the client doesn't know. A key it mints passes `verify`, and coreutils
  `base32 -d` decodes it to `02 01 01` plus the serial.
- The Step 10 PHP block was first written without a lock across its read and write. Under 80
  concurrent requests it minted 30 different keys for one session and lost 38 of 41 log entries.
  With one exclusive lock it mints one key per session and keeps every entry, over three such runs.
  It fails closed on a missing PEM, an RSA or P-384 key, a corrupt log (left untouched) and an
  unwritable log.

## What this does not solve

Signatures stop key **forgery**. They do not stop someone **patching** `IsActivated` in the IL and
passing the patched exe around, and no offline check can. This moves Warden from "anyone can mint
keys from the public repo" to the normal state for offline licensing.

Theme Studio uses the same HMAC scheme (ship plan §1.6), and its repository is public too. The same
fix applies there, as a separate change.
