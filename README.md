# Revenant Workspace Warden

An AI coding **companion** for Windows — a floating workspace that puts a tutor, a quiz
answer-engine, screen-capture OCR, clipboard review, and a built-in security scanner one hotkey
away. Bring your own local model (Ollama) or a cloud provider. Built on .NET 10 + WPF.

Part of the Revenant Systems toolset.

## Features
- **Tutor mode** — reviews your code against fully customizable tutor materials and replies in a
  structured Summary / Issues / Before-After / Teaching Point / Practice format.
- **Quiz mode** — a direct answer-key engine (no hand-holding).
- **Sidekick security scan** — runs the RevenantHardening (RSH) rule engine over a project and
  reports a letter grade, score, and per-finding *Why / Fix*. *(Pro)*
- **Screen capture + OCR** — grab the active window and pull its text via Tesseract.
- **Clipboard review** — send whatever you just copied straight to the model.
- **File attach** — drop a source file in for review (5 MB guard).
- **8 model backends** — local **Ollama** plus Anthropic, Gemini, OpenAI, OpenRouter, Groq, xAI,
  and MegaLLM. API keys are stored encrypted (Windows DPAPI), never in the repo.

## Requirements
- **Windows 10/11** (WPF, DPAPI, per-user data paths).
- **.NET 10 SDK** (targets `net10.0-windows`).
- **The sibling `revenant-workspace-sidekick` repo** — required to build (see *Repo layout*).
- *Optional:* **Ollama** for local models (`ollama serve`, default `http://localhost:11434`).
- *Optional:* **Tesseract `eng.traineddata`** for OCR (see *OCR setup*).

## Repo layout (important)
Warden references the Sidekick scanner via **relative** project references, so the sibling repo
must be cloned into the same parent folder:

```
<parent>\
├── Revenant-Workspace-Warden\        (this repo)
│   └── RevenantWorkspaceWarden.csproj
└── revenant-workspace-sidekick\      (sibling — clone next to this repo)
    └── src\
        ├── RevenantWorkspaceSidekick.Core\
        └── RevenantWorkspaceSidekick.Rules\
```

The `.csproj` references `..\revenant-workspace-sidekick\src\RevenantWorkspaceSidekick.Core` and
`..\revenant-workspace-sidekick\src\RevenantWorkspaceSidekick.Rules`. If the sibling folder is missing, the build fails.

## Build & run
```powershell
# from the Revenant-Workspace-Warden folder
dotnet build
dotnet run
```

## Where your data lives (per-user, portable)
| What | Location |
|------|----------|
| Settings | `%AppData%\RevenantWarden\settings.json` |
| Encrypted API keys / license | `%AppData%\RevenantWarden\` (DPAPI, per Windows user) |
| Chat sessions | `Documents\RevenantWarden\ChatSessions\` |
| Tutor materials | `Documents\RevenantWarden\TutorMaterials\` |
| Screen captures | `%LocalAppData%\RevenantWorkspaceWarden\screen_captures\` |

Nothing is written next to the executable, so Warden runs fine from Program Files or any read-only
location.

## Default hotkeys
| Action | Keys |
|--------|------|
| Show / hide Warden | `Ctrl+Alt+F5` |
| Clipboard review | `Ctrl+Alt+Num/` |
| Screen capture + OCR | `Ctrl+Alt+F2` |

Rebind any of them in-app under Engine Settings.

## Customizing the tutor
On first tutor use, Warden creates `Documents\RevenantWarden\TutorMaterials\tutor_profile.md`. Drop
any `.md` or `.txt` files in that folder — course syllabi, coding standards, style guides — and each
one is injected into the tutor's system prompt. Files are re-read on every dispatch, so edit or add
anytime.

## Model providers & API keys
Pick a provider in Engine Settings. For cloud providers, add your key via **Manage API Keys**; keys
are encrypted with Windows DPAPI (current-user scope) under `%AppData%\RevenantWarden\`, never in
code or the repo. Local Ollama needs no key — Warden will start `ollama serve` if it isn't already
running and wait for the model list to come up.

## OCR setup
OCR needs Tesseract's `eng.traineddata`. Warden searches the app folder, then `%AppData%`,
`Downloads`, and `Desktop`. If it's missing, OCR warns and the rest of the app keeps working.

## Pro activation
Tutor, Quiz, and the Sidekick scanner are Pro features. Activate with:

```
/activate <your-license-key>
```

Licenses are signed keys, checked offline against a public key built into Warden, and stored
encrypted on this machine.

---
*Revenant Systems LLC.*
