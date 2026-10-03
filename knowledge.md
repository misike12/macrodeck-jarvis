# Macro Deck 3 Plugin Development — Knowledge Base

Everything learned from reading the Macro Deck 3 source, docs, ADRs and CI. Written for building
JARVIS, but the platform facts apply to any plugin.

**Where the detail lives now.** This file is the distilled, corrected summary. The full platform
documentation with verbatim code samples is `docs-research.md` (62 pages), and the complete public API
surface of the SDK, every constant value and member enumerated by reflection, is `sdk-surface.md`
(1007 types). Read those instead of guessing; this file records what is worth knowing up front.

Source of truth: `https://github.com/Macro-Deck-App/Macro-Deck` (Apache-2.0)
Docs: `https://docs.macro-deck.app/`

---

## 1. Environment

| Item | Value |
|---|---|
| .NET SDK (repo pins) | `10.0.401`, `rollForward: latestFeature` |
| Target framework | `net10.0` everywhere |
| Analyzers only | `netstandard2.0`, `LangVersion=latest` pinned |
| CLI | `dotnet tool install --global MacroDeck.Plugin.Cli --prerelease` |
| Verified working CLI | `3.0.0-beta.14` |
| ASP.NET Core runtime | **required** — `run` and `test` start a real Kestrel loopback host |

**SemVer gotcha — this bit us, hard.** The template ships
`<MacroDeckSdkVersion>3.0.0-*</MacroDeckSdkVersion>` with floating versions enabled.
NuGet resolves that to **`3.0.0-preview.10`**, not the `3.0.0-beta.14` the CLI is on, because `preview`
sorts below `beta`. The result is code compiling against a much older SDK than the tool that builds it:
`WidgetTypeDescriptor.SupportsFlows`, `.AppearanceProperties`, `UiModifier`, `UiFirstFit` and
`UiVideoStream` simply do not exist, and the errors read as *"does not contain a definition"* rather than
as a version problem.

**Pin the SDK version explicitly** and verify what actually resolved:

```powershell
dotnet build   # then:
(Get-Content src\X\obj\project.assets.json | ConvertFrom-Json).libraries.PSObject.Properties.Name |
  Where-Object { $_ -match 'MacroDeck' }
```

Always check that list before believing a "missing member" error. The **cloned repo HEAD is newer than
the published packages**, so reading repo source to learn an API can mislead: verify against the resolved
package or the code will not compile.

---

## 2. Project shape

```
MyPlugin/
├── MyPlugin.slnx
├── Directory.Build.props        # net10.0, Nullable, TreatWarningsAsErrors, tabs
├── Directory.Packages.props     # CentralPackageFloatingVersionsEnabled, MacroDeckSdkVersion
├── NuGet.config
├── src/MyPlugin/
│   ├── manifest.json            # THE identity source
│   ├── macrodeck-build.json     # one publish target per RID
│   ├── MyPlugin.csproj
│   ├── Program.cs
│   ├── PluginIntegration.cs
│   ├── Localization/Strings.resx
│   ├── Assets/icon.svg
│   └── Properties/launchSettings.json
└── tests/MyPlugin.Tests/
```

**csproj:** `Microsoft.NET.Sdk` + `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
**Not** `Microsoft.NET.Sdk.Web`.

`manifest.json` and the icon must be `<Content ... CopyToOutputDirectory="PreserveNewest" />`
because the SDK reads both from the content root at startup.

---

## 3. Program.cs — the shape

```csharp
var builder = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PluginIntegration>();

builder.Services.AddSingleton<MyThing>();   // plain MS DI, before Build()

var plugin = builder.Build();                // throws with EVERY problem at once
await plugin.RunAsync();
```

- `RegisterIntegration<T>()` is **the one door**. It registers the integration *and* a capability
  handler for every SDK interface the type implements. Never write a handler for a built-in kind.
- `builder.WebApplicationBuilder`, `Services`, `Configuration`, `Logging`, `Environment` all reachable.
- `Build()` order: read manifest → validate → DI author callbacks → `MessagingCapabilityHandler` last
  → `PluginConnectionHostedService` last → build app → map routes → validate capabilities.
- `RegisterIntegration<T>(Func<IServiceProvider,T>)` overload exists.
- **`Func<T>` cannot be resolved by DI.** If a service needs a delegate, register a factory.

---

## 4. Identity rules (hard constraints)

- `manifest.json` is the **only** identity source. `IPluginIntegration` has no `Id`/`Name`/`Version`/
  `IsInitialized` and never implements `IIntegrationIconProvider`. Restating them = **MDP1004**.
- Package id regex: `^[a-z][a-z0-9]*(-[a-z0-9]+)*(\.[a-z][a-z0-9]*(-[a-z0-9]+)*)+$`
- Local ids: `^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$`, max 64, **never** contain `::`.
- **Action ids must be unique across the whole plugin process**, not per integration.
- Event ids and variable `DefinitionId`s are persisted in user data. Frozen forever.
- `manifestVersion` is always `1`. Never put identity in `macrodeck-build.json`.

### manifest.json fields

`manifestVersion`, `id`, `name`, `version`, `entrypoints` = schema `required`.
Then publication metadata: `description`, `icon`, `publisher`, `license`, `repository`, `compatibility`.
Then recommended: `homepage`, `additionalLinks`, `shutdown`, `health`, `permissions`, `languages`, `ai`,
`dependencies`, `conflicts`, `iconPacks`, `bundledIconPacks`.
Then generated: `files`, `signature` — **never hand-maintain either.**

- Prefer `runtime: { kind: "FrameworkDependent", dotnetVersion: "10.0" }`.
  ~0.7 MB compressed per platform vs ~43 MB self-contained.
- Framework-dependent ⇒ entrypoint must be `.dll`. Self-contained ⇒ must not be `.dll`.
- Entrypoint `executable` must be relative, inside the version directory, and **never a script**
  (`.sh .bat .cmd .ps1 .command` rejected).
- `compatibility.macroDeck: ">=3.0.0-0"` — the `-0` admits 3.0.0 prereleases.
- Health defaults: interval 15 s (5–120), timeout 2 s (1–10), unhealthy threshold 3 (2–10).
  Values are **clamped, never rejected**.
- Shutdown graceful timeout default 10 s (1–60).
- RID resolution: exact → `osx-arm64`→`osx-x64`, `win-arm64`→`win-x64` → nothing.
  No `"any"` key.

### macrodeck-build.json

```json
{ "version": 1, "targets": { "win-x64": {
  "executable": "dotnet",
  "arguments": ["publish","MyPlugin.csproj","-c","Release","-r","win-x64",
                "--self-contained","false","-p:UseAppHost=false","-o","bin/publish/win-x64"],
  "output": "bin/publish/win-x64" } } }
