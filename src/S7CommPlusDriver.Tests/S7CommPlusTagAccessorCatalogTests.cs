using System;
using System.IO;
using System.Linq;
using System.Text;
using S7CommPlusDriver.ClientApi;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    /// <summary>Verifies compact tag-accessor creation, coverage tracking, and the persistent catalog format.</summary>
    public sealed class S7CommPlusTagAccessorCatalogTests
    {
        /// <summary>Ensures one browse result creates exact and indexed accessors while retaining known-missing coverage.</summary>
        [Fact]
        public void CreateCatalogResolvesRequestedSymbolsAndTracksMissingSymbols()
        {
            var catalog = S7CommPlusClient.CreateTagAccessorCatalog(
                new[]
                {
                    new VarInfo
                    {
                        Name = "DB.Value",
                        AccessSequence = "8A0E0001.F",
                        SymbolCrc = 0x12345678,
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_INT,
                    },
                    new VarInfo
                    {
                        Name = "DB.Counters",
                        AccessSequence = "8A0E0001.10",
                        SymbolCrc = 0x87654321,
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_UDINT,
                        ArrayElementCount = 4,
                        ArrayDimensions = new[]
                        {
                            new S7CommPlusArrayDimension(1, 2),
                            new S7CommPlusArrayDimension(-1, 2),
                        },
                    },
                },
                new[] { "DB.Value", "DB.Counters[2,0]", "DB.Missing", "DB.Value" },
                "HASH-A");

            var tags = catalog.CreateTags(new[] { "DB.Value", "DB.Counters[2,0]", "DB.Missing" });

            Assert.Equal("HASH-A", catalog.ProgramStructureHash);
            Assert.Equal(3, catalog.RequestedSymbolCount);
            Assert.Equal(2, catalog.ResolvedSymbolCount);
            Assert.Equal(new[] { "DB.Missing" }, catalog.MissingSymbols);
            Assert.True(catalog.CoversSymbols(new[] { "DB.Value", "DB.Missing" }));
            Assert.False(catalog.CoversSymbols(new[] { "DB.New" }));
            Assert.IsType<PlcTagInt>(tags["DB.Value"]);
            Assert.Equal(0x12345678U, tags["DB.Value"].Address.SymbolCrc);
            Assert.Equal("8A0E0001.10.3", tags["DB.Counters[2,0]"].Address.GetAccessString());
            Assert.Same(tags.Keys.First(key => key == "DB.Value"), tags["DB.Value"].Name);
            Assert.Equal(0U, tags["DB.Counters[2,0]"].Address.SymbolCrc);
            Assert.False(tags.ContainsKey("DB.Missing"));
        }

        /// <summary>Ensures round-tripping preserves aggregate addresses and creates independent mutable object graphs.</summary>
        [Fact]
        public void StreamRoundTripCreatesIndependentAggregateTags()
        {
            var catalog = S7CommPlusClient.CreateTagAccessorCatalog(
                new[]
                {
                    new VarInfo
                    {
                        Name = "DB.Flags",
                        AccessSequence = "8A0E0001.F",
                        SymbolCrc = 0x12345678,
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL,
                        ArrayElementCount = 6,
                        ArrayDimensions = new[]
                        {
                            new S7CommPlusArrayDimension(1, 2),
                            new S7CommPlusArrayDimension(1, 3),
                        },
                    },
                },
                new[] { "DB.Flags" },
                "HASH-B");
            using var stream = new MemoryStream();
            catalog.WriteTo(stream);
            stream.Position = 0;

            var restored = S7CommPlusTagAccessorCatalog.ReadFrom(stream, "HASH-B");
            var first = Assert.IsType<PlcTagBoolArray>(restored.CreateTags(new[] { "DB.Flags" })["DB.Flags"]);
            var second = Assert.IsType<PlcTagBoolArray>(restored.CreateTags(new[] { "DB.Flags" })["DB.Flags"]);

            Assert.NotSame(first, second);
            Assert.NotSame(first.Address, second.Address);
            Assert.Equal(
                new[] { "8A0E0001.F.0", "8A0E0001.F.1", "8A0E0001.F.2", "8A0E0001.F.8", "8A0E0001.F.9", "8A0E0001.F.A" },
                first.AggregateElements.Select(element => element.Address.GetAccessString()));
            Assert.All(first.AggregateElements.Zip(second.AggregateElements, (firstElement, secondElement) => (firstElement, secondElement)), pair =>
            {
                Assert.NotSame(pair.firstElement, pair.secondElement);
                Assert.NotSame(pair.firstElement.Address, pair.secondElement.Address);
            });
        }

        /// <summary>Ensures version-two readers preserve access to catalogs written before root names were deduplicated.</summary>
        [Fact]
        public void ReadSupportsVersionOneCatalogs()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("S7PACT01"));
                writer.Write(1);
                WriteString(writer, "HASH-V1");
                writer.Write(1);
                WriteString(writer, "DB.Value");
                writer.Write(true);
                WriteString(writer, "DB.Value");
                writer.Write(Softdatatype.S7COMMP_SOFTDATATYPE_BOOL);
                writer.Write(0x12345678U);
                writer.Write(0x8A0E0001U);
                writer.Write(Ids.DB_ValueActual);
                writer.Write(1);
                writer.Write(0xFU);
                writer.Write(0);
            }
            stream.Position = 0;

            var catalog = S7CommPlusTagAccessorCatalog.ReadFrom(stream, "HASH-V1");
            var tag = catalog.CreateTags(new[] { "DB.Value" })["DB.Value"];

            Assert.Equal("8A0E0001.F", tag.Address.GetAccessString());
            Assert.Equal(0x12345678U, tag.Address.SymbolCrc);
        }

        /// <summary>Ensures a valid file cannot be used for an unverified or changed PLC program.</summary>
        [Fact]
        public void ReadRejectsProgramStructureHashMismatch()
        {
            var catalog = S7CommPlusClient.CreateTagAccessorCatalog(
                Array.Empty<VarInfo>(),
                new[] { "DB.Missing" },
                "HASH-C");
            using var stream = new MemoryStream();
            catalog.WriteTo(stream);
            stream.Position = 0;

            var exception = Assert.Throws<InvalidDataException>(
                () => S7CommPlusTagAccessorCatalog.ReadFrom(stream, "HASH-D"));

            Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ensures truncated or unrelated data is reported as an invalid cache instead of leaking decoder exceptions.</summary>
        [Theory]
        [InlineData(new byte[] { })]
        [InlineData(new byte[] { 1, 2, 3, 4 })]
        [InlineData(new byte[] { 83, 55, 80, 65, 67, 84, 48, 49, 2, 0, 0, 0 })]
        public void ReadRejectsMalformedOrUnsupportedCatalog(byte[] data)
        {
            using var stream = new MemoryStream(data);

            Assert.Throws<InvalidDataException>(() => S7CommPlusTagAccessorCatalog.ReadFrom(stream, "HASH"));
        }

        /// <summary>Ensures callers cannot silently request a symbol absent from a catalog produced for an older configuration.</summary>
        [Fact]
        public void CreateTagsRejectsUncoveredSymbol()
        {
            var catalog = S7CommPlusClient.CreateTagAccessorCatalog(
                Array.Empty<VarInfo>(),
                new[] { "DB.KnownMissing" },
                "HASH");

            var exception = Assert.Throws<ArgumentException>(() => catalog.CreateTags(new[] { "DB.New" }));

            Assert.Contains("does not cover", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
}
