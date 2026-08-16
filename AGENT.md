# AGENT.md — Revenant-Workspace-Warden (project)

**Project:** Revenant Workspace Warden  
**Location:** `M:\Projects\Revenant-Workspace-Warden`  
**Role:** Project-level loader for this repo. **This file is not machine law.**

Machine law is only:

`C:\Program Files\ClaudeCode\NOMOS-LOGOS.md`

Bindings: `M:\birds-of-odin\MACHINE-INDEX.md`  
State: `M:\Birds-of-Odin\references\MUNNIN.md`

---

## Read first (Grok / any agent)

1. Nomos Logos  
2. Machine index  
3. Muninn  
4. Grok agent home (if Grok): `C:\Users\Dave\.grok\AGENT.md` · `GROK.md`  
5. Optional surfaces: `M:\AGENT.md`, `M:\Projects\AGENT.md` (loaders, not law)  
6. This file  
7. This project’s `AGENTS.md` (project conventions / LLM behavior for this workspace)

---

## Project overview

Windows WPF / .NET app that manages AI agent workspaces, sessions, worktrees, and related tooling (providers, voice, OCR, chat). Meta-tool: changes here have high leverage; prefer small scoped changes and explicit review.

---

## Project-specific notes (subordinate to Nomos)

- Work for this repo lives under `M:\Projects\Revenant-Workspace-Warden` (write zone = machine index / Nomos).  
- Prefer a dedicated branch; do not develop on `main`/`master` without Dave’s direction.  
- Edit / delete / move of existing files need explicit approval (Nomos destructive set). Create is not destructive.  
- `AGENTS.md` in this folder holds detailed project behavioral guidance — still **below** Nomos on any conflict.

---

## Key local docs

- `AGENTS.md` — project LLM / workspace behavioral guidance  
- `implementation_plan.md`, `task.md`, `walkthrough.md` — task context when present  
- `RevenantWorkspaceWarden.csproj` — .NET WPF app

---

**Update policy:** Edits to this file need Dave’s explicit approval (edit = destructive).

*Corrected 2026-07-22: no longer treats M:\AGENT.md / Projects AGENT.md as living law.*
