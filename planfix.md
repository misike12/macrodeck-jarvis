## Resolution

Every finding below, and what became of it. The findings themselves are left exactly as they were
written: they are the record of what was wrong, and rewriting them would destroy the only account of
the starting state.

| Prefix | Findings | Closed | By decision | Deferred |
| --- | --- | --- | --- | --- |
| SEC | 31 | 31 | 0 | 0 |
| LIF | 14 | 13 | 1 | 0 |
| ACT | 32 | 32 | 0 | 0 |
| VAR | 14 | 14 | 0 | 0 |
| LOC | 18 | 18 | 0 | 0 |
| PKG | 15 | 14 | 1 | 0 |

**Closed** means the code or the document now behaves as the fix describes, and there is a test or a
verified check that fails if it stops.

**By decision** means the finding was real and was resolved by choosing differently, not by changing
code. Both are recorded in *Known limitations* in `current workstate.md`.

### The three that are not a code change

- **SEC-02 · pipe direction.** You chose inversion. It was not built, because inversion has LocalSystem
  connect *out* to a name any unprivileged process can create first, so a hostile process could receive
  registry writes meant for the service. The service instead owns the pipe and grants it to one recorded
  account. The `Plugin integration` decision table in this file still says inversion and is superseded by
  this section.
- **SEC-03 · the registry allowlist.** Built as an allowlist of `HKLM\SOFTWARE\Jarvis` only. The plugin has
  no Defender, firewall or machine-wide-settings feature, so the keys you asked to keep were only ever
  reachable through the generic model-facing registry tool, and that tool is now scoped rather than
  removed. Every path you might have wanted is still there; the persistence vectors are not.
- **LIF-04 · `IPluginCatalogNotifier`.** Not injected. Every catalogue this integration serves is fixed at
  process start, so there is no staleness to announce. Injected with nothing to say would have been worse
  than absent; the reasoning is on the type.

### The two the owner's machine has to confirm

- **PKG-03 · release provenance.** Closed as far as the repository can close it: a tag now triggers a
  workflow that packs, validates and registers a build through OIDC, with no long-lived credential. The
  release itself completes in the Creator Portal, which needs the account.
- **PKG-13 · `publisher.name`.** Resolved by decision: set to `misike12`, matching the repository owner, on
  the account owner's call. The plugin id moved with it, from `com.misu.jarvis` to `com.misike12.jarvis`,
  which the Store treats as a new listing rather than an upgrade. Recorded in *Known limitations* so the
  consequence is not discovered after the fact.

### Verified, not asserted

| | |
| --- | --- |
| Build | `dotnet build -c Release`, 0 warnings, 0 errors |
| Tests | `dotnet test`, 542 passed, 0 failed |
| Manifest | `macrodeck-plugin validate --artifact ... --level Publication`, 0 errors, 0 warnings |
| Conformance | both reports conformant; the artifact report carries MDC0104 through MDC0107 as PASS |
| Installed service | Running from `%ProgramFiles%\Jarvis Service` as LocalSystem, Auto, STOPPABLE |
| Pipe boundary | a framed ping answered `pong`; a frame declaring 1 GiB refused in 108 bytes |

The two service bugs found while chasing a flaky test, and the two bugs in the test suite itself, are
written up in the commit messages that fixed them. They are worth knowing about because both had been
masked: the pipe-name collision was invisible while the service allowed unlimited instances per name, and
the two test bugs were invisible because the tests asserted nothing that could distinguish them.

# planfix.md — every audit finding, and the work to close each one

Complete inventory of what the six audit agents reported against `macrodeck-plugin.md`, plus the two
findings I verified myself. Nothing is omitted; items an agent marked UNCERTAIN are included and
marked. Items an agent verified as correct are recorded at the end so they are not re-litigated.

## Decisions taken before writing this file

| Question | Decision |
| --- | --- |
| Pipe trust model | **Invert the direction.** The plugin creates a `CurrentUserOnly` server pipe as the interactive user; the LocalSystem service connects out as a client. Kernel-enforced boundary, no SYSTEM-owned pipe, `InteractiveSid` and all ACL reasoning deleted. Complexity accepted. |
| Service binary location | **Publish to `%ProgramFiles%\Jarvis Service`**, ACL to SYSTEM + Administrators only, register that as `ImagePath`. |
| Credential sources | **Config flow is mandatory.** Delete `LocalSettingsFile` and the `JARVIS_*_KEY` environment reads. Declare `RequiresConfiguration => true` explicitly. |
| Scope | **Literally everything.** All code findings CRITICAL through LOW, the UNCERTAIN ones, docs, README, AGENTS.md, CI, publishing, packaging hygiene. |

## Severity

- **CRITICAL** — data loss, privilege escalation, or a contract violation that breaks the plugin.
- **HIGH** — the plugin reports a falsehood to the user or the host, or a bound that exists only on paper.
- **MEDIUM** — a real defect with a workaround, or a contract rule broken in a way that will bite.
- **LOW** — hygiene, dead code, documentation drift.
- **UNCERTAIN** — reported but not provable from source. Resolve by reasoning or a test.
- **CORRECTION** — the agent's premise was wrong. Recorded so nobody re-audits it.

## Totals

| Section | CRITICAL | HIGH | MEDIUM | LOW | UNCERTAIN |
| --- | --- | --- | --- | --- | --- |
| LIF lifecycle/integration | 1 | 2 | 3 | 3 | 1 |
| ACT actions/tools/timeouts | 3 | 12 | 14 | 6 | 2 |
| VAR variables/config/events | 1 | 9 | 8 | 5 | 0 |
| LOC localization | 1 | 4 | 6 | 11 | 0 |
| PKG manifest/packaging | 0 | 4 | 3 | 5 | 1 |
| SEC service/security | 3 | 5 | 12 | 12 | 0 |
| **Total** | **9** | **36** | **46** | **42** | **4** |

---

# SEC — the elevated service

The service is a separate LocalSystem process. Nothing here is governed by the Macro Deck plugin
contract, so these are judged against Windows correctness and exploitability.

## SEC-01 · CRITICAL · Service binary is registered from a user-writable directory

`src/scripts/install-service.ps1:27,50` and `src/service/Jarvis.Service/ServiceInstaller.cs:268`

The SCM `ImagePath` becomes `C:\Users\<user>\Desktop\ideas\JARVIS\src\service\...\bin\Release\...\Jarvis.Service.exe`, running as LocalSystem at boot. Every ancestor directory is owned and writable by the interactive user, up to `C:\Users\<user>`.

Any process running as the interactive user — the plugin, a browser download, anything — can overwrite the exe and get code execution as SYSTEM on next boot, on `sc start`, or via the tray Reconnect button. This is the classic service-binary-planting escalation and it makes every other ACL question in this section irrelevant.

