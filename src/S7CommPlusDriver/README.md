# DotNetProjects.S7CommPlusDriver

Production-oriented S7CommPlus communication library for Siemens S7-1200/1500 PLCs.

Install from [NuGet](https://www.nuget.org/packages/DotNetProjects.S7CommPlusDriver):

```powershell
dotnet add package DotNetProjects.S7CommPlusDriver
```

Use `S7CommPlusClient` for new applications. It provides async connect, browse, bulk symbol resolution, read, write, alarm, subscription, CPU metadata/control, block and symbol-comment metadata, online block-view, and legitimation operations with request serialization, typed exceptions, connection-state events, reconnect support for read operations, and an explicit write-enable safety gate.

The managed BouncyCastle backend is the default TLS implementation unless `S7CommPlusClientOptions.TlsBackend` is set explicitly. On `net48`, `net8.0`, and `net9.0`, the default security mode is `Auto`: it tries TLS first and reconnects with HarpoS7-derived legacy challenge authentication if the PLC rejects TLS. Set the mode to `Tls` to prohibit fallback, or `LegacyChallenge` to skip the TLS attempt. `net6.0` remains TLS-only. .NET Framework 4.8 uses the managed BouncyCastle backend because the native OpenSSL resolver requires modern .NET.

Default connection parameters are exposed through `S7CommPlusDefaults`: ISO-on-TCP port `102`, local TSAP `0x0600`, HMI remote TSAP `SIMATIC-ROOT-HMI`, and engineering remote TSAP `SIMATIC-ROOT-ES`. `S7CommPlusClientOptions.SessionRole` defaults to `S7CommPlusSessionRole.Hmi` for both TLS and legacy challenge connections. Set it to `S7CommPlusSessionRole.EngineeringSystem` only when an engineering-class session is required; such a session can prevent TIA Portal from downloading hardware configuration while the driver remains connected. An explicitly assigned `RemoteTsap` overrides the standard TSAP selected by the role.

## Older PLCs / Legacy Challenge Auth

Siemens OMS names the old non-TLS mode `SecurityTypeCSI`. This library exposes it as `S7CommPlusSecurityMode.LegacyChallenge`. Because `Auto` is the `net48`/`net8.0`/`net9.0` default, applications that must prohibit fallback should explicitly use `S7CommPlusSecurityMode.Tls`.

```csharp
await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
{
    Address = "10.0.98.34",
    SecurityMode = S7CommPlusSecurityMode.LegacyChallenge,
    WriteEnabled = false
});

await client.ConnectAsync();
var cpuInfo = await client.GetCpuInfoAsync();
await client.DisconnectAsync();
```

Legacy support uses the PLC fingerprint to resolve a Siemens public-key family. Known mappings are S7-1500 (`00`), S7-1200 (`01`), and PLCSIM/VPLC (`03`). The implementation references the `HarpoS7` and `HarpoS7.PublicKeys` NuGet packages for challenge/key/digest primitives on `net48`, `net8.0`, and `net9.0`, while this driver still owns transport, request ordering, timeouts, reconnect behavior, and write protection.

Legacy integrity keys expire even on active connections; ordinary reads do not extend their lifetime. The driver renews them every 25 minutes by default. Use `LegacySessionKeyRefreshEnabled` and `LegacySessionKeyRefreshInterval` to change this behavior. The renewal is an internal session-control exchange, not a PLC tag write, and works while `WriteEnabled` remains `false`.

## Per-operation timeouts

Ordinary requests use `RequestTimeout`, and browse operations use `BrowseTimeout`. The selected timeout applies consistently to the client task, protocol response wait, and live socket timeouts. A caller can override any individual request without changing the client defaults:

```csharp
var tags = await client.ExecuteWithTimeoutAsync(
    TimeSpan.FromMinutes(2),
    cancellationToken => client.GetTagsBySymbolsAsync(symbols, cancellationToken),
    cancellationToken);
```

The override is async-context-local and the configured request timeout is restored afterward. Connection lifecycle and subscription notification waits continue to use their specialized timeout settings.

## Browsing Primitive Arrays

`BrowseAsync()` returns one `VarInfo` for each primitive array instead of allocating an item for every indexed element. `VarInfo.ArrayElementCount` contains the total size and `VarInfo.ArrayDimensions` preserves every declared lower bound and dimension length. Arrays of structures remain expanded so their readable fields are still discoverable.

Arrays of `String`, `Date_And_Time`, and packed `DTL` values support both complete-array and indexed symbolic reads. `DTL` arrays are returned as `PlcTagDTLArray`, including parallel nanosecond and packed-interface-timestamp values.

Use the compatibility option only when individual indexed browse items are required:

```csharp
var elements = await client.BrowseAsync(new S7CommPlusBrowseOptions
{
    ExpandPrimitiveArrayElements = true
});
```

## Bulk Symbol Resolution and Comments

`GetTagsBySymbolsAsync(symbols)` resolves a configured tag set with one catalog browse and returns a case-sensitive dictionary of the symbols found. It also resolves fully indexed primitive-array elements from aggregate array metadata. The catalog is retained across reconnects; call `InvalidateSymbolCatalogAsync()` after detecting a PLC program-structure change.

`GetSymbolCommentsAsync(relationId)` loads multilingual engineering comments for one data block or absolute I/Q/M area without downloading block code. Use `S7CommPlusSymbolCommentCatalog.TryGetComments(variable, out comments)` to map a browsed `VarInfo` to its LCID-keyed comments, and cache one returned catalog per relation ID while processing a browse.

## CPU Runtime Status and Control

`GetCpuStateAsync()`, `GetCpuCycleTimeAsync()`, and `GetCpuMemoryUsageAsync()` expose current runtime status. `StartCpuAsync()` and `StopCpuAsync()` change PLC state, require `WriteEnabled = true`, and are never retried automatically.

## Subscriptions

Variable and alarm subscriptions are available through disposable production objects:

```csharp
var tag = await client.GetTagBySymbolAsync("MyDb.MyValue");
await using var subscription = await client.SubscribeTagsAsync(new[] { tag });

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
```

Subscriptions share the client connection with normal request/response calls. The driver serializes foreground requests on that physical connection and routes notifications by PLC subscription object id, so reads and metadata requests can run while subscriptions are active without creating another PLC connection. Alarm subscriptions use `SubscribeAlarmsAsync(languageId)` and expose `NotificationReceived`, `CommunicationError`, `StateChanged`, and `Completion` in the same way. `SubscribeAlarmsAsync()` without a language id reads the CPU language catalog and explicitly requests its first three languages, avoiding PLCs that reject an empty all-language subscription filter. `GetActiveAlarmsAsync()` without a language id still requests every alarm text language returned by the PLC. The legacy `AlarmTexts` property contains the selected or first CPU language, and `AlarmTextsByLanguage` contains every language returned for that request. The library does not silently open a second PLC connection for alarm snapshots. If you need a live alarm subscription plus an initial active-alarm snapshot on a separate physical connection, create a second `S7CommPlusClient` yourself and pass it to the `SubscribeAlarmsWithSnapshotAsync(snapshotClient, ...)` overload.

## Low-level PLC Trace Jobs

The driver provides raw TIS trace creation, notification attachment, discovery, activation, deactivation, deletion, and
memory-card measurement access. It does not compile trace definitions or decode configuration/result blobs; use
`TiaFileFormat.S7CommPlus` for that high-level API.

```csharp
await using var subscription = await client.OpenTisTraceAsync(new S7CommPlusTisTraceRequest
{
    JobName = "Raw trace",
    RequestBlob = requestBlob,
    TriggerBlob = triggerBlob,
    InterpretationBlob = interpretationBlob,
    LargeBufferSizeUsed = bufferSize,
    ClientData = optionalClientData,
    UseContinuingJob = true
});

var traces = await client.GetInstalledTracesAsync();
var selected = traces.Single(item => item.Reference.Name == "Raw trace");
await using var attached = await client.AttachTisTraceAsync(selected.Reference);

await client.DeactivateTraceAsync(selected.Reference);
await client.ActivateTraceAsync(selected.Reference);
var stored = await client.GetStoredTraceMeasurementsAsync(includeResultData: true);
await client.DeleteTraceAsync(selected.Reference);
```

`OpenTisTraceAsync` installs and activates the PLC job. Disposing either subscription detaches locally but does not
remove or deactivate the job. Trace mutation and deletion require `WriteEnabled = true`; discovery and attachment do
not. Result buffers are opt-in through `S7CommPlusTraceQueryOptions.IncludeResultData`. Use notification
`IsCompleted` to distinguish a finished recording from an allocated pretrigger buffer.

## PLC Text Lists for Alarms

Alarm text payloads can contain TIA-style text-list placeholders such as `@2%t#519K@`. Load the PLC text-list catalog and pass it to the alarm APIs to resolve those placeholders, including nested system-diagnostic text-list references:

```csharp
var textLists = await client.GetTextListsAsync(); // all CPU LCIDs + language-independent system lists
var alarms = await client.GetActiveAlarmsAsync(1031, textLists);

await using var alarmSubscription = await client.SubscribeAlarmsAsync(1031, textLists);
```

Use `GetTextListsAsync(new[] { 1031, 2057 })` when only selected LCIDs are needed. The catalog remains one API, but each `S7CommPlusTextList` has `TextListType` (`User`, `System`, or `Unknown`) so applications can filter or display the same distinction shown by engineering tools. The online PLC payload does not carry the exact engineering project object kind, so classification follows Siemens runtime list-id conventions; placeholder resolution still uses both user and system lists.

Associated-value placeholders are formatted too. `@2W%d@` means the second associated value, read as a `WORD`, displayed as signed decimal.

Each `S7CommPlusAlarm` exposes `SourceRelationId` and `SourceAlarmId`, decoded from `CpuAlarmId`. For PLC program alarms, `SourceRelationId` can be matched with a separately built online block/catalog model to find the source block.

Communication limits are exposed through `GetCommunicationResourcesAsync()`, including max read/write batch sizes, available PLC subscription slots, and subscription memory.

## Block Metadata and Online View

Block metadata is available through `BrowseBlocksAsync()`, `GetPlcStructureXmlAsync()`, `BrowseBlockStructureAsync()`, and `GetBlockContentAsync(relid)`.

Advanced block-watch scenarios can use `OpenBlockOnlineViewAsync()` with a caller-provided `S7CommPlusTisWatchRequest`; the returned `S7CommPlusTisWatchSubscription` exposes parsed watch notifications and follows the same disposable subscription lifecycle.
