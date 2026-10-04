# How Macro Deck 3 plugins work

A working reference for auditing this plugin against the Macro Deck 3 SDK. Everything here was read
out of the Macro Deck source and docs, not from memory. Where the official docs are wrong or a file
they cite does not exist, that is called out, because following them blindly produces bugs.

Companion to `AGENTS.md`, which holds the style and workflow rules. This file holds the **contracts**.

---

## 0. Where the truth actually lives

The repo named in `AGENTS.md`, `Macro-Deck-App/Macro-Deck-3`, **does not exist**. Cloning it fails
with "Repository not found". The real repositories:

| Repository | What is in it |
| --- | --- |
| `Macro-Deck-App/Macro-Deck` | The app, `docs/`, `sdk/`, `host/`, `protocol/` |
| `Macro-Deck-App/Macro-Deck-Plugin-Template` | This project's origin, plus the authoritative `AGENTS.md` |
| `Macro-Deck-App/Macro-Deck-Sample-Plugins` | Worked examples of every capability |

Docs are at `docs/src/content/docs/`, not `docs/plugin-development/`. Several files that `AGENTS.md`
cites as authoritative do not exist: **`sdk-reference.md`**, `testing-plugins.md`, `cli.md`. Their
content is split across these real files:

| Real location | Covers |
| --- | --- |
| `reference/plugin-hosting.md` | Builder, registration modes, reserved routes, environment |
| `reference/capability-parity.md` | What differs out of process |
| `reference/protocol.md`, `reference/websocket.md` | Wire protocol, envelope, errors |
| `reference/manifest.md` | Manifest schema, validation levels |
| `reference/analyzers.md` | Compile-time diagnostics |
| `reference/conformance.md` | The conformance suite |
| `reference/authentication.md` | Credentials and enrollment |
| `features/*.md` | One page per capability |
| `cli/*.md` | Every `macrodeck-plugin` command |

**Consequence:** any rule attributed to `sdk-reference.md` in `AGENTS.md` is unverifiable from the
docs and must be checked against `sdk/src/` instead.

---

## 1. Project shape

A plugin is a **headless ASP.NET Core process**, not a library. It is `Microsoft.NET.Sdk` plus a
framework reference, deliberately **not** `Microsoft.NET.Sdk.Web`, because a plugin has no HTTP
surface of its own beyond the reserved routes.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Jarvis.Plugin</AssemblyName>   <!-- names the built executable -->
    <RootNamespace>Jarvis.Plugin</RootNamespace>  <!-- where the generated Strings class lives -->
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MacroDeck.Plugin.Analyzers" PrivateAssets="all" />
    <PackageReference Include="MacroDeck.Localization" />
    <PackageReference Include="MacroDeck.Plugin.Hosting" />
    <PackageReference Include="MacroDeck.Plugin.Serilog" />
    <PackageReference Include="MacroDeck.Sdk" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="manifest.json" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Assets\icon.svg" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- Target framework `net10.0`.
- The SDK reads `manifest.json` **from the content root** and resolves `icon` against that same root,
  so both must sit next to the built executable. That is what the two `Content` items are for.
- `AssemblyName` and the manifest's `entrypoints[].executable` must agree. Change both together or
  `macrodeck-plugin build` fails with `entrypoint-missing`.
- Package versions float to the newest published Macro Deck release. Do not pin them casually.

---

## 2. The builder chain

```csharp
var plugin = MacroDeckPlugin.CreatePlugin(args)
    .UseMacroDeckLogging()
    .UseLocalization(Strings.LocalizationCatalog)
    .RegisterIntegration<PluginIntegration>()
    .Build();

await plugin.RunAsync();
```