```

Structured executable + arguments, never an opaque shell string.

---

## 5. Capability interfaces (19 kinds)

`actions, events, variables, icons, config-flow, music-player, weather, virtual-profiles, issues, ui,
localization, device-provider, layout-provider, folder-view-provider, migration, widget-type-provider,
screensaver-provider, messaging, video-stream-provider`

Opt in by **implementing the interface on the integration class**. Nothing else to register.

### Actions
```csharp
public interface IActionDefinition {
	string Id { get; }  LocalizedText Name { get; }  LocalizedText Description { get; }
	IReadOnlyList<ActionParameter> Parameters { get; }
	MacroDeckPlatform Platforms => MacroDeckPlatform.All;
	IActionExecutor CreateExecutor();
}
public interface IActionExecutor { Task<ActionResult> ExecuteAsync(ActionExecutionContext ctx); }
```
Optional mix-ins: `IStateProviderActionDefinition` (2 s poll),
`IIconProviderActionDefinition` (5 s poll),
`IDynamicOptionsActionDefinition`, `IUiConfigurableActionDefinition`,
`IConfigurableActionDefinition`.

### ActionResult
```csharp
ActionResult.Success() / Success(expectedStateId) / SucceededTask
ActionResult.Accepted(message[, expectedStateId])
ActionResult.Failed(code, LocalizedText message)
```
`ActionErrorCodes`: `NotConfigured, NotConnected, PermissionDenied, ProviderError, ProviderRejected,
InvalidParameter, NotFound, Timeout, Unavailable`

**Be truthful.** A press that did nothing must not report success. A legitimate no-op *is* success.
`Accepted` only for work taken but unconfirmable.

### ActionParameter — 27 types + OnlyWhen
`String, Number, Boolean, Password, Secret, Choice, DynamicChoice, Autocomplete, MultiSelect, Color,
File, Folder, Hotkey, Duration, DateTime, Json, Code, KeyValue, Object, Array, IpAddress, Url, Icon,
Image, KeyboardSequence, KeyboardCombo, WidgetTarget`

Factories: `.Text .MultilineText .Number .Slider .Toggle .Password .Secret .Choice .DynamicChoice
.Autocomplete .MultiSelect .Color .File .Folder .Hotkey .Duration .DateTime .Json .Code .KeyValue
.Object .Array .IpAddress .Url .Icon .Image .KeyboardSequence .KeyboardCombo .WidgetTarget`

- `.OnlyWhen(paramName, params string[] values)` returns a **copy** (MemberwiseClone).
- OnlyWhen is **presentation only**. The host still sends hidden parameters. Validate in the executor.
- Unknown `ParameterName` in OnlyWhen ⇒ always visible (a typo can't make a field unreachable).
- `ActionExecutionContext.Parameters` is `IReadOnlyDictionary<string, object>` — note **non-nullable**
  values, unlike everything else.

### Variables
```csharp
VariableDefinition.Eager(name, type, decimalPlaces, refreshInterval) with { Id = "local-id", Unit = "°C" }
VariableDefinition.OnDemand(id, type)
```
- `Id` is **required** for OnDemand. `Name` matches `^[a-z][a-z0-9_]*$`.
- `Materialization` is **enforced** by the host against the surface.
- Readable: `ReadAsync`. Writable: add `Write = new VariableWriteCapability()` and implement
  `SetValueAsync` returning `Applied/NotWritable/NotFound/Unavailable/InvalidValue/Failed`.
- Push: `SupportsCatalog && SupportsPush` → host calls `OnAttachedAsync(sink)` then `SubscribeAsync(ids)`.
  Out-of-set publishes are **dropped silently**.
- `VariableReading.Of(value)` / `Of(value, min, max, step)` / `Unavailable`.
  `Unavailable` carries **no bounds** — an outage must not collapse a slider.
- `DeclaredVariables` must be **side-effect free**. It is read before `InitializeAsync` finishes.
- Max 256 eager variables per provider.

### Events
```csharp
new EventDefinition {
	Id = "...", Name = ..., Description = ..., Category = ..., IconName = ...,
	ConfigurationParameters = [...],   // user-authored, filter triggers
	PayloadParameters = [...],          // read-only occurrence payload
}
```
Host namespaces every id as `integrationId::eventId`. Your payload **cannot forge ownership**.
`IEventPublisher.Publish(eventId, dict)` is fire-and-forget and never throws.

### Config flow
```csharp
ConfigFlowResult.Step(step) / Error(step, message, fieldErrors) / External(url, resumeStepId) / Complete(title, values)
```
- **`Complete(title, …)` takes a plain `string`** — the documented exception. It becomes the config
  entry's name. Write it in the default language.
- `ConfigFlowValue.Secret(v)` / `.Plain(v)` → secrets land in the host's encrypted store.
- Secrets: `SetSecretAsync` to rotate.
- OAuth: return `External(url, resumeStepId)`. **Never run your own redirect server.**
- `IConfigFlowContext.OAuth` gives `RedirectUri`, `State`, `AuthorizationCode`.
- `AdvancedFields` must not contain `Required` parameters.
- **An integration that provides a config flow starts disabled until the flow is completed.**

### Variables exposed to the host
`IIntegrationContext`: `Variables, UserVariables, Config, Deck, Scripts, Widgets, Events,
Notifications, Messages, UiResources`

- Fire-and-forget (never throw, safe with no session): `Events.Publish`, `Notifications.Notify/Dismiss`
- Network round trips (can throw `HostInvocationException`): `Variables`, `Config`, `UserVariables`,
  `Deck` mutators, `Scripts.RunAsync`, `Widgets.ApplyAsync`
- Cached reads (empty before first push): `Deck.GetFolders/GetProfiles`, `Scripts.GetScripts`,
  `Widgets.GetWidgets`

### Catalogs go stale — call `IPluginCatalogNotifier`
Inject it and call `CatalogChanged(CapabilityKinds.X, reason)` when something outside a host-initiated
invocation changes what a later `describe` would answer. The host describes capabilities
**concurrently with** `InitializeAsync`, so a first describe can capture a default before your config
read finishes.

---

## 6. Protocol

- Single **integer** major. `Current = 3`, `Minimum = 1`, `Supported = [1,2,3]`.
- WS subprotocol `macrodeck.plugin.v1` names the **family**, not the major. Not a bug.
- Paths are unversioned. Version negotiated once, in `POST /api/plugins/sessions`.
- Envelope: `type, id, correlationId, sentAt, protocolVersion, deadlineMs, idempotencyKey, payload | error`
  — **`payload` and `error` are mutually exclusive.**
- **28** message types (the docs say "twenty-eight"; an earlier note here said 30, which is wrong). Reply types that require `correlationId`:
  `capability.result`, `capability.declare.ack`, `asset.ack`, `host.result`, `host.asset.ack`.
- Unknown message type → `UNKNOWN_MESSAGE_TYPE` **with the envelope preserved**, socket stays open.
- Malformed → `MALFORMED_ENVELOPE`, socket stays open.
- Only **7** conditions may close the socket:
  `1013` queue overflow, `4000` session replaced, `4001` version unsupported,
  `4002` session expired, `4003` auth failed, `4004` supervisor shutdown (resumable),
  `4005` registration rejected (**terminal**).
- Delivery at-most-once, no replay. `idempotencyKey` (≤128 chars) is the retry story.
- Resume window 60 s. Resumes keep session id, version, capability map, declared catalogue,
  idempotency cache. Drops in-flight invocations, subscriptions, queued outbound.
- `host.asset.*` is a **separate** pipeline from `asset.*` so plugin→host keeps its v1 direction.

### REST
```
GET    /api/plugins/protocol                     descriptor
POST   /api/plugins/registration                 X-MacroDeck-Enrollment-Token
POST   /api/plugins/pairing                      unauthenticated, PKCE S256
GET    /api/plugins/pairing/{requestId}
POST   /api/plugins/pairing/{requestId}/redemption
POST   /api/plugins/sessions                     X-MacroDeck-Plugin-Id + -Secret
DELETE /api/plugins/sessions/{sessionId}
WS     /plugins/ws
```

### Auth
- Launch bootstrap token (managed, 2 min unused lifetime) or per-plugin secret (self-registering,
  no expiry). Plugin secret ≥43 chars. Session token = 15 min JWT, scope `plugin`.
- Secret goes in the session-exchange header **and nowhere else**. Never a cookie, never a query string.
- Redemption: secret minted at **redemption**, not approval. Wrong verifier does not consume.
  **All five redemption failures answer identically `401`.**
- Unknown pairing id → `200 expired`, never `404` (no existence oracle).

### Limits — read at runtime, never hardcode
```
MaxMessageBytes                262144
MaxInboundQueueDepth           256      watermarks 192 / 64
MaxConcurrentInvocations         32
MaxDeclaredCapabilities         512
MaxSessionsPerPlugin              1
MaxUiTreeBytes                196608
MaxUiPatchBytes                65536
MaxUiNodesPerTree               2000
MaxUiTreeBytes                196608
MaxUiUpdatesPerSecond            30      burst 90
MaxUiResourceBytes             2 MiB    per plugin 16 MiB / 256 resources
MaxUiSessionsPerProvider           8      attachments per session 16
MaxLogEventsPerSecond             20      burst 500
```
`MaxUiWidgetSessionsPerProvider` also exists but only as a nullable int property, not a constant. An
earlier note here gave it the value 128; the reflection dump has no such constant, so **128 is wrong and
the value is unverified**. Do not design against it.

Timeouts: handshake 10 s, request/capability 30 s, asset 60 s, keepalive 20/60 s,
resume window 60 s, graceful close 5 s.

**UI patch budget.** The limits are `maxUiUpdatesPerSecond` 30 and `maxUiUpdateBurst` 90, "bounded per
session". An earlier note here added a token-bucket mechanic, including "one token per patch regardless of
how many operations it carries"; **the docs do not state that**, so treat it as unverified. What matters
either way: exceeding it gives `Resync(RATE_LIMITED)` then `Terminate(RATE_LIMITED)`, so design to
**≤25/s**. The orb's 25 Hz sweep is deliberately under the 30/s ceiling.

---

## 7. The UI framework — what actually works

Three packages: `MacroDeck.Ui.Model` (wire contracts, zero deps) ← `MacroDeck.Ui` (C# DSL + reactive
runtime) ← `MacroDeck.Ui.Testing` (headless renderer + test host).

`UiModelVersions`: `Minimum = 3`, `Current = 4`.

### Widget profile: 21 closed component types
`ui.stack ui.text ui.image ui.range-bar ui.slider ui.button ui.layer ui.chart ui.text-field ui.list
ui.transform ui.shape ui.icon ui.grid ui.gauge ui.toggle ui.segmented ui.dial ui.modifier
ui.responsive ui.first-fit`
plus `macrodeck.*`: `dynamic-text clock-dial progress-bar progress-text video-stream`

**The registry is closed and built by the host** (`component-registry.ts`). No protocol message can
register a definition. **A plugin can never add a component or a `tickPeriodMs`.**

### ⚠️ There is NO animation primitive
- No `ui.animate`, no keyframes, no timeline, no tween.
- **No CSS injection.** Every `setStyle`/`setClass`/`setClassName`/`setAttribute` call across 70+ sites
  uses a **hardcoded literal** name. The only dynamic one is `icon-${name}`, validated against
  `UI_ICON_VERSIONS`. Gradients go through `gradientCss`, which takes structured objects and validated
  `#rrggbb`, never a raw CSS string.
