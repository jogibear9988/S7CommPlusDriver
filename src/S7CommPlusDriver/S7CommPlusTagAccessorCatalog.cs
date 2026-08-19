using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using S7CommPlusDriver.ClientApi;

namespace S7CommPlusDriver
{
    /// <summary>
    /// Stores the compact, program-specific information required to create mutable <see cref="PlcTag"/> accessors without
    /// retaining or re-reading the complete PLC symbol structure.
    /// </summary>
    /// <remarks>
    /// A catalog records every requested symbol, including symbols that could not be resolved. This lets applications distinguish
    /// a complete cache containing known-missing configuration from an older cache that has never seen a newly configured symbol.
    /// Catalog instances are immutable and may safely be shared; each call to <see cref="CreateTags(IEnumerable{string})"/> creates
    /// independent mutable tag objects.
    /// </remarks>
    public sealed class S7CommPlusTagAccessorCatalog
    {
        private const int CurrentFormatVersion = 2;
        private const int MinimumSupportedFormatVersion = 1;
        private const int MaximumEntryCount = 2_000_000;
        private const int MaximumAggregateElementCount = 2_000_000;
        private const int MaximumDescriptorDepth = 32;
        private const int MaximumAddressFieldCount = 4_096;
        private const int MaximumStringByteCount = 4 * 1024 * 1024;

        private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("S7PACT01");

        private readonly IReadOnlyDictionary<string, TagDescriptor> _entries;
        private readonly IReadOnlyList<string> _missingSymbols;

        /// <summary>
        /// Initializes an immutable accessor catalog from normalized request names and their resolved mutable tags.
        /// </summary>
        /// <param name="programStructureHash">The PLC program-structure fingerprint that makes the accessors valid.</param>
        /// <param name="requestedSymbols">Every distinct symbol covered by the catalog.</param>
        /// <param name="resolvedTags">The subset of requested symbols that the driver successfully resolved.</param>
        internal S7CommPlusTagAccessorCatalog(
            string programStructureHash,
            IReadOnlyCollection<string> requestedSymbols,
            IReadOnlyDictionary<string, PlcTag> resolvedTags)
        {
            if (programStructureHash == null) throw new ArgumentNullException(nameof(programStructureHash));
            if (string.IsNullOrWhiteSpace(programStructureHash))
            {
                throw new ArgumentException("A verified PLC program-structure hash is required.", nameof(programStructureHash));
            }
            if (requestedSymbols == null) throw new ArgumentNullException(nameof(requestedSymbols));
            if (resolvedTags == null) throw new ArgumentNullException(nameof(resolvedTags));

            ProgramStructureHash = programStructureHash;
            var entries = new Dictionary<string, TagDescriptor>(requestedSymbols.Count, StringComparer.Ordinal);
            foreach (var symbol in requestedSymbols)
            {
                entries.Add(symbol, resolvedTags.TryGetValue(symbol, out var tag) ? TagDescriptor.FromTag(tag) : null);
            }
            _entries = entries;
            _missingSymbols = entries.Where(entry => entry.Value == null).Select(entry => entry.Key).ToArray();
        }

        /// <summary>
        /// Initializes an accessor catalog from descriptors read from the versioned stream format.
        /// </summary>
        /// <param name="programStructureHash">The persisted PLC program-structure fingerprint.</param>
        /// <param name="entries">Every persisted symbol and its optional resolved descriptor.</param>
        private S7CommPlusTagAccessorCatalog(
            string programStructureHash,
            IReadOnlyDictionary<string, TagDescriptor> entries)
        {
            ProgramStructureHash = programStructureHash;
            _entries = entries;
            _missingSymbols = entries.Where(entry => entry.Value == null).Select(entry => entry.Key).ToArray();
        }

        /// <summary>Gets the PLC program-structure hash for which every accessor in this catalog was generated.</summary>
        public string ProgramStructureHash { get; }