| Member | Meaning |
| --- | --- |
| `MacroDeckPlugin.CreatePlugin(args)` | Starts a `PluginHostBuilder`. |
| `RegisterIntegration<T>()` | **The only supported door.** Registers `T` plus a handler per capability interface it implements. |
| `RegisterCapabilityHandler<T>()` | For a genuinely new capability kind. |
| `UseLocalization(catalog)` | Publishes strings, declares the `localization` capability. Catalog scope must be `plugin:<plugin-id>`. |
| `ConfigureServices(...)` / `Configure(...)` | Extra registrations and middleware. |
| `Build()` | Validates and returns a `PluginApplication`. |
| `RunAsync()` | Runs it. |

- `services.AddSingleton<TIntegration>()` instead of `RegisterIntegration<T>()` skips every capability
  handler. **MDP2004** warns about it.
- `AddMacroDeckIntegration<T>()` is `internal` to the hosting package. There is no other door.
- `Build()` collects **every** local problem and throws one `PluginConfigurationException` listing all
  of them. Read the whole message.
- `Build()` constructs every integration and handler as part of validation, so **constructors must be
  side-effect-free and cheap**. A constructor that connects or probes hardware fails the build or
  hangs it.

### Never set the listener URL

No `UseUrls`, no `Configuration["urls"]`, no `ASPNETCORE_URLS` in `launchSettings.json` or
`appsettings.json`. The supervisor binds a port, injects it, and probes `GET /_macrodeck/health`
**before the plugin has finished starting**. Overriding it produces a plugin that is "unhealthy but
running fine" with nothing to explain why. **MDP4002** catches the visible forms.

### Lifecycle

```
connect -> session established -> InitializeAsync
  resume          -> integrations keep running
  session lost    -> ShutdownAsync -> new session -> InitializeAsync
shutdown requested-> ShutdownAsync -> process exits
```

`InitializeAsync` does **not** run at process start. It is gated on a session, and it runs again
after any non-resume reconnect and on a configuration change. **Every integration must be idempotent
across re-initialisation.** That includes unsubscribing before re-subscribing, or handlers fire once
per reconnection.

---

## 3. Identity lives in the manifest, and only there

`IPluginIntegration` carries **no** `Id`, `Name`, `Version`, `Icon` or `IsInitialized`. Restating any
of them is **MDP1004**, an error. Identity is `manifest.json` and nothing else, which is what stops
the two drifting.

```csharp
public interface IPluginIntegration
{
    IReadOnlyList<IActionDefinition> Actions { get; }
    Task InitializeAsync(IIntegrationContext context);
    Task ShutdownAsync();
}
```

### Local ids

Ids written in source are **local ids**: `^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$`, max 64 characters, never
containing `::`. The host qualifies them. Never pass `owner::local` to an API expecting a local id.
**MDP1002** errors on a malformed one.

Action ids are **unique across the whole plugin process**, not per integration, because over the wire
the owner is the plugin. **MDP2001** / `MDC0401`.

### Manifest

The five **runtime** fields are the schema's entire `required` array: `manifestVersion`, `id`, `name`,
`version`, `entrypoints`. Everything else is publication or recommended.

| Field | Rule |
| --- | --- |
| `manifestVersion` | Integer, exactly `1`. Checked before anything else is parsed. |
| `id` | `^[a-z][a-z0-9]*(-[a-z0-9]+)*(\.[a-z][a-z0-9]*(-[a-z0-9]+)*)+$`. **At least two dot segments**, max 128 chars. Must equal the install directory name. |
| `version` | SemVer 2.0, **three components**. `1.0` is invalid. Must equal the install directory name. |
| `entrypoints` | Keyed by runtime identifier, at least one. |
| `executable` | Relative, inside the version directory, no `..`. **Never a script**: `.sh`, `.bat`, `.cmd`, `.ps1`, `.command` are rejected whatever the runtime. |
| `runtime.kind` | `FrameworkDependent` or `SelfContained`. Omitting `runtime` means self-contained. |
| `runtime.dotnetVersion` | `major.minor`. **Required** when `FrameworkDependent`. |
| `compatibility` | Declare at least one of `sdk`, `protocol`, `macroDeck`. An absent member declares nothing, never "incompatible". |
| `files` / `signature` | **Generated.** Never hand-authored. |