**Fix.** Publish to `%ProgramFiles%\Jarvis Service\`. Create the directory with an ACL granting write only to SYSTEM and BUILTIN\Administrators. Copy the binary and its whole output folder there. Register that copy. After install, verify with `icacls` and **fail the install** if BUILTIN\Users has `W` on the directory or the exe.

## SEC-02 · CRITICAL · Pipe grants ReadWrite to S-1-5-4 (INTERACTIVE)

`src/service/Jarvis.Service/PipeServer.cs:103-106`

Openers in service mode: LocalSystem (FullControl), BUILTIN\Administrators (FullControl), S-1-5-4 (ReadWrite). The "creating user" rule at `PipeServer.cs:110` **never applies in service mode** because the creator is LocalSystem and `identity.IsSystem` short-circuits it. So S-1-5-4 is the only non-administrator access.

S-1-5-4 is present in the token of every process in every interactive logon session: every signed-in user, every RDP session, fast-user-switching, and everything any of them runs.

On a shared machine a standard user writes `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\BootExecute` and owns the machine at next boot.

**Fix (decided).** Invert the direction. The plugin, running as the interactive user, creates the named pipe with `PipeOptions.CurrentUserOnly`. The service connects to it as a client. The kernel then enforces "only the token that created it". `WellKnownSidType.InteractiveSid` is deleted, `BuildSecurity` is deleted, and there is no SYSTEM-owned pipe to attack at all. Defence in depth: a random 256-bit token minted at install, required on every request.

## SEC-03 · CRITICAL · Registry guard is a denylist that omits `\SYSTEM`

`src/service/Jarvis.Service/ElevatedOperations.cs:261-275`

Eleven strings are blocked, including `\SAM`, `\SECURITY`, and exactly one leaf service key (`\SYSTEM\CurrentControlSet\Services\Schedule`). There is no allowlist. Reachable today with `hive: HKLM`:

- `SYSTEM\CurrentControlSet\Control\Session Manager\BootExecute` — SYSTEM at every boot.
- `SYSTEM\CurrentControlSet\Services\<any except Schedule>\Start`, `\ImagePath`, `\ServiceDll` — new auto-start SYSTEM service.
- `SYSTEM\CurrentControlSet\Control\Session Manager\Environment` — PATH injection into every process including SYSTEM ones.
- `SYSTEM\ControlSet001` — blocked `\Boot` and `\EFI` but **not** the live control sets.
- `SOFTWARE\Microsoft\Windows\CurrentVersion\Run`.
- `SOFTWARE\Classes\CLSID\{…}\LocalServer32` — COM hijack.
- `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\<name>` — task definitions (the *Schedule service key* is blocked, not the *tasks*).

With `hive: HKU` (allowed at `:325-328`), any loaded profile's `…\CurrentVersion\Run`. And `registry_delete` calls `DeleteSubKeyTree` at `:197`, which can remove `Session Manager` outright — an unbootable machine.

**Fix.** Invert to an **allowlist of roots**. Declare the exact permitted `(hive, path-prefix)` pairs — at minimum `HKLM\SOFTWARE\Jarvis`. Normalise the path before matching (strip leading `\`, collapse `.`, `\.\`, doubled separators, trailing spaces) and match on segment boundaries, never `StartsWith`. One negative test per allowed root.

## SEC-04 · HIGH · `StartsWith` matching makes every denylist entry prefix-bypassable

`src/service/Jarvis.Service/ElevatedOperations.cs:279`

`\SYSTEM\CurrentControlSet\Services\Schedule` is blocked, but `.\SYSTEM\…`, a leading space, and a redundant `.` component all resolve through `RegOpenKeyEx` while failing `StartsWith`. `..` is blocked at `:246`, which helps, but `.` is not.

**Fix.** Covered by SEC-03's normalisation. Add negative `[TestCase]`s for `.\`-prefixed and leading-space variants of every entry in the existing `DeniedPaths` table in `ElevatedOperationTests.cs`.

## SEC-05 · HIGH · The 64 KiB limit is enforced after the line is already in memory

`src/service/Jarvis.Service/PipeServer.cs:254` and `Protocol.cs:114-117`

`StreamReader.ReadLineAsync` accumulates into an internal `StringBuilder` until it sees `\n`. A peer writing 2 GB of `A` with no newline causes unbounded managed allocation **in a LocalSystem process**. The check at `Protocol.cs:117` runs only after the string exists, so the comment claiming protection is wrong. `ServiceProtocolTests.An_oversized_request_is_refused` passes a `string` straight to `ParseRequest`, so it tests the post-hoc guard and cannot catch the real exposure.

**Fix.** Frame the protocol with an explicit 4-byte little-endian length prefix, check against 64 KiB **before** reading the body, reject immediately. Apply the same on the client. Keep the newline as a secondary assertion. Add a test that a real oversized stream is refused rather than buffered.

## SEC-06 · HIGH · The service reports Running before the pipe exists, and a pipe that can never be created never fails the start

`src/service/Jarvis.Service/JarvisServiceHost.cs:34-37`, `PipeServer.cs:58-66,219-225`

`Start()` returns immediately; the `NamedPipeServerStream` is created inside a `Task.Run`. `OnStart` returns and the SCM flips to `Running`, during which a client gets `ERROR_FILE_NOT_FOUND`. Worse, pipe *creation* failures land in the same catch as a failed *accept*, log "A connection could not be served", and retry forever every second — so the service sits `Running` with no pipe and no distinct diagnostic. `OnStart`'s try/catch cannot help because `BuildSecurity` swallows `ArgumentException` and returns null.

**Fix.** Make readiness explicit. `Start()` must construct and await the first `NamedPipeServerStream` instance (construction is synchronous; only `WaitForConnectionAsync` awaits). Propagate creation failure to `OnStart`, which logs and rethrows so SCM records a failed start with a named cause. If the security descriptor cannot be built, fail the start rather than falling back to the OS default.

## SEC-07 · HIGH · One connection, no idle timeout, unlimited instances: single-client denial of service

`src/service/Jarvis.Service/PipeServer.cs:196,211,248-254`

`MaxAllowedServerInstances`, `WaitForConnectionAsync` with no timeout, and a read loop with no per-connection idle deadline. Any process holding S-1-5-4 connects and sends nothing; the plugin's `registry_elevated` then blocks until its 15 s deadline and fails, repeatedly, indefinitely. A single `while(true){ open }` loop denies the legitimate owner all elevated access.

Related: `Handle` runs inline on the listener task and `RegistryGet`/`RegistrySet` are synchronous Win32 calls with no cancellation, so one slow key stalls the listener with no way to abort.

**Fix.** Per-connection linked `CancellationTokenSource` with an idle timeout reset on each successful read. Cap concurrent connections. Move `Handle` to a bounded worker. Under the inverted model (SEC-02) the pipe is owned by the user, so this becomes a self-inflicted problem rather than a cross-user one, but it still needs the idle timeout.

## SEC-08 · MEDIUM · `Stop()` drops the listener task without awaiting it

`src/service/Jarvis.Service/PipeServer.cs:164,192-207`

`Stop()` sets `_listener = null` while the `using var server` in `ListenAsync` may not have observed cancellation. With unlimited instances a second listener on the same name is legal, so a stale server can answer the next client. `Program.cs:209-211` does `pipe.Stop(); pipe.Start();` on every reconnect. Also races `_current` disposal against the `finally { _current = null; }` at `:228`.

**Fix.** Await the listener with a bounded `Task.WhenAny` in `Stop()`, and set `MaxAllowedServerInstances = 1`.

## SEC-09 · MEDIUM · `shutdown` reports success and does nothing

`src/service/Jarvis.Service/PipeServer.cs:284,320`

`"shutdown" => Protocol.Reply(true, "Stopping.")` then `return`. `ServiceBase.RequestStop()` is never called. The reply claims success and the listener immediately creates a fresh server.

Answering the audit question directly: `shutdown` cannot stop anything, including this service, because it stops nothing. There is no path from a pipe message to any other service.

**Fix.** Call `RequestStop()` through a callback into the `ServiceBase` host, or remove the operation and refuse it. Never a truthful-sounding success for a no-op.

## SEC-10 · MEDIUM · `CanShutdown` is never enabled, so `OnShutdown` is dead code

`src/service/Jarvis.Service/JarvisServiceHost.cs:20-24,64-68`

`AcceptShutdown` is never set, so SCM never delivers `SERVICE_CONTROL_SHUTDOWN`.

**Fix.** `CanShutdown = true`, or delete `OnShutdown` and rely on `OnStop`. State `CanStop = true` explicitly since the class depends on it.

## SEC-11 · MEDIUM · A type-mismatched field throws out of `ParseRequest`, outside the try

`src/service/Jarvis.Service/PipeServer.cs:297,308,311` and `Protocol.cs:94,99`

`GetValue<string>()` on a JSON number and `GetValue<int>()` on a JSON string both throw `InvalidOperationException`. `TryParse` catches only `JsonException` (`Protocol.cs:130`). So `{"v":"1","op":"ping"}` throws before the `try` in `Handle`, escapes to the broad handler at `:219`, logs a misleading "A connection could not be served" and burns the retry delay. A trivially remote one-packet connection kill.

**Fix.** `TryGetValue<int>()` / `TryGetValue<string>()`, or move the `try` to wrap the parse and the `["op"]` read. Add test cases for `{"v":"1",…}`, `{"v":1,"op":7}`, `{"v":1,"op":null}`.

## SEC-12 · MEDIUM · A multi-line error reply desynchronises the newline framing

`src/service/Jarvis.Service/PipeServer.cs:276,330`

`exception.Message` from `SecurityException`, registry and IO is not sanitised and can contain CRLF. `StreamReader.ReadLineAsync` treats a bare CR or LF as a terminator, so the client reads only the first line, `TryParse` returns null, and the caller is told "The elevated service did not answer." The real error never arrives.

**Fix.** Escape control characters in every string passed to `Protocol.Reply`, or adopt length-prefixed framing per SEC-05, which removes the problem entirely.

## SEC-13 · MEDIUM · A legitimate large reply is indistinguishable from a dead service

`src/service/Jarvis.Plugin/Core/ServiceProtocol.cs:57` and `ElevatedServiceClient.cs:71-78`

The client rejects anything over 64 KiB and returns null, rendered as "The elevated service did not answer." The server imposes no reply bound: `RegistryGet` with no `name` returns up to `MaximumValues = 256` values of up to `MaximumTextCharacters = 8_000` each (`ElevatedOperations.cs:20,89,358`) — over 1 MB from one request. A working service looks broken and the model is told it is down.

**Fix.** One consistent limit on both sides, sized above the largest legitimate reply, or reduce the server's value/text caps to fit. Distinguish "malformed" from "oversized" in the client's error text.

## SEC-14 · MEDIUM · Install is not an update; a second run reports success and changes nothing

`src/service/Jarvis.Service/ServiceInstaller.cs:260-264`

`AlreadyInstalled` only calls `OpenService`. It never compares the registered `binPath` to the current executable. The code that did handle this — `NativeServiceControl.Create`, which calls `ChangeServiceConfig` (`:133-151`) — **is never called from anywhere**, confirmed by grep.

After moving or renaming the repo the script prints "already installed", exits 0, prints "Installed." and the SCM still points at the old path. This stale-registration case is the most likely cause of the repeated start failures.

**Fix.** On "already installed", call `ChangeServiceConfig` to update `binPath`/`DisplayName`/`start`, then report success only if the new path is registered. Handle `ERROR_SERVICE_EXISTS` and `ERROR_SERVICE_MARKED_FOR_DELETE` (1072) with a clear message.

## SEC-15 · MEDIUM · Unguarded `Console.Out.WriteLine` in a SYSTEM process

`src/service/Jarvis.Service/ServiceLog.cs:57`

Under LocalSystem in session 0 the standard handles can be invalid, so this throws `IOException` from inside `lock (Gate)`. In `OnStart` the path is `catch → ServiceLog.Error(...) → throw` (`JarvisServiceHost.cs:43-48`); if `ServiceLog.Error` itself throws, the original cause is lost and the SCM records only "the dispatch failed". A live suspect for the invisible start failures.

**Fix.** Write the file first, and guard the console write with the same catch, or skip it when `Environment.UserInteractive` is false.

## SEC-16 · MEDIUM · `hive: "HKCU"` in the service resolves to LocalSystem's hive

`src/service/Jarvis.Service/ElevatedOperations.cs:315-318`

`RegistryHive.CurrentUser` in a service is `HKEY_USERS\S-1-5-18`. The plugin-side `RegistryTools.ResolveHive` resolves the same string to the real user's hive, so the same logical operation means two different things depending on which side runs it.

**Fix.** Resolve HKCU to the caller's profile by token, or refuse it with a message naming `HKU\<sid>` as the correct form.

## SEC-17 · MEDIUM · Two tray controls have no effect; one is a fake security switch

`src/service/Jarvis.Service/TrayIconContext.cs:46-58,164,188`

"Start when I sign in" and "Allow administrator operations" both have no handler. `Startup.TryRegister`/`TryUnregister` are never called; `AllowAdminOperations` is written at `:58` and never read. Un-ticking the admin box leaves the pipe wide open and every registry operation still succeeding.

A menu item that lies about a security control is worse than no menu item.

**Fix.** Wire both up — the admin toggle must make `Handle` refuse every operation except `ping`/`status` — or remove both. Under SEC-02 the admin toggle becomes meaningful in a different way and needs rethinking alongside it.

## SEC-18 · MEDIUM · The plugin has no off switch for the service and never probes availability

`src/src/Jarvis.Plugin/PluginServices.cs:104`, `Core/JarvisSettingsStore.cs:46-48`

`ServiceEnabledField`, `ServiceSchedulingField`, `ServiceAdminField` and all three `JarvisSettings` properties are declared and **never read anywhere in the solution**. `ElevatedServiceClient.IsAvailableAsync` is likewise never called outside tests. The tool is registered unconditionally, so a user who turned the service off still has an LLM tool that silently talks to a LocalSystem process.

**Fix.** Gate registration on the setting, probe `IsAvailableAsync` in `InitializeAsync`, and push `CatalogChanged` when availability flips so the model stops being offered a tool that cannot work. Honour the other two fields or delete them.

## SEC-19 · MEDIUM · Elevated scheduled-task creation happens in the plugin process and is ungated

`src/src/Jarvis.Plugin/Llm/ScheduledTaskTools.cs:435-442`

The service exposes **no** `scheduled_task_*` operation — the six names in `Protocol.Operations` are unimplemented and refused by the switch default — so there is no SYSTEM task creation via the pipe. The task path is entirely in-process.

The only validation is `Path.IsPathRooted`, `File.Exists`, and an enum check on `schedule`. Nothing constrains `command`, so the model can register a task running `C:\Windows\System32\cmd.exe /c <anything>` with `RunLevel = 1`. There is **no command injection** into the task definition — `ExecAction.Path`/`Arguments` are set as properties, no shell is involved. `RequiresConfirmation = true` and the `File.Exists` check are real mitigations.

**Fix.** Require the elevated service for `RunLevel = 1` and route through an allowlisted path set under `%ProgramFiles%`, or drop `elevated` from the schema so tasks are always `RunLevel = 0`. Honour `ElevatedServiceScheduling`.

## SEC-20 · LOW · Protocol operation lists describe an interface that does not exist

`src/service/Jarvis.Service/Protocol.cs:45-59`, `PipeServer.cs:313-325`, `src/src/Jarvis.Plugin/Core/ServiceProtocol.cs:18-26`

The service declares `scheduled_task_create`, `scheduled_task_list`, `scheduled_task_delete`, `scheduled_task_run`, `settings_get`, `settings_set`. None is implemented. The plugin's `ServiceProtocol.Operations` correctly lists six and is **never referenced anywhere**.

**Fix.** Delete the six unimplemented names, or implement them. Delete the plugin's unused array or use it to assert before sending.

## SEC-21 · LOW · Provider internals and paths flow verbatim into the model

`src/service/Jarvis.Service/PipeServer.cs:330`, `src/src/Jarvis.Plugin/Llm/ElevatedRegistryTool.cs:117`

`$"{operation} failed: {exception.Message}"` carries full key paths, NTSTATUS text and internal names straight into a tool result the LLM reads. Contract §18: error messages carry no token, path or provider internal.

**Fix.** Log the detail via `ServiceLog` and return a bounded generic reason.

## SEC-22 · LOW · The ACL path fails open

`src/service/Jarvis.Service/PipeServer.cs:120-124,192-198`

The mechanism is **correct**: `SetAccessRuleProtection(true, false)` suppresses inheritance so the rules are the whole list, and the descriptor is passed as the final `pipeSecurity` argument to `NamedPipeServerStreamAcl.Create`. There is no `Everyone` or `Anonymous` rule. What is wrong is the failure mode — if `BuildSecurity()` returns null the service falls back to the OS default descriptor for the LocalSystem token and starts anyway.

**Fix.** Make `BuildSecurity()` failure fatal to the start. Remove the `_security is null` branch. Largely moot after SEC-02.

## SEC-23 · LOW · `Run` can deadlock or throw

`src/service/Jarvis.Service/ServiceInstaller.cs:188-193`

Reading stdout to completion before stderr is the classic two-pipe deadlock. `WaitForExit(30_000)` returning false makes the following `ExitCode` throw, unhandled.

**Fix.** Read both streams concurrently, and check the `WaitForExit` result before reading `ExitCode`.

## SEC-24 · LOW · Service log goes to `%ProgramData%` with the default ACL and is never cleaned up

`src/service/Jarvis.Service/ServiceLog.cs:75-81`

A machine-readable log of service errors readable by every local user. `uninstall-service.ps1` does not remove it. Answering the audit question: the service does **not** duplicate the plugin state directory — `settings_*` is unimplemented and the service is a separate process with no access to `MACRO_DECK_PLUGIN_DATA_DIRECTORY`, so there is no contract violation.

**Fix.** Create the directory with an explicit ACL and have uninstall delete it.

## SEC-25 · LOW · The event-log path is dead code

`src/service/Jarvis.Service/ServiceLog.cs:22,64-67`

`_consoleOnly` defaults true and nothing sets it false, so `EventLog.Write` is never reached and the `JarvisService` source is never registered. The class doc claims the event log keeps lines "a week later", which is not what happens.

**Fix.** Set `ConsoleOnly = false` in the service path, or delete `EventLog` and the flag and correct the comment.

## SEC-26 · LOW · Uninstall leaves three things behind

`src/scripts/uninstall-service.ps1:44-61`

Does not stop a running tray-mode instance (which keeps owning the pipe as the interactive user after the service is gone), does not remove `HKCU\…\Run\JarvisServiceTray` (`TrayIconContext.cs:141-142`), and does not remove `%ProgramData%\Jarvis\Jarvis.Service.log`.

**Fix.** Mirror `install-service.ps1:65-71`'s process kill, call `Startup.TryUnregister`, delete the log directory.

## SEC-27 · LOW · Hardcoded developer path; a service that never reaches Running still exits 0

`src/scripts/install-service.ps1:105,153-165`

An absolute path from one contributor's machine is committed. The `-Start` path — the only path that verifies the service works — warns and then prints "Installed." and exits 0.

**Fix.** Build the diagnostic path from `$binary`. `exit 1` when the service does not reach Running under `-Start`.

## SEC-28 · LOW · Kills any process named `Jarvis.Service` regardless of owner

`src/scripts/install-service.ps1:65-71`

On a shared machine this kills another user's tray instance. A deliberate, documented tradeoff, not a bug — but the comment overstates it.

**Fix (optional).** Filter on `Path` matching `$binary` and `SessionId` equal to the current session.

## SEC-29 · LOW · The manifest comment describes the opposite of the value

`src/service/Jarvis.Service/app.manifest:6-14`

`asInvoker` is **correct** for a service — SCM supplies LocalSystem and the manifest is ignored. But the comment says the manifest requests administrator rights and that asking for less would break HKLM work, which will mislead the next maintainer into "fixing" it to `requireAdministrator`.

**Fix.** Rewrite the comment to say the opposite of what it says.

## SEC-30 · LOW · Unused package reference and two contradictory installer implementations

`src/service/Jarvis.Service/Jarvis.Service.csproj:31`

`Microsoft.Extensions.Hosting.WindowsServices` is referenced but nothing uses it — `ServiceBase.Run` is called directly. `ServiceInstaller.CreateWithSc` says "Deliberately not the service control manager's own entry point" while `NativeServiceControl`'s class comment says "Driven by the service control manager's own API rather than by `sc.exe`". `NativeServiceControl.Create` — the update path SEC-14 needs — is unreachable.

**Fix.** Drop the package reference. Route `Install` through `NativeServiceControl.Create` (preferred, fixes SEC-14) or delete it. Make the comments agree.

## SEC-31 · LOW · The manifest declares nothing about the LocalSystem service

`src/src/Jarvis.Plugin/manifest.json:27-34`

`permissions` lists six entries, none corresponding to the elevated service. Not an enforcement gap — permissions are declarative — but a reviewer or user has no signal that this product installs a LocalSystem service with a named-pipe boundary.

**Fix.** Document the service, its boundary and its install path in README/AGENTS.md.

---

# LIF — lifecycle, builder chain, integration layer

## LIF-01 · CRITICAL · `context.CancellationToken` is discarded, so the host's 30s cap is unenforceable

`src/Jarvis.Plugin/Core/AssistantSession.cs:172,174,410`

`SayAsync` calls `BeginTurn()` first, which sets `_turnCts = new CancellationTokenSource()` — a fresh CTS linked to nothing. Then `var token = CurrentToken ?? cancellationToken;`. Because `BeginTurn` always runs first, `CurrentToken` is never null and the parameter is dead code, so `_conversation().RunAsync(prompt, history, token)` never sees `context.CancellationToken`. Both callers pass it (`Actions/SessionActions.cs:241`, `ListeningPipeline.cs:239`). Contract §6 / MDP3001.

**Fix.** Make the turn token a child of the invocation token: `BeginTurn(CancellationToken request)` using `CreateLinkedTokenSource(request)`.

## LIF-02 · HIGH · Cancellation is translated into `Success`

`src/Jarvis.Plugin/Speech/ListeningPipeline.cs:241-244`, `Core/AssistantSession.cs:225-229`

Both turn paths do `catch (OperationCanceledException) { return ActionResult.Success(); }`. In `ListeningPipeline` this also swallows the 20 s utterance-budget expiry. A turn cancelled at the 30 s cap having produced nothing reports a completed turn to the host.

**Fix.** Rethrow, or return `Failed(ActionErrorCodes.Timeout, …)`. A cancelled answer is not success.

## LIF-03 · HIGH · Two of seven buttons report success while doing nothing

`src/Jarvis.Plugin/Actions/SessionActions.cs:125-128` + `Core/AssistantSession.cs:96-127`, and `SessionActions.cs:210` + `AssistantSession.cs:148-151`

`ActivateMode.WaitForWakeWord` transitions state, calls `BeginTurn()` and returns `Success()`. No microphone is opened, `ConversationRunner` is never invoked, the wake word is not armed — and `_turnCts` is left non-null so every later activate returns `AlreadyRunning` until cancelled. `Toggle`'s common case (not running → activate) lands in the same dead path.

Also `timeoutSeconds` is declared as a parameter (min 1, max 600, default 20) and passed to `Activate`, whose body **never references it** (`AssistantSession.cs:96-127`). The user sets 600 and gets unbounded.

**Fix.** Route wait-for-wake-word through a real wait, or remove the mode. Use `timeoutSeconds` or delete the parameter and the setting.

## LIF-04 · MEDIUM · `IPluginCatalogNotifier` is absent from the entire codebase

Whole plugin tree.

The integration implements `IVariableProvider` but nothing injects `IPluginCatalogNotifier` or calls `CatalogChanged`. Contract §7: after a change, publish a state update; until the host has it, it serves the previous snapshot.

Note: `Actions`, `Variables` and `Surfaces` are currently constant, so nothing is strictly broken **today**. The one place that will need it is after `OrbWidgetTypeProvider.InitializeAsync` populates `_registered` — otherwise the host caches an empty widget-type list and the orb never appears in the picker.

**Fix.** Inject the notifier and call `CatalogChanged(CapabilityKinds.Variables)` / `(WidgetTypes)` wherever a catalogue is populated from configuration.

## LIF-05 · MEDIUM · Three `InitializeAsync` paths only ever turn features on

`src/Jarvis.Plugin/PluginIntegration.cs:135-145,167-174,208-217`

`InitializeAsync` runs again on any non-resume reconnect and on a configuration change, so it must converge on the current config. Three paths only enable:

- **Microphone:** `if (settings.MicrophoneAlwaysOn) { … }` with no `else`. Turning the setting off leaves the mic **open and recording**. `Stop()` is called only from `ShutdownAsync`.
- **Hotkey:** the parse-failure branch returns **before** `_hotkey.Unregister()` and before the handler swap. Clearing the chord leaves it registered with the OS.
- **Wake word:** `if (!_wakeWord.Enabled) return;` leaves `Recognizer` holding a closure that captured the *previous* `settings`, and `_microphone.AttachTap` is never undone.

**Fix.** `else { _microphone.Stop(); }`; move the `-=` to the top of the method and unregister on the parse-failure path; clear `Recognizer` and detach the tap on the disabled path, and re-assign `Recognizer` unconditionally.

## LIF-06 · MEDIUM · `ShutdownAsync` detaches nothing

`src/Jarvis.Plugin/PluginIntegration.cs:272-278`

Stops the mic, cancels the session, resets state. `_hotkey.Pressed -= OnHotkeyPressed`, `_hotkey.Unregister()` and the wake-word detach never happen. Between `ShutdownAsync` and the next `InitializeAsync` the plugin is still live: `OnHotkeyPressed` starts a full voice turn with `CancellationToken.None`, holding a concurrency slot and the microphone for up to 20 s with no session.

**Fix.** Unsubscribe both handlers, unregister the hotkey, disable the detector.

## LIF-07 · MEDIUM · Constructors do I/O and start threads, on the `Build()` validation path

`Core/JarvisSettingsStore.cs:79`, `Memory/MemoryStore.cs:47`, `Runtime/RuntimeManager.cs:45`, `Core/AssistantSession.cs:72`, `Input/GlobalHotkey.cs:51-52`

`Build()` constructs every integration and handler as validation. `JarvisSettingsStore` does `File.Exists` + `File.ReadAllText`; `MemoryStore` reads notes; `RuntimeManager` reads the manifest; `AssistantSession` creates a Win32 Job Object handle; `GlobalHotkey` **starts an OS thread**.

**Fix.** Move file reads into `InitializeAsync`; put the thread and job handle behind `Lazy<T>` or an explicit `Start()`.

## LIF-08 · MEDIUM · Plaintext credentials read from the immutable version directory

`src/Jarvis.Plugin/Core/JarvisSettingsStore.cs:79` → `LocalSettingsFile.cs:31-33`

(Decision: config flow is mandatory.) The file exists in `bin/Debug/net10.0/jarvis.settings.json` right now, 265 bytes, with nvidia/picovoice/selfHostedNim sections. It is gitignored, so it would not be published — but if anyone copies it beside the output, keys ship in plaintext outside the host's encrypted store.

**Fix.** Delete `LocalSettingsFile` and the two `JARVIS_*_KEY` environment reads (`JarvisSettingsStore.cs:233-234`). Keep the injectable `LocalSettingsFile` overload for tests only.

## LIF-09 · MEDIUM · `RequiresConfiguration` is undeclared while three code paths assume otherwise

`src/Jarvis.Plugin/PluginIntegration.cs:110-112`

The interface default is `true`, which is correct in isolation, but the plugin's own documentation at `JarvisSettingsStore.cs:51-55` and `LocalSettingsFile.cs:6-10` describes the file and env-var paths as supported ways to get a key in "before any flow has been completed" — unreachable while the host will not start the integration.

**Fix (decision: mandatory).** `public bool RequiresConfiguration => true;` explicitly, and delete the other two credential sources per LIF-08.

## LIF-10 · MEDIUM · Two standing conditions are logged only, and a doc comment claims otherwise

`src/Jarvis.Plugin/PluginIntegration.cs:141-145,176-181`

No microphone and hotkey-unavailable are both `_logger.Warning` and nothing else. `RegisterHotkey`'s comment says "the failure becomes an issue the user can act on" — no `IntegrationIssue` is created. `RuntimeManager` is the only `IIntegrationIssueProvider` and knows only about downloaded assets.

**Fix.** Add `microphone-unavailable` and `hotkey-unavailable` to the issue provider with `ActionLabel` strings and `ResolveIssueAsync` arms, and fix the comment.

## LIF-11 · UNCERTAIN · `_resources` is written on one thread and read on another

`src/Jarvis.Plugin/PluginIntegration.cs:124,322-325`

`InitializeAsync` writes it; `CreateSessionAsync` reads it concurrently with up to 32 invocations, with no `volatile` or lock.

**Fix.** Make the field `volatile`, or resolve the registry per session.

## LIF-12 · LOW · Variables report `0` and `""` where they should report unavailable

`src/Jarvis.Plugin/PluginIntegration.cs:292,306`

`download-percent` returns `0` for "no download running" and `download-label` returns `string.Empty`. Contract §7: use `Unavailable` for "no value right now", never `""` or `0`.

**Fix.** Return `VariableReading.Unavailable` when `!download.Active`.

## LIF-13 · LOW · SDK package versions are pinned

`src/Directory.Packages.props:10`

`<MacroDeckSdkVersion>3.0.0-beta.14</MacroDeckSdkVersion>` pins all six Macro Deck packages. The pin is deliberate and documented — the template default `3.0.0-*` resolves to `3.0.0-preview.10`, which predates `beta.14` in SemVer ordering and lacks `SupportsFlows`, `AppearanceProperties` and `UiModifier`.

**Fix.** None required. Keep the comment and re-verify when 3.0 ships stable.

## LIF-14 · UNCERTAIN · The wake word can never fire

`src/Jarvis.Plugin/Input/WakeWordDetector.cs`

`OfferAsync` is called **only from tests**. `ConfigureWakeWord` subscribes `_wakeWord.Detected` and attaches the mic tap, but nothing ever offers a microphone level, so the detector never receives audio regardless of configuration. Outside the SDK contract but a hard functional defect.

**Fix.** Feed levels from the microphone tap in `PluginIntegration`.

---

# ACT — actions, tools, timeouts

## ACT-01 · CRITICAL · Waits far beyond the 30 s capability-invoke bound

Contract §5 and §14: `ProtocolTimeouts.CapabilityInvoke` is 30 s; past it the host sends `capability.cancel`, releases the slot and cancels the token.

| Sub | Location | Bound |
| --- | --- | --- |
| ACT-01a | `Llm/ToolRegistry.cs:53,91,95` | `ConfirmationTimeout = TimeSpan.FromMinutes(10)`. Reachable `SessionActions.cs:133 → ListeningPipeline.cs:239 → AssistantSession.cs:187 → ConversationRunner.cs:88 → ToolRegistry.cs:95`. The *tool* correctly returns a Failure on timeout at `:107`; the defect is that the executor slot is held for 600 s first. |
| ACT-01b | `Speech/WhisperTranscriber.cs:59,191` | `Budget = TimeSpan.FromMinutes(5)` guarding `process.WaitForExitAsync` at `:195`. Reached from `ActivateAction` via `ListeningPipeline.cs:219`. |
| ACT-01c | `src/Jarvis.Plugin/PluginServices.cs:144` | `client.Timeout = TimeSpan.FromSeconds(120)` on the `nim` client, the only client `ChatClient` (`:48`) and `RuntimeManager` (`:56`) use. Multiplicative: `ConversationRunner.cs:37` clamps iterations to 16, `ChatClient.cs:74` calls `SendAsync` per iteration → **1920 s** worst case in one executor call. |
| ACT-01d | `Speech/ListeningPipeline.cs:139,205,219` | 20 s record + 300 s transcribe + up to 1920 s speech = per-stage budgets summing far past 30 s. The whole voice turn needs one end-to-end budget. |
| ACT-01e | `Actions/CheckModelsAction.cs:54,59` | `ProbeTimeout = 10 s` twice = 20 s for "both", then an unbounded modal at `:66`. |
| ACT-01f | `Llm/BrowserTools.cs:42,105-115,123` | `StartupTimeoutSeconds = 30` polling loop alone consumes the entire budget, then `CreateAsync` (`:503-527`) follows. Then `NavigateAsync:661 → WaitForLoadAsync:679 (20 s) → ReadPageAsync:611`. |
| ACT-01g | `Actions/ManageComponentsAction.cs:77-79` | `EnsureComponentAsync` is unbounded: `RuntimeManager.cs:144-152` loops assets → `AssetDownloader.InstallAsync:80 → FetchAsync:138` streams a model file bounded only by the 120 s client timeout, then `RuntimeManager.cs:95` runs a 20 s executable probe per asset. |

**Fix.** One end-to-end budget per executor call, well under 30 s, threaded down. The 10-minute confirmation window is right for *voice* but cannot live on the deck-action path.

## ACT-02 · CRITICAL · `BeginTurn` disposes a live CTS without cancelling it

`src/Jarvis.Plugin/Core/AssistantSession.cs:409-410`

`_turnCts?.Dispose(); _turnCts = new CancellationTokenSource();`. Two concurrent invocations cross turns: the in-flight turn's token now belongs to a disposed CTS, and its `finally { EndTurn(); }` at `:232` clears `_turnCts` and cancels the *second* turn. Disposing without cancelling also orphans any waiter on `_decision` (`WaitForDecisionAsync:336`).

**Fix.** Cancel-then-dispose, and make `BeginTurn` reject a second concurrent turn.

## ACT-03 · CRITICAL · The LLM HTTP client has no bound the SDK can enforce

Same root cause as ACT-01c, listed separately because the fix is different: `PluginServices.cs:144`.

**Fix.** Give `ChatClient` its own client with a bound derived from the caller's remaining budget, not a fixed 120 s.

## ACT-04 · HIGH · The SSE stream read has no deadline

`src/Jarvis.Plugin/Llm/ChatClient.cs:96-97,406`

Because `:74` uses `HttpCompletionOption.ResponseHeadersRead`, `HttpClient.Timeout` does **not** cover the content stream. `ReadStreamAsync`'s `while (await reader.ReadLineAsync(cancellationToken) … is { } line)` has no deadline of its own. A server holding the connection open and emitting keepalive comments never terminates the loop, and this is the first thing every executor that speaks reaches.

**Fix.** A linked CTS with `CancelAfter` around the stream read.

## ACT-05 · HIGH · `CheckModels` reports Success on every failure except one

`src/Jarvis.Plugin/Actions/CheckModelsAction.cs:77-79,85` + `Llm/ChatClient.cs:149,157,161,165`

The only recognised failure is the literal `"(unreachable)"`. `ChatClient.ProbeAsync` can also return `NotConfigured` (`:121,128`), `Unauthorized` and `RateLimited` (`:149`), `NotFound` (`:157`) and `Timeout` (`:165`), rendering as `(notconfigured)`, `(unauthorized)`, `(ratelimited)`, `(notfound)`, `(timeout)`. Every one falls through to `ActionResult.Success()`.

An unconfigured provider, a rejected API key, a rate limit, a probe timeout and a misspelled model are all reported to the user as success. This is also why `ActionErrorCodes.Timeout`, `.NotFound` and `.ProviderRejected` are never used anywhere in the plugin.

**Fix.** Switch on `probe.Failure` and map to the correct code.

## ACT-06 · HIGH · `CheckModels` returns Success on an unvalidated parameter

`src/Jarvis.Plugin/Actions/CheckModelsAction.cs:37,48,52,57,77-79`

`required: true`, but the executor only tests `selection is "llm" or "both"` / `"vision" or "both"`. An unrecognised value falls through both branches, leaving `lines` empty and `report` empty, then returns `Success()` plus an empty modal.

**Fix.** Validate against the three declared options; `Failed(InvalidParameter, …)` otherwise.

## ACT-07 · HIGH · The `CheckModels` modal points at a view that does not exist

`src/Jarvis.Plugin/Actions/CheckModelsAction.cs:66-74`

`ViewId = "jarvis-model-report"` appears nowhere else in the repository. `PluginIntegration.cs:320` declares only `Widget`, `Preview` and `Config` surfaces — there is no Modal surface and no such view. The modal cannot render. It also awaits a human dismissal bounded only by the host's 30 s cap.

**Fix.** Declare the view, or drop the modal and return the report through action state.

## ACT-08 · HIGH · `ManageComponents` returns `Accepted` for an already-confirmed operation

`src/Jarvis.Plugin/Actions/ManageComponentsAction.cs:85`

`return result.AlreadyPresent ? Success() : Accepted(Strings.Runtime.Installed(component));`. `RuntimeManager.EnsureAsync:88` awaited the whole download, verified the SHA-256 pin (`:101-103`) and ran the executable probe (`:95`), returning `AssetFailure.Unusable` if it would not start (`:98-100`). `Installed == true` means on-disk, hash-verified and runnable.

**Fix.** `ActionResult.Success()`.

## ACT-09 · HIGH · `ActionResult.Success(string)` is the **expectedStateId** overload

`src/Jarvis.Plugin/Actions/ConfirmAction.cs:60-66`

The localized sentence is passed as an expected state id. The host receives "Approved run_shell." where a state identifier belongs, and `AssistantStateReader.cs:24-42` defines exactly `unavailable|idle|listening|thinking|speaking|executing|confirming|error`. The in-code comment shows the author believed this was a message parameter.

**Fix.** `ActionResult.Success()`. The outcome is already observable through `IStateProviderActionDefinition`.

## ACT-10 · HIGH · `SetSystemPower` drops the cancellation token entirely

`src/Jarvis.Plugin/Llm/SystemTools.cs:148,184,201`

`InvokeAsync(JsonObject, CancellationToken)` never reads `cancellationToken`; all four call sites pass through `ExecuteAsync(fileName, arguments, message)` which takes no token. `rundll32.exe powrprof.dll,SetSuspendState 0,1,0` (`:169-172`) can block indefinitely.

**Fix.** Add the parameter; pass it to `WaitForExitAsync`.

## ACT-11 · HIGH · Blocking calls inside whole contract types

`src/Jarvis.Plugin/Llm/ScheduledTaskTools.cs:63-116`, `Llm/DesktopTools.cs:309-332`

`new Thread(() => { … completion.SetResult(action()); })` returned as a Task, no timeout, no cancellation. Inside, `target.GetTasks(0)` (`:179`), `RegisterTaskDefinition` (`:447`) and `task.Run(null)` (`:582`) are unbounded COM calls. If the scheduler hangs, `completion.Task` never completes and **the slot is never released — not even at the 30 s cap**. Thread-per-call with 32 slots is the starvation risk MDP3002 exists for.

**Fix.** Bound it, or route through a dedicated sized pool.

## ACT-12 · HIGH · Unbounded thread-per-call in `DesktopTools`

`src/Jarvis.Plugin/Llm/DesktopTools.cs:309-332`

Same shape, blocking on `OpenClipboard`.

**Fix.** As ACT-11.

## ACT-13 · MEDIUM · `WebFetch` / `WebSearchAndRead` read the body with the wrong token

`src/Jarvis.Plugin/Llm/WebTools.cs:227,368`

The per-request `timeout.Token` (`:209-211`, `:351-352`) is not used for the body read; the outer token is. Because `:140`/`:354` use `ResponseHeadersRead`, `HttpClient.Timeout` does not cover the content read either. A slow-trickling body is bounded only by the host's cap.

**Fix.** Both to `timeout.Token`.

## ACT-14 · MEDIUM · Browser DevTools round trips have no deadline and no length cap

`src/Jarvis.Plugin/Llm/BrowserTools.cs:551-567,834-867`

`while (true) { var reply = await ReceiveAsync(cancellationToken); }` and `foreach (var character in text)` with two `SendAsync` per character. No per-message deadline, and `TypeAsync` has no length cap — unlike `KeyboardTypeTool` which caps at 8,000 characters (`InputTools.cs:558`). A model asking for 100k characters makes 200k round trips.

**Fix.** Per-message deadline and a length cap.

## ACT-15 · MEDIUM · The pipe client drops the token on write

`src/Jarvis.Plugin/Core/ElevatedServiceClient.cs:69`

`WriteLineAsync(request.ToJsonString())` with no token; `:71` correctly passes `timeoutSource.Token` to the read.

**Fix.** `WriteLineAsync(memory, token)`.

## ACT-16 · MEDIUM · `Accepted` used for refusals

`src/Jarvis.Plugin/Core/AssistantSession.cs:102`, `Speech/ListeningPipeline.cs:183`

`return ActionResult.Accepted(Strings.Errors.AlreadyRunning());` when `_turnCts is not null`. Nothing was taken by the provider; the request was refused, and refusal is entirely confirmable.

**Fix.** `Failed(ActionErrorCodes.Unavailable, …)`.

## ACT-17 · MEDIUM · `ReadMode` silently defaults an unrecognised value

`src/Jarvis.Plugin/Actions/SessionActions.cs:87,191,25-38`

`required: true`, but a non-string wire type or an unrecognised string becomes `ActivateMode.OneShot` — and via ACT-03 that path reports Success.

**Fix.** `Failed(InvalidParameter, …)` on an unknown value.

## ACT-18 · MEDIUM · Asset URL and SHA digests reach the user-facing issue

`src/Jarvis.Plugin/Runtime/AssetDownloader.cs:98` + `Runtime/RuntimeManager.cs:314-333`

`AssetInstallResult.Failed(AssetFailure.Unreachable, asset.Url)` embeds the full download URL, which flows into the issue description. `RuntimeManager.cs:311-313` even comments that the URL is not worth including — and then passes it.

**Fix.** Drop `asset.Url` from the failure detail; log it instead.

## ACT-19 · MEDIUM · `probe.Detail` is unlocalised provider text shown in the UI

`src/Jarvis.Plugin/Actions/CheckModelsAction.cs:87,72` + `Llm/ChatClient.cs:161,237,240`

`$"{model}: {verdict} - {probe.Detail}"` where `Detail` is `exception.Message` or up to 300 characters of raw provider response body.

**Fix.** Map `ModelFailure` to `Strings.Errors.*`; keep `Detail` in the log.

## ACT-20 · MEDIUM · `ConnectTimeout` is declared and never used

`src/Jarvis.Plugin/Runtime/AssetDownloader.cs:75`

`private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);` — dead field; the connect is bounded by the 120 s client timeout.

**Fix.** Use it or delete it.

## ACT-21 · MEDIUM · `ResolveIssueAsync` returns `Ok()` for an unknown id

`src/Jarvis.Plugin/Runtime/RuntimeManager.cs:289-292`

An id that was never reported resolves successfully. No `UnknownIssue` key exists in the catalog.

**Fix.** Add `Strings.Runtime.Issue.UnknownIssue()` and return `IssueResolution.Failed`.

## ACT-22 · MEDIUM · Structured log property loses the asset from the persisted log

`src/Jarvis.Plugin/Runtime/RuntimeManager.cs:295`

`_logger.Information("Retrying {Asset} after the user asked.", issueId)` — contract §13: structured properties reach the live viewer but are **not persisted** to the log file, so the record keeps only "Retrying after the user asked."

**Fix.** Put the asset in the message template.

## ACT-23 · MEDIUM · The widget type is never unregistered, despite a comment saying it is

`src/Jarvis.Plugin/PluginIntegration.cs:313-314` vs `:272-278`

The comment says widget types "are withdrawn when the integration stops". `IWidgetTypeProviderContext.UnregisterWidgetTypeAsync` exists and is never called.

**Fix.** Hold the context and unregister, or delete the sentence.

## ACT-24 · MEDIUM · `OrbAssetCache` is reconstructed per session

`src/Jarvis.Plugin/PluginIntegration.cs:322-325` → `Orb/OrbUiProvider.cs:25`

A new `OrbUiProvider` per `CreateSessionAsync`, each with its own `OrbAssetCache` — whose entire purpose is to be a process-wide cache. Opening a second widget re-encodes every GIF. Worse, each re-encode calls `RegisterAsync` under the **same** resource name (`OrbAssetCache.cs:117`), so the host accumulates duplicate registrations.

**Fix.** Construct one provider/cache in `InitializeAsync` and reuse it.

## ACT-25 · HIGH · `OrbUiSession` stores an invocation `CancellationToken` for the life of a widget session

`src/Jarvis.Plugin/Orb/OrbUiProvider.cs:55` → `Orb/OrbUiSession.cs:28,42-44,66,93`

The token passed to `CreateSessionAsync` is scoped to the `ui.create` invocation. A widget session outlives that invocation by hours. Once the invocation completes the host cancels the token, so every later `_assets.GetAsync(…)` throws at `OrbAssetCache.cs:110`, is swallowed at `:132-136`, evicts the entry and returns null — **the orb silently freezes on its last frame forever**, at Debug log level only. `OnSnapshot` fires on every state change, so it triggers the moment the user next speaks.

**Fix.** Give the session its own `CancellationTokenSource`, cancelled in `DisposeAsync`.

## ACT-26 · MEDIUM · `IConfigFlow` step dispatch and a drifted field table

`src/Jarvis.Plugin/JarvisConfigFlow.cs` + `Core/JarvisSettingsStore.cs:102-135`

Three hand-maintained copies of the field list — `StringFields`, the step builders, and the `Complete` dictionary — have already drifted:

- `TtsModelField` is declared as a Voice-step field (`JarvisConfigFlow.cs:173`) and read back (`JarvisSettingsStore.cs:113,259`) but is **absent from `Complete`**.
- Thirteen settings are read back but unreachable from the flow at all: `WakeSensitivityField`, `MicrophoneIdField`, `MicrophoneNameField`, `MicrophoneAlwaysOnField`, `WakeWordEnabledField`, `BargeInField`, `BargeInThresholdField`, `MaxIterationsField`, `TimeoutField`, `LifetimeField`, `ServiceEnabledField`, `ServiceSchedulingField`, `ServiceAdminField`.

**Fix.** Drive `StringFields` and the step builders from one declared table.

## ACT-27 · LOW · `Thread.Sleep` on an executor-reachable path

`src/Jarvis.Plugin/Llm/NativeClipboard.cs:153-161`

Five attempts with `Thread.Sleep(20)` between — max 100 ms on a dedicated STA thread, so it does not hold a slot. Literal MDP3002 wording.

**Fix.** Spin/yield instead.

## ACT-28 · UNCERTAIN · Sync-over-async in `WindowsSynthesizer`

`src/Jarvis.Plugin/Speech/WindowsSynthesizer.cs:121`

`RunAsync(scriptPath, [], CancellationToken.None).GetAwaiter().GetResult()` inside `RunProbe`, called from `InstalledVoices()` → `VoiceService.AvailableVoices()`. Real, and it shells out to powershell.exe, but **not currently reachable from an executor** — grep finds no caller of `VoiceService.AvailableVoices()`.

**Fix.** Fix now; it becomes reachable the moment a config-flow edit surfaces the voice dropdown.

## ACT-29 · LOW · `Platforms` is set on all seven actions and is inert

`Actions/SessionActions.cs:105,154,194,232`, `CheckModelsAction.cs:40`, `ManageComponentsAction.cs:41`, `ConfirmAction.cs:34`

MDP2006 Warning. The build reports 0 warnings, so the analyzer is evidently not firing — worth checking.

**Fix.** Delete all seven; platform gating comes from the manifest's `entrypoints`.

## ACT-30 · LOW · `_ => component` returns a raw id as a user-visible label

`src/Jarvis.Plugin/Actions/ManageComponentsAction.cs:35,46-51`

Unreachable today because the catalog has exactly `piper` and `whisper`, both mapped.

**Fix.** Make the switch exhaustive.

## ACT-31 · LOW · Loose parameter coercion

`src/Jarvis.Plugin/Actions/SessionActions.cs:40-68`

`ReadText` does `raw?.ToString()`, so a JSON number 5 becomes `"5"`. `ReadFlag` matches only `bool` or a parseable string, so `"yes"` silently becomes false. Consistent with the SDK's non-nullable `IReadOnlyDictionary<string, object>`, but there is no rejection path.

**Fix.** Reject wrong-typed values.

## ACT-32 · CORRECTION · There are 7 actions, not 34

`IActionDefinition` appears in exactly **5 files** declaring **7 types**: `ActivateAction`, `CancelAction`, `ToggleAction`, `SayAction`, `CheckModelsAction`, `ManageComponentsAction`, `ConfirmAction`. The 49 `ITool` types in `Llm/` are the plugin's private LLM function-calling schema and never cross the Macro Deck wire.

**Consequence for limits.** Total declared capabilities ≈ 19 against a 512 cap. The `actions/describe` payload is roughly 60–70 KB against 256 KiB, dominated by the localization catalog (`Strings.resx` 25,922 B + `Strings.de.resx` 24,356 B). **MDC0305 has no plausible risk**; the catalog is the only term that could grow.

**All 7 action ids verified valid** and unique: `jarvis-activate`, `jarvis-cancel`, `jarvis-toggle`, `jarvis-say`, `jarvis-check-models`, `jarvis-manage-components`, `jarvis-confirm`.

---

# VAR — variables, config flow, events, issues

## VAR-01 · CRITICAL · The setup flow wipes every stored setting, including all three credentials

`src/Jarvis.Plugin/JarvisConfigFlow.cs:68-98` — **verified by direct reading**

The flow is a five-step wizard: `provider → keys → models → voice → behaviour`. `Complete()` runs on the last step but builds all 24 stored values from **that step's `input` only**:

```csharp
private static string Read(input, name) => input.GetValueOrDefault(name)?.ToString() ?? string.Empty;
```

The keys were collected on step 2. By step 5, `input` has no key fields, so `Read` returns `string.Empty` and `:72-74` writes `ConfigFlowValue.Secret("")` for all three credentials. The same applies to all 21 plain fields — model names, persona, prompt, notes.

**The host already persists form values across steps**, so the entire dictionary is unnecessary. This is data loss triggered by simply completing setup.

**Fix.** `return ConfigFlowResult.Complete("JARVIS");` and delete the dictionary. Then fix ACT-26's drift.

## VAR-02 · HIGH · `IUserNotifier` is never used; two standing conditions are invisible to the user

Whole tree — zero references.

See LIF-10 for the detail. The design rule (§10) is that a standing condition belongs in `IIntegrationIssueProvider`, and microphone-unavailable and hotkey-unavailable currently go to a log line only.

**Fix.** Per LIF-10.

## VAR-03 · HIGH · Variable `Name` vs `Id` audit

`src/Jarvis.Plugin/PluginIntegration.cs:76-93`

Six eager variables. The split is the most error-prone part of the variables contract: `Name` is what the user types (`[a-z0-9_]`, plugin-prefixed), `Id` is what `ReadAsync` receives and what is persisted.

**Fix.** Verify each of the six declares an explicit `Id` and that no `Id` equals a non-suffixed `Name`.

## VAR-04 · MEDIUM · `PluginIntegration.cs:288` publishes a raw enum name

`"state" => VariableReading.Of(snapshot.State.ToString().ToLowerInvariant())` — `idle`, `listening`, `unavailable` as user-visible values, while `States.Idle`/`Listening`/`Unavailable` exist to render them in the reader's language.

**Fix.** Publish a localized label, or add a sibling variable and document which is display text.

## VAR-05 · MEDIUM · `PluginIntegration.cs:307-308` publishes an untranslated download label

`$"{progress.AssetId} done"` and `$"{progress.AssetId} {progress.Phase} {progress.Percent}%"`. `" done"`, the spacing and `"%"` are literals, and `Phase` is itself an English literal (`AssetDownloader.cs:145,197,201`: `"downloading"`, `"verifying"`; `RuntimeProgressReporter.cs:72`: `"done"`).

**Fix.** Add `Strings.Variables.DownloadLabel.*` keys with named placeholders.

## VAR-06 · MEDIUM · `AssetCatalog.Purpose` is an English literal shown to users

`src/Jarvis.Plugin/Runtime/AssetCatalog.cs:92,104,116,133,144`

Documented at `:45` as "Human-readable purpose, shown in an issue when the asset cannot be fetched", but all five values are bare English literals rendered through `{purpose}` at `RuntimeManager.cs:329`.

**Fix.** Add `Strings.Runtime.Asset.Purpose.<asset-id>` or a `PurposeKey` on `PinnedAsset`.

## VAR-07 · MEDIUM · `DescribeProbe` English text flows into a user-facing issue

`src/Jarvis.Plugin/Runtime/RuntimeManager.cs:340-345`

Its own comment says this is "log output rather than something to translate" — but the value flows through `RecordFailure(…, DescribeProbe(…))` at `:98-99` into `IntegrationIssue.Description` at `:329`.

**Fix.** Localize, or log it and leave `failure.Detail` null.

## VAR-08 · MEDIUM · `"downloaded"` is an English sentinel used as control flow

`src/Jarvis.Plugin/Runtime/AssetDownloader.cs:53,56`

`Ok(path, alreadyPresent: false)` sets `Detail = "downloaded"` and `AlreadyPresent => Installed && Detail is null` uses that English string as a discriminator.

**Fix.** A bool or nullable enum member.

## VAR-09 · MEDIUM · `LocalizedText` forced through a `string` placeholder

`src/Jarvis.Plugin/Runtime/RuntimeManager.cs:329`

`Strings.Runtime.Issue.Detail(description, …)` where `description` is a `LocalizedText` binding to the `string` `{reason}` parameter, forcing an implicit `.ToString()` in the **plugin process culture**, not the reading client's. A German reader gets an English reason inside a German sentence.

**Fix.** Take the reason as its own reference, or resolve through the caller's culture.

## VAR-10 · MEDIUM · `IWidgetTypeProviderContext.UnregisterWidgetTypeAsync` never called

See ACT-23.

## VAR-11 · MEDIUM · Manifest permissions drift from what is implemented

`src/Jarvis.Plugin/manifest.json:27-34`

`events:publish` and `host:notifications` are declared, but the plugin implements no `IEventProvider` and never calls `IUserNotifier`. `plan.md:128` lists `events` among chosen capabilities, so this is an unfinished capability rather than a deliberate omission. Not an enforcement gap — permissions are declarative — but `MDC0804` passes vacuously.

**Fix.** Implement `IEventProvider` (with `CatalogChanged`) or drop both permissions and correct `plan.md`.

## VAR-12 · LOW · `ConfigFlow.CompleteTitle` is an orphan key

`Localization/Strings.resx:190-192` vs `JarvisConfigFlow.cs:98`

The key "JARVIS" exists but is never referenced; `Complete("JARVIS", …)` hard-codes the literal, which §9 explicitly permits. The orphan invites a well-meaning "fix" that localizes a name the host stores.

**Fix.** Delete the key, or reference it with a comment citing §9.

## VAR-13 · LOW · Variables with no config surface

See ACT-26's list of thirteen. After VAR-01's fix, decide per setting whether it gets a field or is deleted.

**Fix.** One declared table drives both.

## VAR-14 · LOW · `PluginIntegration.cs:306` returns `string.Empty`

See LIF-12.

---

# LOC — localization

## LOC-01 · HIGH · The model-check report is built from English literals

`src/Jarvis.Plugin/Actions/CheckModelsAction.cs:85,87`

`var verdict = probe.IsReachable ? "ok" : $"({probe.Failure.ToString().ToLowerInvariant()})";` and `return $"{model}: {verdict} - {probe.Detail}";` — verdict, separators and body are all literals rendered into a modal.

**Fix.** `Strings.Actions.CheckModels.Verdict.*` and `Strings.Actions.CheckModels.ReportLine(model, verdict, detail)`.

## LOC-02 · HIGH · `ConfirmAction` forces a localized string through `.ToString()`

`src/Jarvis.Plugin/Actions/ConfirmAction.cs:60-66`

`.ToString()` resolves in the plugin process culture (effectively English), so a German user sees English — and the result is written into `expectedStateId`. Same location as ACT-09.

**Fix.** `ActionResult.Success()`, and surface the sentence via `Accepted(Strings.Actions.Confirm.Approved(…))` which takes a `LocalizedText`.

## LOC-03 · HIGH · See VAR-06 (asset purpose literals) and VAR-07 (probe text)

Listed once, fixed once.

## LOC-04 · HIGH · See ACT-19 (`probe.Detail` unlocalised in UI)

## LOC-05 · MEDIUM · Undocumented `<comment>` prefix form

`Localization/Strings.resx:516-519`

`<comment>[reason][purpose][detail] reason, purpose, and technical detail.</comment>` — three separate brackets with no `:type`. This is the MDLOC004 surface. It compiles because the build emits 0 warnings, i.e. the generator silently ignores the syntax rather than validating it.

**Fix.** `[reason:string] [purpose:string] [detail:string] …`

## LOC-06 · MEDIUM · `Runtime.Issue.Detail` welds three fragments in a fixed word order

`Localization/Strings.resx:517` + `Runtime/RuntimeManager.cs:329`

`{reason} {purpose} {detail}` is untranslatable as one sentence in any language with different word order.

**Fix.** Three keys, or accept it and stop routing user-visible text through it.

## LOC-07 · MEDIUM · Asset internals in user-facing text

`src/Jarvis.Plugin/Runtime/AssetDownloader.cs:56,98,112,125,129,253`

`Detail` is set to the asset URL, `$"expected {asset.Sha256}, got {actual}"`, `exception.Message` and `"downloaded"`. All reach the issue description. See VAR-08.

**Fix.** Pass null for internal detail; log it.

## LOC-08 · MEDIUM · `CheckModels` dead `ViewId`

See ACT-07.

## LOC-09 · MEDIUM · Untranslated variable values

See VAR-04, VAR-05.

## LOC-10 · MEDIUM · `[plural]` families: none exist

`Localization/Strings.resx`, `Strings.de.resx`

Zero plural families in either file. Not a violation, but if any count-dependent sentence is added the `.One`/`.Other` pair with `Other` required is the rule.

**Fix.** None now; add a test that fails if a `[plural]` key lacks both forms.

## LOC-11 · LOW · 17 dead keys of 197

`Localization/Strings.resx` — 180 referenced (91.4%).

| Dead keys | Note |
| --- | --- |
| `Actions.DownloadComponent.*` (7 keys, `:136-153`) | `ManageComponentsAction` replaced that action |
| `Actions.ViewOutput.Name` / `.Description` (`:107-112`) | `AssistantSession.LastOutput` has no consumer action — the feature is announced and does not exist |
| `Errors.MicUnavailable` (`:414-419`) | Identical English duplicate of `Errors.MicrophoneUnavailable` (`:462-473`), which is the used one |
| `Errors.ComponentMissing` (`:429-431`) | Unreferenced, but is the natural message for the pre-install path |
| `Errors.NothingToCancel` (`:435-437`) | Unreferenced, natural for `CancelAction` |
| `Orb.Config.Rings`, `.Colours`, `.Text`, `.Reactivity`, `.Behaviour` (`:524-629`) | `OrbConfigView.cs` builds a flat list of 15 inputs and never emits a section header, so the orb editor is an unlabelled wall |

**Fix.** Delete the dead ones; wire the four that have an obvious call site.

## LOC-12 · LOW · Six German keys missing for the safety-critical control

`Localization/Strings.de.resx:79`

All six `Actions.Confirm.*` keys are absent — the file jumps from `Actions.Say.Prompt.Description` to `Actions.ViewOutput.Name`. Legal (English fallback), but the confirm button is the plugin's safety control and a German user gets it in English.

**Fix.** Add the six keys.

## LOC-13 · LOW · `Errors.ComponentMissing` has no `<comment>`

`Localization/Strings.resx:429-431`

Its five siblings all declare `[string] <name>:`. Inconsistent and one edit away from an untyped placeholder.

**Fix.** Add the comment.

## LOC-14 · LOW · `AssistantSession.cs:206` reuses a state label as an action message

`ActionResult.Accepted(Strings.States.Confirming())` — the key is "Waiting for confirmation", a button-state label. It is a key so it localises correctly, but the name lies about its use.

**Fix.** Add `Strings.Errors.ConfirmationRequired`; keep `States.Confirming` for `AssistantStateReader.cs:38`.

## LOC-15 · LOW · `ManageComponentsAction.cs:50` raw-id fallback

See ACT-30.

## LOC-16 · LOW · Missing `AssemblyName`/`RootNamespace` in the csproj

`src/Jarvis.Plugin/Jarvis.Plugin.csproj:3-7`

They default from the project file name, so the manifest coupling works by accident. See PKG-09.

## LOC-17 · LOW · `PluginIntegration.cs:306` `string.Empty`

See LIF-12.

## LOC-18 · VERIFIED CLEAN · Localization

Recorded so it is not re-audited.

- Exactly two files: `Strings.resx` (197 keys, required default) and `Strings.de.resx` (191 keys). No `zh`, no `de_DE`, no underscore separator, no invalid region. Satellite assembly produced.
- All 197 keys dotted, zero contain `_`.
- No MDLOC008 key/group collision.
- Zero duplicate keys in either file.
- Zero positional `{0}` placeholders — all 7 keys with placeholders use named ones.
- Placeholder sets identical between default and translation for every shared key. Zero MDLOC003.
- Catalog scope verified from the built assembly as exactly `plugin:com.misike12.jarvis`.
- **198 `Strings.` references, 181 distinct, 0 missing.** The one apparent miss was a regex false positive on `MacroDeckStrings.Validation.Required(…)`, which is the correct reuse of the host catalogue.
- All LLM-facing tool descriptions and tool-name identifiers are correctly English literals — they go to the model, not the UI. Full list retained in the audit; `ToolOutcome.*` text in `Llm/*Tools.cs`, `ToolDefinition.Description`, and every `public string Name => "snake_case"`.
- All exception messages correctly English per §12.
- `ConfigFlowResult.Complete("JARVIS", values)` is §9's documented plain-string exception.
- `OrbWidgetTypeProvider.ProviderName => "JARVIS"` is a proper noun matching the manifest; must not be translated.
- Windows shutdown dialog text and model-supplied toast text are not plugin-authored UI.
- `AssistantSnapshot.StatusLine` is not currently rendered anywhere — dead field. If surfaced it becomes a user-facing literal, and `effectiveMode.ToString()` would emit `WaitForWakeWord` verbatim.

---

# PKG — manifest, packaging, conformance, docs

## PKG-01 · HIGH · No `.github/` exists at all

`JARVIS/.github/` — does not exist. A recursive scan for `*.yml`/`*.yaml` across the tree returns zero files; `git ls-files` (151 tracked files) contains no workflow.

Each consequence is independently fatal to a Store submission:

- No `publish-plugin.yml` referencing `Macro-Deck-App/GitHub-Actions/.github/workflows/publish-plugin.yml@v1`, so no build is ever registered in the Creator Portal's Builds list.
- Nothing runs `macrodeck-plugin build`, so no portal-visible artifact can exist.
- Nothing runs the conformance suite as a gate. The two committed reports are hand-run with no automation.

**Fix.** Add `.github/workflows/publish-plugin.yml` keyed on `release: types: [published]`, `permissions: contents: read, id-token: write`. Add a second workflow running `macrodeck-plugin test` so conformance is enforced on every push.

## PKG-02 · HIGH · `manifest.json` declares no `ai` block

`src/Jarvis.Plugin/manifest.json:26-27`

Contract §17 requires it on the build output. It goes straight from `compatibility` to `permissions`. Its validator requirement level is `recommended`, not `publication`, so `validate --level publication` returns 0 errors — which is why the workstate log records "0 errors, 0 warnings". **The CLI validator does not enforce this Store requirement.**

Substantive for this plugin: `Llm/ChatClient.cs`, `Llm/ToolRegistry.cs` and the `nvidia` provider (`Core/JarvisSettings.cs:11`) exist, so the disclosure is real.

**Fix.**

```json
"ai": {
  "interaction": true,
  "generatedContent": true,
  "generatedAssets": false,
  "services": ["NVIDIA NIM"]
}
```

## PKG-03 · HIGH · The release was uploaded with a PAT, not built by the workflow

`current workstate.md:567,601-609`

Tags exist remotely (`v1.1.0` = `92ed3a2`), so the tag precondition is half-satisfied. Build provenance is not:

1. No Builds entry exists. "Build in Builds → Create release" cannot start.
2. No upload-and-adopt path exists for a release asset.
3. The artifact has `files[]` (26 entries) and **no `signature`** — correct per §17, but disqualifying alone since the portal verifies that signature.
4. Nothing binds the DLL to commit `92ed3a2`; a reviewer cannot reproduce the validation.
5. The PAT itself is a standing credential on the manual release path. Per §17 no signing or release credential is needed by an author at all.

**Fix.** After PKG-01, cut a fresh tag (`v1.1.1`) so the workflow produces a genuine build record. Publish from that build. Rotate the exposed PAT.

## PKG-04 · HIGH · The project conformance report validates none of the manifest

`src/conformance.md:13-16`

All four manifest checks are skipped with the same reason, "This subject has no manifest - only an artifact subject does":

- `MDC0104` manifest version and protocol range — **Required**
- `MDC0105` manifest id equals what the subject reports — **Required**
- `MDC0106` manifest name and version match — **Required**
- `MDC0107` icon extension implies the reported media type — **Required**

The header still claims `Conformant: **yes**`. The substance: `manifestVersion`, id validity, SemVer, icon media type and protocol range are **all unchecked**. `current workstate.md:353-354` frames this as a feature ("artifact conformance is stronger") which is backwards for a Store submission.

**Fix.** Treat `artifact-conformance.md` as the only manifest-bearing evidence. Investigate why the `--project` subject cannot see `manifest.json` (the csproj copies it to the output content root).

## PKG-05 · MEDIUM · No committed script regenerates either report, and the artifact command is undocumented

`src/scripts/` contains only the two service scripts; neither invokes `macrodeck-plugin test`. §16 documents only the `--project` form, so the `--artifact` invocation that produced the only manifest-bearing report exists nowhere in the repo.

On the positive side both files were last written in `92ed3a2` alongside the version bump, and `git diff 92ed3a2 HEAD` on them is empty, so they are not stale. That is manual discipline, not enforcement.

**Fix.** Add `scripts/conformance.ps1` running both forms, writing per-platform reports, exiting non-zero on any Required failure. Document the `--artifact` invocation in README. Call it from CI.

## PKG-06 · MEDIUM · `README.md` is the unmodified template and propagates a forbidden manifest value

`src/README.md:146,149-156,166-169,190,1,105,539-547`

The canonical example teaches `"compatibility": { "macroDeck": ">=3.0.0" }` — contract §3 requires `-0` for prereleases of the lower bound. The real manifest correctly says `>=3.0.0-0`, so the README is the error, but it is documented precedent for dropping it, and with the SDK on `3.0.0-beta.14` a `>=3.0.0` would make the plugin uninstallable on any preview host.

Also wrong: `version` says `1.0.0` against the manifest's `1.1.0`; three self-contained platforms with `.exe`/extensionless entrypoints against the real single `FrameworkDependent` `.dll` one; `--self-contained true` against the real `false` + `UseAppHost=false`; title still "Macro Deck plugin template"; documents `LogMessageAction.cs` which does not exist; "Further reading" links `Macro-Deck-3`, `sdk-reference.md`, `cli.md`, `testing-plugins.md` — none of which exist.

**Note:** the conformance command at `:487` is **correct** and matches §16 exactly.

**Fix.** Rewrite the manifest and build-config sections to the real values, correct the range, update the version, drop `LogMessageAction`, replace the dead links with the real paths from `macrodeck-plugin.md` §0.

## PKG-07 · MEDIUM · `current workstate.md` contradicts both committed reports

`current workstate.md:350,353` vs `src/conformance.md:6` and `src/artifact-conformance.md:6`

Lines 350/353 report 38 passed / 11 skipped and "38 against 33". The committed reports say 34/15 and 37/12. Both committed headers were recounted and are arithmetically correct (49 rows each), and line 591 matches them. The workstate document contradicts itself.

**Fix.** Correct lines 350 and 353. Treat the workstate as derived documentation regenerated from the reports.

## PKG-08 · LOW · The artifact is gitignored, so the published asset is untraceable

`.gitignore:7,9` — `artifacts/`, `src/artifacts/`. No `.macroDeckPlugin` is tracked. Gitignoring build output is defensible, but combined with the absent CI nothing in the repository can reproduce or verify the published bytes.

**Fix.** No change to the ignore rule once PKG-01 lands; until then treat the release asset as unverifiable.

## PKG-09 · LOW · `AssemblyName` / `RootNamespace` absent from the plugin csproj

`src/Jarvis.Plugin/Jarvis.Plugin.csproj:3-7`

Verified to agree today — default `AssemblyName` is `Jarvis.Plugin`, matching `manifest.json:11`, and the shipped artifact contains `runtimes/win-x64/Jarvis.Plugin.dll` with no `.exe` because `UseAppHost=false`. So **not a current breakage**, but the agreement is accidental.

**Fix.** Declare both explicitly. Same finding as LOC-16; fix once.

## PKG-10 · LOW · `.gitignore` has no `*.macroDeckPlugin` rule

`src/.gitignore:75-77` lists `*.zip`, `*.tar.gz`, `*.rar` but omits `.macroDeckPlugin`. Currently covered only incidentally by the `artifacts/` directory rule; a `--output` elsewhere would make it trackable, including its 26 generated digests.

**Fix.** Add `*.macroDeckPlugin`.

## PKG-11 · LOW · Absolute developer path committed in the installer

`src/scripts/install-service.ps1:105`

`& "C:\Users\Misu\Desktop\ideas\JARVIS\src\service\...\Jarvis.Service.exe" --diagnose` bypasses the correct `$PSScriptRoot` composition used at `:25-27`, and does not exist on any other machine. Same finding as SEC-27.

## PKG-12 · LOW · SDK pinned

`src/Directory.Packages.props:10`. Same as LIF-13; deliberate and documented, no action.

## PKG-13 · UNCERTAIN · `publisher.name` is `misu`, repo owner is `misike12`

`src/Jarvis.Plugin/manifest.json:18-20` vs `:22`

§17 ties `publisher.name` to listing ownership and the portal is expected to validate it against the submitting account, compared ignoring case. Not verifiable from the repository — the portal account name is not recorded in `plan.md` or `current workstate.md`. Invisible to `validate`, since `publisher` is publication-level and checked only for presence.

**Fix.** Confirm the Creator Portal account name and set `publisher.name` to exactly that string.

## PKG-14 · VERIFIED CLEAN · Manifest and artifact

Every §3 rule passes: `manifestVersion` exactly 1; `id` three segments, 16 chars; `name` 6 chars no control chars; `version` three-component SemVer; one non-empty `entrypoints` key; executable relative, inside the version directory, no `..`, not a script extension; `FrameworkDependent` ⇒ `.dll`; `FrameworkDependent` ⇒ `dotnetVersion` `major.minor`; `compatibility` declares `macroDeck`; range grammar `>=3.0.0-0` correct; no authored `files[]`, `signature` or `languages`; all six permissions from the documented vocabulary.

Artifact layout verified by unzip: 27 entries, `runtimes/win-x64/Jarvis.Plugin.dll` present with `.deps.json`, `.runtimeconfig.json`, the Macro Deck assemblies and `de/Jarvis.Plugin.resources.dll`; packed manifest carries derived `"languages": ["de", "en"]` and a 26-entry `files[]`; id and version agree across manifest, both reports, artifact manifest and filename. `macrodeck-build.json` has one target per declared RID.

## PKG-15 · MEDIUM · Conformance coverage gaps

From the skip analysis.

- **`MDC0502` and `MDC0504` are skipped** — "no declared action ran long enough, under a 300 ms deadline" and "stayed in flight long enough to be cancelled". Both Recommended. **For a voice assistant whose actions are inherently long-running, this leaves deadline enforcement and mid-flight cancellation untested** — the exact behaviour §5 and §6 care most about, and the exact area where LIF-01 and ACT-01 are broken. Treat as a real gap.
- **`MDC0801` fails only in the artifact build** — "no declared action produced any observable log output". The project report passes this. A meaningful behavioural difference between the two subjects.
- `MDC0103`, `MDC0402` (no weather), `MDC0311` (no variable catalog), `MDC0314` (no writable variable), `MDC0204`, `MDC0206` (suite structural limit for out-of-process subjects) are legitimate capability gaps.
- `MDC0312`, `MDC0313` skip with "no declared action reports ProvidesIcon" although the manifest declares an icon. Consistent, worth confirming as intentional.

**Fix.** Add a long-running action to the test subject, or a fixture, so the timeout and cancellation checks have something to exercise.

---

# Execution order

## Wave 1 — data loss and the unenforced cap

1. **VAR-01** config flow credential wipe. One-line fix, verified by a test that completes the flow and asserts credentials survive.
2. **LIF-01 + ACT-02** unlinked cancellation token, and cancel-before-dispose. The 30 s cap is currently fiction.
3. **LIF-03 + ACT-09** two dead buttons and the `expectedStateId` misuse.

## Wave 2 — truthfulness

4. **ACT-05, ACT-06, ACT-08, ACT-16, LIF-02** every `ActionResult` that lies.
5. **ACT-01** the timeout budget, end to end.
6. **ACT-04, ACT-10, ACT-13, ACT-15, ACT-14, ACT-11, ACT-12** the remaining unbounded waits and dropped tokens.

## Wave 3 — service security

7. **SEC-01** Program Files install with a verified ACL.
8. **SEC-02** invert the pipe direction. Largest single change; needs its own tests.
9. **SEC-03, SEC-04** registry allowlist with normalisation.
10. **SEC-05, SEC-06, SEC-07, SEC-08** framing, readiness, idle timeout, listener lifecycle.
11. **SEC-09 through SEC-16** the remaining service defects.
12. **SEC-17, SEC-18, ACT-19** wire the dead controls or delete them.

## Wave 4 — lifecycle and capability correctness

13. **LIF-04, LIF-05, LIF-06, LIF-07, ACT-23, ACT-24** initialisation idempotence and catalogues.
14. **LIF-14** feed the wake word.
15. **ACT-25** the orb session token freeze.

## Wave 5 — localisation and settings structure

16. **ACT-26 + VAR-01 follow-through** one declared field table.
17. **LOC-01 through LOC-09** user-facing literals and placeholders.
18. **LIF-08, LIF-09** delete the plaintext credential paths.
19. **LIF-10, VAR-02, ACT-21, ACT-22** surface the standing conditions.

## Wave 6 — docs, packaging, publishing

20. **PKG-02** `ai` block. **PKG-09/LOC-16** csproj properties. **PKG-10** gitignore.
21. **PKG-06** README rewrite. **PKG-07** workstate correction. **PKG-11/SEC-27** installer path.
22. **PKG-01** CI workflow. **PKG-05** conformance script. **PKG-15** coverage gap.
23. **PKG-13** resolve `publisher.name`. **PKG-03** new tag, rotate the PAT.

## Wave 7 — low and uncertain

24. Everything marked LOW, plus LIF-11, ACT-28, ACT-29, ACT-30, ACT-31, LOC-11 through LOC-17.

---

# Recorded as correct — do not re-audit

- **Builder chain** `Program.cs:7-12` is exactly `CreatePlugin` → `UseMacroDeckLogging` → `UseLocalization` → `RegisterIntegration` → `Build` → `RunAsync`, with `.AddJarvis()` correctly placed before `Build`. No `AddSingleton<PluginIntegration>`, no `AddMacroDeckIntegration`, no stray `TryAddSingleton` anywhere.
- **Identity** — `PluginIntegration` declares no `Id`/`Name`/`Version`/`Icon`/`IsInitialized` and does not implement `IIntegrationIconProvider`. MDP1004 clean.
- **No listener override** — no `UseUrls`, no `Configuration["urls"]`, no `ASPNETCORE_URLS`, and no `appsettings.json` exists. `launchSettings.json` sets only legal variables.
- **No reserved routes** — zero `MapGet`/`MapPost`/`UseRouting` in the tree. MDP2005 and MDC0703 clean.
- **csproj shape** — `Microsoft.NET.Sdk` not `.Web`, `OutputType Exe`, `FrameworkReference Microsoft.AspNetCore.App`, both `Content` items with `PreserveNewest`, `PrivateAssets="all"` on the analyzers, `net10.0` from `Directory.Build.props`.
- **Action ids** — all seven kebab-case, none containing `::`, unique plugin-wide.
- **MDP3001/3002/3003/4001** — no blocking calls in contract types, no `async void`, no singleton depending on `ICapabilityInvocationContext`. `PluginIntegration.cs:119`'s `CancellationToken.None` is correct: `IIntegrationContext` carries none.
- **`ActionExecutionContext` null handling** — `Ui` null-checked, `OriginClientId` read only inside the guard. `Interactions`, `OwnerWidgetId`, `CallDepth` never referenced.
- **Action ids and parameter names** — kebab-case, consistent. `OnlyWhen` used nowhere, so no hidden parameters. The overlapping `mode`/`wait-for-wake-word` pair is always both sent and the combination honoured, which is exactly what §5 requires.
- **`ActionErrorCodes`** — all 13 `Failed` call sites use the nine constants. No invented error strings.
- **Localized `Name`/`Description`** — every action uses a `Strings` key. No plain-string `Name`/`Description`, no nulls.
- **Eager variable count** — 6 of 256; `declaredVariables` not implemented. MDC0315 passes.
- **No `Write` declared**, so MDC0314 cannot fail. No catalog, so MDC0311 has no subject. No user-variable API use.
- **No file writes next to the executable** — every write resolves `MACRO_DECK_PLUGIN_DATA_DIRECTORY` first (`RuntimePaths.cs:28-32`, `MemoryStore.cs:66-67`, `VoiceService.cs:200-211`) with a temp fallback. `RuntimePaths.Guard` rejects `..`, `/` and `\` in names.
- **Secrets in the flow** — the three credentials are declared with `ActionParameter.Secret` and written with `ConfigFlowValue.Secret`. No secret reaches a log line. `jarvis.settings.json` is gitignored in both ignore files. The defect is the *separate* plaintext file path, handled as LIF-08.
- **Issue provider wiring** — `RuntimeManager` is a DI singleton so the forwarded `GetIssuesAsync` sees the same table the actions write. `GetIssuesAsync` is lock-guarded with stable ids through one `IssueIdFor` helper, which is the correct pattern. `RetryAsync` forwards the caller's token and reports a real failure rather than a false success, and a successful retry clears the entry.
- **`RegisterIntegration<T>()`** used in both `Program.cs:10` and the test harness — the only two registrations in the tree.
- **Service ACL mechanism** — `SetAccessRuleProtection(true, false)` is correct and the descriptor is passed as the final `pipeSecurity` argument to `NamedPipeServerStreamAcl.Create`. No `Everyone`/`Users`/`Anonymous` rule. The defect is the allow-list contents (SEC-02) and the fail-open fallback (SEC-22), not the mechanism.
- **No scheduled-task operation exists in the service** — the six names are unimplemented and refused by the switch default, so no SYSTEM task creation via the pipe. Confirmed by `ElevatedOperationTests.An_operation_this_build_lacks_is_named_in_the_answer`.
- **No command injection** into task definitions — `ExecAction.Path`/`Arguments` are set as properties, no shell involved.
- **No path traversal in the service** — it performs no file operations except its own log; registry paths reject `..` and control characters.
- **Version checked on both sides** of the pipe — `Protocol.cs:94` and `ServiceProtocol.cs:87`.
- **An unparseable message does not kill the service** — `Handle` returns a refusal and the loop continues. (The type-mismatch case does kill the connection; SEC-11.)
- **`WhisperTranscriber.cs:203-204`** — verified NOT a dropped token: both `ReadToEndAsync` tasks were started with the token at `:187-188`.
- **Installer quoting** — `Join-Path` composition plus `ArgumentList`-based `ProcessStartInfo` avoid quoting bugs; `"exe" --console` is the form SCM expects. Elevation is checked first with a plain-language message and exit 5.
- **Plugin-side client timeout** — 15 s default, correctly linked to the caller's token so `OperationCanceledException` distinguishes deadline from user cancellation. Missing service → "The elevated service did not answer.", handled and tested.
- **`.editorconfig` does not exist** in the repository. `AGENTS.md`'s claim that analysis settings live in `Directory.Build.props` is accurate.
- **No `AddScoped`** anywhere; all state is a deliberate singleton, each internally locked.

## Known limitations of this audit

- `IPluginCatalogNotifier`'s absence is currently latent (LIF-04) because no catalogue is derived from configuration yet. It becomes a live bug the moment one is.
- ACT-28's sync-over-async is real but unreachable today; it becomes reachable on the next config-flow edit.
- SEC-04's `StartsWith` bypass was not confirmed against a live hive. Treated as plausible and covered by SEC-03's normalisation regardless.
- PKG-13 cannot be resolved from the repository; it needs the Creator Portal account name.
- PKG-04's root cause is uncertain — the suite's wording implies a `--project` subject is not expected to expose a manifest, yet the csproj copies one to the output. The observable fact stands regardless.
- No finding here was validated at runtime as LocalSystem or over a real socket. `MDC0502`/`MDC0504` being skipped confirms deadline and cancellation behaviour is untested, which is exactly where the worst findings are.