- `@keyframes` in `ui/runtime/styles/animations.css` are Angular-app chrome, unreachable from a tree.
- **SVG is rejected from plugins**: `UiResourceRules.cs` allows only
  `image/png`, `image/jpeg`, `image/webp`, `image/gif`. MacroDeck's own animated weather icons ARE
  animated SVGs — a plugin cannot do that.

### The three mechanisms that DO work
| Mechanism | Smoothness | Patch traffic |
|---|---|---|
| **animated GIF/WebP via `ui.image` + `UiResource`** | native 60 fps | **zero** |
| `ui.transform` + `rotation` property patches | ~25 Hz ceiling | 1 patch/frame |
| `borderStyle`: `heartbeat breathing blink comet ants hue-shift rgb` | native CSS | zero |

- Animated WebP beats GIF for size at the same frame rate. `ui-image.component.ts` just sets `src`;
  the browser decodes it. No ImageSharp pass on the UI-resource path (unlike the icon path).
- `rotation`/`zoom`/`offsetX`/`offsetY`/`originX`/`originY` are ordinary patchable scalars. The
  framework's own documented idiom is a gauge needle driven by one `set-properties` carrying
  `rotation` alone.
- Border animations are phase-locked to a shared clock across clients and deliberately keep running
  under `prefers-reduced-motion`.