Cross-field: `FrameworkDependent` requires `executable` to end `.dll`; self-contained requires it not
to. Prefer `FrameworkDependent` with `dotnetVersion` `10.0` for a .NET plugin: about 0.7 MB
compressed against about 43 MB self-contained.

Version ranges use `*`, `>=`, `<`, `=`, `>`, `<=`, comma for AND. **No caret, tilde or `||`.**
Prereleases of the lower bound need `-0`, so `>=3.0.0-0` and not `>=3.0.0`.

Unknown properties are **ignored everywhere**. Do not validate with a schema that rejects them.

---

## 4. Capabilities

Nineteen kinds: `actions`, `events`, `variables`, `icons`, `config-flow`, `music-player`, `weather`,
`virtual-profiles`, `issues`, `ui`, `localization`, `device-provider`, `layout-provider`,
`folder-view-provider`, `migration`, `widget-type-provider`, `screensaver-provider`, `messaging`,
`video-stream-provider`.

Implementing the interface on the integration is enough; `RegisterIntegration<T>()` wires the handler.
Capabilities are opted into by interface, not declared:

| Interface | Handler |
| --- | --- |
| always | `ActionsCapabilityHandler`, `UiCapabilityHandler` |
| `IVariableProvider` | `VariablesCapabilityHandler` |
| `IEventProvider` | `EventsCapabilityHandler` |
| `IConfigFlowProvider` | `ConfigFlowCapabilityHandler` |
| `IIntegrationIssueProvider` | `IssuesCapabilityHandler` |
| and so on per kind | |

Every kind but `actions` and `variables` declares its capability at local id **`provider`** (`icon`
for `icons`). **MDC0303** enforces this.

### The extension point

```csharp
public interface ICapabilityHandler
{
    string Kind { get; }
    IReadOnlyList<DeclaredCapability> DeclareCapabilities();
    Task<CapabilityInvocationResult> InvokeAsync(
        CapabilityInvocation invocation, CancellationToken cancellationToken);
}
```

`DeclareCapabilities` is called **before initialisation and possibly again mid-session**, so it must
be side-effect-free and must not need a live connection. Failures are returned, not thrown.

`ICapabilityInvocationContext` (`Kind`, `LocalId`, `Operation`, `CorrelationId`, `Deadline`,
`CancellationToken`) resolves **only inside a per-invocation DI scope**. A singleton must not depend
on it. **MDP4001.**

---

## 5. Actions

```csharp
public interface IActionDefinition
{
    string Id { get; }
    LocalizedText Name { get; }
    LocalizedText Description { get; }
    IReadOnlyList<ActionParameter> Parameters { get; }
    MacroDeckPlatform Platforms => MacroDeckPlatform.All;   // default implementation
    IActionExecutor CreateExecutor();
}

public interface IActionExecutor
{
    Task<ActionResult> ExecuteAsync(ActionExecutionContext context);
}
```

- `Name` and `Description` are `LocalizedText`, **not** `string`. No user-facing literal.
- `Platforms` on the definition is **inert out of process** (**MDP2006**); platform gating comes from
  the manifest's `entrypoints`.
- `Actions` is read **before `InitializeAsync`**, so building the list must not connect, probe
  hardware or do any I/O.

### Parameters

Factories exist per editor control: `Text`, `MultilineText`, `Number`, `Slider`, `Toggle`,
`Password`, `Secret`, `Choice`, `DynamicChoice`, `Autocomplete`, `MultiSelect`, `Color`, `File`,
`Folder`, `Hotkey`, `Duration`, `DateTime`, `Json`, `Code`, `KeyValue`, `Object`, `Array`,
`IpAddress`, `Url`, `Icon`, `Image`, `KeyboardSequence`, `KeyboardCombo`, `WidgetTarget`.

