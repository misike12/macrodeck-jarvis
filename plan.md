# JARVIS — Macro Deck 3 Plugin Plan

Status: **IMPLEMENTED.** Every tool in this plan is built, registered and tested. The final section records
what was left out on purpose and why. Verified numbers are in `current workstate.md`.
Target: `win-x64` only, framework-dependent on .NET 10, no Node/Python runtimes.
Identity: `com.misike12.jarvis` / display name `JARVIS`.
Repo consulted: `https://github.com/Macro-Deck-App/Macro-Deck` (cloned to `%TEMP%\opencode\macrodeck3`).

---

## 0. Environment (verified)

| Item | State |
|---|---|
| .NET SDK | 10.0.400 (repo pins 10.0.401, `latestFeature` accepts it) |
| ASP.NET Core runtime | 10.0.11 — **required**, `run` and `test` start real Kestrel |
| `macrodeck-plugin` | **3.0.0-beta.14 already installed globally** |
| Package availability | only `-preview` / `-beta` 3.0 packages exist → all PackageReferences need `--prerelease` |

---

## 1. API keys

An NVIDIA NIM API key has been supplied for development use. It is treated as a
**local secret**, not a source artifact:

- Stored in `jarvis.settings.json` **next to the plugin exe, outside the source tree, gitignored** — never in a `.cs` file, never in `manifest.json`, never committed, never in an artifact.
- Read at runtime through the same resolution chain as every other secret, so moving it into the Macro Deck config flow later is a no-op for the code.
- Resolution order (first hit wins): integration config flow secret → environment variable → external `jarvis.settings.json` → user-picked file.
- Log redaction is already mandatory: `ProtocolDiagnostics.Redact` masks any detail key matching `secret|token|password|authorization|api[-_]?key|credential`. Never log the key, never log an `Authorization` header.
- **Before this plugin is ever shared or published, that key must be rotated.** It has been through a chat log.
- Same rule for the Picovoice AccessKey and any self-hosted NIM token.

Local file shape (gitignored, outside the repo):

```json
{
  "nvidia": { "apiKey": "nvapi-...", "baseUrl": "https://integrate.api.nvidia.com/v1" },
  "picovoice": { "accessKey": "..." },
  "selfHostedNim": { "baseUrl": "", "token": "" }
}
```

---

## 2. Hard platform constraints found in the source

These are not opinions; they are read out of the repo and they dictate the design.

### 2.1 The orb cannot be animated the obvious way

The widget-profile UI framework has **no animation primitive**:

- `ui/runtime/src/ui-components/ui-component-types.ts:1-32` — the registry is a closed 21-type list (`ui.stack` … `ui.first-fit`). No `animate`, `keyframes`, `timeline`.
- The registry is built once from two hardcoded arrays (`component-registry.ts:99-136`). No protocol message exists to register a definition, so a plugin can never add a component or a `tickPeriodMs`.
- **No CSS injection.** Every `setStyle` / `setClass` / `setClassName` / `setAttribute` call across all 70+ sites in `ui/runtime/src/ui-components/*.ts` uses a hardcoded literal name. The only dynamic one is `icon-${name}`, validated against `UI_ICON_VERSIONS`. Gradients go through `gradientCss` (`node-modifiers.ts:77-111`) which accepts structured objects and validated `#rrggbb`, never a raw CSS string.
- `tickPeriodMs` is implemented by exactly four host-owned components (`macrodeck-clock-dial`, `macrodeck-dynamic-text`, `macrodeck-progress-bar`, `macrodeck-progress-text`). Not reachable by a plugin.
- **SVG is rejected from plugins**: `protocol/.../Assets/UiResourceRules.cs:14-16` allows only `image/png`, `image/jpeg`, `image/webp`, `image/gif`. MacroDeck's own animated weather icons *are* animated SVGs; a plugin cannot do that.

### 2.2 The three mechanisms that actually work