- **`ui.image` is the only way to get genuinely smooth, free animation from a plugin.**

### Animated assets — the GIF LZW trap


A plugin cannot ship animated SVG, so an animated orb has to be GIF or animated WebP. Encoding GIF by
hand means writing LZW, and the code-size/table-growth bookkeeping is the single easiest thing in the
format to get subtly wrong. The failure mode is nasty: the header, the palette, the sub-blocks and the
frame count all look correct, so a structural check passes, and the animation renders as noise in a
browser.

**What is actually required, and what is not:**
- The header must be little-endian width/height, then a **3-byte** packed/background/aspect triple
  (reading 4 eats the first palette entry).
- The image descriptor is `0x2C`, left(2), top(2), width(2), height(2), packed(1).
- Extensions (`0x21`) and compressed data are both **sub-block chains**: length-prefixed runs of at
  most 255 bytes, ended by a zero byte that must itself be consumed.
- **Literal-mode LZW is valid**: emit a clear code, then every pixel as its own 9-bit root code, never
  growing the dictionary. It costs about 1.125x raw size and is trivially correct. A 96px orb at 24
  frames is about 250 KB against a 2 MiB per-resource and 16 MiB per-plugin limit, so the trade is
  clearly worth it.

If run-encoding LZW is ever wanted, the encoder and decoder must agree on *when* the code size grows,
and the asymmetry is that the encoder adds a dictionary entry on the same step it emits a code while
the decoder cannot add one on the first code after a clear.

Quantising to a hand-built palette beats a generic 256-colour cube when the content is known: the orb
only ever emits dark cyan-to-violet, so a neutral ramp plus a narrow hue sweep spends every entry well.
Memoise the nearest-colour lookup on the packed RGB triple; a radial gradient has few dozen distinct
colours, so the search becomes per-colour rather than per-pixel.
### Sizing — all lengths are fractions, never pixels
```csharp
UiLength.Cell = 120d   // reference coordinate space, not a measurement
UiSize.FromBasis(f)  UiSize.Capped(f, extent)  UiSize.FromBasis(f, maxOfCross)
Fill / MainSize       // row sharing
```
`resolveLength` = `min(Basis*basis, MaxOfCross*crossExtent, MaxOfCell*cell)`.
A widget draws at whatever size its grid cell happens to be, on clients whose densities differ.

### Theming — no theme object, it's a convention
`role` resolves against the reader's theme; literal `color`/`background`/`startColor`/`endColor`/
`levelColor`/`borderColor`/`tint` do not. Reader owns defaults for absent values.
`color` is `#rrggbb` only, and only for user-chosen data colours.

### Events (18)
`change adjust press long-press press-start press-end reveal double-press drag drag-end swipe pinch
pinch-end pointer-down pointer-move pointer-up tap`
- Events must be **declared** on the node. Undeclared ⇒ `Ignored`, never an error.
- Pointer family is **exclusive ownership**: a node declaring any of it owns every pointer starting on
  it, innermost wins.
- `change` vs `adjust`: `change` is terminal, `adjust` is intermediate.