Values arrive as `object?`. Treat every one as needing validation and conversion; nothing coerces a
wire type to the CLR type you want.

`OnlyWhen` is **presentation only**. A hidden parameter keeps its value, skips validation, and **is
still sent to the executor**. Never infer anything from a field being hidden.

`ActionExecutionContext` exposes `Parameters`, `CancellationToken`, `OwnerWidgetId` (null for
widget-less runs), `OriginClientId`, `Interactions`, `Ui`, `CallDepth`. The last three can be null.

### Results

```csharp
public enum ActionResultStatus { Succeeded, Accepted, Failed }

public sealed class ActionResult
{
    public static ActionResult Success();
    public static ActionResult Success(string expectedStateId);
    public static Task<ActionResult> SucceededTask { get; }        // cache this for sync executors
    public static ActionResult Accepted(LocalizedText message = default);
    public static ActionResult Accepted(LocalizedText message, string expectedStateId);
    public static ActionResult Failed(string code, LocalizedText message);
}
```

`ActionErrorCodes` is a static class of **string constants**, not an enum. Nine values:
`NotConfigured`, `NotConnected`, `PermissionDenied`, `ProviderError`, `ProviderRejected`,
`InvalidParameter`, `NotFound`, `Timeout`, `Unavailable`.

The rules that matter:

- `Success()` claims the operation **completed**. A press that did nothing must never return it.
- `Accepted` is for work the provider took and **genuinely cannot confirm**. Where the API can
  confirm, poll until it does within a bound.
- **`Accepted` is transport-*succeeded*.** At the wire level there are only `capability.result` or
  `protocol.error`. Anything reading the in-payload status rather than the transport outcome will
  report an unverified operation as done.
- A legitimate no-op **is** success: blank optional parameter, repeat count zero, already in the
  requested state.
- Error messages are localized, read as an explanation, and carry no tokens, paths or provider
  internals. Throwing is allowed but the caller only ever sees a generic code.

### Long-running work

**Never poll or wait unbounded in an executor.** The hard protocol bound is **30 seconds**
(`ProtocolTimeouts.CapabilityInvoke`). Past it the host sends `capability.cancel`, releases the
concurrency slot immediately, and cancels your token. Use a bounded wait, then return
`Failed(Timeout)` if completion was confirmable, or `Accepted` if the provider simply never reports.

---

## 6. Async and concurrency

- Invocations dispatch **concurrently**, 32 slots (`MaxConcurrentInvocations`).
- Each invocation gets **its own DI scope**.
- **No** `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` or `Thread.Sleep` anywhere in a type
  implementing `ICapabilityHandler`, `IActionExecutor` or `IConfigFlow`. The rule is whole-type, not
  just the method, because a block anywhere reachable starves the other 31 slots. **MDP3002.**
- **Forward `context.CancellationToken`** into everything awaited. Dropping it is **MDP3001**.
- No `async void` on an SDK contract type; an exception there kills the process. The
  `(object?, EventArgs)` handler shape is the one exception. **MDP3003.**
- Instance state touched from more than one invocation needs its own synchronisation, exactly like a
  concurrently-invoked ASP.NET Core endpoint.

---

## 7. Variables

This is the area with the most room to get it wrong, because variables have **two** identifiers where
actions have one.

| | Actions / events | Variables |
| --- | --- | --- |
| User-facing | *nothing*, the host qualifies it | `Name`, what the user types: `music_track`, `[a-z0-9_]`, plugin-prefixed |
| Wire / persisted | `Id` | `Id`, what `ReadAsync` receives; **set it explicitly** |

Leaving `Id` off derives it from `Name`, which costs you the ability to rename later.
**MDC0403** requires no two eager variables to resolve to the same local id.

`VariableDefinition` is a **record**, so it is built with `with { }`:

