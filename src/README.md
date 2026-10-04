# JARVIS

A voice-driven assistant for [Macro Deck 3](https://macro-deck.app). It listens for a wake word or a
push-to-talk key, sends what you said to the model you configured, and does what the answer asks using
the tools it has: your windows, your files, the clipboard, the microphone, a browser, and the registry.

Everything except the optional elevated service runs as your own user account, in the plugin process
Macro Deck starts for it.

## What it can do

Seven actions on the deck, and forty-nine tools the model can call.

| Area | Tools |
| --- | --- |
| Desktop | list and focus windows, open an application, close a window, list and kill processes |
| Input | mouse move, click, scroll and drag; type text; keyboard shortcuts and sequences |
| Screen | screenshot, screen reading through the configured vision model |
| Files | read, write, list, search, move, delete, check existence |
| Clipboard | read and write |
| Audio | set the volume, media keys, send a desktop notification |
| Web | fetch a URL, search, and search-then-read |
| Browser | navigate, read the page, click, type, list tabs |
| Registry | read, write and delete keys, as your user or through the elevated service |
| Scheduling | create, list, run and delete scheduled tasks |
| Shell | run a command from an allowlist, or any command when safety is off |

The actions are `jarvis-activate`, `jarvis-cancel`, `jarvis-toggle`, `jarvis-say`, `jarvis-check-models`,
`jarvis-manage-components` and `jarvis-confirm`.

## Installing

Install the plugin from the Creator Portal, or build it yourself (see [Building](#building)).

The plugin is disabled until you finish its setup flow. The flow asks for one language provider, one
speech-to-text provider, one text-to-speech provider and, optionally, API keys. Keys are stored in the
host's encrypted secret store; this plugin never writes a credential to a file of its own.

Two components can be downloaded on demand rather than at setup:

- **Whisper** for speech recognition.
- **Piper** for speech synthesis.

`jarvis-manage-components` fetches them. Nothing is fetched unless you ask.

### Configuration

`jarvis-check-models` probes whatever you have configured and tells you which endpoints answer, which
reject your key, and which are merely unreachable. Every failure is mapped to a distinct outcome, so a
rate limit does not arrive as "success".

## The elevated service

**Optional. JARVIS works without it.**

Some things genuinely need administrator rights: writing under `HKLM`, and creating a scheduled task
that runs with the highest privileges the account has. Rather than prompt you on every call, those are
routed to a separate Windows service.

```powershell
# From an elevated PowerShell:
cd src\scripts
.\install-service.ps1 -Start
```

To remove it:

```powershell
.\uninstall-service.ps1
```

### What it is, and where it runs

One binary, two modes. Started by the service control manager it is a background service with no window.
Started by a person it shows a notification-area icon.

### Its security boundary

This is the part worth reading before installing it.

The service runs as **LocalSystem**, which is the most privileged account on the machine. What keeps that
from being a problem is everything below, and each of these is a place where being slightly less careful
would have handed any process on the machine a SYSTEM shell.

**It is installed into Program Files, not a build directory.** A service registered from anywhere you can
write is a privilege escalation: you replace the executable, the next start runs your code as SYSTEM. The
installer publishes to `%ProgramFiles%\Jarvis Service`, applies a descriptor that grants SYSTEM and
Administrators full control and everyone else read-and-execute, and then **fails the install** if
ordinary users still hold any write right.

**It will not run from your source tree.** An earlier version was registered straight out of `bin\`, which
is inside your profile. The installer now removes any existing registration that points outside Program
Files before it registers anything.

**The pipe is granted to one account.** The installer records your own security identifier, and the pipe's
descriptor allows exactly that account, SYSTEM and Administrators. It is deliberately *not* granted to
the interactive group, which is in the token of every signed-in user on the machine.

**The registry surface is an allowlist of one root.** The service writes `HKLM\SOFTWARE\Jarvis` and keys
below it. It is an allowlist rather than a denylist because the paths that matter most are short, and a
denylist written by someone enumerating dangerous paths is missing at least one. Paths are normalized
before matching and compared on directory boundaries, so `.\SYSTEM\...`, a leading space and a redundant
`.` component all resolve to the same verdict.

**It only speaks HKLM.** `HKCU` used to be accepted and silently resolved to *LocalSystem's* hive, so a
model asked to edit the user's own settings wrote somewhere else entirely. The plugin runs as you and can
reach your hive directly; the service is for the machine.

**It bounds every message before reading it.** Each frame carries its own length, and a declared length
outside 1 to 64 KiB is refused without the body being touched. A newline-delimited protocol cannot do
that: the reader has already allocated whatever the sender declared, in a LocalSystem process.

**There is a switch, and it works.** The tray menu has "Allow administrator operations". Unticking it
refuses every operation except `ping` and `status`, so you can still find out why.

### It is optional, and nothing depends on it

The plugin probes the service once per connection. If it is not answering, the tools that need it are not
offered to the model at all, with the reason stated, rather than being offered and silently failing. Three
settings in the setup flow control this: whether to use the service, whether it may schedule tasks, and
whether it may change the registry.

### Logs

`%ProgramData%\Jarvis\Jarvis.Service.log`, readable only by SYSTEM and administrators, rotated at 1 MiB
with one previous file kept. `uninstall-service.ps1` removes it.

## Building

```powershell
cd src
dotnet build
dotnet test
```

## Packing

```powershell
macrodeck-plugin build --source src/Jarvis.Plugin --output ./artifacts
macrodeck-plugin inspect --artifact ./artifacts/com.misu.jarvis-1.1.1.macroDeckPlugin
```

A `dotnet build -c Release` output is **not** packable. The manifest points at `runtimes/win-x64/`, which
only `macrodeck-plugin build` assembles.

## Conformance

Two reports are committed, one per subject:

| Report | Subject | Carries |
| --- | --- | --- |
| `conformance.md` | the built project | capability contracts, cancellation, the reserved endpoints, logging limits |
| `artifact-conformance.md` | the packed artifact | the same, plus the manifest itself |

The project report **skips** the four manifest checks (`MDC0104`–`MDC0107`) by construction, because a
project's manifest points at a `runtimes/` slot that only the packer produces. That is expected. The
artifact report is the one that verifies the manifest version, id, SemVer, icon media type and protocol
range.

Regenerate both:

```powershell
cd src\scripts
.\regenerate-conformance.ps1
```

`.\require-conformance.ps1 -Report ..\conformance.md` fails if a report claims conformance while a
*Required* check skipped. It did, once: both committed reports read "Conformant: yes" with all four
manifest checks skipped.

## Releasing

Cutting a tag is the whole release. The `Publish plugin` workflow builds, validates at publication level
and registers the build with the Creator Portal using OIDC, so there is no long-lived credential on the
machine that made the release and the artifact is bound to a commit.

| File | What it does |
| --- | --- |
| `.github/workflows/verify.yml` | build, test, validate and gate on conformance for every push |
| `.github/workflows/publish-plugin.yml` | pack, validate and register the build on a published release |

## Layout

```
src/Jarvis.Plugin/          the plugin
  manifest.json             identity, icon, entrypoints, the ai disclosure, the service description
  JarvisFields.cs           every setting, declared once; the flow and the reader both derive from it
  JarvisConfigFlow.cs       the setup steps, built from that table
  PluginIntegration.cs      lifecycle, variables, widgets, issues, the orb
  Llm/                      the tool registry and one file per tool family
  Localization/             Strings.resx and Strings.de.resx
src/service/Jarvis.Service/ the optional LocalSystem service
  RegistryGuard.cs          the registry allowlist
  PipeServer.cs             the named pipe, its framing and its descriptor
src/scripts/                install, uninstall, conformance
src/tests/                  the test suite
```

## Licence

MIT. See [LICENSE](LICENSE).