### Patches — 5 frozen op names (strings, NOT an enum)
`set-properties  insert-node  remove-node  move-node  replace-node`
- Revision advances by **exactly one** per patch; an operation-less patch is inapplicable and must
  never be emitted.
- `move-node`'s `Index` is post-removal. Out-of-range rejects, never clamps.
- Property key in both `Properties` and `RemovedProperties` ⇒ reject.
- Whole patch is atomic. Client applies with `applyUiPatch`, which returns `null` on any failure and
  then re-attaches for a fresh snapshot.

### References (zero-patch animation)
`{"$time":{"zone":…}}` and `{"$progress":{"positionMs":…,"anchor":…,"durationMs":…,"rate":…}}`.
Resolve against the reader's own clock ⇒ a ticking clock costs **zero patches** and keeps advancing
while the socket is down. Absent `rate` = 1; explicit `0` = halted.
`UiVideoStreamReference` = `plugin.id::provider-id`.

### Id derivation
Dot-joined key path from root. `UiWhen`/`UiFragment` are transparent. `UiRepeat` is transparent but
each item's `KeySelector` key participates. A top-level input's id is its **bare key**; inside
`UiInputContainer<T>` it becomes `containerId.key`.
Grammar `\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z` — **`:` is excluded so an id can never contain the
reserved `::`**. Duplicates throw naming both declaration paths.

### Config profile — ~60 primitives
Inputs: `string number boolean choice password secret dynamic-choice autocomplete multiselect color
file folder hotkey duration datetime json code keyvalue object array ipaddress url icon image
keyboard-sequence keyboard-combo widget-target actions-list-editor action-picker variable-picker
device-picker integration-picker icon-display state-mapping-editor`
Chrome: `flow step stack tabs tab heading prose instructions instruction copy-value link
advanced-section divider banner validation-message busy button widget-configuration widget-properties
widget-editor status menu dialog`

### Config surfaces
- `entryPoint`: `integration-config | action-config | folder-view-config | widget-config | screensaver-config`
- `MaskedSecretValue = "$masked"` — a tree can render "configured" without the plugin ever seeing it.
- `Transient` inputs render and raise events but the client's draft composer **skips them**, so no key
  is ever written.
- `visibleWhen` **emits the node** and lets the renderer hide it (unlike `UiWhen`, which emits nothing).
- Closed vocabularies never contain a value spelling "off". Absence already spells it.

### UiSession
```csharp
UiTree BuildTree();  IReadOnlyList<UiPatch> DrainPatches();  void Dispatch(UiEvent e);
event EventHandler? Changed;  event EventHandler<UiSessionFaultedEventArgs>? Faulted;
```
`UiState<T>` is mutable reactive state; reading `.Value` in a view records a dependency.
`view.Batch()` batches. `view.DrainPatches()` gives patches.
`UiViewException` on duplicate ids / depth > 32.

**Declining is not faulting.** `UiEventOutcome.Rejected(reason)` ⇒ dispatch rejected with your reason,
`HandlerFaulted` NOT raised. A handler that throws ⇒ rejected **and** reported.

### DSL shapes reflection found that the docs do not state
Full detail in `sdk-surface.md`. These three cost real time:

- **`UiBinding<T>` has no usable public surface.** Its ctor is `private`; `Value` and `CanWrite` are
  `internal`. `new UiBinding<T>(value, set)` **cannot compile**. Use `Bind.To(UiState<T>)`,
  `Bind.ReadOnly(UiValue<T>)` or `Bind.Custom(Func<T> get, Action<T> set)`.
- **`UiValue<T>` and `UiText` are structs with unusable ctors** (`internal` and `private`). Use the
  factories: `UiValue.Of<T>(constant)`, `UiValue.From<T>(Func<T>)`, `UiValue.None<T>()`,
  `UiValue.Optional<T>(...)`, and `UiText.Of(string)`, `UiText.Of(LocalizedString)`, `UiText.From(...)`,
  `UiText.FromLocalized(...)`, `UiText.None()`, `UiText.Optional(...)`. `UiText` has **four** implicit
  conversions: `string`, `LocalizedString`, `LocalizedText`, `UiValue<string>`. `UiValue` without a type
  argument is a *static class* holding those factories, a different type from the `UiValue<T>` struct.
- **`UiValue<T>.IsDeclared` and `UiText.Value` are `internal`.** You cannot ask whether a value was
  supplied. `UiText.IsDeclared` is public; that is the only readable one.

**Every `[Obsolete]` in beta.14 is compiler-generated noise** on ~200 record copy-constructors, message
`"Constructors of types with required members are not supported in this version of your compiler."`
There are **zero** genuinely deprecated public members. Do not chase them.

Only **3 enums** exist in the whole UI stack; everything enum-like is a static class of string constants.
`UiWidgetAppearanceFields` is an `int` bitfield (`All = 63`).

### Widget configuration surface
`config` surface with `entryPoint: widget-config`. Check **`WidgetType`** as well as the entry point: a
widget has no declared field list to fall back to, so declining leaves the user with JSON mode only.

```
new UiWidgetConfiguration {
  Key = "root",
  Properties = new UiWidgetProperties { Key = "properties", Children = [ /* inputs */ ] },
  Editor      = new UiWidgetEditor      { Key = "editor",      Children = [ /* roomy */ ] },  // optional
}
```

- **Macro Deck supplies the widget preview, the split layout, the narrow-window drawer, JSON mode,
  scrolling, saving and the unsaved-changes prompt.** A plugin supplies fields only. Do not hand-roll a
  preview; it would duplicate the host's and could not track the host's draft.
- **A top-level input's node id *is* the widget data key it writes**, in both regions, which share one
  namespace. `UiObjectInput` and `UiArrayInput` do open a scope, making the id `containerId.key`. Address
  array items by their own stable key, never by position: a positional id loses focus on every reorder.