```csharp
VariableDefinition.Eager("music_position", VariableType.Numeric, refreshInterval: TimeSpan.FromSeconds(1))
    with { Id = "position", Unit = "s", SemanticKind = VariableSemanticKinds.Duration }
```

- A value is a `string`, number or `bool`. **Anything else, and `VariableReading.Unavailable`, renders
  as "not available".** Use `Unavailable` for "no value right now", never `""` or `0`.
- Max **256** eager variables per plugin (**MDC0315** counts `variables` and `declaredVariables`
  together, across the whole plugin). The host silently keeps the first 256.
- Writing requires `Write = new VariableWriteCapability()`. **Declaring `Write` and then answering
  `NotWritable` fails MDC0314.**
- `ResolveAsync` returns `null` only for an **invalid** id. A resource that is merely gone right now
  must still resolve, or the binding becomes a broken reference the user has to fix by hand.
- `SupportsPush` applies to the **catalog only**. Eager variables are always polled.
- `Min`/`Max`/`Step` come from the reading, not the definition, because they change.

### Catalogs go stale

Anything outside a host-initiated invocation that changes what a later `describe` answers needs an
invalidation:

```csharp
// MacroDeck.Plugin.Hosting.Integrations.HostApis
public interface IPluginCatalogNotifier
{
    void CatalogChanged(string kind, string? localId = null, string? reason = null);
}
```

`IPluginCatalogNotifier` is **not** on `IIntegrationContext` and **not** in `MacroDeck.Sdk`; it is a
DI singleton in the hosting package. Inject it. Call `CatalogChanged(CapabilityKinds.Variables)`
after a config change changes the variable set. Without it the host serves the previous snapshot
indefinitely and the UI quietly disagrees with the plugin.

`CatalogChanged` is an invalidation *signal*, not a diff. The refresh is host-driven and asynchronous.

---

## 8. Events

```csharp
public sealed class EventDefinition
{
    public required string Id { get; init; }
    public required LocalizedText Name { get; init; }
    public LocalizedText Description { get; init; }
    public LocalizedText Category { get; init; }
    public string? IconName { get; init; }
    public EventDeliveryKind DeliveryKind { get; init; } = EventDeliveryKind.Push;
    public IReadOnlyList<ActionParameter> ConfigurationParameters { get; init; } = [];
    public IReadOnlyList<ActionParameter> PayloadParameters { get; init; } = [];
}
```

`IEventProvider` supplies `EventDefinitions` and an optional `ProviderName`.

- `Publish` is **fire-and-forget**: no reply, never throws, and an occurrence nobody subscribed to is
  dropped. Payload keys must match `PayloadParameters` exactly.
- Delivery is **at-most-once**. There are no sequence numbers and no replay log. **Do not buffer
  occurrences across a reconnect and replay them** — that fails **MDC0804**.
- An object or array value is delivered as its compact JSON **text**, and a condition compares that
  text.
- `EventBinding`/`GetBindings()` are served from the host's last push, so they are **empty until the
  first push arrives** and stay empty forever on a host that predates the API. Treat empty as
  "nothing bound", never as an error.
- `BindingsChanged` fires on a thread-pool thread, and **only** when the list changed for your
  integration. Remove the handler before re-adding it in `InitializeAsync`, or it fires once per
  re-initialisation. Unsubscribe in `ShutdownAsync`.
- There is **no way to tell a configuration-parameter request from a payload-parameter request**: the
  context names only the event and the parameter. Do not give the same name to both with different
  option sets.
- After changing `EventDefinitions`, call `CatalogChanged(CapabilityKinds.Events)`.

---

## 9. Config flows

```csharp
public interface IConfigFlowProvider
{
    IConfigFlow CreateConfigFlow();
    bool AllowsMultipleConfigurations => true;
    bool RequiresConfiguration => true;   // an integration with a flow starts DISABLED
}

public interface IConfigFlow
{
    Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken);
    Task<ConfigFlowResult> SubmitAsync(
        string stepId, IReadOnlyDictionary<string, object?> input,
        IConfigFlowContext context, CancellationToken cancellationToken);
}
```

