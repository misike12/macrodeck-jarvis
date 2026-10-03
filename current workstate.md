# JARVIS — Current Workstate

Last updated: after the elevated-service milestone. Every tool in `plan.md` is implemented, the elevated
service is built, and the test suite is fully green.
Plan: `plan.md` · Platform knowledge: `knowledge.md`

---

## Identity

| | |
|---|---|
| Plugin id | `com.misu.jarvis` |
| Display name | `JARVIS` |
| Target | `win-x64` only, framework-dependent on .NET 10, no Node/Python |
| Source root | `C:\Users\Misu\Desktop\ideas\JARVIS\src` |
| Plugin project | `src\src\Jarvis.Plugin\` |
| CLI | `macrodeck-plugin` 3.0.0-beta.14 (global) |

---

## Milestones

### ✅ M1 — Scaffold
`macrodeck-plugin new` → manifest, csproj, slnx, test project, icon.
`run --stub-host` registers as `com.misu.jarvis`, negotiates protocol v3.

### ✅ M2 — State machine + actions
- `Core/AssistantState.cs` — 8 states
- `Core/AssistantStateHolder.cs` — single lock, immutable snapshots, 0.04 amplitude deadband
- `Core/AssistantSession.cs` — turn lifecycle, one CTS per turn, command tracking
- `Core/ProcessTracker.cs` — `CreateNoWindow`, tree kill via `taskkill`
- `Actions/SessionActions.cs` — `jarvis-activate`, `jarvis-cancel`, `jarvis-toggle`, `jarvis-say`
- `Actions/AssistantStateReader.cs` — one shared state set, `unavailable` always present
- `PluginIntegration.cs` — `IPluginIntegration` + `IVariableProvider` + `IConfigFlowProvider`
- 4 eager variables: `state`, `transcript`, `reply`, `amplitude`

### ✅ M3 — Settings + config flow
- `Core/JarvisSettings.cs` — every enum + setting
- `Core/JarvisSettingsStore.cs` — config flow secret → local file → env, merged
- `Core/LocalSettingsFile.cs` — `jarvis.settings.json` beside the exe, gitignored
- `JarvisConfigFlow.cs` — 5 steps: provider → keys → models → voice → behaviour

### ✅ M4 — NIM LLM client + tool calling
- `Llm/ChatMessages.cs` — `ChatMessage` as `JsonObject`, `ToolCall`, `ToolDefinition`, `ChatRequest`
- `Llm/ChatClient.cs` — SSE streaming, tool-call assembly by index, `ProbeAsync` for reachability
- `Llm/ToolRegistry.cs` — safety mode enforced here, refusals returned as tool results
- `Llm/Tools.cs` — `run_shell`, `read_file`, `write_file`, `list_directory`, `FileGuard`
- `Core/ConversationRunner.cs` — agentic loop capped at `MaxIterations` (default 4)
- `Core/PersonaResolver.cs` — immutable safety prefix + editable persona block
- `Actions/CheckModelsAction.cs` — `jarvis-check-models`

### ✅ M5 — Conformance green
**30 passed, 0 failed, 19 skipped, exit 0.** The 19 skips are capabilities not declared yet.

**Flake found and fixed.** One run showed MDC0604 (SupervisorShutdown close) failing, and skip counts
varied between 30/19 and 31/18. Cause: `jarvis-check-models` performed a live call to NVIDIA during
the suite, and the shared `HttpClient` is on a 120 s timeout, so a probe could hold an invocation open
and perturb the timing-sensitive shutdown checks. Fixed by giving `ChatClient.ProbeAsync` its own
10 s `CancellationTokenSource` budget. Three consecutive runs now give an identical 30/0/19.

**Rule going forward: no SDK action may make an unbounded network call.** Timeouts must be owned by
the call site, not inherited from the shared client.

### ✅ M6 — Orb widget
- `Orb/OrbWidgetTypeProvider.cs` — `IWidgetTypeProvider`, frozen local id `jarvis-orb`
- `Orb/OrbWidgetData.cs` — stored config, default JSON + JSON Schema, defensive parse
- `Orb/OrbUiProvider.cs` — `IUiProvider`, 3 surfaces, declines what it does not serve
- `Orb/OrbView.cs` — the composition: glow (radial gradient) → core → rings → text
- `Orb/OrbUiSession.cs` — `IUiSession` bridging the view and `HandlerFaulted` → `Faulted`

Conformance went **30 → 33 passed, 0 failed, 16 skipped**. MDC0306/0307/0308, the `ui` capability
checks, now pass.

**The orb is currently static geometry only.** The animated WebP core, the `ui.transform` rotation
patch and the amplitude reactor are the next three items.

### ✅ M7 — Animated orb asset
- `Orb/AnimatedGif.cs` — hand-written GIF89a encoder, **zero new dependencies**
- `Orb/OrbFrameRenderer.cs` — analytic RGBA frame renderer, no drawing library, 3x3 supersampled
- `Orb/OrbAssetCache.cs` — builds one GIF per state on demand, caches, registers as a `UiResource`
- `Orb/OrbUiSession.cs` — 25 Hz `ui.transform` sweep timer, state-driven asset swap
- `Orb/OrbView.cs` — glow → animated core → counter-rotating rings → text

**Decisions taken here**

- **Skipped SkiaSharp on purpose.** It would need approval under the dependency rule and a plugin
  shipping one orb does not justify it. A hand-rolled GIF encoder keeps the dependency count at zero
  and `image/gif` is explicitly permitted by `UiResourceRules`.
- **Literal-mode LZW, not run-encoding.** Run-encoding LZW's table-growth bookkeeping is the easiest
  thing in the GIF format to get subtly wrong, and the failure mode is invisible to a structural check:
  header, palette, sub-blocks and frame count all validate while the browser renders noise. Literal
  mode costs about 1.125x raw size and is trivially correct. Measured: **251 KB per state** at 96px /
  24 frames, ~188 KB for idle's 18 frames, ~1.7 MB total against a 16 MiB per-plugin limit.
- Frames are generated **on demand** and cached, so a fresh install answers `/_macrodeck/health`
  immediately and a state nobody reaches is never paid for.

**Verification:** `dotnet test` **20/20 passing**, including an *independent* GIF decoder written
against the spec rather than against the encoder. Two real bugs were caught this way and would not
have shown up any other way: the encoder originally wrote all LZW data as one run instead of ≤255-byte
sub-blocks, and the decoder mis-parsed the image descriptor's left/top as width/height.

Conformance: **35 passed, 0 failed, 14 skipped, exit 0**.

### ✅ M8 — Microphone amplitude reactivity
- `Audio/AmplitudeMeter.cs` — per-block RMS, fast attack / slow release, perceptual expansion curve
- `Audio/AudioDeviceCatalog.cs` — endpoint enumeration and id-then-name-then-default resolution
- `Audio/MicrophoneMonitor.cs` — WASAPI capture, 20 Hz publish, `MemoryMarshal.Cast` off the span
- `AssemblyInfo.cs` — `[assembly: SupportedOSPlatform("windows")]`
- `AmplitudeMeterTests.cs` — 8 tests

**Dependency added: NAudio 3.1.0** (+ `NAudio.Wasapi`). Approved by the user for this milestone.
The alternative was hand-rolling several hundred lines of WASAPI COM vtable interop plus format
negotiation, which is bad value against the de-facto standard.

**NAudio 3.x gotchas that cost time**

- The package split: `NAudio.CoreAudioApi` **does not exist as a package**. Those types ship inside
  `NAudio.Wasapi`.
- `WasapiCapture` is **obsolete**; use `WasapiRecorderBuilder` → `WasapiRecorder`.
- Both recorder types live in namespace **`NAudio.Wave`**, not `NAudio.Wasapi`. The `NAudio.Wasapi`
  string in the assembly is only the assembly name, so it looks like the namespace exists.
- Builder methods: `WithDevice(MMDevice)` · `WithSharedMode()` · `WithPollingSync()` ·
  `WithFormat(WaveFormat)` · `WithLoopbackCapture()` · `WithProcessLoopback(uint, mode)`.
- `CaptureDataAvailableHandler` is
  `(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)`.
- `MMDevice` has only an internal constructor; get one from `MMDeviceEnumerator.GetDevice(id)`.
- `WaveFormat.CreateIeeeFloatWaveFormat(int sampleRate, int channels)` — two arguments.
- Reflection over the DLL string heap is not enough to find namespaces; load with dependencies resolved
  and enumerate `GetExportedTypes()`.

**The user's microphone, verified working**

```
resolved = Mikrofon (WO Mic Device)     <- also the system default
open ok  = True
format   = 32 bit IEEEFloat: 48000Hz 1 channels
3s capture: 143,040 frames = 47,680/s   (exactly 48 kHz, no dropouts)
level 0.036, peak 0.036                 (room tone, correct when silent)
```

**A real bug the tests caught:** `AmplitudeMeter` never cleared its sum-of-squares accumulator, so the
figure was a running average over the whole session — it both lagged badly and never settled. Two tests
failed on it. The accumulator is now cleared every block.

**Verification:** `dotnet test` **28/28**, `dotnet build` 0 warnings, conformance **0 failures** across
three consecutive runs. The pass count moves between 33 and 35 because several `Recommended` checks skip
when no action happened to stay in flight long enough; that is the suite's designed behaviour, not a
regression.

### ? M9 - Spoken replies
- `Speech/WindowsSynthesizer.cs` - PowerShell `System.Speech` renderer to a temporary WAV, voice
  enumeration, RIFF header reader for duration
- `Speech/SpeechPlayer.cs` - `WasapiPlayer` playback, render-device loopback metering, mid-word stop
- `Speech/VoiceService.cs` - owns synthesis, playback and the temp file so one token reaches all three
- `AssistantSession.SpeakReplyAsync` - speaks while the turn is still held; `EndTurn` stops speech

**No new dependency.** SAPI is reached through `powershell.exe` rather than a new package, which keeps
the plugin framework-dependent and adds nothing to the shipped surface. Piper and the NIM TTS endpoints
are configured and selectable but not yet implemented; `SpeakAsync` currently renders with SAPI for
either provider so the feature is usable before those land.

**NAudio 3.x playback gotchas**

- `WaveOutEvent` **exists but throws `NotSupportedException`** in 3.x on this machine. `WasapiOut` is
  **obsolete** in favour of `WasapiPlayerBuilder` -> `WasapiPlayer` (zero-copy buffers, MMCSS thread
  priority, `IAudioClient3`). The builder takes `WithDevice(MMDevice)` -> `WithSharedMode()` ->
  `WithPollingSync()` -> `WithLatency(int)` -> `Build()`.
- `WasapiPlayer.IsFormatSupported`, `GetPosition`, `Init`, `Volume`, `PlaybackStopped` are the surface
  that matters; the loudness controls are per-stream, not per-device.

**Three bugs that only a real playback run could find.** All three are invisible to a structural check,
which is the argument for driving the actual audio stack rather than trusting the code:

1. A missing `AppendLine()` merged `$s.Rate = $rate` and `$s.Volume = $volume` into one line, which is a
   PowerShell `ParserError: UnexpectedToken`. Synthesis had never worked.
2. The RIFF walk read `fmt ` and then fell through to the next chunk without skipping the chunk body.
   `fmt ` is 18 bytes but only 12 were consumed, so every following chunk id was misaligned and **every
   clip measured as zero length**.
3. A clip that reached its end on its own released nothing: `IsPlaying` stayed true forever, and the
   caller could not delete the file because the handle was still open. The stop event now clears the
   fields and hands disposal to the pool, signalling completion only once the handle is closed.

**Verified against the installed voices** (`TtsProbe`, `[Explicit]` so it never plays unattended):

```
voices  = Microsoft Zira Desktop | Microsoft David Desktop
synth   = 208,814 bytes, reported 4.73s, RIFF header 4.73s   (both paths agree)
play    = 1.90s clip, 1.99s elapsed, IsPlaying False, loopback 187 reports, peak 0.080
stop    = cut short of full length, IsPlaying False
```

**Verification:** `dotnet build` 0 warnings, `dotnet test` **28/28** (probe is `[Explicit]`, so the
default run stays silent), conformance **33 passed / 0 failed / 16 skipped**, exit 0.

### ? M10 - Widget configuration surface
- `Orb/OrbConfigView.cs` - every orb key is a real input; the host's appearance fields beside them
- `Orb/OrbUiProvider.cs` - the config surface is served by `OrbConfigSession`, which checks `WidgetType`

**Macro Deck supplies the widget preview, the split layout, the narrow-window drawer, JSON mode, saving
and the unsaved-changes prompt.** A plugin supplies fields only. A hand-rolled preview frame would have
duplicated the host's own and could never have tracked the host's draft. The `Editor` region carries the
action flows, bound to the top-level `flows` key because that is the only key the host runs event flows
from. `WidgetType` is checked before serving: a widget has no declared field list to fall back on, so
declining leaves the user with JSON mode only.

**Two API facts reflection found and the docs do not state** (full detail in `sdk-surface.md`):
`UiBinding<T>` has a private ctor with `Value`/`CanWrite` internal, so `Bind.To(state)` is the only way
to bind; `UiValue<T>` and `UiText` are structs whose ctors are internal/private, reachable only through
`UiValue.Of/From/None/Optional` and `UiText.Of/From/None/Optional`.

**A pre-existing bug the tests found:** `OrbWidgetData` writes its enums kebab-case (`"arc-reactor"`) and
`ReadEnum` asked `Enum.TryParse` for exactly that string, which matches neither a hyphen nor anything but
the exact member name. Every default payload silently fell back, so a freshly placed orb drew its preset
wrong. Both spellings are accepted now.

### ? M11 - Native asset manager
- `Runtime/AssetCatalog.cs` - pinned assets with **measured** SHA-256, install groups, installed manifest
- `Runtime/RuntimePaths.cs` - runtime layout under the plugin data directory, path-traversal guard
- `Runtime/AssetDownloader.cs` - Range resume, digest verify, atomic install
- `Runtime/RuntimeManager.cs` - `EnsureAsync`, manifest, `IIntegrationIssueProvider` and its retry
- `Runtime/RuntimeProgressReporter.cs` - progress snapshot for a variable or a widget
- `Actions/ManageComponentsAction.cs` - the user-facing trigger, and two progress variables

**Digests were measured from the pinned URLs, not copied from a release note.** A pin nobody verified is
worse than no pin: it fails closed, and the failure looks exactly like a corrupt download.

| Component | Asset | Bytes | SHA-256 (first 16) |
|---|---|---|---|
| piper | piper_windows_amd64.zip | 22,477,236 | `f3c58906402b24f3` |
| piper | en_GB-alan-medium.onnx | 63,201,294 | `0a309668932205e76` |
| piper | en_GB-alan-medium.onnx.json | 4,888 | `c0f0d124e5895c00` |
| whisper | whisper-bin-x64.zip | 3,675,974 | `0d2eca299c248f96` |
| whisper | ggml-tiny.en.bin | 77,704,715 | `921e4cf8686fdd99` |

Both GitHub and Hugging Face honour `Range`, so resume is real rather than aspirational.

**Three properties that had to hold, and do:**

- A partial file is never complete. Distinct path, reused only on a `206`, digest-checked before install.
  A server that ignores `Range` and answers `200` restarts, because appending a full body to a partial
  silently concatenates two copies.
- Installs are atomic: written under a temporary name, verified, then moved into place.
- Wrong content is **deleted**, not retried: a mirror serving wrong bytes will serve them again.

**Three bugs the tests found:** the failure table was keyed by asset id while the host calls back with the
issue id, so every retry was a silent no-op; a retry re-entered through the catalogue and so could never
retry an asset the catalogue did not hold; the progress variables declared camelCase local ids, which the
plugin harness rejected at `Build()`.

**Verified:** `dotnet test` **52/52**, build 0 warnings, conformance **33/0/16 with nothing downloaded**,
and both components install and verify against the live URLs (`RuntimeLiveDownloadTests`, `[Explicit]`).

### ? M12 - Piper local voice
- `Speech/PiperSynthesizer.cs` - child process, stdin text, WAV out, espeak data and tashkeel model resolved
- `Speech/VoiceService.cs` - Piper first, SAPI fallback, the chain stated once

Piper is the preferred engine with SAPI **kept as the fallback rather than replaced by it**: Piper is an
optional download, so an uninstalled machine still gets a spoken reply instead of silence. The fallback is
silent to the user, because they asked for a spoken reply, not an explanation of which engine produced it.

Piper is driven as a child process reading stdin and writing a WAV, which is the only interface it
offers; reimplementing its ONNX pipeline in-process would add a native dependency for no gain. Measured
here: **real-time factor 0.16**, roughly six times faster than speaking.

**The bug worth recording:** a voice is an ONNX model *and* its JSON config together, and the config
carries the phoneme map and the sample rate. The manager gave each asset its own directory, so the two
landed in different folders and **no complete voice existed on disk at all**. Discovery walks for the
pair, found nothing, and every reply would have fallen back to SAPI forever while looking healthy.
Assets now declare an **install group**, and a group shares one directory.

A test asserts the *configured default* voice is among the voices on disk. The failure that matters is
not a broken render but a default naming a voice nothing installs, which is indistinguishable from
working.

**Verified:** the component installs through the manager, the binary lands where the synthesizer looks, a
voice is discovered, and both the discovered voice and the configured default render.

### ? M13 - Local speech recognition
- Speech/WhisperTranscriber.cs - child process, transcript read from the file it writes
- Speech/VoiceRecorder.cs - energy-endpointed capture, 16 kHz mono 16-bit WAV
- Speech/ListeningPipeline.cs - the whole voice loop in one place
- Speech/UtteranceAudio.cs - the one WAV format both callers agree on

**The pin changed because of a hardware finding.** whisper.cpp 1.7.6 through 1.8.x all die with
STATUS_ILLEGAL_INSTRUCTION on this machine's 2012 i5-3570, **including the BLAS build**, so it is their own
compute path and not a bundled library. **1.9.2 is the first release verified to run here.** The old pin
would have downloaded, hashed correctly, installed perfectly, and then failed on first use.

That produced the milestone's real lesson, and the fix in Runtime/ExecutableProbe.cs: **hashing correctly
is not the same as running.** Every executable component is now run once after install, and a failure becomes
an issue with a plain explanation. The bytes are kept, because an instruction-set mismatch is a property of
the machine, not of the file.

**Audio is never kept.** The recording is deleted the moment its transcript exists, before the model is
called. Utterances end on energy with a hard cap, never on a fixed duration.

### ? M14 - Global hotkey and offline wake word
- Input/GlobalHotkey.cs - RegisterHotKey bound to the plugin's own thread, pumped on a dedicated thread
- Input/HotkeyChord.cs - chord text form, accepting Ctrl/cmd/super alongside the enum names
- Input/WakeWordDetector.cs - level-gated keyphrase check over a bounded ring buffer

Neither wake-word engine the plan named was usable: Porcupine needs a Picovoice account, NanoWakeWord needs
an ONNX runtime. Both were avoidable, because **the recogniser already installed can answer the question the
wake word is asking**: the level decides someone spoke, and that audio is transcribed and checked for the
word. Offline, no account, nothing downloaded, exactly as good as the recogniser underneath.

A ring buffer was needed because by the time a level meter decides someone spoke, the word is already gone
from a pull model.

### ? M15 - Vision
- Vision/ScreenCaptureService.cs - GDI capture, JPEG, nothing touches disk
- Vision/VisionClient.cs - inline data URI on the same endpoint resolution the text client uses
- Llm/ScreenshotTool.cs - the tool, confirmation-required

Verified against the real display: the model read the actual screen. Handles the virtual desktop origin,
which is often negative on a multi-monitor arrangement and silently yields a black picture otherwise.

**Dependency added deliberately:** System.Drawing.Common 10.0.0, Windows-only and Microsoft-maintained.
Webcam capture is **deliberately not implemented** (needs Media Foundation or DirectShow, a large native
dependency for a rarely wanted feature). Noted rather than stubbed.

### ? M16 - Memory, persona, desktop control
- Memory/MemoryStore.cs - append-only transcript plus a hand-editable notes file
- Llm/SetPersonaTool.cs - bounded self-modification
- Llm/DesktopTools.cs - processes, clipboard, volume
- Llm/NativeClipboard.cs - the clipboard over Win32 rather than through WinForms

The persona tool can only write the persona field; the safety rules live in a separate immutable prefix the
prompt builder appends and never reads from settings. **There is no argument that reaches them.** That is
the point: a bounded capability is one the model cannot argue past. The persona is stored in the notes file,
not the integration config, so a config rewrite cannot erase it.

**Three bugs found by tests, two of them safety-critical:**
- the critical-process guard listed xplorer.exe while ProcessName never carries an extension, so it
  matched nothing and protected nothing, on exactly the input it was meant to catch
- GetProcessesByName needs a bare name, so xplorer.exe again sailed past that guard
- Marshal.Copy counts bytes even when copying a char[], so the clipboard stored half the string

### ? M17 - German
All 191 keys. Checked through the resolver rather than by file presence: de-AT resolves through its
neutral culture, an untranslated key falls back to English rather than rendering as [[plugin:...]], and
placeholders are still substituted in the German sentence.

### ? M18 - Packaging and the final gate
`
macrodeck-plugin build  -> com.misu.jarvis-1.0.0.macroDeckPlugin (27 entries, 2.1 MB)
macrodeck-plugin inspect-> languages: de, en
validate --level Publication -> 1 error: 'repository' is required
test --artifact  -> 38 passed / 0 failed / 11 skipped, conformant
`

**Artifact conformance is stronger than project conformance:** 38 passed against 33, because MDC0104-0107
only run for an artifact. A dotnet build -c Release output is **not** validatable at publication level;
the manifest points at untimes/<rid>/, which only macrodeck-plugin build assembles.

**One thing left, and it is deliberately not guessed:** the manifest needs a epository URL. It is an
absolute URL the plugin ecosystem links to, so it cannot be invented.

---

## ? Git

Repository initialised at `JARVIS`, branch `main`, one commit per milestone. `jarvis.settings.json`
is **gitignored** and has never been committed; `Properties/launchSettings.json` is tracked because it
holds only the dev launch profile and no secret.

```powershell
cd C:\Users\Misu\Desktop\ideas\JARVIS\src
git log --oneline
```

---

## ⚠️ SDK version pin — do not remove

`Directory.Packages.props` pins `MacroDeckSdkVersion` to **`3.0.0-beta.14`**.

The template default is the floating range `3.0.0-*`, which NuGet resolves to **`3.0.0-preview.10`**,
older than the `3.0.0-beta.14` CLI, because `preview` sorts below `beta`. Building against it silently
loses `SupportsFlows`, `AppearanceProperties`, `UiModifier` and other contracts.

Verify after any version change:

```powershell
(Get-Content src\Jarvis.Plugin\obj\project.assets.json | ConvertFrom-Json).libraries.PSObject.Properties.Name |
  Where-Object { $_ -match 'MacroDeck' }
