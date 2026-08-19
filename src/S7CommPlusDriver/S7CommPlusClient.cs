using Microsoft.Extensions.Logging;
using S7CommPlusDriver.Alarming;
using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace S7CommPlusDriver
{
    public sealed class S7CommPlusClient : IAsyncDisposable
    {
        private const int DefaultTagsPerRequest = 20;
        private const int DefaultAlarmLanguageCount = 3;
        private const int CpuStopRequest = 1;
        private const int CpuRunRequest = 3;
        private readonly S7CommPlusClientOptions _options;
        private readonly Func<IS7CommPlusSession> _sessionFactory;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly AsyncLocal<TimeSpan?> _operationTimeout = new AsyncLocal<TimeSpan?>();
        private IS7CommPlusSession _session;
        private bool _disposed;
        private S7CommPlusConnectionState _state = S7CommPlusConnectionState.Disconnected;
        private int _tagsPerReadRequestMax = DefaultTagsPerRequest;
        private int _tagsPerWriteRequestMax = DefaultTagsPerRequest;
        private IReadOnlyDictionary<string, VarInfo> _symbolCatalog;

        public S7CommPlusClient(S7CommPlusClientOptions options)
            : this(options, () => new S7CommPlusProtocolSession())
        {
        }

        internal S7CommPlusClient(S7CommPlusClientOptions options, Func<IS7CommPlusSession> sessionFactory)
        {
            _options = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        }

        public event EventHandler<S7CommPlusConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<S7CommPlusCommunicationErrorEventArgs> CommunicationError;

        public S7CommPlusConnectionState State => _state;
        public bool IsConnected => _session?.IsConnected == true && _state == S7CommPlusConnectionState.Connected;
        public S7CommPlusClientOptions Options => _options.Clone();

        /// <summary>
        /// Executes one or more client requests with an operation-specific timeout instead of their configured defaults.
        /// </summary>
        /// <typeparam name="T">The result returned by the client request.</typeparam>
        /// <param name="timeout">The deadline applied to each client request executed by <paramref name="operation"/>.</param>
        /// <param name="operation">Invokes the desired API on this client with the supplied cancellation token.</param>
        /// <param name="cancellationToken">Cancels the operation independently of its timeout.</param>
        /// <returns>The result produced by <paramref name="operation"/>.</returns>
        /// <remarks>
        /// The override flows only through the current asynchronous execution context, so concurrent callers can safely choose
        /// different deadlines. For each request the driver updates the client-side deadline, protocol response wait, and live
        /// transport receive/send timeouts, then restores <see cref="S7CommPlusClientOptions.RequestTimeout"/>. Connect and
        /// disconnect retain their dedicated configured timeouts.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or exceeds the protocol limit.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
        public async Task<T> ExecuteWithTimeoutAsync<T>(
            TimeSpan timeout,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            ValidateOperationTimeout(timeout);
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ThrowIfDisposed();
            var previousTimeout = _operationTimeout.Value;
            _operationTimeout.Value = timeout;
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationTimeout.Value = previousTimeout;
            }
        }

        /// <summary>
        /// Executes one or more client requests with an operation-specific timeout instead of their configured defaults.
        /// </summary>
        /// <param name="timeout">The deadline applied to each client request executed by <paramref name="operation"/>.</param>
        /// <param name="operation">Invokes the desired API on this client with the supplied cancellation token.</param>
        /// <param name="cancellationToken">Cancels the operation independently of its timeout.</param>
        /// <returns>A task that completes when <paramref name="operation"/> finishes.</returns>
        /// <inheritdoc cref="ExecuteWithTimeoutAsync{T}(TimeSpan, Func{CancellationToken, Task{T}}, CancellationToken)" path="remarks"/>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or exceeds the protocol limit.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
        public async Task ExecuteWithTimeoutAsync(
            TimeSpan timeout,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            await ExecuteWithTimeoutAsync(
                timeout,
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ConnectCoreAsync(S7CommPlusConnectionState.Connecting, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>
        /// Browses readable PLC symbols while representing each primitive array as one item with dimension metadata.
        /// </summary>
        /// <param name="cancellationToken">Cancels the browse request and its client-side timeout wait.</param>
        /// <returns>The scalar symbols, aggregate primitive arrays, and expanded members of structure arrays reported by the PLC.</returns>
        public Task<IReadOnlyList<VarInfo>> BrowseAsync(CancellationToken cancellationToken = default)
        {
            return BrowseAsync(new S7CommPlusBrowseOptions(), cancellationToken);
        }

        /// <summary>
        /// Browses readable PLC symbols using an explicit primitive-array representation.
        /// </summary>
        /// <param name="options">
        /// Controls whether primitive arrays are returned once with bounds metadata or flattened into indexed element items.
        /// </param>
        /// <param name="cancellationToken">Cancels the browse request and its client-side timeout wait.</param>
        /// <returns>The browse items produced from the PLC type-information catalog.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public Task<IReadOnlyList<VarInfo>> BrowseAsync(S7CommPlusBrowseOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var expandPrimitiveArrayElements = options.ExpandPrimitiveArrayElements;
            return ExecuteReadOperationAsync("Browse", session =>
            {
                var error = session.BrowseVariables(expandPrimitiveArrayElements, out var vars);
                ThrowIfError("Browse", error);
                return (IReadOnlyList<VarInfo>)(vars ?? new List<VarInfo>());
            }, _options.BrowseTimeout, cancellationToken);
        }

        /// <summary>
        /// Resolves many exact symbolic PLC names from one type-catalog browse instead of issuing one metadata request per symbol.
        /// </summary>
        /// <param name="symbols">Fully qualified PLC symbols to resolve. Duplicate names are resolved only once.</param>
        /// <param name="cancellationToken">Cancels the browse request and its client-side timeout wait.</param>
        /// <returns>
        /// A case-sensitive mapping from every requested symbol found in the current PLC program to a typed, ready-to-read
        /// <see cref="PlcTag"/>. Names absent from the PLC catalog are omitted so callers can handle stale configuration per item.
        /// </returns>
        /// <remarks>
        /// This method is intended for initializing large tag caches after connecting or after the PLC program changes. It preserves
        /// the access sequence, symbol CRC, datatype, and aggregate-array element addresses produced by normal symbolic resolution,
        /// while requiring only one PLC browse operation for the complete requested set. A fully indexed primitive-array symbol is
        /// derived from its aggregate catalog metadata, including declared lower bounds and packed multidimensional BOOL strides.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The collection contains a null, empty, or whitespace-only symbol.</exception>
        public Task<IReadOnlyDictionary<string, PlcTag>> GetTagsBySymbolsAsync(
            IEnumerable<string> symbols,
            CancellationToken cancellationToken = default)
        {
            var requestedSymbols = NormalizeRequestedSymbols(symbols);
            if (requestedSymbols.Count == 0)
            {
                return Task.FromResult<IReadOnlyDictionary<string, PlcTag>>(
                    new Dictionary<string, PlcTag>(StringComparer.Ordinal));
            }

            return ExecuteReadOperationAsync("GetTagsBySymbols", session =>
            {
                var symbolCatalog = GetOrCreateSymbolCatalog(session);
                return ResolveTagsBySymbols(symbolCatalog, requestedSymbols);
            }, _options.BrowseTimeout, cancellationToken);
        }

        /// <summary>
        /// Creates a compact accessor catalog from an existing PLC browse result without retaining the complete symbol metadata.
        /// </summary>
        /// <param name="variables">The aggregate <see cref="BrowseAsync(CancellationToken)"/> result from the matching PLC program.</param>
        /// <param name="symbols">Fully qualified symbols that the compact catalog must cover, including desired missing results.</param>
        /// <param name="programStructureHash">The verified structure hash returned by <see cref="GetPlcStructureXmlAsync(CancellationToken)"/>.</param>
        /// <returns>An immutable catalog that can create fresh mutable accessors and be persisted through its stream API.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">A symbol is invalid.</exception>
        public static S7CommPlusTagAccessorCatalog CreateTagAccessorCatalog(
            IEnumerable<VarInfo> variables,
            IEnumerable<string> symbols,
            string programStructureHash)
        {
            if (variables == null) throw new ArgumentNullException(nameof(variables));
            if (programStructureHash == null) throw new ArgumentNullException(nameof(programStructureHash));
            if (string.IsNullOrWhiteSpace(programStructureHash))
            {
                throw new ArgumentException("A verified PLC program-structure hash is required.", nameof(programStructureHash));
            }

            var requestedSymbols = NormalizeRequestedSymbols(symbols);
            var symbolCatalog = CreateSymbolCatalog(variables);
            var resolvedTags = ResolveTagsBySymbols(symbolCatalog, requestedSymbols);
            return new S7CommPlusTagAccessorCatalog(programStructureHash, requestedSymbols, resolvedTags);
        }

        /// <summary>
        /// Browses the current PLC once and returns compact accessors for a complete configured symbol set without retaining the
        /// full symbol catalog on this client.
        /// </summary>
        /// <param name="symbols">Fully qualified symbols that the compact catalog must cover.</param>
        /// <param name="programStructureHash">The verified PLC structure hash associated with the browse.</param>
        /// <param name="cancellationToken">Cancels the browse and its client-side timeout wait.</param>
        /// <returns>An immutable, persistable accessor catalog containing resolved and known-missing symbols.</returns>
        /// <remarks>
        /// Unlike <see cref="GetTagsBySymbolsAsync(IEnumerable{string}, CancellationToken)"/>, this operation never stores the full
        /// <see cref="VarInfo"/> lookup in the client. It is intended for applications that resolve their complete tag set at once.
        /// </remarks>
        public Task<S7CommPlusTagAccessorCatalog> CreateTagAccessorCatalogAsync(
            IEnumerable<string> symbols,
            string programStructureHash,
            CancellationToken cancellationToken = default)
        {
            if (programStructureHash == null) throw new ArgumentNullException(nameof(programStructureHash));
            if (string.IsNullOrWhiteSpace(programStructureHash))
            {
                throw new ArgumentException("A verified PLC program-structure hash is required.", nameof(programStructureHash));
            }
            var requestedSymbols = NormalizeRequestedSymbols(symbols);
            if (requestedSymbols.Count == 0)
            {
                return Task.FromResult(new S7CommPlusTagAccessorCatalog(
                    programStructureHash,
                    requestedSymbols,
                    new Dictionary<string, PlcTag>(StringComparer.Ordinal)));
            }

            return ExecuteReadOperationAsync("CreateTagAccessorCatalog", session =>
            {
                var error = session.BrowseVariables(false, out var variables);
                ThrowIfError("CreateTagAccessorCatalog", error);
                var symbolCatalog = CreateSymbolCatalog(variables ?? Enumerable.Empty<VarInfo>());
                var resolvedTags = ResolveTagsBySymbols(symbolCatalog, requestedSymbols);
                return new S7CommPlusTagAccessorCatalog(programStructureHash, requestedSymbols, resolvedTags);
            }, _options.BrowseTimeout, cancellationToken);
        }

        /// <summary>
        /// Validates and deduplicates public symbol input while retaining deterministic first-occurrence order.
        /// </summary>
        /// <param name="symbols">The caller-provided symbol sequence.</param>
        /// <returns>A case-sensitive collection containing every distinct requested symbol.</returns>
        private static IReadOnlyCollection<string> NormalizeRequestedSymbols(IEnumerable<string> symbols)
        {
            if (symbols == null) throw new ArgumentNullException(nameof(symbols));

            var requestedSymbols = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    throw new ArgumentException("Symbol collection cannot contain null, empty, or whitespace-only entries.", nameof(symbols));
                }
                requestedSymbols.Add(symbol);
            }
            return requestedSymbols;
        }

        /// <summary>Builds the case-sensitive symbol lookup shared by retained and one-shot catalog resolution.</summary>
        /// <param name="variables">The aggregate PLC browse metadata.</param>
        /// <returns>A lookup containing the first valid browse item for every symbolic name.</returns>
        private static IReadOnlyDictionary<string, VarInfo> CreateSymbolCatalog(IEnumerable<VarInfo> variables)
        {
            var symbolCatalog = new Dictionary<string, VarInfo>(StringComparer.Ordinal);
            foreach (var variable in variables)
            {
                if (variable != null && !string.IsNullOrWhiteSpace(variable.Name))
                {
                    if (!symbolCatalog.ContainsKey(variable.Name))
                    {
                        symbolCatalog.Add(variable.Name, variable);
                    }
                }
            }
            return symbolCatalog;
        }

        /// <summary>Creates mutable tags for exact symbols and indexed aggregate-array elements from one shared lookup.</summary>
        /// <param name="symbolCatalog">The complete case-sensitive PLC symbol lookup.</param>
        /// <param name="requestedSymbols">The normalized symbols to resolve.</param>
        /// <returns>A mapping that omits absent and unsupported symbols.</returns>
        private static IReadOnlyDictionary<string, PlcTag> ResolveTagsBySymbols(
            IReadOnlyDictionary<string, VarInfo> symbolCatalog,
            IEnumerable<string> requestedSymbols)
        {
            var resolvedTags = new Dictionary<string, PlcTag>(StringComparer.Ordinal);
            foreach (var requestedSymbol in requestedSymbols)
            {
                if (symbolCatalog.TryGetValue(requestedSymbol, out var variable))
                {
                    var tag = S7CommPlusProtocolSession.CreateResolvedPlcTag(variable);
                    if (tag != null)
                    {
                        resolvedTags.Add(requestedSymbol, tag);
                    }
                }
                else if (TryResolveAggregateArrayElement(symbolCatalog, requestedSymbol, out var elementTag))
                {
                    resolvedTags.Add(requestedSymbol, elementTag);
                }
            }
            return resolvedTags;
        }

        /// <summary>
        /// Resolves an indexed primitive-array element from its aggregate catalog entry without another PLC metadata request.
        /// </summary>
        /// <param name="symbolCatalog">The retained aggregate PLC symbol catalog.</param>
        /// <param name="requestedSymbol">The fully indexed scalar symbol requested by the caller.</param>
        /// <param name="tag">Receives a scalar tag when the parent array and indices are valid.</param>
        /// <returns><see langword="true"/> when the requested symbol identifies one declared primitive-array element.</returns>
        private static bool TryResolveAggregateArrayElement(
            IReadOnlyDictionary<string, VarInfo> symbolCatalog,
            string requestedSymbol,
            out PlcTag tag)
        {
            tag = null;
            var indexStart = requestedSymbol.LastIndexOf('[');
            if (indexStart <= 0 || requestedSymbol[requestedSymbol.Length - 1] != ']')
            {
                return false;
            }

            var aggregateSymbol = requestedSymbol.Substring(0, indexStart);
            return symbolCatalog.TryGetValue(aggregateSymbol, out var aggregateVariable)
                && S7CommPlusProtocolSession.TryCreateResolvedPrimitiveArrayElement(
                    aggregateVariable,
                    requestedSymbol,
                    requestedSymbol.Substring(indexStart + 1, requestedSymbol.Length - indexStart - 2),
                    out tag);
        }

        /// <summary>
        /// Discards the retained PLC symbol metadata after the caller detects that the controller program structure changed.
        /// </summary>
        /// <param name="cancellationToken">Cancels waiting for another client operation to finish.</param>
        /// <returns>A task that completes after subsequent bulk resolutions are forced to browse the PLC again.</returns>
        /// <remarks>
        /// Normal disconnects and reconnects intentionally retain the catalog because access metadata remains valid while the PLC
        /// program is unchanged. Call this method when a program-structure hash changes, before rebuilding cached <see cref="PlcTag"/>
        /// accessors. The next <see cref="GetTagsBySymbolsAsync(IEnumerable{string}, CancellationToken)"/> call then refreshes the catalog.
        /// </remarks>
        public async Task InvalidateSymbolCatalogAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _symbolCatalog = null;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public Task<IReadOnlyList<S7CommPlusBlockInfo>> BrowseBlocksAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("BrowseBlocks", session =>
            {
                var error = session.BrowseBlocks(out var blocks);
                ThrowIfError("BrowseBlocks", error);
                return (IReadOnlyList<S7CommPlusBlockInfo>)(blocks ?? new List<S7CommPlusBlockInfo>());
            }, cancellationToken);
        }

        public Task<S7CommPlusPlcStructureSnapshot> GetPlcStructureXmlAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetPlcStructureXml", session =>
            {
                var error = session.GetPlcStructureXml(out var structure);
                ThrowIfError("GetPlcStructureXml", error);
                return structure ?? PlcStructureXmlParser.CreateSnapshot(string.Empty);
            }, cancellationToken);
        }

        public Task<IReadOnlyList<S7CommPlusPlcStructureNode>> BrowseBlockStructureAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("BrowseBlockStructure", session =>
            {
                var error = session.GetPlcStructureXml(out var structure);
                ThrowIfError("BrowseBlockStructure", error);
                try
                {
                    return structure?.Structure ?? Array.Empty<S7CommPlusPlcStructureNode>();
                }
                catch (Exception ex)
                {
                    throw new S7CommPlusConnectionException(
                        "BrowseBlockStructure",
                        Endpoint,
                        S7Consts.errIsoInvalidPDU,
                        false,
                        $"BrowseBlockStructure failed for PLC {Endpoint}: PLC structure XML could not be parsed.",
                        ex);
                }
            }, cancellationToken);
        }

        public Task<S7CommPlusClientBlockContent> GetBlockContentAsync(uint relid, CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetBlockContent", session =>
            {
                var error = session.GetBlockContent(relid, out var blockContent);
                ThrowIfError("GetBlockContent", error);
                return blockContent;
            }, cancellationToken);
        }

        /// <summary>
        /// Retrieves multilingual engineering comments for one data block or absolute I/Q/M area without downloading block code.
        /// </summary>
        /// <param name="relationId">
        /// The relation ID obtained from a browsed variable access sequence or <see cref="BrowseBlocksAsync(System.Threading.CancellationToken)"/>.
        /// Absolute input, output, and marker areas use relation IDs <c>0x50</c>, <c>0x51</c>, and <c>0x52</c> respectively.
        /// </param>
        /// <param name="cancellationToken">Cancels the PLC request, decompression, and client-side timeout wait.</param>
        /// <returns>A catalog that can resolve comments for the <see cref="VarInfo"/> values returned by the same PLC program.</returns>
        /// <remarks>
        /// The request is intentionally narrower than <see cref="GetBlockContentAsync(uint, System.Threading.CancellationToken)"/>:
        /// it downloads line comments and the DB interface needed for path translation, but omits block bodies, executable code,
        /// debug data, and network metadata. Callers should cache the returned catalog per relation ID while processing one browse.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="relationId"/> is zero.</exception>
        public Task<S7CommPlusSymbolCommentCatalog> GetSymbolCommentsAsync(
            uint relationId,
            CancellationToken cancellationToken = default)
        {
            if (relationId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(relationId), "A PLC block or area relation ID is required.");
            }

            return ExecuteReadOperationAsync("GetSymbolComments", session =>
            {
                try
                {
                    var error = session.GetSymbolComments(relationId, out var comments);
                    ThrowIfError("GetSymbolComments", error);
                    return comments;
                }
                catch (S7CommPlusException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new S7CommPlusConnectionException(
                        "GetSymbolComments",
                        Endpoint,
                        S7Consts.errIsoInvalidPDU,
                        false,
                        $"GetSymbolComments failed for PLC {Endpoint}: PLC comment metadata could not be parsed.",
                        exception);
                }
            }, _options.BrowseTimeout, cancellationToken);
        }

        public Task<S7CommPlusCpuInfo> GetCpuInfoAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetCpuInfo", session =>
            {
                var error = session.GetCpuInfo(out var cpuInfo);
                ThrowIfError("GetCpuInfo", error);
                return cpuInfo;
            }, cancellationToken);
        }

        public Task<S7CommPlusCpuState> GetCpuStateAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetCpuState", session =>
            {
                var error = session.GetCpuState(out var cpuState);
                ThrowIfError("GetCpuState", error);
                return cpuState;
            }, cancellationToken);
        }

        public Task<S7CommPlusCpuCycleTime> GetCpuCycleTimeAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetCpuCycleTime", session =>
            {
                var error = session.GetCpuCycleTime(out var cycleTime);
                ThrowIfError("GetCpuCycleTime", error);
                return cycleTime;
            }, cancellationToken);
        }

        public Task<S7CommPlusCpuMemoryUsage> GetCpuMemoryUsageAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetCpuMemoryUsage", session =>
            {
                var error = session.GetCpuMemoryUsage(out var memoryUsage);
                ThrowIfError("GetCpuMemoryUsage", error);
                return memoryUsage;
            }, cancellationToken);
        }

        public Task StopCpuAsync(CancellationToken cancellationToken = default)
        {
            return SetCpuOperatingStateAsync("StopCpu", CpuStopRequest, cancellationToken);
        }

        public Task StartCpuAsync(CancellationToken cancellationToken = default)
        {
            return SetCpuOperatingStateAsync("StartCpu", CpuRunRequest, cancellationToken);
        }

        public Task<S7CommPlusCpuCultureInfo> GetCpuCultureInfoAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetCpuCultureInfo", session =>
            {
                var error = session.GetCpuCultureInfo(out var cultureInfo);
                ThrowIfError("GetCpuCultureInfo", error);
                return cultureInfo;
            }, cancellationToken);
        }

        /// <summary>
        /// Reads PLC text lists for all CPU languages, plus language-independent system text lists.
        /// </summary>
        public Task<S7CommPlusTextListCatalog> GetTextListsAsync(CancellationToken cancellationToken = default)
        {
            return GetTextListsAsync(Array.Empty<int>(), cancellationToken);
        }

        /// <summary>
        /// Reads PLC text lists for the requested LCIDs, plus language-independent system text lists.
        /// Pass an empty collection to request all CPU languages.
        /// </summary>
        public Task<S7CommPlusTextListCatalog> GetTextListsAsync(IEnumerable<int> languageIds, CancellationToken cancellationToken = default)
        {
            var languageIdList = languageIds?.ToList() ?? new List<int>();
            if (languageIdList.Any(languageId => languageId < 0 || languageId > UInt16.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(languageIds), "Language ids must be positive LCID values.");
            }

            return ExecuteReadOperationAsync("GetTextLists", session =>
            {
                var error = session.GetTextLists(languageIdList, out var textLists);
                ThrowIfError("GetTextLists", error);
                return textLists ?? S7CommPlusTextListCatalog.Empty;
            }, cancellationToken);
        }

        public Task<S7CommPlusCommunicationResources> GetCommunicationResourcesAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadOperationAsync("GetCommunicationResources", session =>
            {
                var error = session.GetCommunicationResources(out var resources);
                ThrowIfError("GetCommunicationResources", error);
                var result = new S7CommPlusCommunicationResources(resources);
                _tagsPerReadRequestMax = Math.Max(1, result.TagsPerReadRequestMax);
                _tagsPerWriteRequestMax = Math.Max(1, result.TagsPerWriteRequestMax);
                return result;
            }, cancellationToken);
        }

        public async Task LegitimateAsync(string password, string username = "", CancellationToken cancellationToken = default)
        {
            if (password == null)
            {
                throw new ArgumentNullException(nameof(password));
            }
            username ??= string.Empty;

            await ExecuteSessionOperationAsync("Legitimate", session =>
            {
                var error = session.Legitimate(password, username);
                ThrowIfError("Legitimate", error);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the currently active PLC alarms and requests all alarm text languages returned by the PLC.
        /// The legacy <see cref="S7CommPlusAlarm.AlarmTexts"/> property contains the first returned language;
        /// use <see cref="S7CommPlusAlarm.AlarmTextsByLanguage"/> to access every returned language.
        /// </summary>
        public Task<IReadOnlyList<S7CommPlusAlarm>> GetActiveAlarmsAsync(CancellationToken cancellationToken = default)
        {
            return GetActiveAlarmsCoreAsync(0, cancellationToken);
        }

        /// <summary>
        /// Reads the currently active PLC alarms and selects the requested alarm text language from the returned text payload.
        /// </summary>
        /// <param name="languageId">LCID to expose through <see cref="S7CommPlusAlarm.AlarmTexts"/>, for example 1031 for de-DE.</param>
        public Task<IReadOnlyList<S7CommPlusAlarm>> GetActiveAlarmsAsync(int languageId, CancellationToken cancellationToken = default)
        {
            return GetActiveAlarmsAsync(languageId, null, cancellationToken);
        }

        /// <summary>
        /// Reads active PLC alarms and resolves text-list placeholders using a catalog returned by <see cref="GetTextListsAsync(CancellationToken)"/>.
        /// </summary>
        public Task<IReadOnlyList<S7CommPlusAlarm>> GetActiveAlarmsAsync(S7CommPlusTextListCatalog textLists, CancellationToken cancellationToken = default)
        {
            return GetActiveAlarmsCoreAsync(0, CreateTextListResolver(textLists), cancellationToken);
        }

        /// <summary>
        /// Reads active PLC alarms for one LCID and resolves text-list placeholders using a catalog returned by <see cref="GetTextListsAsync(CancellationToken)"/>.
        /// </summary>
        public Task<IReadOnlyList<S7CommPlusAlarm>> GetActiveAlarmsAsync(int languageId, S7CommPlusTextListCatalog textLists, CancellationToken cancellationToken = default)
        {
            if (languageId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(languageId), "Language ids must be positive LCID values.");
            }

            return GetActiveAlarmsCoreAsync(languageId, CreateTextListResolver(textLists), cancellationToken);
        }

        private Task<IReadOnlyList<S7CommPlusAlarm>> GetActiveAlarmsCoreAsync(int alarmTextLanguageId, CancellationToken cancellationToken)
        {
            return GetActiveAlarmsCoreAsync(alarmTextLanguageId, null, cancellationToken);
        }

        private Task<IReadOnlyList<S7CommPlusAlarm>> GetActiveAlarmsCoreAsync(int alarmTextLanguageId, Func<string, long, int, string> textListResolver, CancellationToken cancellationToken)
        {
            return ExecuteReadOperationAsync("GetActiveAlarms", session =>
            {
                var error = session.GetActiveAlarms(out var alarmList, alarmTextLanguageId, textListResolver);
                ThrowIfError("GetActiveAlarms", error);
                return (IReadOnlyList<S7CommPlusAlarm>)((alarmList ?? new List<S7CommPlusAlarm>()).Where(alarm => alarm != null).ToList());
            }, cancellationToken);
        }

        public async Task<S7CommPlusTagSubscription> SubscribeTagsAsync(IEnumerable<PlcTag> tags, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }

            var tagList = tags.ToList();
            if (tagList.Count == 0)
            {
                throw new ArgumentException("At least one tag is required.", nameof(tags));
            }
            if (tagList.Any(tag => tag == null))
            {
                throw new ArgumentException("Tag list cannot contain null entries.", nameof(tags));
            }

            var subscriptionOptions = (options ?? new S7CommPlusSubscriptionOptions()).Clone();
            subscriptionOptions.Validate(requireCycleTime: true);
            var tagsByReferenceId = tagList
                .Select((tag, index) => new KeyValuePair<uint, PlcTag>((uint)(index + 1), tag))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var subscription = new S7CommPlusTagSubscription(tagsByReferenceId);
            var operationTimeout = _operationTimeout.Value ?? _options.RequestTimeout;

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsureConnectedCoreAsync(cancellationToken).ConfigureAwait(false);
                uint subscriptionObjectId = 0;
                var error = await RunOperationAttemptAsync(
                    "CreateTagSubscription",
                    session => session.CreateTagSubscription(tagList, subscriptionOptions.CycleTimeMilliseconds, subscriptionOptions.InitialCreditLimit, out subscriptionObjectId),
                    operationTimeout,
                    cancellationToken).ConfigureAwait(false);
                ThrowIfError("CreateTagSubscription", error);

                subscription.Start(token => RunTagSubscriptionLoopAsync(subscription, subscriptionOptions, subscriptionObjectId, token));
                return subscription;
            }
            catch (S7CommPlusException ex)
            {
                RaiseCommunicationError(ex);
                if (ex.IsTransient || ex is S7CommPlusTisWatchUnavailableException)
                {
                    SetState(S7CommPlusConnectionState.Faulted, ex);
                }
                subscription.MarkFaulted(ex);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task<S7CommPlusTisWatchSubscription> OpenBlockOnlineViewAsync(S7CommPlusTisWatchRequest request, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            request.Validate();
            var watchRequest = request.Clone();
            var subscriptionOptions = (options ?? new S7CommPlusSubscriptionOptions()).Clone();
            subscriptionOptions.Validate(requireCycleTime: false);
            var subscription = new S7CommPlusTisWatchSubscription(watchRequest.ResultModel);
            var operationTimeout = _operationTimeout.Value ?? _options.RequestTimeout;

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsureConnectedCoreAsync(cancellationToken).ConfigureAwait(false);
                uint subscriptionObjectId = 0;
                var error = await RunOperationAttemptAsync(
                    "CreateTisWatchSubscription",
                    session => session.CreateTisWatchSubscription(watchRequest, out subscriptionObjectId),
                    operationTimeout,
                    cancellationToken).ConfigureAwait(false);
                var operation = String.IsNullOrWhiteSpace(watchRequest.LastLifecycleStage)
                    ? "CreateTisWatchSubscription"
                    : $"CreateTisWatchSubscription ({watchRequest.LastLifecycleStage})";
                ThrowIfError(operation, error);

                subscription.Start(token => RunTisWatchSubscriptionLoopAsync(subscription, subscriptionOptions, subscriptionObjectId, token));
                return subscription;
            }
            catch (S7CommPlusException ex)
            {
                RaiseCommunicationError(ex);
                if (ex.IsTransient || ex is S7CommPlusTisWatchUnavailableException)
                {
                    SetState(S7CommPlusConnectionState.Faulted, ex);
                }
                subscription.MarkFaulted(ex);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>
        /// Creates a live alarm subscription for the first three languages advertised by the CPU.
        /// </summary>
        public Task<S7CommPlusAlarmSubscription> SubscribeAlarmsAsync(S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return SubscribeAlarmsAsync(Array.Empty<int>(), 0, options, cancellationToken);
        }

        /// <summary>
        /// Creates a live alarm subscription and requests alarm texts for one LCID.
        /// </summary>
        public Task<S7CommPlusAlarmSubscription> SubscribeAlarmsAsync(int languageId, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return SubscribeAlarmsAsync(new[] { languageId }, languageId, options, cancellationToken);
        }

        /// <summary>
        /// Creates a live alarm subscription for one LCID and resolves text-list placeholders using a catalog returned by <see cref="GetTextListsAsync(CancellationToken)"/>.
        /// </summary>
        public Task<S7CommPlusAlarmSubscription> SubscribeAlarmsAsync(int languageId, S7CommPlusTextListCatalog textLists, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return SubscribeAlarmsAsync(new[] { languageId }, languageId, textLists, options, cancellationToken);
        }

        /// <summary>
        /// Creates a live alarm subscription first, then uses the supplied separate snapshot client to read the
        /// initially active alarms with the CPU's first three alarm languages. Early live notifications are buffered by the subscription.
        /// </summary>
        public Task<S7CommPlusAlarmSubscriptionWithSnapshot> SubscribeAlarmsWithSnapshotAsync(S7CommPlusClient snapshotClient, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return SubscribeAlarmsWithSnapshotAsync(snapshotClient, Array.Empty<int>(), 0, options, cancellationToken);
        }

        /// <summary>
        /// Creates a live alarm subscription first, then uses the supplied separate snapshot client to read the
        /// initially active alarms for the requested LCID. Early live notifications are buffered by the subscription.
        /// </summary>
        public Task<S7CommPlusAlarmSubscriptionWithSnapshot> SubscribeAlarmsWithSnapshotAsync(S7CommPlusClient snapshotClient, int languageId, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return SubscribeAlarmsWithSnapshotAsync(snapshotClient, new[] { languageId }, languageId, options, cancellationToken);
        }

        /// <summary>
        /// Creates a live alarm subscription for one LCID, reads initially active alarms, and resolves text-list placeholders using a catalog returned by <see cref="GetTextListsAsync(CancellationToken)"/>.
        /// </summary>
        public Task<S7CommPlusAlarmSubscriptionWithSnapshot> SubscribeAlarmsWithSnapshotAsync(S7CommPlusClient snapshotClient, int languageId, S7CommPlusTextListCatalog textLists, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return SubscribeAlarmsWithSnapshotAsync(snapshotClient, new[] { languageId }, languageId, textLists, options, cancellationToken);
        }

        /// <summary>
        /// Creates a live alarm subscription first, then uses the supplied separate snapshot client to read the
        /// initially active alarms. An empty language collection selects the first three languages advertised by the CPU.
        /// </summary>
        public async Task<S7CommPlusAlarmSubscriptionWithSnapshot> SubscribeAlarmsWithSnapshotAsync(S7CommPlusClient snapshotClient, IEnumerable<int> languageIds, int alarmTextLanguageId, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return await SubscribeAlarmsWithSnapshotAsync(snapshotClient, languageIds, alarmTextLanguageId, null, options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a live alarm subscription first, then uses the supplied separate snapshot client to read the
        /// initially active alarms. An empty language collection selects the first three languages advertised by the CPU.
        /// Text-list placeholders are resolved through the supplied catalog.
        /// </summary>
        public async Task<S7CommPlusAlarmSubscriptionWithSnapshot> SubscribeAlarmsWithSnapshotAsync(S7CommPlusClient snapshotClient, IEnumerable<int> languageIds, int alarmTextLanguageId, S7CommPlusTextListCatalog textLists, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            if (snapshotClient == null)
            {
                throw new ArgumentNullException(nameof(snapshotClient));
            }
            if (ReferenceEquals(this, snapshotClient))
            {
                throw new ArgumentException("The snapshot client must be a separate S7CommPlusClient instance.", nameof(snapshotClient));
            }

            var subscription = await SubscribeAlarmsAsync(languageIds, alarmTextLanguageId, textLists, options, cancellationToken).ConfigureAwait(false);
            try
            {
                var activeAlarms = await snapshotClient.GetActiveAlarmsCoreAsync(
                    subscription.AlarmTextLanguageId,
                    CreateTextListResolver(textLists),
                    cancellationToken).ConfigureAwait(false);
                return new S7CommPlusAlarmSubscriptionWithSnapshot(activeAlarms, subscription);
            }
            catch
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Creates a live alarm subscription. An empty language collection selects the first three languages advertised by the CPU.
        /// The <paramref name="alarmTextLanguageId"/> selects the language exposed through the legacy
        /// <see cref="S7CommPlusAlarm.AlarmTexts"/> property; use 0 to expose the first returned language there and
        /// inspect <see cref="S7CommPlusAlarm.AlarmTextsByLanguage"/> for the full set.
        /// </summary>
        public async Task<S7CommPlusAlarmSubscription> SubscribeAlarmsAsync(IEnumerable<int> languageIds, int alarmTextLanguageId, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            return await SubscribeAlarmsAsync(languageIds, alarmTextLanguageId, null, options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a live alarm subscription. An empty language collection selects the first three languages advertised by the CPU.
        /// Text-list placeholders are resolved through the supplied catalog.
        /// </summary>
        public async Task<S7CommPlusAlarmSubscription> SubscribeAlarmsAsync(IEnumerable<int> languageIds, int alarmTextLanguageId, S7CommPlusTextListCatalog textLists, S7CommPlusSubscriptionOptions options = null, CancellationToken cancellationToken = default)
        {
            var languageIdList = languageIds?.ToList() ?? new List<int>();
            if (languageIdList.Any(languageId => languageId < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(languageIds), "Language ids must be positive LCID values.");
            }
            if (alarmTextLanguageId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(alarmTextLanguageId), "Language ids must be positive LCID values.");
            }

            var subscriptionOptions = (options ?? new S7CommPlusSubscriptionOptions()).Clone();
            subscriptionOptions.Validate(requireCycleTime: false);
            S7CommPlusAlarmSubscription subscription = null;
            var operationTimeout = _operationTimeout.Value ?? _options.RequestTimeout;

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsureConnectedCoreAsync(cancellationToken).ConfigureAwait(false);
                if (languageIdList.Count == 0)
                {
                    S7CommPlusCpuCultureInfo cultureInfo = null;
                    var cultureError = await RunOperationAttemptAsync(
                        "GetCpuCultureInfo",
                        session => session.GetCpuCultureInfo(out cultureInfo),
                        operationTimeout,
                        cancellationToken).ConfigureAwait(false);
                    ThrowIfError("GetCpuCultureInfo", cultureError);
                    languageIdList = cultureInfo?.LanguageIds
                        .Where(languageId => languageId > 0)
                        .Take(DefaultAlarmLanguageCount)
                        .ToList()
                        ?? new List<int>();
                }

                var effectiveAlarmTextLanguageId = alarmTextLanguageId != 0
                    ? alarmTextLanguageId
                    : languageIdList.FirstOrDefault();
                subscription = new S7CommPlusAlarmSubscription(
                    languageIdList,
                    effectiveAlarmTextLanguageId,
                    CreateTextListResolver(textLists));
                var languageIdsUint = languageIdList.Select(languageId => checked((uint)languageId)).ToArray();
                uint subscriptionObjectId = 0;
                var error = await RunOperationAttemptAsync(
                    "CreateAlarmSubscription",
                    session => session.CreateAlarmSubscription(languageIdsUint, subscriptionOptions.InitialCreditLimit, out subscriptionObjectId),
                    operationTimeout,
                    cancellationToken).ConfigureAwait(false);
                ThrowIfAlarmSubscriptionError("CreateAlarmSubscription", error);

                subscription.Start(token => RunAlarmSubscriptionLoopAsync(subscription, subscriptionOptions, subscriptionObjectId, token));
                return subscription;
            }
            catch (S7CommPlusException ex)
            {
                RaiseCommunicationError(ex);
                if (ex.IsTransient)
                {
                    SetState(S7CommPlusConnectionState.Faulted, ex);
                }
                subscription?.MarkFaulted(ex);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public Task<PlcTag> GetTagBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            }

            return ExecuteReadOperationAsync("GetTagBySymbol", session =>
            {
                PlcTag tag;
                try
                {
                    tag = session.GetPlcTagBySymbol(symbol);
                }
                catch (ArgumentException exception)
                {
                    throw new S7CommPlusConnectionException(
                        "GetTagBySymbol",
                        Endpoint,
                        S7Consts.errCliItemNotAvailable,
                        false,
                        $"PLC tag '{symbol}' has invalid symbolic array syntax: {exception.Message}",
                        exception);
                }
                catch (S7CommPlusException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new S7CommPlusConnectionException(
                        "GetTagBySymbol",
                        Endpoint,
                        S7Consts.errCliFunctionRefused,
                        true,
                        $"Unexpected failure while resolving PLC tag '{symbol}': {exception.Message}",
                        exception);
                }
                if (tag == null)
                {
                    throw new S7CommPlusConnectionException("GetTagBySymbol", Endpoint, S7Consts.errCliItemNotAvailable, false, $"PLC tag '{symbol}' could not be resolved.");
                }
                return tag;
            }, cancellationToken);
        }

        public Task<S7CommPlusBatchResult<S7CommPlusReadResult>> ReadAsync(IEnumerable<ItemAddress> addresses, CancellationToken cancellationToken = default)
        {
            if (addresses == null)
            {
                throw new ArgumentNullException(nameof(addresses));
            }

            var addressList = addresses.ToList();
            if (addressList.Count == 0)
            {
                return Task.FromResult(new S7CommPlusBatchResult<S7CommPlusReadResult>(Array.Empty<S7CommPlusReadResult>()));
            }

            return ExecuteReadOperationAsync("Read", session =>
            {
                var error = session.ReadValues(addressList, out var values, out var itemErrors);
                ThrowIfError("Read", error);
                var items = new List<S7CommPlusReadResult>(addressList.Count);
                for (var i = 0; i < addressList.Count; i++)
                {
                    var value = i < values.Count ? values[i] : null;
                    var itemError = i < itemErrors.Count ? itemErrors[i] : ulong.MaxValue;
                    items.Add(new S7CommPlusReadResult(addressList[i], value, itemError));
                }
                return new S7CommPlusBatchResult<S7CommPlusReadResult>(items);
            }, cancellationToken);
        }

        public Task<S7CommPlusBatchResult<S7CommPlusTagReadResult>> ReadAsync(IEnumerable<PlcTag> tags, CancellationToken cancellationToken = default)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }

            var tagList = tags as IReadOnlyList<PlcTag> ?? tags.ToList();
            if (tagList.Any(tag => tag == null))
            {
                throw new ArgumentException("Tag list cannot contain null entries.", nameof(tags));
            }
            if (tagList.Count == 0)
            {
                return Task.FromResult(new S7CommPlusBatchResult<S7CommPlusTagReadResult>(Array.Empty<S7CommPlusTagReadResult>()));
            }

            return ExecuteReadOperationAsync("ReadTags", session =>
            {
                var requestTags = ExpandAggregateTags(tagList);
                var (values, itemErrors) = ReadTagValuesInBatches(session, requestTags);
                var items = new List<S7CommPlusTagReadResult>(tagList.Count);
                var requestIndex = 0;
                foreach (var tag in tagList)
                {
                    var aggregateError = 0UL;
                    if (tag.AggregateElements.Count == 0)
                    {
                        var value = requestIndex < values.Count ? values[requestIndex] : null;
                        var itemError = requestIndex < itemErrors.Count ? itemErrors[requestIndex] : ulong.MaxValue;
                        tag.ProcessReadResult(value, itemError);
                        aggregateError = itemError;
                        requestIndex++;
                    }
                    else
                    {
                        foreach (var elementTag in tag.AggregateElements)
                        {
                            var value = requestIndex < values.Count ? values[requestIndex] : null;
                            var itemError = requestIndex < itemErrors.Count ? itemErrors[requestIndex] : ulong.MaxValue;
                            elementTag.ProcessReadResult(value, itemError);
                            if (aggregateError == 0 && itemError != 0)
                            {
                                aggregateError = itemError;
                            }
                            requestIndex++;
                        }
                        tag.CompleteAggregateRead(aggregateError);
                    }
                    items.Add(new S7CommPlusTagReadResult(tag, aggregateError));
                }
                return new S7CommPlusBatchResult<S7CommPlusTagReadResult>(items);
            }, cancellationToken);
        }

        internal Task<S7CommPlusBatchResult<S7CommPlusWriteResult>> WriteAsync(IEnumerable<ItemAddress> addresses, IEnumerable<PValue> values, CancellationToken cancellationToken = default)
        {
            if (addresses == null)
            {
                throw new ArgumentNullException(nameof(addresses));
            }
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var addressList = addresses.ToList();
            var valueList = values.ToList();
            if (addressList.Count != valueList.Count)
            {
                throw new ArgumentException("Address and value counts must match.", nameof(values));
            }
            if (addressList.Count == 0)
            {
                return Task.FromResult(new S7CommPlusBatchResult<S7CommPlusWriteResult>(Array.Empty<S7CommPlusWriteResult>()));
            }

            return ExecuteWriteOperationAsync("Write", session =>
            {
                var error = session.WriteValues(addressList, valueList, out var itemErrors);
                ThrowIfError("Write", error);
                var items = new List<S7CommPlusWriteResult>(addressList.Count);
                for (var i = 0; i < addressList.Count; i++)
                {
                    var itemError = i < itemErrors.Count ? itemErrors[i] : ulong.MaxValue;
                    items.Add(new S7CommPlusWriteResult(addressList[i], itemError));
                }
                return new S7CommPlusBatchResult<S7CommPlusWriteResult>(items);
            }, cancellationToken);
        }

        public Task<S7CommPlusBatchResult<S7CommPlusWriteResult>> WriteAsync(IEnumerable<PlcTag> tags, CancellationToken cancellationToken = default)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }

            var tagList = tags as IReadOnlyList<PlcTag> ?? tags.ToList();
            if (tagList.Any(tag => tag == null))
            {
                throw new ArgumentException("Tag list cannot contain null entries.", nameof(tags));
            }
            if (tagList.Count == 0)
            {
                return Task.FromResult(new S7CommPlusBatchResult<S7CommPlusWriteResult>(Array.Empty<S7CommPlusWriteResult>()));
            }

            return ExecuteWriteOperationAsync("WriteTags", session =>
            {
                foreach (var tag in tagList)
                {
                    tag.PrepareAggregateWrite();
                }
                var requestTags = ExpandAggregateTags(tagList);
                var itemErrors = WriteTagValuesInBatches(session, requestTags);
                var items = new List<S7CommPlusWriteResult>(tagList.Count);
                var requestIndex = 0;
                foreach (var tag in tagList)
                {
                    var aggregateError = 0UL;
                    if (tag.AggregateElements.Count == 0)
                    {
                        var itemError = requestIndex < itemErrors.Count ? itemErrors[requestIndex] : ulong.MaxValue;
                        aggregateError = itemError;
                        requestIndex++;
                    }
                    else
                    {
                        foreach (var elementTag in tag.AggregateElements)
                        {
                            var itemError = requestIndex < itemErrors.Count ? itemErrors[requestIndex] : ulong.MaxValue;
                            elementTag.ProcessWriteResult(itemError);
                            if (aggregateError == 0 && itemError != 0)
                            {
                                aggregateError = itemError;
                            }
                            requestIndex++;
                        }
                    }
                    tag.ProcessWriteResult(aggregateError);
                    items.Add(new S7CommPlusWriteResult(tag.Address, aggregateError));
                }
                return new S7CommPlusBatchResult<S7CommPlusWriteResult>(items);
            }, cancellationToken);
        }

        /// <summary>
        /// Reads resolved tags in PLC-supported item counts and concatenates the per-item values and errors in request order.
        /// </summary>
        /// <param name="session">The connected protocol session that owns the resolved addresses.</param>
        /// <param name="requestTags">Scalar tags after aggregate arrays have been expanded into their elements.</param>
        /// <returns>All values and item errors in the same order as <paramref name="requestTags"/>.</returns>
        private (List<object> Values, List<ulong> ItemErrors) ReadTagValuesInBatches(
            IS7CommPlusSession session,
            IReadOnlyList<PlcTag> requestTags)
        {
            var values = new List<object>(requestTags.Count);
            var itemErrors = new List<ulong>(requestTags.Count);
            for (var batchStart = 0; batchStart < requestTags.Count; batchStart += _tagsPerReadRequestMax)
            {
                var batchCount = Math.Min(_tagsPerReadRequestMax, requestTags.Count - batchStart);
                var addresses = new List<ItemAddress>(batchCount);
                for (var index = 0; index < batchCount; index++)
                {
                    addresses.Add(requestTags[batchStart + index].Address);
                }

                var error = session.ReadValues(addresses, out var batchValues, out var batchErrors);
                ThrowIfError("ReadTags", error);
                for (var index = 0; index < batchCount; index++)
                {
                    values.Add(index < batchValues.Count ? batchValues[index] : null);
                    itemErrors.Add(index < batchErrors.Count ? batchErrors[index] : ulong.MaxValue);
                }
            }
            return (values, itemErrors);
        }

        /// <summary>
        /// Writes resolved tags in PLC-supported item counts and concatenates item errors in request order.
        /// </summary>
        /// <param name="session">The connected protocol session that owns the resolved addresses.</param>
        /// <param name="requestTags">Scalar tags after aggregate arrays have been expanded and populated.</param>
        /// <returns>All item errors in the same order as <paramref name="requestTags"/>.</returns>
        private List<ulong> WriteTagValuesInBatches(
            IS7CommPlusSession session,
            IReadOnlyList<PlcTag> requestTags)
        {
            var itemErrors = new List<ulong>(requestTags.Count);
            for (var batchStart = 0; batchStart < requestTags.Count; batchStart += _tagsPerWriteRequestMax)
            {
                var batchCount = Math.Min(_tagsPerWriteRequestMax, requestTags.Count - batchStart);
                var addresses = new List<ItemAddress>(batchCount);
                var values = new List<PValue>(batchCount);
                for (var index = 0; index < batchCount; index++)
                {
                    var tag = requestTags[batchStart + index];
                    addresses.Add(tag.Address);
                    values.Add(tag.GetWriteValue());
                }

                var error = session.WriteValues(addresses, values, out var batchErrors);
                ThrowIfError("WriteTags", error);
                for (var index = 0; index < batchCount; index++)
                {
                    itemErrors.Add(index < batchErrors.Count ? batchErrors[index] : ulong.MaxValue);
                }
            }
            return itemErrors;
        }

        /// <summary>Expands aggregate array accessors, retaining the caller's list when every tag is scalar.</summary>
        private static IReadOnlyList<PlcTag> ExpandAggregateTags(IReadOnlyList<PlcTag> tags)
        {
            var expandedCount = tags.Count;
            var hasAggregate = false;
            foreach (var tag in tags)
            {
                if (tag.AggregateElements.Count > 0)
                {
                    hasAggregate = true;
                    expandedCount += tag.AggregateElements.Count - 1;
                }
            }

            if (!hasAggregate)
            {
                return tags;
            }

            var result = new List<PlcTag>(expandedCount);
            foreach (var tag in tags)
            {
                if (tag.AggregateElements.Count == 0)
                {
                    result.Add(tag);
                    continue;
                }

                result.AddRange(tag.AggregateElements);
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _options.Logger.LogWarning(ex, "Error while disposing S7CommPlusClient for {Endpoint}.", Endpoint);
            }
            finally
            {
                _disposed = true;
                _operationGate.Dispose();
            }
        }

        /// <summary>
        /// Returns the retained aggregate symbol catalog or builds it through one PLC metadata browse for the current program.
        /// </summary>
        /// <param name="session">The connected session used only when no catalog has been retained yet.</param>
        /// <returns>A case-sensitive lookup containing the first browse result for every valid symbolic name.</returns>
        /// <remarks>
        /// The catalog deliberately survives reconnects. Its access metadata describes the PLC program rather than a transport
        /// session and is refreshed explicitly through <see cref="InvalidateSymbolCatalogAsync(CancellationToken)"/> when the caller
        /// detects a program-structure change.
        /// </remarks>
        private IReadOnlyDictionary<string, VarInfo> GetOrCreateSymbolCatalog(IS7CommPlusSession session)
        {
            if (_symbolCatalog != null)
            {
                return _symbolCatalog;
            }

            var error = session.BrowseVariables(false, out var variables);
            ThrowIfError("GetTagsBySymbols", error);
            _symbolCatalog = CreateSymbolCatalog(variables ?? Enumerable.Empty<VarInfo>());
            return _symbolCatalog;
        }

        private Task<T> ExecuteReadOperationAsync<T>(string operation, Func<IS7CommPlusSession, T> operationFunc, CancellationToken cancellationToken)
        {
            return ExecuteReadOperationAsync(operation, operationFunc, _options.RequestTimeout, cancellationToken);
        }

        private Task<T> ExecuteReadOperationAsync<T>(string operation, Func<IS7CommPlusSession, T> operationFunc, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return ExecuteOperationAsync(operation, allowReconnect: true, operationFunc, timeout, cancellationToken);
        }

        private Task<T> ExecuteSessionOperationAsync<T>(string operation, Func<IS7CommPlusSession, T> operationFunc, CancellationToken cancellationToken)
        {
            return ExecuteOperationAsync(operation, allowReconnect: false, operationFunc, _options.RequestTimeout, cancellationToken);
        }

        private async Task SetCpuOperatingStateAsync(string operation, int operatingStateRequest, CancellationToken cancellationToken)
        {
            await ExecuteWriteOperationAsync(operation, session =>
            {
                var error = session.SetCpuOperatingState(operatingStateRequest);
                ThrowIfError(operation, error);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private Task<T> ExecuteWriteOperationAsync<T>(string operation, Func<IS7CommPlusSession, T> operationFunc, CancellationToken cancellationToken)
        {
            if (!_options.WriteEnabled)
            {
                throw new S7CommPlusWriteDisabledException(Endpoint);
            }
            return ExecuteOperationAsync(operation, allowReconnect: false, operationFunc, _options.RequestTimeout, cancellationToken);
        }

        private async Task<T> ExecuteOperationAsync<T>(string operation, bool allowReconnect, Func<IS7CommPlusSession, T> operationFunc, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var effectiveTimeout = _operationTimeout.Value ?? timeout;
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureConnectedCoreAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await RunOperationAttemptAsync(operation, operationFunc, effectiveTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (S7CommPlusException ex) when (allowReconnect && _options.AutoReconnect && ex.IsTransient)
                {
                    RaiseCommunicationError(ex);
                    _options.Logger.LogWarning(ex, "Transient {Operation} failure for {Endpoint}; reconnecting and retrying once.", operation, Endpoint);
                    await ReconnectCoreAsync(cancellationToken).ConfigureAwait(false);
                    return await RunOperationAttemptAsync(operation, operationFunc, effectiveTimeout, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (S7CommPlusException ex)
            {
                RaiseCommunicationError(ex);
                if (ex.IsTransient)
                {
                    SetState(S7CommPlusConnectionState.Faulted, ex);
                }
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>
        /// Applies one request deadline to the protocol and transport for a serialized operation attempt, then restores the configured default.
        /// </summary>
        private async Task<T> RunOperationAttemptAsync<T>(
            string operation,
            Func<IS7CommPlusSession, T> operationFunc,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var session = _session;
            var previousTimeoutMilliseconds = session.RequestTimeoutMilliseconds;
            var timeoutMilliseconds = ValidateOperationTimeout(timeout);
            if (previousTimeoutMilliseconds != timeoutMilliseconds)
            {
                session.SetRequestTimeout(timeoutMilliseconds);
            }

            try
            {
                return await RunWithTimeoutAsync(operation, () => operationFunc(session), timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_session, session) && previousTimeoutMilliseconds != timeoutMilliseconds)
                {
                    session.SetRequestTimeout(previousTimeoutMilliseconds);
                }
            }
        }

        /// <summary>
        /// Converts a public operation timeout to the positive millisecond range supported by protocol and socket APIs.
        /// </summary>
        private static int ValidateOperationTimeout(TimeSpan timeout)
        {
            var totalMilliseconds = timeout.TotalMilliseconds;
            if (double.IsNaN(totalMilliseconds)
                || double.IsInfinity(totalMilliseconds)
                || totalMilliseconds <= 0
                || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"Timeout must be between 1 ms and {int.MaxValue} ms.");
            }

            return Math.Max(1, (int)Math.Ceiling(totalMilliseconds));
        }

        private async Task EnsureConnectedCoreAsync(CancellationToken cancellationToken)
        {
            if (_session?.IsConnected == true && _state == S7CommPlusConnectionState.Connected)
            {
                return;
            }

            await ConnectCoreAsync(S7CommPlusConnectionState.Connecting, cancellationToken).ConfigureAwait(false);
        }

        private async Task ConnectCoreAsync(S7CommPlusConnectionState connectingState, CancellationToken cancellationToken)
        {
            if (_session?.IsConnected == true && _state == S7CommPlusConnectionState.Connected)
            {
                return;
            }

            SetState(connectingState);
            _session = _sessionFactory();
            _options.Logger.LogInformation("Connecting to PLC {Endpoint}.", Endpoint);

            var connectOperationTimeout = _options.SecurityMode == S7CommPlusSecurityMode.Auto
                ? TimeSpan.FromTicks(_options.ConnectTimeout.Ticks * 2)
                : _options.ConnectTimeout;
            var error = await RunWithTimeoutAsync("Connect", () => _session.Connect(_options), connectOperationTimeout, cancellationToken).ConfigureAwait(false);
            if (error != 0)
            {
                var exception = CreateException("Connect", error);
                _session = null;
                throw exception;
            }

            SetState(S7CommPlusConnectionState.Connected);
            _tagsPerReadRequestMax = DefaultTagsPerRequest;
            _tagsPerWriteRequestMax = DefaultTagsPerRequest;
            _options.Logger.LogInformation("Connected to PLC {Endpoint}.", Endpoint);
        }

        private async Task ReconnectCoreAsync(CancellationToken cancellationToken)
        {
            SetState(S7CommPlusConnectionState.Reconnecting);
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
            await ConnectCoreAsync(S7CommPlusConnectionState.Reconnecting, cancellationToken).ConfigureAwait(false);
        }

        private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            if (_session == null && _state == S7CommPlusConnectionState.Disconnected)
            {
                return;
            }

            SetState(S7CommPlusConnectionState.Disconnecting);
            var session = _session;
            _session = null;
            if (session != null)
            {
                try
                {
                    var error = await RunWithTimeoutAsync("Disconnect", () => session.Disconnect(_options.DisconnectTimeoutMilliseconds), _options.DisconnectTimeout, cancellationToken).ConfigureAwait(false);
                    if (error != 0)
                    {
                        var exception = CreateException("Disconnect", error);
                        RaiseCommunicationError(exception);
                        _options.Logger.LogWarning("PLC disconnect for {Endpoint} returned error {ErrorCode}.", Endpoint, error);
                    }
                }
                catch (S7CommPlusException ex)
                {
                    RaiseCommunicationError(ex);
                    _options.Logger.LogWarning(ex, "PLC disconnect for {Endpoint} failed or timed out.", Endpoint);
                }
            }
            SetState(S7CommPlusConnectionState.Disconnected);
        }

        private async Task RunTagSubscriptionLoopAsync(S7CommPlusTagSubscription subscription, S7CommPlusSubscriptionOptions subscriptionOptions, uint subscriptionObjectId, CancellationToken cancellationToken)
        {
            try
            {
                await RunSubscriptionLoopAsync(
                    "WaitForTagSubscriptionNotifications",
                    subscription,
                    subscriptionOptions,
                    waitFunc: () =>
                    {
                        var error = _session.WaitForTagSubscriptionNotifications(
                            subscriptionObjectId,
                            subscriptionOptions.NotificationTimeoutMilliseconds,
                            subscriptionOptions.CreditLimitStep,
                            out var notifications);
                        return (error, notifications);
                    },
                    publish: notification => subscription.Publish(notification)).ConfigureAwait(false);
            }
            finally
            {
                await TryDeleteSubscriptionAsync("DeleteTagSubscription", subscription, subscriptionOptions, () => _session.DeleteTagSubscription(subscriptionObjectId)).ConfigureAwait(false);
            }
        }

        private async Task RunAlarmSubscriptionLoopAsync(S7CommPlusAlarmSubscription subscription, S7CommPlusSubscriptionOptions subscriptionOptions, uint subscriptionObjectId, CancellationToken cancellationToken)
        {
            try
            {
                await RunSubscriptionLoopAsync(
                    "WaitForAlarmNotifications",
                    subscription,
                    subscriptionOptions,
                    waitFunc: () =>
                    {
                        var error = _session.WaitForAlarmNotifications(
                            subscriptionObjectId,
                            subscriptionOptions.NotificationTimeoutMilliseconds,
                            subscriptionOptions.CreditLimitStep,
                            out var notifications);
                        return (error, notifications);
                    },
                    publish: notification => subscription.Publish(notification)).ConfigureAwait(false);
            }
            finally
            {
                await TryDeleteSubscriptionAsync("DeleteAlarmSubscription", subscription, subscriptionOptions, () => _session.DeleteAlarmSubscription(subscriptionObjectId)).ConfigureAwait(false);
            }
        }

        private async Task RunTisWatchSubscriptionLoopAsync(S7CommPlusTisWatchSubscription subscription, S7CommPlusSubscriptionOptions subscriptionOptions, uint subscriptionObjectId, CancellationToken cancellationToken)
        {
            try
            {
                await RunTisWatchLoopAsync(
                    "WaitForTisWatchNotifications",
                    subscription,
                    subscriptionOptions,
                    waitFunc: () =>
                    {
                        var error = _session.WaitForTisWatchNotifications(
                            subscriptionObjectId,
                            subscriptionOptions.NotificationTimeoutMilliseconds,
                            out var notifications);
                        return (error, notifications);
                    },
                    publish: notification => subscription.Publish(notification)).ConfigureAwait(false);
            }
            finally
            {
                await TryDeleteSubscriptionAsync("DeleteTisWatchSubscription", subscription, subscriptionOptions, () => _session.DeleteTisWatchSubscription(subscriptionObjectId)).ConfigureAwait(false);
            }
        }

        private async Task RunSubscriptionLoopAsync(
            string operation,
            S7CommPlusSubscription subscription,
            S7CommPlusSubscriptionOptions subscriptionOptions,
            Func<(int Error, List<Notification> Notifications)> waitFunc,
            Action<Notification> publish)
        {
            var consecutiveTimeouts = 0;
            while (!subscription.IsStopRequested)
            {
                try
                {
                    var result = await RunWithTimeoutAsync(
                        operation,
                        waitFunc,
                        subscriptionOptions.NotificationTimeout + TimeSpan.FromSeconds(1),
                        CancellationToken.None).ConfigureAwait(false);

                    if (result.Error == S7Consts.errCliJobTimeout || result.Error == S7Consts.errTCPReceiveTimeout)
                    {
                        consecutiveTimeouts++;
                        if (subscriptionOptions.MaxConsecutiveTimeoutsBeforeFault > 0
                            && consecutiveTimeouts >= subscriptionOptions.MaxConsecutiveTimeoutsBeforeFault)
                        {
                            throw CreateException(operation, result.Error);
                        }
                        continue;
                    }

                    ThrowIfError(operation, result.Error);
                    consecutiveTimeouts = 0;

                    foreach (var notification in result.Notifications ?? Enumerable.Empty<Notification>())
                    {
                        publish(notification);
                    }
                }
                catch (S7CommPlusException ex)
                {
                    RaiseCommunicationError(ex);
                    if (ex.IsTransient)
                    {
                        SetState(S7CommPlusConnectionState.Faulted, ex);
                    }
                    subscription.MarkFaulted(ex);
                    throw;
                }
                catch (Exception ex)
                {
                    var wrapped = new S7CommPlusConnectionException(operation, Endpoint, S7Consts.errCliFunctionRefused, true, $"{operation} failed for PLC {Endpoint}.", ex);
                    RaiseCommunicationError(wrapped);
                    SetState(S7CommPlusConnectionState.Faulted, wrapped);
                    subscription.MarkFaulted(wrapped);
                    throw wrapped;
                }
            }
        }

        private async Task RunTisWatchLoopAsync(
            string operation,
            S7CommPlusSubscription subscription,
            S7CommPlusSubscriptionOptions subscriptionOptions,
            Func<(int Error, List<S7CommPlusTisWatchNotification> Notifications)> waitFunc,
            Action<S7CommPlusTisWatchNotification> publish)
        {
            var consecutiveTimeouts = 0;
            while (!subscription.IsStopRequested)
            {
                try
                {
                    var result = await RunWithTimeoutAsync(
                        operation,
                        waitFunc,
                        subscriptionOptions.NotificationTimeout + _options.RequestTimeout + TimeSpan.FromSeconds(1),
                        CancellationToken.None).ConfigureAwait(false);

                    if (result.Error == S7Consts.errCliJobTimeout || result.Error == S7Consts.errTCPReceiveTimeout)
                    {
                        consecutiveTimeouts++;
                        if (subscriptionOptions.MaxConsecutiveTimeoutsBeforeFault > 0
                            && consecutiveTimeouts >= subscriptionOptions.MaxConsecutiveTimeoutsBeforeFault)
                        {
                            throw CreateTisWatchException(operation, result.Error);
                        }
                        continue;
                    }

                    ThrowIfTisWatchError(operation, result.Error);
                    consecutiveTimeouts = 0;

                    foreach (var notification in result.Notifications ?? Enumerable.Empty<S7CommPlusTisWatchNotification>())
                    {
                        publish(notification);
                    }
                }
                catch (S7CommPlusException ex)
                {
                    RaiseCommunicationError(ex);
                    if (ex.IsTransient)
                    {
                        SetState(S7CommPlusConnectionState.Faulted, ex);
                    }
                    subscription.MarkFaulted(ex);
                    throw;
                }
                catch (Exception ex)
                {
                    var wrapped = new S7CommPlusConnectionException(operation, Endpoint, S7Consts.errCliFunctionRefused, true, $"{operation} failed for PLC {Endpoint}.", ex);
                    RaiseCommunicationError(wrapped);
                    SetState(S7CommPlusConnectionState.Faulted, wrapped);
                    subscription.MarkFaulted(wrapped);
                    throw wrapped;
                }
            }
        }

        private void ThrowIfTisWatchError(string operation, int errorCode)
        {
            if (errorCode == 0)
            {
                return;
            }

            throw CreateTisWatchException(operation, errorCode);
        }

        private S7CommPlusException CreateTisWatchException(string operation, int errorCode)
        {
            var diagnostic = _session?.LastTisWatchDiagnostic;
            var effectiveOperation = string.IsNullOrWhiteSpace(diagnostic)
                ? operation
                : $"{operation}: {diagnostic}";
            return CreateException(effectiveOperation, errorCode);
        }

        private void ThrowIfAlarmSubscriptionError(string operation, int errorCode)
        {
            if (errorCode == 0)
            {
                return;
            }

            throw CreateAlarmSubscriptionException(operation, errorCode);
        }

        private S7CommPlusException CreateAlarmSubscriptionException(string operation, int errorCode)
        {
            var diagnostic = _session?.LastAlarmSubscriptionDiagnostic;
            var effectiveOperation = string.IsNullOrWhiteSpace(diagnostic)
                ? operation
                : $"{operation}: {diagnostic}";
            return CreateException(effectiveOperation, errorCode);
        }

        private async Task TryDeleteSubscriptionAsync(string operation, S7CommPlusSubscription subscription, S7CommPlusSubscriptionOptions subscriptionOptions, Func<int> deleteFunc)
        {
            if (!subscriptionOptions.DeleteOnStop || _session == null)
            {
                return;
            }
            if (subscription.FaultException != null && S7CommPlusErrorClassifier.IsConnectionDefinitelyClosed(subscription.FaultException.ErrorCode))
            {
                _options.Logger.LogDebug(
                    "Skipping {Operation} for {Endpoint} because the subscription already observed connection loss {ErrorCode}.",
                    operation,
                    Endpoint,
                    subscription.FaultException.ErrorCode);
                return;
            }

            try
            {
                var error = await RunWithTimeoutAsync(operation, deleteFunc, _options.DisconnectTimeout, CancellationToken.None).ConfigureAwait(false);
                if (error != 0)
                {
                    var exception = CreateException(operation, error);
                    RaiseCommunicationError(exception);
                    subscription.MarkFaulted(exception);
                    _options.Logger.LogWarning("PLC subscription delete for {Endpoint} returned error {ErrorCode}.", Endpoint, error);
                }
            }
            catch (S7CommPlusException ex)
            {
                RaiseCommunicationError(ex);
                subscription.MarkFaulted(ex);
                _options.Logger.LogWarning(ex, "PLC subscription delete for {Endpoint} failed or timed out.", Endpoint);
            }
        }

        private async Task<T> RunWithTimeoutAsync<T>(string operation, Func<T> func, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                return await RuntimeCompatibility.WaitAsync(Task.Run(func), timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new S7CommPlusTimeoutException(operation, Endpoint, S7Consts.errCliJobTimeout, $"{operation} timed out after {timeout}.", ex);
            }
            catch (S7CommPlusException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new S7CommPlusConnectionException(operation, Endpoint, S7Consts.errCliFunctionRefused, true, $"{operation} failed for PLC {Endpoint}.", ex);
            }
        }

        private void ThrowIfError(string operation, int errorCode)
        {
            if (errorCode != 0)
            {
                throw CreateException(operation, errorCode);
            }
        }

        private S7CommPlusException CreateException(string operation, int errorCode)
            => S7CommPlusErrorClassifier.CreateException(operation, Endpoint, errorCode, _session?.LastErrorDetail);

        private void RaiseCommunicationError(S7CommPlusException exception)
        {
            CommunicationError?.Invoke(this, new S7CommPlusCommunicationErrorEventArgs(exception));
        }

        private void SetState(S7CommPlusConnectionState newState, Exception exception = null)
        {
            var oldState = _state;
            if (oldState == newState)
            {
                return;
            }

            _state = newState;
            ConnectionStateChanged?.Invoke(this, new S7CommPlusConnectionStateChangedEventArgs(oldState, newState, exception));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(S7CommPlusClient));
            }
        }

        private static Func<string, long, int, string> CreateTextListResolver(S7CommPlusTextListCatalog textLists)
        {
            return textLists == null ? null : new Func<string, long, int, string>(textLists.ResolveText);
        }

        private string Endpoint => $"{_options.Address}:{_options.Port}";
    }
}