`ConfigFlowResult` is factory-only: `Step`, `Error`, `Complete`, `External`.

**The trap:** `SubmitAsync` can name a step *earlier* than the one returned last, because the user
can go back. Dispatch on `stepId` and rebuild that step's state. Values collected by later steps are
**not** in `input`.

- `Complete(title, ...)` takes a plain `string`, deliberately. It is the one plain-string exception.
- Form fields persist automatically; a `Secret` field is encrypted.
- **Never run your own OAuth redirect server.** Return `ConfigFlowResult.External(url, resumeStepId)`
  and let the host own the callback.
- Read values back through `IIntegrationContext.Config`. Rotating a credential goes through
  `SetSecretAsync`.

---

## 10. Notifications and issues are different things

```csharp
public interface IUserNotifier
{
    void Notify(UserNotificationRequest notification);   // fire-and-forget, must never throw
    void Dismiss(string key);
}
```

- Session-scoped and in-memory. **Not** a durable channel; nothing survives a host restart.
- `Key` makes a notification replaceable. Use it for anything recurring.
- A standing condition the user must resolve belongs in `IIntegrationIssueProvider`, **not** in a
  notification.

---

## 11. Reserved routes and injected environment

Reserved, and mapping under the prefix **fails `Build()`**:

| Route | Answers |
| --- | --- |
| `/_macrodeck/health` | 200 as soon as the process serves, regardless of session |
| `/_macrodeck/ready` | 200 only once a session is open, else 503 |
| `/_macrodeck/info` | Metadata and mode |
| `/_macrodeck/diagnostics` | Connection state, queue depths, capability counts |

`/_macrodeckery` is **not** reserved. Enforced three ways: `Build()` rejects a colliding route
(**MDP2005**), a startup filter 404s anything unmapped under the prefix (**MDC0703**), and the
analyzer catches the constant-path case.

The host checks **only the HTTP status** of the health probe. Any 2xx passes; the body is never read.

### Injected environment

The launcher **scrubs every inherited `MACRO_DECK_PLUGIN_*` and `ASPNETCORE_URLS`**, then sets:

| Variable | Value |
| --- | --- |
| `MACRO_DECK_PLUGIN_MODE` | `Managed` |
| `MACRO_DECK_PLUGIN_HOST_URL` | `http://127.0.0.1:<host port>` |
| `MACRO_DECK_PLUGIN_ID` | Plugin id |
| `MACRO_DECK_PLUGIN_SECRET` | Launch credential, 2 minutes while unused |
| `MACRO_DECK_PLUGIN_DATA_DIRECTORY` | **The only writable location to rely on.** Survives updates and rollbacks. |
| `MACRO_DECK_PLUGIN_INSTANCE_ID` | New per launch |
| `MACRO_DECK_PLUGIN_LAUNCH_ID` | Diagnostics only; the host never reads it back |
| `MACRO_DECK_PLUGIN_HOST_PROCESS_ID`, `..._HOST_STARTED_AT` | Used by the host-liveness watch |
| `ASPNETCORE_URLS` | The health port the host pre-bound |

Read but **never set** by the supervisor: `MACRO_DECK_PLUGIN_ENROLLMENT_TOKEN`,
`MACRO_DECK_PLUGIN_STATE_DIRECTORY`, `MACRO_DECK_PLUGIN_PAIRING`, `MACRO_DECK_PLUGIN_PAIRING_TIMEOUT`.
All of `PluginHostOptions` also binds from `MacroDeck:Plugin`, so `appsettings.json` works too.

Anything written next to the executable lives in an immutable version directory and disappears on the
next update or rollback.

### What the host actually enforces about permissions

The manifest `permissions` array is **declarative**. It is validated, persisted and exposed, and the
host enforces **exactly one**: `host:adb`. Every other permission is enforced nowhere. Do not treat
that list as a security boundary.