        /// <summary>Gets the number of distinct requested symbols covered by this catalog, including known-missing symbols.</summary>
        public int RequestedSymbolCount => _entries.Count;

        /// <summary>Gets the number of requested symbols that can be materialized as supported <see cref="PlcTag"/> accessors.</summary>
        public int ResolvedSymbolCount => _entries.Count - _missingSymbols.Count;

        /// <summary>Gets the requested symbols that were absent from the source metadata or use an unsupported datatype.</summary>
        public IReadOnlyList<string> MissingSymbols => _missingSymbols;

        /// <summary>
        /// Determines whether the catalog records a result for every supplied symbol, including known-missing results.
        /// </summary>
        /// <param name="symbols">The configured symbols whose cache coverage must be verified.</param>
        /// <returns><see langword="true"/> when every distinct symbol is represented in the catalog.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The collection contains a null, empty, or whitespace-only symbol.</exception>
        public bool CoversSymbols(IEnumerable<string> symbols)
        {
            if (symbols == null) throw new ArgumentNullException(nameof(symbols));

            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    throw new ArgumentException("Symbol collection cannot contain null, empty, or whitespace-only entries.", nameof(symbols));
                }
                if (!_entries.ContainsKey(symbol))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Creates independent mutable PLC tags for the requested symbols resolved by this catalog.
        /// </summary>
        /// <param name="symbols">The covered symbols to materialize. Known-missing symbols are omitted from the result.</param>
        /// <returns>A case-sensitive lookup of newly created accessors that share no mutable tag or address state.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The collection contains an invalid or uncovered symbol.</exception>
        public IReadOnlyDictionary<string, PlcTag> CreateTags(IEnumerable<string> symbols)
        {
            return CreateTagDictionary(symbols);
        }

        /// <summary>
        /// Creates an owned mutable dictionary of independent PLC tags for the requested symbols resolved by this catalog.
        /// </summary>
        /// <param name="symbols">The covered symbols to materialize. Known-missing symbols are omitted from the result.</param>
        /// <returns>A case-sensitive dictionary that the caller may retain directly without copying.</returns>
        public Dictionary<string, PlcTag> CreateTagDictionary(IEnumerable<string> symbols)
        {
            if (symbols == null) throw new ArgumentNullException(nameof(symbols));

            var capacity = symbols is IReadOnlyCollection<string> collection ? collection.Count : 0;
            var result = new Dictionary<string, PlcTag>(capacity, StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    throw new ArgumentException("Symbol collection cannot contain null, empty, or whitespace-only entries.", nameof(symbols));
                }
                if (result.ContainsKey(symbol))
                {
                    continue;
                }
                if (!_entries.TryGetValue(symbol, out var descriptor))
                {
                    throw new ArgumentException($"The accessor catalog does not cover symbol '{symbol}'.", nameof(symbols));
                }
                result.Add(symbol, descriptor?.CreateTag(symbol));
            }
            foreach (var missingSymbol in _missingSymbols)
            {
                result.Remove(missingSymbol);
            }
            return result;
        }