| Mechanism | Smoothness | Patch traffic | Verdict |
|---|---|---|---|
| Animated GIF / animated WebP via `ui.image` + `UiResource` | native 60 fps | **zero** | **Primary.** Browser decodes it; `ui-image.component.ts:47-55` just sets `src`. |
| `ui.transform` + `rotation` property patches | ~25 Hz ceiling | 1 patch/frame | **Secondary**, slow motion only. |
| `borderStyle` = `heartbeat` / `breathing` / `blink` / `comet` / `ants` / `hue-shift` / `rgb` | native CSS | zero | **Free garnish**, fixed ring, phase-locked across clients. |

Animated WebP beats GIF for size and smoothness at the same frame rate — preferred where the source allows it.

### 2.3 Rate limits (read at runtime, never hardcoded)

`protocol/src/MacroDeck.Plugin.Protocol/Limits/ProtocolLimits.cs`:

| Constant | Value | Line |
|---|---|---|
| `MaxMessageBytes` | 262 144 | 9 |
| `MaxUiTreeBytes` | 196 608 | 112 |
| `MaxUiPatchBytes` | 65 536 | 116 |
| `MaxUiNodesPerTree` | 2 000 | 118 |
| `MaxUiUpdatesPerSecond` | **30** | 120 |
| `MaxUiUpdateBurst` | 90 | 122 |
| `MaxUiResourceBytes` | 2 MiB | 127 |
| `MaxUiResourceBytesPerPlugin` | 16 MiB | 131 |
| `MaxUiResourcesPerPlugin` | 256 | 134 |
| `MaxUiSessionsPerProvider` | 8 | 141 |
| `MaxUiWidgetSessionsPerProvider` | 128 | 154 |

Enforcement is a per-session token bucket in
`host/src/MacroDeckHost.Application/Ui/Sessions/UiSessionRegistry.cs`:
bucket starts at 90, refills `+30/sec`, one token per accepted patch (`:689-709`).
The validity/revision gates run **before** the budget is spent (`:464-465`), so a
rejected patch is free. On an empty bucket: first breach `Resync(RATE_LIMITED)`
(`:475-476`), second breach while a resync is pending `Terminate(RATE_LIMITED)`
(`:468-473`).

**Consequence:** batching does not help — one patch is one token regardless of how
many operations it carries. 30/s is a hard wall. Design target: **≤ 25 patches/sec
sustained**, leaving headroom.

### 2.4 Other host-enforced facts

- Plugin manifest supports `win-x64` only. Host launch is framework-dependent via a bundled .NET muxer.
- Only `host:adb` is actually enforced among declared permissions; the rest are declarative.
- Reserved routes: everything under `/_macrodeck`. Mapping there is **MDP2005**.
- Action ids must be unique across the **whole plugin process**, not per integration.
- `IPluginIntegration` deliberately has no `Id` / `Name` / `Version` / `IsInitialized`; restating them is **MDP1004**.
- `manifest.json` is the only identity source.
- `health` is `GET /_macrodeck/health` on the plugin's own loopback listener.

---

## 3. Capability mapping

| JARVIS feature | Macro Deck capability |
|---|---|
| Activate / Cancel / Toggle actions | `IActionDefinition` + `IActionExecutor` |
| Button colour per state | `IStateProviderActionDefinition` |
| The orb widget | `IWidgetTypeProvider` → `IUiProvider` widget surface |
| Widget configuration view | `IUiConfigFlowProvider` / widget `config` surface |
| Show state elsewhere on your deck | `IVariableProvider` (pushed, `SupportsPush`) |
| React to JARVIS finishing / errors | `IEventProvider` |
| Modal confirmations | `IActionInteractions.RequestItemPicker` / `IUiInteractions.ShowModalAsync` |
| Setup, keys, models, engines | `IConfigFlowProvider` (multi-step, OAuth-capable) |
| "Something is broken" banner | `IIntegrationIssueProvider` |
| Notification popups | `IIntegrationContext.Notifications` |
| Play a sound / macro on wake | `IIntegrationContext.Scripts.RunAsync` |