```

**The cloned repo HEAD is newer than the published packages.** Reading repo source to learn an API can
mislead: check the resolved package before writing against it.

---

## Live environment

- `.NET SDK 10.0.400`, `ASP.NET Core 10.0.11`
- NVIDIA NIM key present in `bin\Debug\net10.0\jarvis.settings.json` (dev only, never in source)
- Key verified working: **81 models** offered on `integrate.api.nvidia.com/v1`
- Tool calling verified end to end: `nemotron-3-super-120b-a12b` emitted
  `index:0 / id:call-… / name:run_shell / arguments:{…}` with `finish_reason: tool_calls`

### Verified model ids
| Role | Model |
|---|---|
| LLM default | `nvidia/nemotron-3-super-120b-a12b` |
| LLM fast alt | `nvidia/nemotron-3.5-lightning-30b-a3b` |
| Vision default | `meta/llama-3.2-90b-vision-instruct` |
| ASR (separate endpoint) | `nvidia/parakeet-tdt-0.6b-v2` |
| TTS (separate endpoint) | `nvidia/magpie-tts-flow` |

ASR and TTS are **not** in the `/v1/models` text list — they are separate NIM endpoints, so the
`ProbeAsync` "not listed" result is expected for them and must not be read as a failure.

---

## Decisions locked

| Item | Decision |
|---|---|
| NIM defaults | as above, all editable text fields with a live probe |
| Orb assets | **generated from C#** via SkiaSharp at build time |
| Lifetime tier default | `plugin-only` (upgradeable to background-process / tray-companion) |
| Native binaries | **all downloaded on first run**, pinned version + SHA-256, never bundled |
| Orb rendering | animated WebP core + `ui.transform` ring at ≤25 Hz + `borderStyle` garnish |
| Safety default | `confirm-all`; refusal returns a tool result, never throws |
| Persona | immutable safety prefix blocks JARVIS from editing tool permissions or safety rules |
| Default language | English; German translation pending |

---

## Next up, in order

Everything in plan.md is now implemented. Remaining before this is publishable:

1. **Add a epository URL to manifest.json.** The only thing alidate --level Publication still
   complains about. It is an absolute URL, so it has to come from the user rather than be guessed.
---

## Known gaps / things to watch

- `Activate` currently only arms a turn; no microphone or wake word is wired yet.
- `Cancel` and `Toggle` work against the real state machine.
- `Say` runs a real model turn through `run_shell` and friends.
- `CheckModelsAction` shows a modal via `context.Ui` with view id `jarvis-model-report`; that view is
  **not implemented yet**, so the modal renders as an unsupported node until a dialog surface serves it.
- The orb animation is **done in M7/M8**: animated GIF core, `ui.transform` ring sweep, microphone
  amplitude at 20 Hz with a deadband, and render-loopback reactivity during speech.
- Spoken replies are **done in M9** for SAPI. Piper is selected in settings but not implemented yet.
- The orb `config` surface is declared but `OrbView` does not build a configuration tree yet, so opening
  the widget editor yields an empty config. This is the next item.
- `AssistantSession._history` is in-memory only; persistence is the memory milestone.
- The `PersonaResolver` self-modification path exists but nothing calls it yet.
- `CoreLayer` and `RingLayer` take a `JarvisSettings` / produce empty children as placeholders; the
  animated asset fills `CoreLayer`.

---

## Commands

```powershell
cd C:\Users\Misu\Desktop\ideas\JARVIS\src

