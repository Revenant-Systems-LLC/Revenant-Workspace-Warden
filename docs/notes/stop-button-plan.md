# Plan: "Stop generation" button for Warden

## Context
A coder needs to be able to kill an in-flight LLM response (wrong answer, runaway ramble,
picked the wrong model). Warden currently cannot: `OllamaService.SendToProviderAsync` hardcodes
`var ct = CancellationToken.None;` (OllamaService.cs:99), feeding a dead token into a cancellation
pipeline that is otherwise already built — every `ILLMProvider.ChatAsync` takes a `CancellationToken`
(ILLMProvider.cs:19) and `OllamaProvider` already honors it (`SendAsync(..., ct)` + a per-line
`ct.ThrowIfCancellationRequested()`). So this is mostly *enabling* wiring that exists, plus a button.

Goal: a Stop control that appears while a response is streaming and halts it cleanly, returning
Warden to a ready state without the "Rage" error styling.

## Constraints (Warden AGENTS.md + Birds-of-Odin)
Writes only under M:\Projects. New files free; edits to existing files are pre-approved via this plan.
Verify with `dotnet build`; runtime testing happens with Dave (do not launch unannounced).

## Changes

### 1. `OllamaService.cs` — accept a real token (EDIT)
- `SendToProviderAsync(string prompt, string? lessonContext = null, CancellationToken ct = default)`.
- Delete `var ct = CancellationToken.None;` (line ~99). Use the parameter.
- It already passes `ct` to `haiku.ChatAsync(..., ct)` (quiz path) and `provider.ChatAsync(..., ct)` —
  no further change there.

### 2. `MainWindow.xaml.cs` — own the token, expose Stop (EDIT)
- Add field: `private CancellationTokenSource? _activeGeneration;`
- In `DispatchLlmAsync` (line 541): before the call, cancel/dispose any prior CTS, then
  `_activeGeneration = new CancellationTokenSource();` and call
  `await _ollamaService.SendToProviderAsync(prompt, lessonContext, _activeGeneration.Token);`
  In `finally`: dispose + null `_activeGeneration`, and call `SetLoadingState(false)`.
- Add a `catch (OperationCanceledException)` **before** the general `catch (Exception)` (line 550):
  post `[Warden] Stopped.` and do NOT call `SetWardenState(true)` (cancellation is not an error).
- Add `private void StopGeneration_Click(object sender, RoutedEventArgs e) => _activeGeneration?.Cancel();`
- In `SetLoadingState(bool isLoading)` (line 504): show the Stop button when `isLoading`, hide it otherwise
  (single source of truth for the control's visibility).

### 3. `MainWindow.xaml` — the button (EDIT)
- Add a `◼ Stop` button near the input row (by `AttachBtn`, ~line 299), `Visibility="Collapsed"` by
  default, `Click="StopGeneration_Click"`. `SetLoadingState` toggles its visibility.

### 4. Providers — confirm the token reaches the HTTP call (EDIT if needed)
- `OllamaProvider.ChatAsync` already threads `ct` into `SendAsync` — good.
- Verify `AnthropicProvider`, `OpenAICompatibleProvider`, `GeminiProvider` pass their `ct` into their
  `HttpClient.SendAsync/GetAsync` and streaming reads; thread it where it's currently ignored. If a
  provider swallows `ct`, Cancel still unblocks the UI (the exception surfaces to DispatchLlmAsync),
  but the underlying request won't abort until the next await — so thread it for a clean stop.

## Edge cases
- Rapid re-send / overlapping generations: cancel + dispose the previous CTS before starting a new one.
- Cancel after completion: `_activeGeneration` is null in `finally`; `StopGeneration_Click` null-checks.
- Providers that catch `OperationCanceledException` internally (Ollama does, line ~131) return null;
  DispatchLlmAsync's cancel-catch covers the rest.

## Verification
- `dotnet build` → 0/0.
- With Dave (AM): select Ollama + a model, send a long prompt, hit **Stop** mid-stream → generation
  halts within a line or two, a `Stopped.` message shows, Warden returns to ready (Sage Gold, not Rage),
  and the next prompt works normally.

## Est. effort
Small–medium. ~1 XAML button, ~15 lines in MainWindow, a 1-line signature change in OllamaService,
plus a quick token-threading pass across 3 provider classes.