Chosen capabilities: `actions`, `variables`, `events`, `config-flow`, `ui`, `widget-type-provider`, `issues`, `localization`.

---

## 4. Actions

All ids are **frozen forever** — they are persisted inside user profiles.

| Action id | Purpose |
|---|---|
| `jarvis-activate` | Arm / start a session per the configured mode. |
| `jarvis-cancel` | Stop speech + LLM stream. See §6 for how deep. |
| `jarvis-toggle` | Activate when idle, cancel when active. |

Parameters on `jarvis-activate`: `mode` (Choice: `one-shot` \| `conversation` \| `arm-and-wait-for-wake-word`), `prompt` (optional Text, pre-fills the turn), `use-hotkey` (Toggle), `max-seconds` (Number, conversation timeout).
`jarvis-cancel`: `kill-running-command` (Toggle, default **false**).
`jarvis-toggle`: `mode` (Choice) only.

**Action modes** — `one-shot` (default) captures one utterance, answers, closes.
`conversation` stays in turn-taking until cancelled or `silence-timeout` seconds elapse.
`arm-and-wait-for-wake-word` opens the mic but waits for the keyword first.

**Button states** (`IStateProviderActionDefinition`, 2 s default poll):
`idle`, `listening`, `thinking`, `speaking`, `executing`, `confirming`, `error`, `unavailable`.
Colours follow the built-in `ActionStates` convention — always include an `unavailable` state.

---

## 5. The orb — golden middle path

Decision: **per-state animated asset as the smooth core, plus a `ui.transform` ring at ~25 Hz, plus a CSS `borderStyle` ring for free.**

### 5.1 Composition

```
ui.stack (fills tile, clip: circle)
├── ui.image            ← per-state animated WebP/GIF, the core   (2 MiB cap)
├── ui.transform        ← rotating outer ring, `rotation` patches (~25 Hz)
├── ui.gauge            ← static arc track, level driven by amplitude (cheap)
└── ui.modifier         ← glow: radial gradient background
```

Border of the whole tile uses `borderStyle: comet | hue-shift | ants` — real CSS keyframes, phase-locked across every client, zero protocol cost.

### 5.2 Assets

Generated at build time **from C#** (SkiaSharp → animated WebP), fully reproducible, no external art pipeline, no third-party artwork to attribute.
Target budget per state asset: **≤ 1.5 MiB** (2 MiB is a hard protocol ceiling).

Presets, all selectable in the widget config:

1. **Arc Reactor** — Iron Man 2008 HUD. Concentric segmented rings, cyan/white on deep blue-black, thin bright arcs, slow sweep.
2. **Halo** — clean modern assistant. Soft radial glow, single breathing halo, calm gradient.
3. **Pulse** — Cortana-ish. Particle swirl reacting to audio amplitude.

Plus: **custom image/GIF upload** so you can drop in your own.

### 5.3 Amplitude reactivity, staying legal

Naive 60 fps amplitude would need 60 patches/sec and get the session terminated.

Plan:
- Compute RMS in 10 ms audio windows (WASAPI), convert to a smoothed 0..1 level.
- Ship at **20 Hz**, not 60.
- **Deadband**: only emit a patch when the level moves more than `0.04`. Below that, emit nothing. A patch that changes nothing must never be emitted anyway — `UiView` skips the flush and does not advance the revision, which is a correctness rule, not an optimisation.
- Target ≤ **20 patches/sec**, cap 25.
- During `idle` the whole amplitude channel is suspended — zero traffic when nothing is happening.

---

## 6. Cancel semantics

Configurable, with a sensible default.

| Depth | Behaviour |
|---|---|
| `speech-and-stream` (default) | Abort TTS mid-word, abort the in-flight LLM HTTP stream, drop any partial reply. A shell command already launched is **left running**. |
| `stop-running-command` | Additionally kills the child process tree JARVIS started. |