        /// <summary>
        /// Writes the catalog to a versioned binary stream without closing the caller-owned stream.
        /// </summary>
        /// <param name="destination">A writable stream positioned where the catalog should begin.</param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.</exception>
        public void WriteTo(Stream destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));

            using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
            writer.Write(FileMagic);
            writer.Write(CurrentFormatVersion);
            WriteString(writer, ProgramStructureHash);
            writer.Write(_entries.Count);
            foreach (var entry in _entries)
            {
                WriteString(writer, entry.Key);
                writer.Write(entry.Value != null);
                entry.Value?.WriteTo(writer, includeName: false);
            }
        }

        /// <summary>
        /// Reads and validates a catalog from a versioned binary stream without closing the caller-owned stream.
        /// </summary>
        /// <param name="source">A readable stream positioned at the catalog header.</param>
        /// <param name="expectedProgramStructureHash">The currently verified PLC structure hash required for cache use.</param>
        /// <returns>The validated immutable accessor catalog.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="source"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">The stream is malformed, unsupported, or belongs to another PLC structure.</exception>
        public static S7CommPlusTagAccessorCatalog ReadFrom(Stream source, string expectedProgramStructureHash)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (expectedProgramStructureHash == null) throw new ArgumentNullException(nameof(expectedProgramStructureHash));
            if (string.IsNullOrWhiteSpace(expectedProgramStructureHash))
            {
                throw new ArgumentException("A verified PLC program-structure hash is required.", nameof(expectedProgramStructureHash));
            }
            if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));

            try
            {
                using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
                var magic = reader.ReadBytes(FileMagic.Length);
                if (magic.Length != FileMagic.Length || !magic.SequenceEqual(FileMagic))
                {
                    throw new InvalidDataException("The stream is not an S7CommPlus tag-accessor catalog.");
                }

                var version = reader.ReadInt32();
                if (version < MinimumSupportedFormatVersion || version > CurrentFormatVersion)
                {
                    throw new InvalidDataException($"Unsupported S7CommPlus tag-accessor catalog version {version}.");
                }

                var structureHash = ReadString(reader, "program structure hash");
                if (string.IsNullOrWhiteSpace(structureHash))
                {
                    throw new InvalidDataException("The tag-accessor catalog has no verified PLC program-structure hash.");
                }
                if (!string.Equals(structureHash, expectedProgramStructureHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The tag-accessor catalog does not match the current PLC program structure.");
                }

                var entryCount = ReadBoundedCount(reader, MaximumEntryCount, "catalog entry");
                var entries = new Dictionary<string, TagDescriptor>(entryCount, StringComparer.Ordinal);
                for (var index = 0; index < entryCount; index++)
                {
                    var symbol = ReadString(reader, "symbol name");
                    if (string.IsNullOrWhiteSpace(symbol) || entries.ContainsKey(symbol))
                    {
                        throw new InvalidDataException("The tag-accessor catalog contains an invalid or duplicate symbol name.");
                    }
                    entries.Add(
                        symbol,
                        reader.ReadBoolean()
                            ? TagDescriptor.ReadFrom(reader, 0, version >= 2 ? symbol : null)
                            : null);
                }
                return new S7CommPlusTagAccessorCatalog(structureHash, entries);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException or OverflowException)
            {
                throw new InvalidDataException("The S7CommPlus tag-accessor catalog is malformed.", exception);
            }
        }

        /// <summary>
        /// Normalizes and validates a public symbol sequence while preserving the first occurrence order.
        /// </summary>
        /// <param name="symbols">The caller-supplied symbols.</param>
        /// <returns>A distinct, case-sensitive symbol list.</returns>
        private static IReadOnlyList<string> NormalizeSymbols(IEnumerable<string> symbols)
        {
            if (symbols == null) throw new ArgumentNullException(nameof(symbols));

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    throw new ArgumentException("Symbol collection cannot contain null, empty, or whitespace-only entries.", nameof(symbols));
                }
                if (seen.Add(symbol))
                {
                    result.Add(symbol);
                }
            }
            return result;
        }

        /// <summary>Writes one UTF-8 string with a fixed-width length prefix used by the bounded reader.</summary>
        /// <param name="writer">The target catalog writer.</param>
        /// <param name="value">The non-null string to persist.</param>
        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>Reads one length-prefixed UTF-8 string while rejecting unreasonable allocations and invalid UTF-8.</summary>
        /// <param name="reader">The source catalog reader.</param>
        /// <param name="fieldName">The diagnostic field name used for malformed data.</param>
        /// <returns>The decoded string.</returns>
        private static string ReadString(BinaryReader reader, string fieldName)
        {
            var byteCount = ReadBoundedCount(reader, MaximumStringByteCount, fieldName + " byte");
            var bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
            {
                throw new EndOfStreamException();
            }
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        /// <summary>Reads a non-negative count and prevents corrupt files from requesting excessive allocations.</summary>
        /// <param name="reader">The source catalog reader.</param>
        /// <param name="maximum">The largest supported value.</param>
        /// <param name="fieldName">The diagnostic field name.</param>
        /// <returns>The validated count.</returns>
        private static int ReadBoundedCount(BinaryReader reader, int maximum, string fieldName)
        {
            var value = reader.ReadInt32();
            if (value < 0 || value > maximum)
            {
                throw new InvalidDataException($"The {fieldName} count {value} is outside the supported range.");
            }
            return value;
        }

        /// <summary>Captures only the immutable fields needed to recreate one mutable scalar or aggregate PLC tag.</summary>
        private sealed class TagDescriptor
        {
            private readonly AddressDescriptor _address;
            private readonly IReadOnlyList<TagDescriptor> _aggregateElements;

            /// <summary>Initializes one immutable tag descriptor.</summary>
            /// <param name="name">The symbolic or generated aggregate-element name.</param>
            /// <param name="datatype">The Siemens soft-datatype identifier.</param>
            /// <param name="address">The immutable item-address descriptor.</param>
            /// <param name="aggregateElements">Scalar elements used to transfer an aggregate array.</param>
            private TagDescriptor(
                string name,
                uint datatype,
                AddressDescriptor address,
                IReadOnlyList<TagDescriptor> aggregateElements)
            {
                Name = name;
                Datatype = datatype;
                _address = address;
                _aggregateElements = aggregateElements;
            }

            /// <summary>Gets the symbolic or generated element name.</summary>
            private string Name { get; }

            /// <summary>Gets the Siemens soft-datatype identifier.</summary>
            private uint Datatype { get; }

            /// <summary>Creates a detached immutable descriptor from a resolved mutable driver tag.</summary>
            /// <param name="tag">The resolved source tag.</param>
            /// <returns>A recursively captured descriptor.</returns>
            internal static TagDescriptor FromTag(PlcTag tag)
            {
                if (tag == null) throw new ArgumentNullException(nameof(tag));
                return new TagDescriptor(
                    tag.Name,
                    tag.Datatype,
                    AddressDescriptor.FromAddress(tag.Address),
                    tag.AggregateElements.Select(FromTag).ToArray());
            }

            /// <summary>Creates a fresh mutable driver tag and recursively recreates aggregate element tags.</summary>
            /// <returns>An accessor that shares no mutable state with previous materializations.</returns>
            internal PlcTag CreateTag(string nameOverride = null)
            {
                var tag = PlcTags.TagFactory(nameOverride ?? Name, _address.CreateAddress(), Datatype, _aggregateElements.Count > 0);
                if (tag == null)
                {
                    throw new InvalidDataException($"The cached datatype {Datatype} for symbol '{Name}' is not supported by this driver version.");
                }
                if (_aggregateElements.Count > 0)
                {
                    tag.SetAggregateElements(_aggregateElements.Select(element => element.CreateTag()).ToArray());
                }
                return tag;
            }

            /// <summary>Writes this descriptor and its aggregate elements to the catalog stream.</summary>
            /// <param name="writer">The destination writer.</param>
            internal void WriteTo(BinaryWriter writer, bool includeName = true)
            {
                if (includeName)
                {
                    WriteString(writer, Name);
                }
                writer.Write(Datatype);
                _address.WriteTo(writer);
                writer.Write(_aggregateElements.Count);
                foreach (var element in _aggregateElements)
                {
                    element.WriteTo(writer);
                }
            }

            /// <summary>Reads and validates one recursive descriptor from the catalog stream.</summary>
            /// <param name="reader">The source reader.</param>
            /// <param name="depth">The current nesting depth used to reject maliciously recursive cache files.</param>
            /// <returns>The validated descriptor.</returns>
            internal static TagDescriptor ReadFrom(BinaryReader reader, int depth, string nameOverride = null)
            {
                if (depth > MaximumDescriptorDepth)
                {
                    throw new InvalidDataException("The cached aggregate tag nesting exceeds the supported depth.");
                }
                var name = nameOverride ?? ReadString(reader, "tag name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidDataException("A cached tag descriptor has no name.");
                }
                var datatype = reader.ReadUInt32();
                var address = AddressDescriptor.ReadFrom(reader);
                var elementCount = ReadBoundedCount(reader, MaximumAggregateElementCount, "aggregate element");
                var elements = new TagDescriptor[elementCount];
                for (var index = 0; index < elementCount; index++)
                {
                    elements[index] = ReadFrom(reader, depth + 1);
                }
                return new TagDescriptor(name, datatype, address, elements);
            }
        }

        /// <summary>Captures the scalar address values serialized on the S7CommPlus wire without retaining <see cref="ItemAddress"/>.</summary>
        private sealed class AddressDescriptor
        {
            private readonly IReadOnlyList<uint> _localIds;

            /// <summary>Initializes an immutable item-address descriptor.</summary>
            private AddressDescriptor(uint symbolCrc, uint accessArea, uint accessSubArea, IReadOnlyList<uint> localIds)
            {
                SymbolCrc = symbolCrc;
                AccessArea = accessArea;
                AccessSubArea = accessSubArea;
                _localIds = localIds;
            }

            /// <summary>Gets the Siemens symbol CRC validated with the access sequence.</summary>
            private uint SymbolCrc { get; }

            /// <summary>Gets the root relation or controller access area.</summary>
            private uint AccessArea { get; }

            /// <summary>Gets the value sub-area sent before local IDs.</summary>
            private uint AccessSubArea { get; }

            /// <summary>Copies a mutable driver item address into an immutable descriptor.</summary>
            /// <param name="address">The resolved source address.</param>
            /// <returns>The detached address descriptor.</returns>
            internal static AddressDescriptor FromAddress(ItemAddress address)
            {
                if (address == null) throw new ArgumentNullException(nameof(address));
                return new AddressDescriptor(address.SymbolCrc, address.AccessArea, address.AccessSubArea, address.CopyLocalIds());
            }

            /// <summary>Creates a fresh mutable item address for one materialized tag.</summary>
            /// <returns>A detached address containing a new local-ID list.</returns>
            internal ItemAddress CreateAddress()
            {
                var address = new ItemAddress(AccessArea, AccessSubArea) { SymbolCrc = SymbolCrc };
                foreach (var localId in _localIds)
                {
                    address.AddLocalId(localId);
                }
                return address;
            }

            /// <summary>Writes the address fields to the versioned catalog stream.</summary>
            /// <param name="writer">The destination writer.</param>
            internal void WriteTo(BinaryWriter writer)
            {
                writer.Write(SymbolCrc);
                writer.Write(AccessArea);
                writer.Write(AccessSubArea);
                writer.Write(_localIds.Count);
                foreach (var localId in _localIds)
                {
                    writer.Write(localId);
                }
            }

            /// <summary>Reads one bounded item address from the versioned catalog stream.</summary>
            /// <param name="reader">The source reader.</param>
            /// <returns>The validated immutable address descriptor.</returns>
            internal static AddressDescriptor ReadFrom(BinaryReader reader)
            {
                var symbolCrc = reader.ReadUInt32();
                var accessArea = reader.ReadUInt32();
                var accessSubArea = reader.ReadUInt32();
                var localIdCount = ReadBoundedCount(reader, MaximumAddressFieldCount, "address field");
                var localIds = new uint[localIdCount];
                for (var index = 0; index < localIdCount; index++)
                {
                    localIds[index] = reader.ReadUInt32();
                }
                return new AddressDescriptor(symbolCrc, accessArea, accessSubArea, localIds);
            }
        }
    }
}