---

## 12. Localization

- **No user-facing literal.** Every string a user reads is a key in `Localization/Strings.resx`,
  reached through the generated `Strings` class. Log and exception messages stay English literals.
- Keys are **dotted, not underscored**: `Actions.LogMessage.Name` is
  `Strings.Actions.LogMessage.Name()`.
- Placeholders are **named** (`{host}`) and become method parameters. Positional `{0}` gives no such
  safety.
- A count-dependent sentence is one key with `[plural]` on every form, `.One` and `.Other`,
  `Other` required.
- A translation is `Localization/Strings.<culture>.resx` with a well-formed BCP-47 name: `de`,
  `pt-BR`, `zh-Hant-TW`. Never `zh` alone, never an underscore.
- `Strings.resx` is required even for one language. It is what the fallback chain ends on and what
  every translation is checked against.
- Catalog scope must be `plugin:<plugin-id>`; the host rejects a scope it does not own.
- Check `MacroDeckStrings` before adding a key; `Common.*`, `Validation.*`, `Connection.*`,
  `Settings.*` are already translated everywhere. A duplicated `Save` is one more string every
  translator has to keep in sync.
- Diagnostics **MDLOC001**-**MDLOC008** cover key-only-in-a-translation, placeholder mismatch,
  duplicate key, bad parameter type, malformed culture suffix, removed `MacroDeckStrings` key,
  broken plural family, and key/group collision. Fix them; do not suppress.

---

## 13. Logging

- Use **Serilog** through `UseMacroDeckLogging()`. `Microsoft.Extensions.Logging` is not the contract.
- Logging is rate-limited and a flood is dropped. Use an episode tracker rather than a line per tick.
- Structured properties reach the live viewer but **are not persisted to the log file**. Anything that
  must survive goes in the message template.
- Never write a token, secret or key.

---

## 14. Hard limits

| Limit | Value |
| --- | --- |
| Concurrent invocations | **32** |
| Capability invoke timeout | **30s** |
| Declared capabilities | **512** |
| Message size | **256 KiB** |
| Inbound queue depth | **256**, pause at 192, resume at 64 |
| Eager variables | **256** per plugin |
| Variable catalog page | 200 |
| Variable subscribe | 1024 ids |
| Variable publish batch | 128 |
| Message topic length / payload | 128 chars / 64 KiB |
| Session token | 15 minutes, no refresh endpoint |
| Keepalive | 20s interval, 60s timeout |
| `shutdown.gracefulTimeoutSeconds` | 1-60, default 10 |
| `health.intervalSeconds` | 5-120, default 15 |
| `health.timeoutSeconds` | 1-10, default 2 |
| `health.unhealthyThreshold` | 2-10, default 3 |
| Plugin artifact entries / total | 20 000 / 1 GiB |

Numeric settings are **clamped, never rejected**: `unhealthyThreshold: 1` produces no diagnostic and
becomes 2.

---

## 15. Diagnostics

| Id | Severity | Rule |
| --- | --- | --- |
| MDP1001 | Error | Manifest missing or invalid `id`, `name` or `version`. |
| MDP1002 | Error | A local id is not valid kebab-case or contains `::`. |
| MDP1003 | Error | Manifest icon has an unsupported extension. |
| MDP1004 | Error | An integration restates identity or icon metadata the manifest owns. |
| MDP2001 | Error | A capability id is declared twice for the same kind. |
| MDP2002 | Error | A capability handler declares an unknown kind. |
| MDP2003 | Error | A handler is constructed but never reaches the catalog. |
| MDP2004 | Error | Integration registered with raw `AddSingleton` instead of `RegisterIntegration<T>()`. |
| MDP2005 | Error | A route is mapped under `/_macrodeck`. |
| MDP2006 | Warning | `Platforms` is inert out of process. |
| MDP3001 | Warning | `CancellationToken.None`/default where a real one is available. |
| MDP3002 | Error | Blocking call in a capability/action/config-flow type. |
| MDP3003 | Error | `async void` on an SDK contract type. |
| MDP4001 | Error | A singleton depends on `ICapabilityInvocationContext`. |
| MDP4002 | Error | The plugin overrides its listener URL. |