Always configurable on the action and in settings. Spoken support too: "stop", "cancel that", "never mind" aborts speech/stream; "kill it" / "stop the command" escalates to killing the process.

Implementation: one `CancellationTokenSource` per turn chained to the session CTS. Child processes tracked in a registry so the kill path knows what to terminate (Windows `CREATE_NO_WINDOW` + Job Object, matching how MacroDeck's own supervisor does it).

---

## 7. Engines — all selectable, one default

### 7.1 Speech to text

| Provider | Default? | Notes |
|---|---|---|
| **whisper.cpp (local)** | **yes** | Fully offline. `whisper-cli.exe` + ggml model, both **downloaded on first run**, never bundled. |
| NIM cloud | fallback | `parakeet-tdt-0.6b-v2` (Free Endpoint) or `whisper-large-v3`. Same nvapi key. |
| Windows SAPI | last resort | Zero install, clearly worse. |

Models are **multilingual unless the name ends `.en`** — language is configurable.
Nothing ships in the artifact. A model manager downloads on demand with a progress
action, resume support, and a SHA-256 check.

Source: `huggingface.co/ggerganov/whisper.cpp`

| Model | Size | Use |
|---|---|---|
| `tiny-q5_1` | 31 MiB | fastest, rough |
| `base-q5_1` | 57 MiB | **default** — best balance for command-length audio |
| `small-q5_1` | 181 MiB | most accurate, still practical |
| `small-q8_0` | 252 MiB | |

The whisper.cpp release zip itself is also a first-run download (pinned version + checksum), not a bundled payload.

### 7.2 Text to speech

| Provider | Default? | Notes |
|---|---|---|
| **Piper (local ONNX)** | **yes** | Fully offline, redistributable voices, fast. The "golden middle path". Binary + voice `.onnx` are both first-run downloads. |
| Windows SAPI | fallback | Zero install, never fails. |
| NIM / Riva | optional | `magpie-tts-flow` or `magpie-tts-multilingual` (Free Endpoints). |

Silent fallback chain: Piper if a voice is installed, else SAPI. Voice choice is a dropdown populated at runtime from what is actually on disk.

### 7.3 LLM

All supported, one default, all configurable:

| Provider | Notes |
|---|---|
| **NVIDIA NIM cloud** | `https://integrate.api.nvidia.com/v1`, OpenAI-compatible `chat/completions`. |
| Self-hosted NIM | Configurable base URL + token. |
| Local llama.cpp | Downloaded native binary + local `.gguf`. Fully offline, no key. |

**Model picker is a free-text field with a live availability probe.** A `check-model` action hits the endpoint and reports reachable / not found / rate-limited / unauthorized. Same probe reused for the VLM and for STT/TTS models.

NVIDIA free tier reality: **no per-token billing**, but **~40 RPM shared per model** and ~1,000 credits. Fine for a personal assistant; not fine for streaming. Design accordingly.

### 7.4 Vision

**Webcam capture is deliberately not implemented.** It needs Media Foundation or DirectShow, which is a
large native dependency for a feature that is rarely wanted, and screen vision is the one that matters.
Noted here so it is not re-added blindly; screen capture and the vision tool are implemented and verified
against the real display.

Configurable VLM provider (NIM cloud / local llama.cpp), model id a free text field with the same availability probe.

Sources, **all on demand only — never continuous, no spying**:
- Screen capture (any monitor) via GDI/BitBlt or DXGI Desktop Duplication.
- Webcam frame via Media Foundation.

The screen capture doubles as the model input for "what am I looking at" and for GUI automation.

---

## 8. Wake word and lifetime

### 8.1 Wake word

**There is one wake word implementation, not three.** Porcupine, NanoWakeWord and Vosk were all considered
and all three were dropped: Porcupine needs a Picovoice account, NanoWakeWord needs an ONNX runtime
dependency, and Vosk needs a 40 MB model - for a job the speech recognition already installed can do.

Instead the microphone's own level decides that someone has spoken, and the audio that triggered it is
transcribed and checked for the word. The wake word is therefore exactly as good as the recogniser and
exactly as offline, at the cost of a recognition pass per utterance, which is why the trigger is
deliberately conservative. Sensitivity is a slider defaulting to 0.06, which sits well above the measured
0.036 room tone and well below a speaking voice.

The `wakeWordEngine` setting is retained so an existing configuration still loads; it selects nothing.

### 8.2 Activation methods — all four, all live at once

1. Offline wake word ("Jarvis").
2. Global push-to-talk hotkey (configurable, default something like `Ctrl+Alt+J`).
3. The `jarvis-activate` action.
4. Spoken wake word while already in a session (barge-in).

### 8.3 Lifetime — three tiers, selectable

The plugin process only lives while Macro Deck runs. A true always-on assistant needs more, so all three ship and you pick:

| Tier | Mechanism |
|---|---|
| `plugin-only` | Everything inside the plugin. Wake word works while Macro Deck is open. |
| `background-process` | The plugin spawns and supervises a detached child process that survives Macro Deck exiting. It holds the mic and the hotkey; the plugin reconnects to it over a named pipe / loopback socket. |
| `tray-companion` | A tiny always-on native tray app holds the mic and hotkey; the plugin talks to it over a local socket. Cleanest separation, one extra process. |

### 8.4 Barge-in

Configurable, **default on**. Speaking over JARVIS stops TTS and starts listening. A configurable energy threshold and a short cooldown prevent a cough from triggering it.

---

## 9. Computer control

Windows-only, all native. **No window ever pops up** for commands JARVIS runs — see below.

### 9.1 Hidden execution

`CreateProcessW` with `CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP`, `STARTF_USESHOWWINDOW` + `SW_HIDE`, `DETACHED_PROCESS` where appropriate, output redirected to pipes so there is no console at all.
Child processes are tracked in a Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) so cancel-with-kill works and nothing is orphaned. This mirrors what MacroDeck's own `WindowsPluginProcessJob` does.