- **A config tree persists nothing.** The host accumulates the edits and writes them through the ordinary
  save path, which is what keeps schema validation and the unsaved-changes prompt working. A key the tree
  never mentions survives a save untouched, so partial configuration drops nothing.
- `UiWidgetAppearance.Section(data, fields, key = "appearance")` builds Macro Deck's own appearance
  fields, already translated. Pass the groups named in the widget type's `AppearanceProperties` so the
  appearance actions reach them. It owns the ids `appearance-heading` and `border-heading`.
- The host runs a widget's **event** flows only from the top-level `flows` key, so bind
  `UiActionsListEditor` there. Press flows additionally need `SupportsFlows` on the widget type.
- A `UiState<JsonElement>` must hold a defined value: `default(JsonElement)` has no JSON form and building
  the view throws a `UiViewException` naming the node and property. Start from an empty array.

### Artifact conformance and publication validation
macrodeck-plugin test --artifact covers **MDC0104-0107**, which **skip for any subject but an artifact**:

| Id | Checks |
|---|---|
| MDC0104 | the manifest declares a supported manifest version and protocol range this suite satisfies |
| MDC0105 | the manifest's id equals what the subject reports at /_macrodeck/info |
| MDC0106 | the manifest's name and version equal what the subject reports at /_macrodeck/info |
| MDC0107 | when the manifest declares an icon, icons/describe reports the media type its extension implies |

Measured on this project: **33 passed against --project, 38 against --artifact**, same 0 failures. CI must
test the packed artifact, not only the project.

alidate takes **no --project**. It takes --manifest, --artifact or --directory. A source directory
fails with source-directory: the manifest points at untimes/<rid>/, which only
macrodeck-plugin build assembles, so a dotnet build -c Release output is never validatable at
publication level.

--level values are Development | Package | Publication, and the default follows the selector:
development for --manifest/--directory, package for --artifact.
### Resources
```csharp
await ctx.UiResources.RegisterAsync(name, bytes, mediaType)   // ≤2 MiB
await ctx.UiResources.GetPluginIconAsync(key, name)
```
No bytes, no base64, no data URLs in the model. `ContentHash` is a cache hint, never a lookup key.

---

## 8. Analyzers - 26 diagnostics, plus MDP5005/5006/5007 which exist only at run time

| Id | Sev | Checks |
|---|---|---|
| MDP1001 | Error | invalid manifest package id, or missing name/version |
| MDP1002 | Error | declared local id fails the declared-kind grammar |
| MDP1003 | Error | manifest icon extension has no known media type |
| MDP1004 | Error | integration restates manifest identity |
| MDP2001 | Error | duplicate capability id |
| MDP2002 | Error | capability kind outside the 19 |
| MDP2003 | Warn | handler registered but never routed to the catalog |
| MDP2004 | Warn | raw `AddSingleton` instead of `RegisterIntegration<T>()` |
| MDP2005 | Error | route under reserved `/_macrodeck` |
| MDP2006 | Warn | inert `[MacroDeckIntegration]` on a plugin integration |
| MDP3001 | Warn | **cancellation token discarded** |
| MDP3002 | Warn | blocking wait (`.Result`/`.Wait()`/`Sleep`) — **whole-type rule** |
| MDP3003 | Warn | `async void` on an SDK contract type |
| MDP4001 | Error | singleton takes `ICapabilityInvocationContext` |
| MDP4002 | Warn | **plugin overrides its own listener URL** |
| MDP5001–5004 | Warn/Error | obsolete / deprecated / bad metadata / removed API |
| MDLOC001–008 | Error | localization generator diagnostics |

**No code-fix providers** — deliberately. Most rules only fire on statically provable literals.
`EnforceExtendedAnalyzerRules` on, so RS2000/RS2001 apply.

Reserved routes: `/_macrodeck/health`, `/ready`, `/info`, `/diagnostics`.
**Never** `UseUrls`, `Configuration["urls"]`, or `ASPNETCORE_URLS` in launchSettings — it makes the
health probe fail silently and permanently.

---

## 8b. Windows audio (NAudio 3.x notes)

Relevant because mic capture and loopback are P/Invoke-shaped work that is far better delegated.

- **Package split:** `NAudio.CoreAudioApi` does **not** exist as a package. Those types ship inside
  `NAudio.Wasapi`.
- `WasapiCapture` is **obsolete**. Use `WasapiRecorderBuilder` → `WasapiRecorder`.
- Both recorder types live in namespace **`NAudio.Wave`**, not `NAudio.Wasapi`. The `NAudio.Wasapi`
  string visible in the DLL is only the assembly name, which makes it look like the namespace exists.
- Builder: `WithDevice(MMDevice)` · `WithSharedMode()` · `WithPollingSync()` · `WithFormat(WaveFormat)`
  · `WithLoopbackCapture()` · `WithProcessLoopback(uint, ProcessLoopbackMode)` · `Build()`.
- `CaptureDataAvailableHandler` is
  `(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)`.
  The buffer is a span over the driver's memory; cast and copy with
  `MemoryMarshal.Cast<byte, float>(buffer).ToArray()`.
- `MMDevice` has only an internal constructor. Get one from `MMDeviceEnumerator.GetDevice(id)`.
- `WaveFormat.CreateIeeeFloatWaveFormat(int sampleRate, int channels)` takes two arguments.
- Ask for shared mode + IEEE float 32-bit. An endpoint's native mix format usually is not float32, and
  demanding a specific one is what makes capture fail on odd hardware.

