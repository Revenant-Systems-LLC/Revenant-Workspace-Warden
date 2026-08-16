# Deprecated Warden icon assets

Retired 2026-08-09. Kept, not deleted. Nothing in the build references these.

Replaced by `Assets\rww_logo.png`, `Assets\RWW_RAGE.png`, and `Assets\RWW_LOGO.ico`,
all sourced from the canonical branding folder
`B:\The-Ossuary\Revenant-Systems\Branding-Marketing\revenantsystems-net\assets`.

## RWW_SAGEGOLD.ico

Was `<ApplicationIcon>`, the Window icon, and the success state of
`SetWardenState`. Two defects:

1. **Opaque black background.** Every pixel at alpha 255; corners measured
   `#010201`. The likely cause is visible in the canonical folder — there is a
   `RWW_SAGEGOLD.jpg` and no matching PNG. JPEG cannot carry an alpha channel,
   so building the icon from it baked the black in permanently.
2. **Single 256x256 frame.** Windows had to downscale 256 to 16 on the fly for
   the title bar and Explorer details view every time.

## RWW_RAGE.ico

Was the error state of `SetWardenState`. Transparency was fine, but it was an
early flat orange/red design that no longer matched the obsidian/gold/stone
material language, and it was also a single 256x256 frame. Dave: "there was like
a real early on version of it and it's no good, I'm not using it."

## The replacement, and one trap worth remembering

Two formats, deliberately:

- `RWW_LOGO.ico` — 7 frames (16/24/32/48/64/128/256). `<ApplicationIcon>` **only**.
- `rww_logo.png` / `RWW_RAGE.png` — the Window's `Icon=` and the runtime swap in
  `SetWardenState`.

Do not point `Window.Icon` or `SetWardenState` at the `.ico`. WPF's icon loaders
(`ImageSourceConverter`, `BitmapFrame.Create`, `BitmapImage`) all take
**Frames[0]** of a multi-frame icon, which is the 16x16 — Windows then upscales
that into alt-tab and the taskbar. Measured 2026-08-09: the `.ico` via the XAML
path yields 16x16, the PNG yields full resolution.