`cmd.exe /c` and PowerShell are launched with the same flags. Elevated operations go through `ShellExecuteEx` with the `runas` verb, which raises one UAC prompt that you confirm — the elevated service (§9.3) is the alternative for when you do not want a prompt each time.

### 9.2 Tools (granular, balanced)

One function per capability so the model picks precisely and each is independently permission-gated:

```
run_shell          read_file        write_file        list_directory
delete_path        move_path         file_exists       search_files
screenshot         list_windows      focus_window      close_window
open_app           kill_process      list_processes
set_volume         get_volume        media_play_pause  media_next
set_system_power   send_notification clipboard_read     clipboard_write
mouse_move         mouse_click       mouse_scroll      mouse_drag
keyboard_type      keyboard_combo    keyboard_sequence
web_fetch          browser_*         registry_*        scheduled_task_*
```

Browser control goes through the Chrome/Edge DevTools protocol, reusing the same approach as `macrodeck-plugin preview render` (`sdk/src/MacroDeck.Plugin.Cli/Rendering/`).

### 9.3 Elevated service

Optional Windows service, installed on demand, LocalSystem. Two independently feature-gated purposes:

1. **Scheduled prompts and startup** — wake word and hotkey when nothing is open.
2. **Admin operations without a per-command UAC prompt** — services, drivers, protected paths.

Feature-gated so you only pay for what you enable. Communicates over a named pipe with an explicit ACL.

### 9.4 Safety model

Default is **confirm everything**, with these modes all selectable:

| Mode | Behaviour |
|---|---|
| `confirm-all` (default) | Every command and every file write shows a modal with the exact command / diff. |
| `allowlist` | Configurable prefix allowlist auto-runs; everything else prompts. |
| `tool-permissions` | Per-tool-class permission (read-only / write / execute / admin). |
| `autonomous` | No prompts. Real risk. Offered, not the default. |