dotnet build -v q --nologo                       # must stay 0 warnings / 0 errors

macrodeck-plugin run  --project src/Jarvis.Plugin --stub-host   # blocks forever, Ctrl-C or kill
macrodeck-plugin test --project src/Jarvis.Plugin --report text
macrodeck-plugin build   --source src/Jarvis.Plugin --output .\artifacts
macrodeck-plugin validate --artifact .\artifacts\com.misu.jarvis-1.0.0.macroDeckPlugin --level publication
```

**Gotcha:** a lingering `run` process locks `bin\Debug\net10.0\Jarvis.Plugin.dll` and the next build
fails with MSB3021/MSB3027. Kill it before rebuilding.

---

## Verification bar

Build: **0 warnings, 0 errors** (TreatWarningsAsErrors is on, `latest-recommended` analysis).
Conformance: **exit 0**, no `Required` check failing. Any `Required` check going pass→fail is a
blocking regression.
---

## Milestone: every plan tool implemented

All 34 tools from `plan.md` are registered in `PluginServices.cs`. Groups added since M18:

| Group | Tools | File |
|---|---|---|
| Files | `file_exists`, `search_files`, `move_path`, `delete_path` | `Llm/FileTools.cs` |
| Windows | `list_windows`, `focus_window`, `close_window`, `open_app` | `Llm/WindowTools.cs` |
| System | `get_volume`, `media_play_pause`, `media_next`, `set_system_power`, `send_notification` | `Llm/SystemTools.cs` |
| Input | `mouse_move`, `mouse_click`, `mouse_scroll`, `mouse_drag`, `keyboard_type`, `keyboard_combo`, `keyboard_sequence` | `Llm/InputTools.cs` |
| Web | `web_fetch`, `web_search`, `web_search_and_read` | `Llm/WebTools.cs` |
| Browser | `browser_navigate`, `browser_read`, `browser_key`, `browser_type`, `browser_click`, `browser_tabs` | `Llm/BrowserTools.cs` |
| Registry | `registry_get`, `registry_set`, `registry_delete` | `Llm/RegistryTools.cs` |
| Tasks | `list_scheduled_tasks`, `get_scheduled_task`, `create_scheduled_task`, `delete_scheduled_task`, `run_scheduled_task` | `Llm/ScheduledTaskTools.cs` |
| Elevated | `registry_elevated` | `Llm/ElevatedRegistryTool.cs` |

## Milestone: the elevated service

`service/Jarvis.Service` is a separate Windows executable, because it has to be installable and run as
LocalSystem. It talks to the plugin over a named pipe and does nothing on its own initiative.

| | |
|---|---|
| Project | `service/Jarvis.Service/Jarvis.Service.csproj`, `net10.0-windows`, `WinExe` |
| Modes | service (`--console`) and tray (no arguments) |
| Pipe | `jarvis-service-<user>`, `PipeOptions.CurrentUserOnly` |
| Protocol | line-delimited JSON, `Protocol.Version` = 1 |
| Tray | drawn icon, status, identity, pipe name, start-with-Windows, allow-admin, reconnect, exit |
| Install | `scripts/install-service.ps1`, or `Jarvis.Service.exe --install` from an elevated shell |

The service is **built but not installed on this machine**: registering a service needs an elevated shell
and this session is not one. The refusal is deliberate and returns exit code 5 with an explanation rather
than a raw access-denied error.

### What the service will not do

`ElevatedOperations` re-validates every argument, because the caller is an assistant whose input is a
model's output. A denylist covers `\SAM`, `\SECURITY`, the `Schedule` service key, `Winlogon`, `Shell`,
`Image File Execution Options`, Defender, `\Boot` and `\EFI`. Control characters and `..` in paths and
names are refused rather than stripped.

---

## Bugs found and fixed

Recorded because each was invisible from the outside: nothing threw, nothing logged an error, and the
feature appeared to work.

| Where | What was wrong |
|---|---|
| `ProcessTracker.KillTree` | P/Invoked `IsProcessRunning` from `kernel32.dll`, which does not exist. Cancellation could never terminate a command that was not already dead. |
| `ProcessTracker` | `Id` was read in the constructor, before `Start`, so any caller touching it before the start threw. |
| `WakeWordDetector.Mentions` | The window was the word plus two characters, so it could never match "hey jarvis" or "jarvis, what time is it". The wake word did not fire for its most natural phrasing. |
| `WakeWordDetector.Mentions` | Threw on a null transcript, which is what a failed recogniser returns. |
| `ToolRegistry` | A confirmation wait had no deadline. One unanswered question held the turn for the rest of the session. Bounded at ten minutes. |
| `BrowserTools.BrowserNavigateTool` | Connected to, and so launched, a browser *before* validating the address. |
| `BrowserTools.BuildClickExpression` | Partial matching was reversed, so "Sign" could never match a button reading "Sign in". |
| `PipeServer` / `ElevatedServiceClient` | Both used `Encoding.UTF8`, whose byte order mark deadlocked on a duplex pipe and would otherwise have prefixed every request with three non-JSON bytes. |
| `RegistryTools.RegistrySetTool` | `CreateSubKey` creates the key even when the caller said not to. |
| `RegistryTools` | A text value written as a `dword` became `0` rather than being refused. |
| `OrbAssetCache` | Keyed on state alone, so whichever preset rendered first filled the cache and every preset showed that one. `OrbPreset` never reached the renderer. |
| `MemoryStore.LoadHistory` | Written every turn and never read. The notes file was written and never read by `PersonaResolver`. |
| `WebTools.FromHtml` | The page title was stripped with `<head>` before it was read. |

---

## Deliberate exclusions

| | Why |
|---|---|
| Kernel-level input injection | Refused. Input is synthesised with `SendInput`, which reaches everything user-mode automation reaches. A signed kernel driver that deliberately evades anti-cheat detection was not built. |
| Webcam capture | `Media Foundation`/`DirectShow` is disproportionate. Screen vision covers "what am I looking at". |
| Porcupine, NanoWakeWord, Vosk | The installed speech recogniser already does the job; see `plan.md` section 8.1. |

## Not verified here

| | |
|---|---|
| Scheduled task round trip | The Task Scheduler COM interface returns `0x800704E3` from this process even with the service running. The five tests that need a reachable scheduler are `[Explicit]`; the eighteen validation tests run. |
| Service installation | Needs an elevated shell. Not performed. |
| GitHub release | No `gh` CLI and no token in the environment. The artifact is built and validated but not uploaded. |

---

## Test suite

**435 passed, 0 failed**, exit 0, in one invocation. 0 warnings, 0 errors.

The suite used to abort partway through and report a passing count that was really a partial one. The
cause was a single test: `Tool_permissions_runs_a_permitted_class_without_asking` used a test double, which
belongs to no permission class, so the gate asked for confirmation and the call waited for an answer that
never came. Everything sorting after it never ran, and because the host stayed alive rather than exiting,
the truncation looked like a crash. Fixed in the product (a bounded wait) and in the test (a real
`write_file` tool).