Ids are stable public contracts: an id never changes meaning, and a retired rule is deprecated
rather than reused.

---

## 16. Conformance

```bash
macrodeck-plugin test --project src/Jarvis.Plugin --report markdown --output conformance.md
```

| Exit | Meaning |
| --- | --- |
| 0 | Conformant |
| 1 | A **Required** check failed |
| 2 | Usage error |
| 3 | Subject could not be built or launched |
| 4 | Cancelled |

Categories: `MDC01xx` manifest-and-identifiers, `MDC02xx` registration-and-negotiation, `MDC03xx`
capability-serialization, `MDC04xx` duplicate-ids, `MDC05xx` timeout-and-cancellation, `MDC06xx`
disconnect-and-reconnect, `MDC07xx` health-endpoint, `MDC08xx` bounded-queues.

`--list-checks` prints every id with its category, requirement and title. Recommended failures are
allowed; Required failures are not.

---

## 17. Building and publishing

```bash
macrodeck-plugin build --source src/Jarvis.Plugin --output ./artifacts
macrodeck-plugin inspect --artifact ./artifacts/<id>-<version>.macroDeckPlugin
```

- `dotnet build -c Release` output is **not packable**: the manifest points at `runtimes/<rid>/`,
  which only `build` assembles.
- `build` stages declared files first and target output on top, derives `languages` from
  `Localization/**/*.resx`, drops any authored `files`/`signature`, and hard-fails on a missing
  entrypoint.
- `pack` recomputes **every** `files[]` digest unconditionally, discarding what the manifest
  declared. Never hand-maintain `files[]`.
- Signing happens **after** packing, because packing recomputes the digests. `pack` never signs;
  `sign` refuses to replace an existing signature.
- Store artifacts are signed by the Creator Portal. **No signing key ever reaches an author or CI.**
- Artifact name: `<id>-<version>.macroDeckPlugin`, or `<id>-<version>-<rid>` for `--rid`.

### The Store gate

Publishing is: GitHub release → `publish-plugin.yml` → build in Builds → Create release → Add to
submission → Submit for Review. The portal **only accepts builds produced by that workflow**, and
rejects anything not from a tag. Required on the build output: `description`, `icon`, `license`,
`repository`, `compatibility`, `publisher.name`, a declared `ai` block, and a passing conformance
report for **every** declared platform.

`publisher.name` must be the creator or organisation that owns the listing. `repository` must be the
GitHub repository the plugin is released from.

---

## 18. Audit checklist

Use this when reviewing a change.

- [ ] No user-facing string literal; every one is a `Strings` key.
- [ ] Every `await` forwards its `CancellationToken`.
- [ ] No `.Result`, `.Wait()`, `GetAwaiter().GetResult()` or `Thread.Sleep` in a contract type.
- [ ] No blocking wait longer than 30s inside an executor.
- [ ] `InitializeAsync` is idempotent; handlers unsubscribed before resubscribed.
- [ ] Anything derived from configuration pushes `CatalogChanged` for that kind.
- [ ] `Accepted` is only returned when confirmation is genuinely impossible.
- [ ] Error messages carry no token, path or provider internal.
- [ ] No invented error string; `ActionErrorCodes` is reused.
- [ ] Nothing is written next to the executable.
- [ ] The listener URL is not overridden anywhere.
- [ ] No route under `/_macrodeck`.
- [ ] Local ids are kebab-case and action ids unique across the plugin.
- [ ] Writes go to `MACRO_DECK_PLUGIN_DATA_DIRECTORY`.