**Voice confirmation** — configurable, both mechanisms available:

- **Yes/no** — JARVIS asks aloud, STT decides. Convenient, and a misheard "yes" can authorise anything.
- **Spoken random challenge** — JARVIS speaks a random word, you say it back. Not spoofable by a TV or another assistant.
- **Hybrid (recommended)** — yes/no for ordinary commands, a random challenge for anything destructive (delete, format, registry, scheduled task).

All of it is settable per tool class.

### 9.5 Command output

- Truncated stdout/stderr is fed back to the LLM so it can answer about the result.
- Raw output is also shown in the widget text box.
- A dedicated **output viewer** action opens the full untruncated output in a scrollable dialog.

---

## 10. Text box

Configurable mode, with live interim STT text while listening:
`full-transcript` (both sides, newest at bottom, scrolling) | `last-reply` | `interim-plus-reply` (default).
Font size, colour, wrap, max lines, and whether to show timestamps are all widget-configurable.

Interim text arrives as STT partials. Patch budget applies — interim updates are throttled to ~10/s and only emitted when the text actually changes.

---

## 11. Memory

Configurable, all four levels:

| Mode | Behaviour |
|---|---|
| `none` | Stateless. |
| `session` | Rolling transcript per session, gone on restart. |
| `persistent` | Full history across restarts, plus learned facts about you. |
| `persistent-notes` (default) | Above, plus a hand-editable notes file injected into the system prompt. |

Storage: `MACRO_DECK_PLUGIN_DATA_DIRECTORY`, which the host guarantees survives plugin updates and rollback. Never inside the version directory.

---

## 12. Persona

All three, and JARVIS can rewrite his own prompt.

- Preset dropdown: `Classic JARVIS` (British, dry, understated wit, addresses you as "sir") | `terse` | `sarcastic` | `formal` | `custom`.
- Free-text system prompt override.
- **Self-modification**: "Jarvis, from now on be more formal" rewrites the stored system prompt after confirming the exact new text back to you. Bounded — it can only edit its own persona block, never the tool permission block or the safety system rules, which live in a separate immutable prefix.
- Default: Classic JARVIS.

---

## 13. Agentic loop and rate limiting

Configurable `max-iterations` per turn, default **4** — enough for real multi-step work, small enough to stay under the ~40 RPM ceiling for a normal conversation.
A local token-bucket rate limiter **queues rather than drops**, and auto-falls back to a local llama.cpp model when the cloud is saturated or the key is missing.
Cancel aborts the loop immediately.

---

## 14. Widget configuration surface

Everything is configurable on the widget itself, backed by the global integration config for things that must be shared.

**Per widget**: orb preset, custom image/GIF, colours, ring count and speed, glow on/off, border animation style, text mode, font, text colour, wrap, max lines, show timestamps, audio-reactive on/off, sensitivity, session scope (`local` \| `global`, default global), activate mode, barge-in on/off, show interim text.

**Global setup flow**: NIM key, Picovoice AccessKey, optional self-hosted NIM URL + token, STT provider + model + language, TTS provider + voice, LLM provider + model, VLM provider + model, persona preset + prompt, memory mode, safety mode, per-tool permissions, hotkey, wake word engine + sensitivity, lifetime tier, elevated service toggles.

---

## 15. Localization

- `Localization/Strings.resx` (English default) plus `Strings.de.resx` at minimum. Locale drives STT language, TTS voice and reply language together.
- Everything user-facing goes through the resource: action names, parameter labels, widget config labels, issue titles, error messages.
- `MacroDeckStrings` reused where a key already exists (`Common.*`, `States.*`, `Validation.*`).
- Manifest `languages` is derived automatically by `macrodeck-plugin build` from the `.resx` files present.

---

## 16. Manifest sketch

