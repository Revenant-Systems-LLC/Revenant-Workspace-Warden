# AGENT.md — Revenant-Workspace-Warden (Project Level)

**Project:** Revenant Workspace Warden  
**Location:** M:\Projects\Revenant-Workspace-Warden  
**Purpose:** Local instructions for this specific project. These supplement (and are subordinate to) the higher-level rules.

**Related Global Files (Read These First):**
- `M:\AGENT.md` (M: drive root – primary persistent rules for all work on this drive)
- `M:\Projects\AGENT.md` (Projects root – covers all Revenant projects)
- `M:\Projects\Revenant-Workspace-Warden\AGENTS.md` (existing detailed behavioral and guardrail rules for LLMs operating in/around this workspace)

---

## Project Overview

Revenant Workspace Warden is a Windows WPF / .NET application designed to manage AI agent workspaces, sessions, worktrees, and related tooling. It acts as a "warden" or host for local AI interactions (LLM providers, voice commands, OCR, chat sessions, etc.).

Because this project is *meta* (it helps manage how AI agents like Grok, Claude, etc. operate in workspaces), it must exemplify the highest standards of the safety and operational rules defined at the M: and Projects levels.

## Core Operating Rules for This Project (Reinforced)

All work in this repository **must** follow the rules in the higher files, with these project-specific emphases:

- **M: Drive Only**: All development, file changes, and git work for this project happen under M:\. No writes or primary work on other drives.
- **Branch Only**: Always create and work on a dedicated branch. Never edit directly on main/master. Suggested branch naming: `grok/<descriptive-name>` or similar when using Grok.
- **Write Confirmation**: Explicit user approval is required before any create, edit, move, delete, or other write-based action. Do not assume permission. Propose changes clearly and wait for confirmation (natural language is fine; assumption is not).
- **Rules Files**: Read and respect `M:\AGENT.md`, `M:\Projects\AGENT.md`, this file, and especially the existing `AGENTS.md` in this folder. The AGENTS.md contains strict behavioral rules that apply to *all* LLMs and AI agents in this workspace.

## Key Existing Documentation in This Project

- `AGENTS.md` — Primary detailed rules for LLM behavior, technical guardrails, secret handling, and Revenant Systems policies. This is the main reference for how AI tools should behave here.
- `implementation_plan.md`, `task.md`, `walkthrough.md` — Current task context and planning documents.
- Source is a .NET 10 Windows WPF app (RevenantWorkspaceWarden.csproj) with providers for Ollama, Gemini, local models, voice/Whisper, OCR/Tesseract, etc.

## Recommendations for AI-Assisted Work Here

- When starting a Grok (or other AI) session for this project, launch from `M:\Projects\Revenant-Workspace-Warden` on a fresh branch.
- Use the global confirmation protocol and branch discipline.
- Because this tool manages workspaces and agents, changes here have high leverage — err on the side of more review and smaller scoped changes.
- Any modifications should strengthen (or at minimum not weaken) the safety and isolation features the Warden is meant to provide.

---

**Update Policy**: Changes to this AGENT.md require explicit user approval. It should be re-read at the start of any session working in this directory.

This file exists to make the global M: safety protocol easily discoverable and project-specific when working inside Revenant-Workspace-Warden.