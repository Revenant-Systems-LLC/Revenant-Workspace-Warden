# Fixes applied 2026-07-26 (~03:40)

Scope approved by Dave: the five code fixes from SHIP-PLAN-2026-07-26.md plus secret rotation.
Verified with `dotnet build` in both configurations: **Debug 0/0, Release 0/0.**

## 1. Stop button (SHIP-PLAN step 2, per STOP-BUTTON-PLAN.md)
- `OllamaService.cs` — `SendToProviderAsync` now takes `CancellationToken ct = default`; the dead
  `var ct = CancellationToken.None;` is gone.
- `MainWindow.xaml.cs` — `_activeGeneration` CTS field; `DispatchLlmAsync` cancels any in-flight
  generation, owns a fresh token, catches `OperationCanceledException` before the general catch
  (posts `[Warden] Stopped.`, no Rage state), and disposes in `finally` with a
  `ReferenceEquals` guard so an overlapping dispatch cannot dispose the newer CTS.
  `StopGeneration_Click` added.
- `MainWindow.xaml` — `◼ STOP` button added **inside `LoadingPanel`** rather than by AttachBtn.
  Deviation from the plan doc, deliberate: every provider already calls
  `SetLoadingState(true/false)` around its request, so the panel's visibility IS the loading
  state and the button shows/hides with zero extra wiring.
- Providers needed no edits: all five already thread `ct` into `SendAsync`/stream reads, check
  it per line, and catch `OperationCanceledException` internally.
- **Runtime verification still owed** (with Dave): send a long prompt on a local model, hit STOP
  mid-stream, confirm clean halt + Sage Gold + next prompt works.

## 2. Licence hygiene (step 3) + secret rotation (step 1)
- `LicenseManager.cs` — `_secret` rotated to new random 32 bytes (old value had been through
  agent context windows; zero keys ever issued, so nothing broke). **Any dev key minted under
  the old secret no longer validates** — mint a fresh one from a Debug build.
- `GenerateProKey()` wrapped in `#if DEBUG`. Verified: the string `GenerateProKey` appears in
  the Debug DLL and does **not** appear in the Release DLL. No callers existed, so no gating
  needed elsewhere.
- `README.md` — false "bind to the machine" claim replaced with "stored encrypted on this
  machine".
- **When key delivery is built server-side, the PHP endpoint must use the NEW secret bytes**
  (read them from `LicenseManager.cs:18-24`; do not put them in a repo or a chat).

## 3. Assembly metadata (step 4)
- `RevenantWorkspaceWarden.csproj` — Version 1.0.0, Product, Company, Authors, Copyright,
  Description added. Verified on the Release exe: ProductName "Revenant Workspace Warden",
  CompanyName "Revenant Systems LLC", FileVersion 1.0.0.0.

## 4. Assets glob narrowed (step 5)
- `Assets\**` copy replaced with the five files the app references: `Circuits.png`,
  `RWW_SAGEGOLD.ico`, `RWW_RAGE.ico`, `Ghost theory 1.ttf`, `Ghost theory 2.ttf`.
- Verified: `bin/Release/.../Assets/` contains exactly those five. No BACKGROUND.mp4, no alt
  marks, no unused fonts. Nothing over 7 MB in the whole Release output.
- Visual check owed at next launch: circuit graphic renders, Ghost fonts render.

## 5. UNPLANNED, forced by a pre-existing break: sidekick retarget
The build failed before any of the above could be verified, and not because of these edits:
**the sibling `revenant-workspace-sidekick` repo was restructured after the Jul 25 23:00 build**
(uncommitted in that repo). `src\RevenantHardening.Core` and `.Rules` are now empty husks
(bin/obj only); the live projects are `RevenantWorkspaceSidekick.Core/.Rules/.Cli`; the old
code survives only in `src_backup_pre_split\` and a stale worktree. The API is identical —
only the root namespace changed.

- `RevenantWorkspaceWarden.csproj` — both ProjectReferences retargeted to
  `RevenantWorkspaceSidekick.Core` / `.Rules`.
- `MainWindow.xaml.cs` — 7 occurrences of `RevenantHardening.` swapped to
  `RevenantWorkspaceSidekick.` (scan call sites + SidekickFindingVm).

If the sidekick split is still mid-flight in another session, a further rename there will
break Warden again; this retarget tracks the state as of tonight.

## Not touched (out of scope, would be deletes)
- `bin/Debug/` still holds the stale 1.5 GB `ggml-medium.en.bin`, `BACKGROUND.mp4`, the old
  `RevenantHardening.*.dll`s, and `RevenantHardening.Companion.exe`. Deleting is Dave's call.
  They are irrelevant to shipping as long as packaging comes from a fresh `dotnet publish -o`
  per SHIP-PLAN step 6.