```json
{
  "$schema": "https://schemas.macro-deck.app/plugin-manifest-v1.schema.json",
  "manifestVersion": 1,
  "id": "com.misike12.jarvis",
  "name": "JARVIS",
  "version": "0.1.0",
  "description": "A voice-driven assistant for your deck.",
  "icon": "Assets/icon.png",
  "entrypoints": {
    "win-x64": {
      "executable": "runtimes/win-x64/Jarvis.Plugin.dll",
      "runtime": { "kind": "FrameworkDependent", "dotnetVersion": "10.0" }
    }
  },
  "publisher": { "name": "misike12", "id": "com.misike12", "url": "" },
  "license": "MIT",
  "compatibility": { "macroDeck": ">=3.0.0-0" },
  "permissions": ["host:variables", "host:config", "host:widgets", "host:notifications", "host:scripts", "host:deck", "events:publish", "assets:upload", "fs:user-files", "process:spawn"]
}
```

`id`, `name`, `version` are frozen the moment this ships.

---

## 17. Build and test loop

```powershell
dotnet tool install --global MacroDeck.Plugin.Cli --prerelease   # already have beta.14

macrodeck-plugin new --name "JARVIS" --id com.misike12.jarvis --publisher "misike12" --project-name Jarvis.Plugin --platform win-x64 --yes

macrodeck-plugin run  --project src/Jarvis.Plugin --stub-host     # expect: "Session established (negotiated plugin protocol v3)."
macrodeck-plugin build   --output artifacts
macrodeck-plugin validate --artifact artifacts/com.misike12.jarvis-0.1.0.macroDeckPlugin
macrodeck-plugin test     --artifact artifacts/com.misike12.jarvis-0.1.0.macroDeckPlugin
```

Target: all **49** conformance checks pass with zero `Required` failures.
(NB: the docs say "51 checks" in one place — that figure is stale; `CheckCatalogTests` pins 49.)

Test strategy mirrors the repo's own: `PluginTestHarness` for in-process unit tests, `MacroDeckTestHost` for wire-level tests, plus a `MacroDeck.Plugin.Testing` conformance run in CI.

---

## 18. Repo rules that constrain this work

From the project's `CLAUDE.md` — these are binding:

- **Every non-obsolete public plugin-facing contract is a compatibility commitment, source and binary.** Action ids, capability local ids, `ui.icon` names and widget type ids must never be renamed once shipped. If a change would break one, stop and ask first.
- Internal implementation code carries **no comments**. Public SDK members get concise `///`. Any comment that does exist is at most two `//` lines, no em dashes, no backticks.
- No hardcoded user-facing strings.
- No new third-party package without explicit approval, and the name must be verified against the real registry first.
- Register `builder.Services.AddSingleton<T>()`; never take an invocation-scoped type into a singleton (**MDP4001**).
- Never discard a `CancellationToken` (**MDP3001**). No blocking waits in a handler (**MDP3002**).
- Forward every `CancellationToken`. Actions, config flows and tool calls are all cancellable.

---

## 19. Open items still to decide

1. Whether the orb assets are generated at build time from C# (plan) or checked in as pre-rendered files.
2. ~~Whether whisper.cpp and llama.cpp ship bundled or download~~ — **decided: all native binaries and models are downloaded on first run and stored in the plugin data directory. Selectable, never bundled.**
3. Which NIM model ids become the defaults (LLM, VLM, ASR, TTS).
4. Whether the tray-companion lifetime tier is worth the extra process, or `background-process` is enough.
5. Whether to implement `IVideoStreamProvider` MJPEG as an alternative orb renderer — true 60 fps at any complexity, but a whole extra pipeline.

### 19.1 Native asset manager (new, from the download decision)

Everything native that is not P/Invoke goes through one manager:

```
%LOCALAPPDATA%\MacroDeck\plugins\com.misike12.jarvis\runtime\
├── whisper\whisper-cli.exe        pinned version + SHA-256
├── whisper\models\*.gguf          selectable
├── llama\llama-server.exe         pinned version + SHA-256
├── llama\models\*.gguf            selectable
├── piper\piper.exe                pinned version + SHA-256
├── piper\voices\*.onnx            selectable
└── manifest.json                  what is installed, versions, digests
```

