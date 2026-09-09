# S7CommPlusDriver

[![NuGet](https://img.shields.io/nuget/v/DotNetProjects.S7CommPlusDriver.svg)](https://www.nuget.org/packages/DotNetProjects.S7CommPlusDriver)
[![NuGet downloads](https://img.shields.io/nuget/dt/DotNetProjects.S7CommPlusDriver.svg)](https://www.nuget.org/packages/DotNetProjects.S7CommPlusDriver)
[![Build](https://github.com/dotnetprojects/S7CommPlusDriver/actions/workflows/dotnet.yml/badge.svg)](https://github.com/dotnetprojects/S7CommPlusDriver/actions/workflows/dotnet.yml)
[![Release](https://github.com/dotnetprojects/S7CommPlusDriver/actions/workflows/release.yml/badge.svg)](https://github.com/dotnetprojects/S7CommPlusDriver/actions/workflows/release.yml)
[![License](https://img.shields.io/github/license/dotnetprojects/S7CommPlusDriver.svg)](LICENSE)

Production-oriented .NET communication library for Siemens S7-1200/1500 PLCs using S7CommPlus over TLS, with legacy challenge authentication available for pre-V17/pre-TLS CPUs on .NET Framework 4.8, `net8.0`, and `net9.0`. The package targets `net48`, `net6.0`, `net8.0`, and `net9.0`.

The supported high-level API for new applications is `S7CommPlusClient`. Lower-level protocol and transport types remain available for compatibility and diagnostics, but production code should use the client surface.

## Acknowledgements

Legacy challenge authentication on `net48`, `net8.0`, and `net9.0` uses the
MIT-licensed [HarpoS7](https://github.com/bonk-dev/HarpoS7) project through
the `HarpoS7` and `HarpoS7.PublicKeys` NuGet packages. HarpoS7 provides the
challenge, public-key, and packet-digest primitives; S7CommPlusDriver provides
the PLC transport, session handling, request ordering, reconnect behavior, and
write protection.

## What The Production Client Provides

- Async connect, disconnect, browse, bulk symbol resolution, read, write, active-alarm, subscription, block and symbol-comment metadata, CPU metadata/control, online block-view, and legitimation methods
- Serialized request execution for safe concurrent callers
- Typed exceptions with PLC endpoint, operation, error code, and transient/non-transient classification
- Connection-state and communication-error events
- Read/browse reconnect retry support
- Explicit write enablement so accidental PLC writes are blocked by default
- Bounded disconnect behavior and better socket disconnect detection
- TLS and legacy S7CommPlus challenge authentication modes
- No library writes to `Console`

## Requirements

### PLC / CPU

The `net48`/`net8.0`/`net9.0` default first tries secure PG/HMI communication over TLS and,
if the PLC rejects it, reconnects with legacy challenge authentication:

- S7-1200 firmware V4.3 or newer, TLS 1.3 from V4.5
- S7-1500 firmware V2.9 or newer
- Software controllers supported by the upstream protocol implementation

For TLS, the PLC project must be configured with a TIA Portal version that supports secure communication, typically TIA Portal V17 or newer. Legacy challenge mode is intended for older projects and CPUs without that TLS configuration.

For older S7-1200/1500 CPUs that do not support TLS, the default `Auto` mode on
`net48`/`net8.0`/`net9.0` falls back to legacy authentication. Set
`S7CommPlusSecurityMode.LegacyChallenge` to skip the initial TLS attempt. Legacy
mode uses HarpoS7-derived challenge authentication and packet digests. `net6.0`
builds remain TLS-only and fail fast if legacy mode is requested.

Most legacy PLCs report a complete Siemens public-key fingerprint. If a PLC
reports only its two-character key family, the driver automatically tries
compatible keys from the bundled `HarpoS7.PublicKeys` catalog on fresh
connections and remembers the successful key for reconnects. This fallback can
be disabled:

```csharp
var options = new S7CommPlusClientOptions
{
    Address = "10.0.110.120",
    SecurityMode = S7CommPlusSecurityMode.LegacyChallenge,
    LegacyPublicKeyFallbackEnabled = false
};
```

Set `LegacyPublicKeyId` to a known 16-character identifier to skip discovery.
The identifier is not a password or private key. Complete PLC fingerprints,
explicit identifiers, and custom `LegacyPublicKeyResolver` implementations
continue to take precedence over automatic fallback.

### TLS Backend

TLS communication uses the managed BouncyCastle backend by default. The older OpenSSL backend remains available through `S7CommPlusClientOptions.TlsBackend = S7CommPlusTlsBackend.OpenSsl`, but it depends on native runtime files and may be less portable across PLC firmware/OpenSSL combinations.

The package includes native OpenSSL runtime files for Windows x86, x64, and ARM64, plus macOS ARM64. Its build target preserves those runtime-specific assets in the application output. If you select the OpenSSL backend on another platform, compatible OpenSSL 3 native libraries must be available to the process.

Windows runtime files include:

- `libcrypto-3.dll` / `libssl-3.dll` for x86
- `libcrypto-3-x64.dll` / `libssl-3-x64.dll` for x64
- `libcrypto-3-arm64.dll` / `libssl-3-arm64.dll` plus `vcruntime140.dll` for ARM64

## Installation

Install the package from [NuGet](https://www.nuget.org/packages/DotNetProjects.S7CommPlusDriver):

```powershell
dotnet add package DotNetProjects.S7CommPlusDriver
```

## Quick Start

Create one `S7CommPlusClient` per PLC endpoint and reuse it for reads. The client serializes operations internally, so multiple callers can safely share the same instance.

```csharp
await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
{
    Address = "10.0.110.120",
    RequestTimeout = TimeSpan.FromSeconds(5),
    AutoReconnect = true
});

client.ConnectionStateChanged += (_, e) =>
{
    Console.WriteLine($"{e.OldState} -> {e.NewState}");
};

client.CommunicationError += (_, e) =>
{
    Console.WriteLine($"{e.Exception.Operation}: 0x{e.Exception.ErrorCode:X8}");
};

await client.ConnectAsync();

var cpuInfo = await client.GetCpuInfoAsync();
var cpuCulture = await client.GetCpuCultureInfoAsync();
var variables = await client.BrowseAsync();

var tag = await client.GetTagBySymbolAsync("MyDb.MyValue");
var read = await client.ReadAsync(new[] { tag });

if (read.Items[0].IsSuccess)
{
    Console.WriteLine(tag.ToString());
}

await client.DisconnectAsync();
```

### Per-operation timeouts

`RequestTimeout` remains the default for ordinary requests, while browse APIs use `BrowseTimeout`. The effective timeout now governs the complete client operation, each protocol response wait, and the connected socket receive/send deadlines. This is important for large symbol catalogs, where a long outer browse deadline alone cannot prevent a shorter protocol wait from failing first.

Use `ExecuteWithTimeoutAsync` when one call needs a different deadline without changing later requests or creating another client:

```csharp
var resolvedTags = await client.ExecuteWithTimeoutAsync(
    TimeSpan.FromMinutes(2),
    cancellationToken => client.GetTagsBySymbolsAsync(symbols, cancellationToken),
    cancellationToken);
```

The override is isolated to the current asynchronous execution context. Concurrent callers can choose different timeouts, and the configured `RequestTimeout` is restored after each request. Connect, disconnect, and subscription-notification waits retain their dedicated timeout settings.

`GetCpuCultureInfoAsync()` reads the CPU text-container LCIDs and exposes both
the raw language IDs and resolved .NET cultures:

```csharp
foreach (var culture in cpuCulture.Cultures)
{
    Console.WriteLine($"{culture.LCID}: {culture.Name}");
}
```

### Browsing arrays

`BrowseAsync()` returns primitive arrays as one `VarInfo` by default. Use
`ArrayElementCount` and `ArrayDimensions` to inspect the PLC bounds without
creating one browse item for every byte, Boolean, or other primitive element:

```csharp
foreach (var variable in await client.BrowseAsync())
{
    if (variable.ArrayElementCount > 0)
    {
        Console.WriteLine($"{variable.Name}: {variable.ArrayElementCount} elements");
    }
}
```

Applications that require the former flattened representation can request it
explicitly:

```csharp
var elements = await client.BrowseAsync(new S7CommPlusBrowseOptions
{
    ExpandPrimitiveArrayElements = true
});
```

Arrays of structures are always traversed because their readable member fields
cannot be represented by one primitive value.

`String`, `Date_And_Time`, and packed `DTL` arrays can be resolved and read both
as complete arrays and as indexed elements. The client expands these requests
into scalar wire addresses when the PLC does not accept the declaration address;
`PlcTagDTLArray` exposes the flattened `Value`, `ValueNanosecond`, and
`DTLInterfaceTimestamps` arrays.

### Resolving many symbols efficiently

Use `GetTagsBySymbolsAsync` to resolve a large configured tag set with one PLC
catalog browse. The result is a case-sensitive dictionary; symbols not found in
the current PLC program are omitted. Fully indexed primitive-array symbols are
resolved from the aggregate array metadata, including non-zero lower bounds and
multidimensional arrays:

```csharp
var symbols = new[]
{
    "MyDb.MyValue",
    "MyDb.Payload[3]",
    "MyDb.Matrix[1,2]"
};

var tagsBySymbol = await client.GetTagsBySymbolsAsync(symbols);
var read = await client.ReadAsync(tagsBySymbol.Values);
```

The client retains this symbol catalog across reconnects. When the PLC program
structure changes, invalidate it before rebuilding application tag caches:

```csharp
await client.InvalidateSymbolCatalogAsync();
var refreshedTags = await client.GetTagsBySymbolsAsync(symbols);
```

Applications that know their complete configured symbol set can avoid retaining
the full PLC catalog. Read and verify the program hash first, then create a
compact accessor catalog whose entries contain only the datatype and wire
addresses required by those symbols:

```csharp
var structure = await client.GetPlcStructureXmlAsync();
var accessors = await client.CreateTagAccessorCatalogAsync(
    symbols,
    structure.ProgramChangeMarker.StructureHash);
var tags = accessors.CreateTags(symbols);
```

The compact catalog also records requested symbols that were not present, so
`CoversSymbols` can distinguish a known-missing tag from a tag added after the
catalog was built. It can be persisted without retaining `VarInfo` objects:

```csharp
await using (var output = File.Create(cachePath))
{
    accessors.WriteTo(output);
}

await using var input = File.OpenRead(cachePath);
var cachedAccessors = S7CommPlusTagAccessorCatalog.ReadFrom(
    input,
    structure.ProgramChangeMarker.StructureHash);
```

`ReadFrom` rejects malformed files, unsupported format versions, and program
hash mismatches. Each `CreateTags` call returns an independent set of mutable
`PlcTag` objects. If an application already has the matching `BrowseAsync`
result, `S7CommPlusClient.CreateTagAccessorCatalog` creates the same compact
catalog locally without another PLC request.

The built-in connection defaults match Siemens S7CommPlus HMI communication:
ISO-on-TCP port `S7CommPlusDefaults.IsoTcpPort` (`102`), local TSAP
`S7CommPlusDefaults.LocalTsap` (`0x0600`), and remote TSAP
`S7CommPlusDefaults.RemoteTsapHmi` (`SIMATIC-ROOT-HMI`). Project/engineering
captures sometimes use `S7CommPlusDefaults.RemoteTsapEs`
(`SIMATIC-ROOT-ES`). Remote TSAP values are validated as ASCII COTP
parameters before a socket is opened.

## CPU Runtime Status and Control

The client exposes the CPU operating state, scan-cycle measurements, and memory
usage in addition to the general CPU information and culture catalog:

```csharp
var state = await client.GetCpuStateAsync();
var cycleTime = await client.GetCpuCycleTimeAsync();
var memory = await client.GetCpuMemoryUsageAsync();

Console.WriteLine($"{state.OperatingState}: {cycleTime.CurrentMilliseconds} ms");
```

`StartCpuAsync` and `StopCpuAsync` change PLC state, so they use the same safety
gate as tag writes and require `WriteEnabled = true`. They are never retried
automatically.

## Password Legitimation

Initial session authentication and PLC password legitimation are separate
steps. If the PLC requires a password for the desired access level, either pass
credentials in the options so they are used during connect, or call
`LegitimateAsync` after connecting:

```csharp
await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
{
    Address = "10.0.110.120",
    Password = "plc-password"
});

await client.ConnectAsync();

// Or, for an already connected session:
await client.LegitimateAsync("plc-password", username: "");
```

Legitimation is a session-security operation, not a PLC signal write, so it
does not require `WriteEnabled = true`. Failed legitimation attempts throw
`S7CommPlusLegitimationException`.

## Active Alarms

Active alarms are exposed through the same serialized read pipeline and can
therefore use the normal timeout, reconnect, logging, and typed-error behavior:

```csharp
var alarms = await client.GetActiveAlarmsAsync(languageId: 1033);
```

Use this call to get alarms that are already active when your application
connects. Alarm subscriptions report new notification frames on that session;
they should not be used as the only source for pre-existing alarms.

Each `S7CommPlusAlarm` exposes the decoded identity parts from `CpuAlarmId`:
`SourceRelationId` and `SourceAlarmId`. For PLC program alarms,
`SourceRelationId` is the PLC object relation id encoded into the alarm id; a
higher-level project/catalog API can map it to a block name or call path.

## PLC Text Lists and Alarm Text Formatting

PLC alarm texts can contain TIA-style text-list placeholders such as
`@2%t#519K@`. Load the PLC text-list catalog once and pass it to the alarm APIs
when you want those placeholders expanded with the same text lists that are
stored in the CPU:

```csharp
var textLists = await client.GetTextListsAsync();
var alarms = await client.GetActiveAlarmsAsync(languageId: 1031, textLists);

foreach (var alarm in alarms)
{
    Console.WriteLine(alarm.AlarmTexts?.AlarmText);
}
```

`GetTextListsAsync()` discovers the CPU languages from the PLC text container
and loads all available localized text-list libraries, plus the
language-independent system text-list library. To load only selected languages,
pass LCIDs explicitly:

```csharp
var textLists = await client.GetTextListsAsync(new[] { 1031, 2057 });
```

The catalog remains a single API surface, but each `S7CommPlusTextList` exposes
`TextListType` so callers can distinguish runtime user lists from system lists:

```csharp
var userLists = textLists.TextLists
    .Where(list => list.TextListType == S7CommPlusTextListType.User);

var systemLists = textLists.TextLists
    .Where(list => list.TextListType == S7CommPlusTextListType.System);
```

The online PLC payload does not expose the full engineering project object
kind, so the driver classifies text lists by Siemens runtime list-id
conventions. The
catalog resolver still uses all lists, because system diagnostic texts and user
alarm texts can reference each other recursively.

Associated-value placeholders are formatted as well. For example, `@2W%d@`
uses the second associated value as a `WORD` and formats it as signed decimal,
matching TIA's standard element-type syntax.

## Communication Limits

PLC communication limits are available through the production client. This is
useful before creating subscriptions or planning large batch reads:

```csharp
var resources = await client.GetCommunicationResourcesAsync();

Console.WriteLine(resources.TagsPerReadRequestMax);
Console.WriteLine(resources.PlcSubscriptionsFree);
```

## Block Metadata and Online View

The production client can browse block metadata, parse the PLC structure XML
into a block tree, read block content, and open TIA-style block online-view
subscriptions:

```csharp
var blocks = await client.BrowseBlocksAsync();
var structure = await client.BrowseBlockStructureAsync();
var content = await client.GetBlockContentAsync(blocks[0].RelationId);
```

Localized engineering comments can be loaded independently of block code. The
returned catalog maps a browsed `VarInfo` to all comments supplied by the PLC,
keyed by Windows locale identifier (LCID):

```csharp
var comments = await client.GetSymbolCommentsAsync(blocks[0].RelationId);

foreach (var variable in await client.BrowseAsync())
{
    if (comments.TryGetComments(variable, out var localized))
    {
        Console.WriteLine($"{variable.Name}: {localized.GetValueOrDefault(1033)}");
    }
}
```

Cache one `S7CommPlusSymbolCommentCatalog` per block or absolute I/Q/M relation
ID while processing a browse. Array indices are normalized to the declaration
comment for data-block symbols; absolute-area access sequences remain exact.

`OpenBlockOnlineViewAsync` creates a disposable `S7CommPlusTisWatchSubscription`
for advanced block-watch scenarios. This API is lower level than tag reads and
requires a caller-provided `S7CommPlusTisWatchRequest` that matches the block
watch points and result model.

## Subscriptions

Subscriptions are exposed as long-running, disposable objects. Notification
frames arrive on the same PLC session as normal request/response traffic, and
the client serializes foreground requests while routing notifications by PLC
subscription object ID.

```csharp
var tag = await client.GetTagBySymbolAsync("MyDb.MyValue");

await using var subscription = await client.SubscribeTagsAsync(
    new[] { tag },
    new S7CommPlusSubscriptionOptions
    {
        CycleTimeMilliseconds = 250,
        NotificationTimeout = TimeSpan.FromSeconds(5)
    });

subscription.NotificationReceived += (_, e) =>
{
    foreach (var item in e.Notification.Items)
    {
        if (item.IsSuccess)
        {
            Console.WriteLine(item.Tag);
        }
    }
};

subscription.CommunicationError += (_, e) =>
{
    Console.WriteLine($"{e.Exception.Operation}: 0x{e.Exception.ErrorCode:X8}");
};
```

Alarm notifications use the same lifecycle and credit handling:

```csharp
await using var snapshotClient = new S7CommPlusClient(options);
var textLists = await client.GetTextListsAsync(new[] { 1033 });
await using var alarmSession = await client.SubscribeAlarmsWithSnapshotAsync(snapshotClient, 1033, textLists);

foreach (var activeAlarm in alarmSession.ActiveAlarms)
{
    Console.WriteLine(activeAlarm.AlarmTexts?.AlarmText);
}

alarmSession.Subscription.NotificationReceived += (_, e) =>
{
    foreach (var alarm in e.Notification.Alarms)
    {
        Console.WriteLine(alarm.ToString());
    }
};
```

Subscriptions can share one `S7CommPlusClient` with reads, metadata calls, and
other subscriptions. The client keeps one foreground request in flight per
physical PLC connection and routes notifications by PLC subscription object id.
Use a second client only when you explicitly want a second physical PLC
connection, for example for true parallel large metadata transfers.

`SubscribeAlarmsAsync()` without a language id reads the CPU language catalog
and explicitly requests its first three languages, avoiding PLCs that reject an
empty all-language subscription filter. `GetActiveAlarmsAsync()` without a
language id still requests every language returned by the PLC. The compatibility
`AlarmTexts` property contains the selected or first CPU language, while
`AlarmTextsByLanguage` contains every language returned for that request.

Alarm snapshots require explicit connection ownership: create another
`S7CommPlusClient` and pass it to `SubscribeAlarmsWithSnapshotAsync` when you
want a live alarm subscription plus an initial active-alarm snapshot on a
separate physical connection. Stopping an alarm subscription deletes only that
subscription and keeps the client session available for later requests.

Idle notification waits are not treated as failures by default. Set
`MaxConsecutiveTimeoutsBeforeFault` when a quiet subscription should become a
typed communication failure after a fixed number of empty waits. Write
protection is unchanged: subscriptions do not enable PLC signal writes.

## Low-level PLC Trace Jobs

This package exposes the raw TIS trace transport and lifecycle API. It deliberately does not define trigger models,
symbol compilation, configuration decoding, or typed measurement decoding. Those high-level features live in
`TiaFileFormat.S7CommPlus`.

A raw request consists of the three protocol blobs plus the requested result-buffer size. Creating it installs and
activates the PLC job before returning. By default, the job continues independently when this connection closes.
Disposing the returned subscription removes only the temporary notification attachment; it never deletes the PLC job.

```csharp
var request = new S7CommPlusTisTraceRequest
{
    JobName = "Raw trace",
    RequestBlob = requestBlob,
    TriggerBlob = triggerBlob,
    InterpretationBlob = interpretationBlob,
    LargeBufferSizeUsed = bufferSize,
    ClientData = optionalClientData,
    UseContinuingJob = true
};

await using var subscription = await client.OpenTisTraceAsync(request);
var reference = subscription.TraceReference;
var completion = new TaskCompletionSource<S7CommPlusTisTraceNotification>(
    TaskCreationOptions.RunContinuationsAsynchronously);
subscription.NotificationReceived += (_, e) =>
{
    if (e.Notification.IsCompleted)
        completion.TrySetResult(e.Notification);
};
if (subscription.LatestResult is { } latest)
    completion.TrySetResult(latest);
using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled());
var completed = await completion.Task;
```

Inventory and lifecycle operations remain low-level and preserve all uninterpreted bytes:

```csharp
var installed = await client.GetInstalledTracesAsync();
var selected = installed.Single(item => item.Reference.Name == "Raw trace");

// Result and large-buffer attributes are opt-in because they may be large.
var withData = await client.GetInstalledTraceAsync(
    selected.Reference,
    new S7CommPlusTraceQueryOptions { IncludeResultData = true });

await using var attached = await client.AttachTisTraceAsync(selected.Reference);
await client.DeactivateTraceAsync(selected.Reference);
await client.ActivateTraceAsync(selected.Reference); // re-arm

var stored = await client.GetStoredTraceMeasurementsAsync(includeResultData: true);
if (stored.Count != 0)
    await client.DeleteStoredTraceMeasurementAsync(stored[0].ObjectId);

await client.DeleteTraceAsync(selected.Reference);
```

Creation, activation, deactivation, job deletion, and stored-measurement deletion require `WriteEnabled = true`.
Discovery and attachment are read-only. With `AutoReconnect`, an active raw attachment rediscovers its PLC-owned job
and recreates the notification subscription after a transient connection failure. A waiting trace may already expose a
pretrigger ring buffer, so use `S7CommPlusTisTraceNotification.IsCompleted` rather than `HasResultData` to decide that
the trigger fired and recording finished.

## Older PLCs / Legacy Challenge Auth

On `net48`/`net8.0`/`net9.0`, `Auto` is the default: it tries TLS first and reconnects with
the older Siemens challenge authentication when TLS is rejected. Applications
that must never downgrade should explicitly set `SecurityMode` to `Tls`.
To force legacy mode and skip the initial TLS attempt:

```csharp
await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
{
    Address = "10.0.110.120",
    SecurityMode = S7CommPlusSecurityMode.LegacyChallenge,
    RequestTimeout = TimeSpan.FromSeconds(5)
});

await client.ConnectAsync();
Console.WriteLine(client.Options.NegotiatedSecurityMode);
```

`S7CommPlusSecurityMode.Auto` tries TLS first and then falls back to legacy
challenge authentication. net6.0 remains TLS-only. Reconnect and write safety
behave the same in all modes: read/browse may reconnect once, writes are never
retried automatically, and writes still require `WriteEnabled = true`.

Legacy integrity keys expire even when requests are still flowing; normal reads do not reset that lifetime. The driver therefore renews a legacy session key every 25 minutes by default, before the roughly 30-minute renewal point observed in TIA Portal traffic. Configure this with `LegacySessionKeyRefreshEnabled` and `LegacySessionKeyRefreshInterval`. Renewal is an internal session-control exchange and does not write PLC tags or require `WriteEnabled = true`.

See [Acknowledgements](#acknowledgements) for the HarpoS7 dependency and
attribution. This project remains LGPL-3.0-or-later unless noted otherwise.

Recent legacy auth work parses `ServerSessionVersion` from the CreateObject
response where available, so S7-1500 V3.x authentication-frame selection is
response-driven before falling back to capture-compatible heuristics.

## Write Safety

Writes are disabled by default. This is intentional for production services and tests.

```csharp
await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
{
    Address = "10.0.110.120",
    WriteEnabled = true
});

await client.ConnectAsync();
await client.WriteAsync(new[] { tag });
```

If `WriteEnabled` is `false`, write calls throw `S7CommPlusWriteDisabledException`.

## Error Handling

Operations throw `S7CommPlusException` subclasses instead of returning only integer error codes. The exception includes:

- `Operation`
- `Endpoint`
- `ErrorCode`
- `IsTransient`

Read and browse operations retry once after a transient communication failure when `AutoReconnect` is enabled. Writes are not retried automatically.

## Logging And Diagnostics

`S7CommPlusClientOptions.Logger` accepts an `ILogger`. If no logger is provided, `NullLogger` is used.

The library no longer writes diagnostics to `Console`. Lower-level diagnostics are written via `System.Diagnostics.Trace`.

TLS key logging for Wireshark analysis is still available through the low-level `S7Client.WriteSslKeyToFile` and `S7Client.WriteSslKeyPath` settings. Use this only in controlled diagnostic environments.

## Testing

Run the normal build and unit tests:

```powershell
dotnet build src\S7CommPlusDriver.slnx /nodeReuse:false
dotnet test src\S7CommPlusDriver.Tests\S7CommPlusDriver.Tests.csproj --no-restore /nodeReuse:false
```

The live PLC smoke test is opt-in and read-only by default:

```powershell
$env:S7COMMPLUS_LIVE_HOST = "10.0.110.120"
dotnet test src\S7CommPlusDriver.Tests\S7CommPlusDriver.Tests.csproj --filter LivePlcReadOnlySmokeTest
```

By default the live test connects, reads CPU information, browses, and
disconnects. To include culture information and PLC text lists on firmware
that supports those APIs, enable extended metadata:

```powershell
$env:S7COMMPLUS_LIVE_EXTENDED_METADATA = "true"
```

To enumerate installed trace jobs without changing them, enable the trace check. Result buffers remain opt-in because
they may be large. The test logs only trace identity and byte counts, never buffer contents:

```powershell
$env:S7COMMPLUS_LIVE_TRACES = "true"
$env:S7COMMPLUS_LIVE_TRACE_RESULTS = "true" # optional
```

For raw trace payload diagnostics, an explicit fixture directory writes one unique text fixture per installed trace. This
requires result downloads and never overwrites an existing file. Trace buffers can contain sampled process values, so
use only a controlled local directory and review fixtures before sharing them:

```powershell
$env:S7COMMPLUS_LIVE_TRACE_FIXTURE_DIRECTORY = "C:\Temp\s7-trace-fixtures"
```

Activation and deactivation can be verified against one explicitly selected, initially inactive trace. This opt-in test
does not create or delete jobs and restores the trace to its inactive state:

```powershell
$env:S7COMMPLUS_LIVE_TRACE_LIFECYCLE = "true"
$env:S7COMMPLUS_LIVE_TRACE_NAME = "My inactive trace"
dotnet test src\S7CommPlusDriver.Tests\S7CommPlusDriver.Tests.csproj --filter LiveTraceLifecycleTests
```

To read explicit tags, provide semicolon-separated tag symbols:

```powershell
$env:S7COMMPLUS_LIVE_TAGS = "MyDb.MyValue;OtherDb.Counter"
```

To exercise legacy auth or auto fallback in the live test:

```powershell
$env:S7COMMPLUS_LIVE_SECURITY_MODE = "LegacyChallenge" # or "Auto"
$env:S7COMMPLUS_LIVE_PUBLIC_KEY_ID = "181B7B0847D11694" # optional: skips automatic discovery
```

TLS backend and timeout overrides are also available when validating a specific
configuration:

```powershell
$env:S7COMMPLUS_LIVE_TLS_BACKEND = "BouncyCastle" # or "OpenSsl"
$env:S7COMMPLUS_LIVE_PORT = "102"
$env:S7COMMPLUS_LIVE_REQUEST_TIMEOUT_SECONDS = "15"
$env:S7COMMPLUS_LIVE_CONNECT_TIMEOUT_SECONDS = "10"
```

Set `S7COMMPLUS_LIVE_RECONNECT=true` to replace the protocol session and verify
that reconnect state, including a discovered legacy public key, is retained.

Never use the live smoke test for writes.

## Tested Communication

Known tested targets include:

- S7 1211 firmware V4.5
- TIA PLCSIM V17 with NetToPLCSim
- TIA PLCSIM V18 with NetToPLCSim
- read-only smoke test against a real PLC at `10.0.110.120`

## Supported Data Types

The `PlcTag` classes convert PLC values into .NET-friendly types. Supported data types include scalar and array variants for common Siemens types such as `Bool`, `Byte`, `Word`, `Int`, `DInt`, `Real`, `LReal`, `String`, `WString`, `Date`, `Date_And_Time`, `DTL`, `Time`, `LTime`, pointer-like types, hardware identifiers, counters, and timers.

For exact mappings, see the implementations in `src/S7CommPlusDriver/ClientApi/PlcTag.cs` and `src/S7CommPlusDriver/ClientApi/PlcTags.cs`.

## License

Unless otherwise noted, all source code is licensed under LGPL-3.0-or-later.

## Authors

- Thomas Wiens - initial work - https://github.com/thomas-v2
- DotNetProjects contributors