**Discovering a third-party API without guessing:** reflect over the DLL string heap is *not* enough —
it yields assembly names alongside namespaces. Load the assembly with its dependencies resolved (load
`NAudio.Core.dll` first) and enumerate `GetExportedTypes()`. Note that `dotnet build` stops at the first
error, so a probe file added alongside broken code will never report its own errors; fix the existing
errors first, then probe.

**Declaring a Windows-only assembly:** `[assembly: SupportedOSPlatform("windows")]` in one file removes
the need to suppress CA1416 at every Windows API call site. Add it to the test project too, or every
test touching the plugin's surface warns.

**Level metering:** a speaking voice sits far below full scale, so a linear RMS maps to a fraction of
the range and the reaction is invisible. Expand it. Also clear the sum-of-squares accumulator every
block — a running average over the session both lags badly and never settles, which is invisible without
a test that asserts the figure decays once the signal stops.

## 9. Testing

### Conformance suite — 49 checks, 8 categories, suite v1.2.0
```
MDC01xx ManifestAndIdentifiers      (7)
MDC02xx RegistrationAndNegotiation  (6)
MDC03xx CapabilitySerialization     (15)
MDC04xx DuplicateIds                (3)
MDC05xx TimeoutAndCancellation      (5)   0502/0504/0505 are Recommended
MDC06xx DisconnectAndReconnect      (4)
MDC07xx HealthEndpoint              (4)
MDC08xx BoundedQueues               (5)   0801/0802/0805 are Recommended
```
Outcomes: `Passed / Failed / Skipped / Inconclusive`. Timeout ⇒ **Inconclusive, not Failed**.
A throwing check ⇒ Failed. `Conformant` blocked only by a `Required` + `Failed` entry.
`CheckCatalogTests` pins exactly 49. (Docs say 51 in one place — stale figure.)

### PluginTestHarness — in process, no socket
```csharp
await using var harness = PluginTestHarness.Create(b => b.RegisterIntegration<MyIntegration>());
await harness.InitializeIntegrationsAsync();
var outcome = await harness.Actions.ExecuteAsync("my-action", new Dictionary<string, object?> { ... });
harness.Logs.Events   // collected Serilog output
harness.Clock        // ManualTimeProvider
```
15 capability clients: `Actions Variables Events Icons ConfigFlow MusicPlayer Weather VirtualProfiles
DeviceProvider LayoutProvider FolderViewProvider WidgetTypeProvider ScreenSaverProvider
VideoStreamProvider Issues`.
Substitutions (non-declinable, appended last): `TimeProvider`→`Clock`,
`IIntegrationContext`→`FakeIntegrationContext`, `IMessageChannel`→`Context.Messages`,
`Serilog.ILogger`→collecting sink.
Built but **not started** — call `InitializeIntegrationsAsync()`.