- `EnsureAsync(component, variant)` downloads on demand, verifies SHA-256 before use, reports progress through `IMessageChannel`/widget state.
- A missing or corrupt binary is an `IntegrationIssue`, never a crash.
- Downloads resume; a partial file is never treated as complete.
- Offline detection surfaces as an issue offering the SAPI fallback.
- `macrodeck-plugin test` and the conformance suite must pass with **nothing downloaded** — the plugin has to start and answer `/_macrodeck/health` with all components absent.

---

## 20. Suggested build order

1. Scaffold, manifest, icon, empty integration — get `run --stub-host` green.
2. State machine (the 8 states) + the 3 actions + button states. Conformance green.
3. Text-only LLM round trip via NIM, key from `jarvis.settings.json`.
4. Orb widget: static first, then the animated asset, then the `ui.transform` ring, then amplitude reactivity at 20 Hz with a deadband.
5. Widget config surface.
6. Native asset manager (download + SHA-256 + progress), then TTS (Piper) and `run_shell` with `CREATE_NO_WINDOW`, confirm-everything safety.
7. STT (whisper.cpp) + hotkey.
8. Wake word (Porcupine).
9. Vision (screen + webcam on demand).
10. Memory, persona self-modification, remaining PC-control tools.
11. Conformance suite clean, validate at `publication` level.
---

## 21. What was built, what was changed, what was left out

### Built as written

Every tool in section 9.2 exists, is registered in `PluginServices.cs` and is covered by tests. The
orb presets in section 5 render distinctly, the microphone amplitude reaches the orb, barge-in in section
8.4 is implemented with a hold and a cooldown, memory in section 11 is written *and read back*, the
persona in section 12 resolves from the notes file, and every config-flow field in section 3 round-trips.

### Changed from the plan

| Plan said | What shipped | Why |
|---|---|---|
| Chrome or Edge via DevTools protocol | Any Chromium browser, Thorium first, driven over its own profile directory | Only Thorium is installed here, and a dedicated profile means automating a site cannot disturb the browser you are using. |
| Three lifetime tiers (plugin-only, ackground-process, 	ray-companion) | The plugin stays plugin-only; the separate executable is the elevated service, not a tray companion | A detached always-on child holding the microphone is a second assistant with its own audio stack, and nothing in this session could verify it. Not built rather than built unverified. |
| Browser control over the user's own browser | A dedicated browser instance | Attaching to the daily browser would risk logging the user out of sites and moving their window. |
| `ShellExecuteEx` with the `runas` verb for elevation | A named pipe to a service | One UAC prompt per command is what section 9.3 says the service exists to avoid. |

### Left out on purpose

| | Why |
|---|---|
| Kernel-level input injection | Refused. Input is synthesised with `SendInput`, which reaches everything user-mode automation reaches and needs no signed driver. A kernel driver built specifically to evade anti-cheat detection was requested and declined. |
| Webcam capture | `Media Foundation`/`DirectShow` is disproportionate for what screen vision already answers. See section 8.1 for the wake-word decision on the same grounds. |
| Porcupine, NanoWakeWord, Vosk | Three dependencies to do what the installed recogniser already does. Section 8.1 has the reasoning; the `wakeWordEngine` setting is retained so old configurations still load and selects nothing. |

### Built but not verified here

- **Scheduled task round trip.** The Task Scheduler COM interface answers `0x800704E3` from this process
  even with the `Schedule` service running. The code is written against the documented interface and the
  argument validation is tested; the five tests needing a reachable scheduler are marked `[Explicit]`.
- **Service installation.** Needs an elevated shell. `scripts/install-service.ps1` and
  `Jarvis.Service.exe --install` both refuse with exit code 5 and a plain explanation when unelevated.