### MacroDeckTestHost — real loopback Kestrel
`HostAsync(builder, credentials, manifest)` writes `MacroDeck:Plugin:*` **configuration** (never env
vars, so a parallel suite isn't affected). `LaunchAsync(spec)` runs a real child process.

21 fakes available: `FakeIntegrationContext` `FakeVariableApi` `FakeVariableSink` `FakeUserVariableApi`
`FakeWidgetApi` `FakeEventPublisher` `FakeIntegrationConfig` `FakeDeckNavigator` `FakeScriptApi`
`FakeUserNotifier` `FakeMessageChannel` `FakeUiResourceRegistry` `FakeActionInteractions`
`FakeAndroidDeviceManager` `FakeDeviceProviderContext` `FakeLayoutProviderContext`
`FakeFolderViewProviderContext` `FakeScreenSaverProviderContext` `FakeWidgetTypeProviderContext`
`FakeVideoStreamProviderContext`

Fixtures in-tree worth copying:
- `sdk/tests/fixtures/…WellBehavedPlugin` — full realistic plugin
- `sdk/tests/fixtures/…MisbehavingPlugin` — 16 switchable violations, one per conformance check

```bash
macrodeck-plugin test --project src/MyPlugin --report markdown --output conformance.md
```

Exit codes: `0` conformant, `1` subject wrong, `2` usage, `3` input unreadable, `4` cancelled.

---

## 10. Localization

- `Localization/Strings.resx` is **required** even for one language.
- A dotted key becomes a nested class: `Actions.LogMessage.Message.Label` ⇒ `Strings.Actions.LogMessage.Message.Label()`.
- **Keys are dotted, not underscored.** Name a key after *where it is used*.
- Placeholders `{name}` become method parameters (positional `{0}` gives no safety).
  Type via `[name:type]` in the entry's `<comment>`.
- `[plural]` on **every** form, keys suffixed `.One`/`.Other`, `Other` required.
  The rule is `count == 1` for every language — deliberately not CLDR.
- A translation needs only the keys it translates; the chain is
  requested → neutral → catalog default → `en`.
- Unresolvable ⇒ `[[scope:Key]]`, deliberately conspicuous.
- Reuse `MacroDeckStrings` (`Common.*`, `States.*`, `Validation.*`, `Connection.*`, `Settings.*`).
- Manifest `languages` is **derived** by build/pack from this folder. Never hand-maintain.
- MDLOC001–008 are real errors, not noise.

---

## 11. Logging & health

- Serilog `ILogger` + `.ForContext<T>()`, never `Microsoft.Extensions.Logging`.
- `UseMacroDeckLogging()` forwards to the host log viewer. Request lines are not logged.
- Host rate-limits and drops floods. Use `FailureEpisodeTracker` for a failing poll loop.
- Structured properties reach the live viewer but are **not persisted to the log file** — put anything
  that must survive into the message template.
- `/_macrodeck/health` · `/_macrodeck/ready` · `/_macrodeck/info` · `/_macrodeck/diagnostics`
- The host duplicates the health path literal rather than referencing the SDK (no backwards project ref).

---

## 12. Code style (binding)

- `.editorconfig`: C# uses **tabs**, max line 120. csproj/props/targets use 4 spaces.
  yml/json use 2. `TreatWarningsAsErrors`, `Nullable enable`, `latest-recommended` analysis.
- **Internal code carries no comments.** Public SDK members get concise `///`.
- Any comment is at most **two `//` lines**. No block comments, no JSDoc, no ASCII banners, no emoji.
- No em dashes anywhere (code, comments, commits, docs). Use a period, comma or parenthesis.
- Suppress a diagnostic with the narrowest scope, **always with a reason**.
- C# comments in English regardless of chat language.
- Follow DRY, but **not on the first occurrence** — extract once a real duplication pattern emerges.
- Dispose everything owning unmanaged or long-lived resources. Watch event subscriptions outliving
  their subscriber.

---

## 13. Build, validate, pack

```bash
dotnet build MacroDeck.slnx -c Release -warnaserror
dotnet test MacroDeck.slnx -c Release

macrodeck-plugin new --name "X" --id com.example.x --publisher "P" --platform win-x64 --yes
macrodeck-plugin run  --project src/X --stub-host      # "Session established (negotiated plugin protocol v3)."
macrodeck-plugin build   --source src/X --output ./artifacts
macrodeck-plugin inspect --artifact ./artifacts/<id>-<version>.macroDeckPlugin
macrodeck-plugin validate --artifact ./artifacts/<id>-<version>.macroDeckPlugin --level publication
macrodeck-plugin test --artifact ./artifacts/<id>-<version>.macroDeckPlugin
macrodeck-plugin pack --source stage --output out.macroDeckPlugin --force
macrodeck-plugin merge artifacts/*.macroDeckPlugin --output dist
```

Validation levels are cumulative: `development ⊂ package ⊂ publication`.
`Development "never becomes stricter."`

**Gotchas:**
- A `dotnet build` output is **not packable**: the manifest points at `runtimes/<rid>/`, which only
  `build` assembles.
- `build`/`pack` **recompute `files[]` from disk** and **fill in `languages` from `Localization/`**.
  They drop any existing `signature`. Signing happens **after** packing.
- Never zip by hand.
- Adding a platform means both `entrypoints` **and** `macrodeck-build.json`.
- `run` is a foreground process and never exits on its own. For bounded verification use `test`.
- A lingering `run` process locks `bin/.../*.dll` and breaks the next build. Kill it.

---

## 14. Repository conventions that bind plugin work

From Macro Deck's own `CLAUDE.md`:
- **Every non-obsolete public plugin-facing contract is a compatibility commitment, source AND
  binary**: `sdk/`, `ui-model/`, `protocol/`, plugin HTTP/WS behaviour, manifest and package formats,
  analyzer diagnostic ids, conformance check ids. If a fix would break one, **stop and ask first**.
- Protocol breaks require a new major with the previous still served.
- Frozen identifiers: `WidgetTypeIds`, `ui.icon` names, the protocol major, analyzer ids, conformance
  ids, `HostApplications` icons, `ProtocolConstants`.
- **Never add a third-party package** because it looks useful or was suggested by an AI —
  hallucinated and typosquatted names are an active supply-chain attack. Requires: a reason existing
  deps can't cover, registry verification, and explicit user approval.
- Derive test expectations from the requirement, never from the current implementation. When test and
  implementation disagree, go back to the requirement; don't edit either side.

---

## 15. Key ADRs worth knowing

| ADR | Decision |
|---|---|
| 0001 | Profiles are JSON, written durably (`tmp`→fsync→`.bak`→rename), recovered on read |
| 0004 | Capabilities are optional interfaces discovered by registry. `ownerId::localId` |
| 0026 | Plugin boundary = versioned JSON protocol + DI-first .NET SDK. **No SignalR** |
| 0028 | Credentials are launch tokens; dev plugins pair interactively with **PKCE** |
| 0029 | `.macroDeckPlugin` is a ZIP, installed atomically, supervised by the host |
| 0037 | Deprecation is `[MacroDeckDeprecated]` + a build-time usage manifest |
| 0038 | The UI model is a versioned package behind a reactive DSL |
| 0042 | Signing is one library, Ed25519, anchored to a pinned root. Portal signs Store artifacts |
| 0044 | Trust is a **verdict** with 11 members; everything unverifiable **fails closed** |
| 0050 | UI sessions are host-brokered; a config tree renders a transaction it does not own |
| 0056 | Widget state addressed by stable state id |
| 0057 | Localized text is a reference resolved by whoever renders it |
| 0062 | UI realtime is a ticketed JSON WebSocket that never blocks on a provider |
| 0064 | Components are a registry over two namespaces |
| 0065 | The component profile's authoring contracts |
| 0068 | Device sessions push full surface snapshots |
| 0075 | Widget types, folder views and modals are provider-registered, served through one provider |
| 0081 | Variables come from one provider catalog with attributes and a write capability |
| 0084 | Built-in icon names are a versioned public vocabulary |
| 0088 | Plugins run on a .NET runtime bundled with the host |
| 0089 | A development build can temporarily take over an installed plugin |
| 0092 | Plugins reach ADB through a permission-gated host API |
| 0093 | Plugins talk over a host-brokered message channel |
| 0096 | The root stays offline and signs one issuer; revocation stops new installs |
| 0097 | Plugin-bundled icon packs are owned by declaration |
| 0098 | Loopback trust requires a per-launch secret |
| 0099/0101 | Video streams are host-brokered sessions; the host relays media, clients play HLS/MJPEG only |
| 0102 | First-fit layouts are chosen by measured text |