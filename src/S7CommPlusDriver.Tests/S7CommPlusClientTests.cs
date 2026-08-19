using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Alarming;
using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class S7CommPlusClientTests
    {
        [Fact]
        public async Task ConnectBrowseAndReadSucceeds()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo> { new VarInfo { Name = "DB.Value" } })
            };
            var client = CreateClient(fake);

            await client.ConnectAsync();
            var vars = await client.BrowseAsync();
            var read = await client.ReadAsync(new[] { new ItemAddress("8A0E0001.F") });

            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
            Assert.Single(vars);
            Assert.Single(read.Items);
            Assert.True(read.Items[0].IsSuccess);
        }

        [Fact]
        public async Task AggregateReadUsesElementAddressesAndPublishesOneArrayResult()
        {
            var fake = new FakeS7CommPlusSession
            {
                ReadHandler = addresses =>
                {
                    Assert.Equal(new[] { "8A0E0001.F.0", "8A0E0001.F.1" }, addresses.Select(address => address.GetAccessString()));
                    return (0, new List<object?> { new ValueUDInt(11), new ValueUDInt(22) }, new List<ulong> { 0, 0 });
                },
            };
            var aggregate = new PlcTagUDIntArray(
                "DB.Values",
                new ItemAddress("8A0E0001.F"),
                Softdatatype.S7COMMP_SOFTDATATYPE_UDINT);
            aggregate.SetAggregateElements(new PlcTag[]
            {
                new PlcTagUDInt("DB.Values[1]", new ItemAddress("8A0E0001.F.0"), Softdatatype.S7COMMP_SOFTDATATYPE_UDINT),
                new PlcTagUDInt("DB.Values[2]", new ItemAddress("8A0E0001.F.1"), Softdatatype.S7COMMP_SOFTDATATYPE_UDINT),
            });
            var client = CreateClient(fake);

            var result = await client.ReadAsync(new[] { aggregate });

            Assert.Single(result.Items);
            Assert.True(result.Items[0].IsSuccess);
            Assert.Equal(new uint[] { 11, 22 }, aggregate.Value);
            Assert.Equal(new uint[] { 11, 22 }, Assert.IsType<uint[]>(aggregate.AggregateValue));
        }

        [Fact]
        public async Task ComplexAggregateArraysReadThroughScalarElementAddresses()
        {
            var expectedDateTimes = new[]
            {
                new DateTime(2026, 7, 20, 15, 10, 48, 120),
                new DateTime(1999, 12, 31, 23, 59, 58, 990),
            };
            var expectedDtls = new[]
            {
                new DateTime(2026, 7, 20, 15, 10, 48),
                new DateTime(2025, 1, 2, 3, 4, 5),
            };
            var fake = new FakeS7CommPlusSession
            {
                ReadHandler = addresses =>
                {
                    Assert.Equal(
                        new[]
                        {
                            "8A0E0001.10.0", "8A0E0001.10.1",
                            "8A0E0001.11.0", "8A0E0001.11.1",
                            "8A0E0001.12.0.1", "8A0E0001.12.1.1",
                        },
                        addresses.Select(address => address.GetAccessString()));
                    return (
                        0,
                        new List<object?>
                        {
                            CreateStringValue("first"),
                            CreateStringValue("second"),
                            CreateDateAndTimeValue(expectedDateTimes[0]),
                            CreateDateAndTimeValue(expectedDateTimes[1]),
                            CreateDtlValue(expectedDtls[0], 123_456_789, 11),
                            CreateDtlValue(expectedDtls[1], 987_654_321, 22),
                        },
                        new List<ulong> { 0, 0, 0, 0, 0, 0 });
                },
            };
            var strings = Assert.IsType<PlcTagStringArray>(CreateAggregateTag(Softdatatype.S7COMMP_SOFTDATATYPE_STRING, "DB.Strings", "8A0E0001.10"));
            var dateAndTimes = Assert.IsType<PlcTagDateAndTimeArray>(CreateAggregateTag(Softdatatype.S7COMMP_SOFTDATATYPE_DATEANDTIME, "DB.DateAndTimes", "8A0E0001.11"));
            var dtls = Assert.IsType<PlcTagDTLArray>(CreateAggregateTag(Softdatatype.S7COMMP_SOFTDATATYPE_DTL, "DB.Dtls", "8A0E0001.12"));
            var client = CreateClient(fake);

            var result = await client.ReadAsync(new PlcTag[] { strings, dateAndTimes, dtls });

            Assert.All(result.Items, item => Assert.True(item.IsSuccess));
            Assert.Equal(new[] { "first", "second" }, strings.Value);
            Assert.Equal(expectedDateTimes, dateAndTimes.Value);
            Assert.Equal(expectedDtls, dtls.Value);
            Assert.Equal(new uint[] { 123_456_789, 987_654_321 }, dtls.ValueNanosecond);
            Assert.Equal(new ulong[] { 11, 22 }, dtls.DTLInterfaceTimestamps);
        }

        [Fact]
        public async Task AggregateReadSplitsExpandedElementsAtDefaultPlcLimit()
        {
            var nextValue = 0U;
            var fake = new FakeS7CommPlusSession
            {
                ReadHandler = addresses =>
                {
                    Assert.InRange(addresses.Count, 1, 20);
                    var values = addresses.Select(_ => (object?)new ValueUDInt(nextValue++)).ToList();
                    return (0, values, new List<ulong>(new ulong[addresses.Count]));
                },
            };
            var aggregate = CreateAggregateUnsignedIntegerTag(25);
            var client = CreateClient(fake);

            var result = await client.ReadAsync(new[] { aggregate });

            Assert.True(result.Items[0].IsSuccess);
            Assert.Equal(2, fake.ReadCount);
            Assert.Equal(Enumerable.Range(0, 25).Select(value => (uint)value), aggregate.Value);
        }

        [Fact]
        public async Task AggregateWriteCopiesParentArrayAndSplitsExpandedElements()
        {
            var writtenValues = new List<uint>();
            var fake = new FakeS7CommPlusSession
            {
                WriteHandler = (addresses, values) =>
                {
                    Assert.InRange(addresses.Count, 1, 20);
                    writtenValues.AddRange(values.Cast<ValueUDInt>().Select(value => value.GetValue()));
                    return (0, new List<ulong>(new ulong[addresses.Count]));
                },
            };
            var aggregate = CreateAggregateUnsignedIntegerTag(25);
            aggregate.Value = Enumerable.Range(1, 25).Select(value => (uint)value).ToArray();
            var client = CreateClient(fake, writeEnabled: true);

            var result = await client.WriteAsync(new[] { aggregate });

            Assert.True(result.Items[0].IsSuccess);
            Assert.Equal(aggregate.Value, writtenValues);
        }

        [Fact]
        public async Task InvalidSymbolSyntaxPreservesSymbolAndDoesNotReconnect()
        {
            var syntaxException = new ArgumentException("Expected an array index.", "symbol");
            var fake = new FakeS7CommPlusSession
            {
                GetTagHandler = _ => throw syntaxException,
            };
            var client = CreateClient(fake);

            var exception = await Assert.ThrowsAsync<S7CommPlusConnectionException>(() => client.GetTagBySymbolAsync("DB.Values"));

            Assert.Contains("DB.Values", exception.Message);
            Assert.Same(syntaxException, exception.InnerException);
            Assert.False(exception.IsTransient);
            Assert.Equal(S7Consts.errCliItemNotAvailable, exception.ErrorCode);
            Assert.Equal(1, fake.ConnectCount);
            Assert.Equal(0, fake.DisconnectCount);
        }

        [Fact]
        public async Task UnexpectedSymbolFailurePreservesSymbolAndInnerException()
        {
            var innerException = new InvalidOperationException("Broken type metadata.");
            var fake = new FakeS7CommPlusSession
            {
                GetTagHandler = _ => throw innerException,
            };
            var client = CreateClient(fake);

            var exception = await Assert.ThrowsAsync<S7CommPlusConnectionException>(() => client.GetTagBySymbolAsync("DB.Broken"));

            Assert.Contains("DB.Broken", exception.Message);
            Assert.Same(innerException, exception.InnerException);
            Assert.True(exception.IsTransient);
            Assert.Equal(S7Consts.errCliFunctionRefused, exception.ErrorCode);
        }

        [Fact]
        public async Task BrowseUsesAggregatePrimitiveArraysByDefaultAndSupportsLegacyExpansion()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            await client.BrowseAsync();
            Assert.False(fake.LastBrowseExpandedPrimitiveArrayElements);

            await client.BrowseAsync(new S7CommPlusBrowseOptions
            {
                ExpandPrimitiveArrayElements = true,
            });
            Assert.True(fake.LastBrowseExpandedPrimitiveArrayElements);
        }

        [Fact]
        public async Task SymbolCommentsResolveDbModelPathsAndAllLanguages()
        {
            const string commentsXml = "<InterfaceLineComments><Part Kind=\"Comments\">" +
                "<Comment Path=\"51:65\"><DictEntry Language=\"de-DE\">Deutscher Text</DictEntry>" +
                "<DictEntry Language=\"en-GB\">English text</DictEntry></Comment>" +
                "</Part></InterfaceLineComments>";
            const string interfaceXml = "<BlockInterface>" +
                "<Part Kind=\"DBSource\"><Payload><Root>" +
                "<Member ID=\"51\" Name=\"General\" LID=\"9\" SubPartIndex=\"0\" />" +
                "</Root></Payload></Part>" +
                "<Part Kind=\"Structure\"><Payload><Root><Sections><Section>" +
                "<Member ID=\"65\" Name=\"FinePosScreenActive\" LID=\"23\" />" +
                "</Section></Sections></Root></Payload></Part>" +
                "</BlockInterface>";
            var catalog = S7CommPlusSymbolCommentParser.Parse(
                0x8A0E1451,
                "Conditions",
                commentsXml,
                interfaceXml);
            var fake = new FakeS7CommPlusSession
            {
                GetSymbolCommentsHandler = relationId =>
                {
                    Assert.Equal(0x8A0E1451U, relationId);
                    return (0, catalog);
                },
            };
            var client = CreateClient(fake);

            var comments = await client.GetSymbolCommentsAsync(0x8A0E1451);
            var found = comments.TryGetComments(new VarInfo
            {
                Name = "Conditions.General.FinePosScreenActive",
                AccessSequence = "8A0E1451.9.17",
            }, out var localizedComments);

            Assert.True(found);
            Assert.Equal("Deutscher Text", localizedComments[1031]);
            Assert.Equal("English text", localizedComments[2057]);
            Assert.Equal(1, fake.GetSymbolCommentsCount);
        }

        [Fact]
        public void SymbolCommentsApplyArrayDeclarationCommentToIndexedElements()
        {
            const string commentsXml = "<InterfaceLineComments><Part Kind=\"Comments\">" +
                "<Comment Path=\"53:51\"><DictEntry Language=\"de-DE\">Endschalter</DictEntry></Comment>" +
                "</Part></InterfaceLineComments>";
            const string interfaceXml = "<BlockInterface>" +
                "<Part Kind=\"DBSource\"><Payload><Root>" +
                "<Member ID=\"53\" Name=\"HoistUnit\" LID=\"11\" SubPartIndex=\"0\" />" +
                "</Root></Payload></Part>" +
                "<Part Kind=\"Structure\"><Payload><Root><Sections><Section>" +
                "<Member ID=\"51\" Name=\"ELimitSwitchTrig\" LID=\"9\" />" +
                "</Section></Sections></Root></Payload></Part>" +
                "</BlockInterface>";
            var catalog = S7CommPlusSymbolCommentParser.Parse(0x8A0E1451, "Conditions", commentsXml, interfaceXml);

            var found = catalog.TryGetComments(new VarInfo
            {
                Name = "Conditions.HoistUnit[2].ELimitSwitchTrig",
                AccessSequence = "8A0E1451.B.2.1.9",
            }, out var comments);

            Assert.True(found);
            Assert.Equal("Endschalter", comments[1031]);
        }

        [Fact]
        public void SymbolCommentsResolveAbsoluteAreaRefIdByExactAccessSequence()
        {
            const string commentsXml = "<CommentDictionary><TagLineComments>" +
                "<Comment RefID=\"4345\"><DictEntry Language=\"de-DE\">Direktes Eingangssignal</DictEntry></Comment>" +
                "</TagLineComments></CommentDictionary>";
            var catalog = S7CommPlusSymbolCommentParser.Parse(0x50, "", commentsXml, "");

            var directFound = catalog.TryGetComments(new VarInfo
            {
                Name = "IArea.DirectInput",
                AccessSequence = "50.10F9",
            }, out var directComments);
            var childFound = catalog.TryGetComments(new VarInfo
            {
                Name = "IArea.StructuredInput.Status",
                AccessSequence = "50.10F9.9",
            }, out _);

            Assert.True(directFound);
            Assert.Equal("Direktes Eingangssignal", directComments[1031]);
            Assert.False(childFound);
        }

        [Fact]
        public async Task BulkSymbolResolutionUsesOneBrowseAndOmitsMissingSymbols()
        {
            var getTagCalls = 0;
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo>
                {
                    new VarInfo
                    {
                        Name = "DB.Temperature",
                        AccessSequence = "8A0E0001.F",
                        SymbolCrc = 0x12345678,
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_REAL,
                    },
                    new VarInfo
                    {
                        Name = "DB.Counter",
                        AccessSequence = "8A0E0001.10",
                        SymbolCrc = 0x87654321,
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_DINT,
                    },
                }),
                GetTagHandler = symbol =>
                {
                    getTagCalls++;
                    return PlcTags.TagFactory(symbol, new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);
                },
            };
            var client = CreateClient(fake);

            var tags = await client.GetTagsBySymbolsAsync(new[]
            {
                "DB.Temperature",
                "DB.Counter",
                "DB.Missing",
                "DB.Temperature",
            });

            Assert.Equal(2, tags.Count);
            var temperature = Assert.IsType<PlcTagReal>(tags["DB.Temperature"]);
            Assert.Equal("8A0E0001.F", temperature.Address.GetAccessString());
            Assert.Equal(0x12345678U, temperature.Address.SymbolCrc);
            Assert.IsType<PlcTagDInt>(tags["DB.Counter"]);
            Assert.False(tags.ContainsKey("DB.Missing"));
            Assert.Equal(1, fake.BrowseVariablesCount);
            Assert.Equal(0, getTagCalls);
        }

        [Fact]
        public async Task BulkSymbolResolutionBuildsPackedMultidimensionalBooleanElements()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo>
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
                }),
            };
            var client = CreateClient(fake);

            var tags = await client.GetTagsBySymbolsAsync(new[] { "DB.Flags" });

            var aggregate = Assert.IsType<PlcTagBoolArray>(tags["DB.Flags"]);
            Assert.Equal(
                new[] { "8A0E0001.F.0", "8A0E0001.F.1", "8A0E0001.F.2", "8A0E0001.F.8", "8A0E0001.F.9", "8A0E0001.F.A" },
                aggregate.AggregateElements.Select(element => element.Address.GetAccessString()));
            Assert.All(aggregate.AggregateElements, element => Assert.Equal(0U, element.Address.SymbolCrc));
        }

        [Fact]
        public async Task BulkSymbolResolutionDerivesIndexedPrimitiveArrayElementsFromAggregateCatalog()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo>
                {
                    new VarInfo
                    {
                        Name = "DB.Counters",
                        AccessSequence = "8A0E0001.F",
                        SymbolCrc = 0x12345678,
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_UDINT,
                        ArrayElementCount = 8,
                        ArrayDimensions = new[]
                        {
                            new S7CommPlusArrayDimension(1, 2),
                            new S7CommPlusArrayDimension(1, 2),
                            new S7CommPlusArrayDimension(1, 2),
                        },
                    },
                }),
            };
            var client = CreateClient(fake);

            var tags = await client.GetTagsBySymbolsAsync(new[]
            {
                "DB.Counters[1,1,1]",
                "DB.Counters[2,1,2]",
                "DB.Counters[3,1,1]",
            });

            Assert.Equal(2, tags.Count);
            Assert.Equal("8A0E0001.F.0", tags["DB.Counters[1,1,1]"].Address.GetAccessString());
            Assert.Equal("8A0E0001.F.5", tags["DB.Counters[2,1,2]"].Address.GetAccessString());
            Assert.All(tags.Values, tag => Assert.Equal(0U, tag.Address.SymbolCrc));
            Assert.Equal(1, fake.BrowseVariablesCount);
        }

        [Fact]
        public async Task BulkSymbolResolutionPreservesPackedBooleanStrideForIndexedElement()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo>
                {
                    new VarInfo
                    {
                        Name = "DB.Flags",
                        AccessSequence = "8A0E0001.F",
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL,
                        ArrayElementCount = 6,
                        ArrayDimensions = new[]
                        {
                            new S7CommPlusArrayDimension(1, 2),
                            new S7CommPlusArrayDimension(1, 3),
                        },
                    },
                }),
            };
            var client = CreateClient(fake);

            var tags = await client.GetTagsBySymbolsAsync(new[] { "DB.Flags[2,1]" });

            Assert.Equal("8A0E0001.F.8", tags["DB.Flags[2,1]"].Address.GetAccessString());
        }

        [Fact]
        public async Task BulkSymbolResolutionRetainsCatalogUntilExplicitInvalidation()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo>
                {
                    new VarInfo
                    {
                        Name = "DB.Value",
                        AccessSequence = "8A0E0001.F",
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_INT,
                    },
                }),
            };
            var client = CreateClient(fake);

            await client.GetTagsBySymbolsAsync(new[] { "DB.Value" });
            await client.DisconnectAsync();
            await client.ConnectAsync();
            await client.GetTagsBySymbolsAsync(new[] { "DB.Value" });

            Assert.Equal(1, fake.BrowseVariablesCount);

            await client.InvalidateSymbolCatalogAsync();
            await client.GetTagsBySymbolsAsync(new[] { "DB.Value" });

            Assert.Equal(2, fake.BrowseVariablesCount);
        }

        /// <summary>Ensures the one-shot accessor-catalog API does not seed the client's retained full-symbol cache.</summary>
        [Fact]
        public async Task OneShotAccessorCatalogDoesNotRetainFullSymbolBrowse()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () => (0, new List<VarInfo>
                {
                    new VarInfo
                    {
                        Name = "DB.Value",
                        AccessSequence = "8A0E0001.F",
                        Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_INT,
                    },
                }),
            };
            var client = CreateClient(fake);

            var catalog = await client.CreateTagAccessorCatalogAsync(new[] { "DB.Value" }, "HASH");
            await client.GetTagsBySymbolsAsync(new[] { "DB.Value" });

            Assert.Equal(1, catalog.ResolvedSymbolCount);
            Assert.Equal(2, fake.BrowseVariablesCount);
        }

        [Fact]
        public async Task DisconnectIsIdempotent()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            await client.DisconnectAsync();
            await client.ConnectAsync();
            await client.DisconnectAsync();
            await client.DisconnectAsync();

            Assert.Equal(S7CommPlusConnectionState.Disconnected, client.State);
            Assert.Equal(1, fake.DisconnectCount);
        }

        [Fact]
        public async Task RequestTimeoutIsTypedFailure()
        {
            var fake = new FakeS7CommPlusSession
            {
                BrowseVariablesHandler = () =>
                {
                    Task.Delay(200).Wait();
                    return (0, new List<VarInfo>());
                }
            };
            var client = CreateClient(fake, requestTimeoutMs: 20);

            var ex = await Assert.ThrowsAsync<S7CommPlusTimeoutException>(() => client.BrowseAsync());

            Assert.Equal("Browse", ex.Operation);
            Assert.True(ex.IsTransient);
        }

        [Fact]
        public async Task BrowseTimeoutControlsProtocolWaitAndRestoresRequestTimeout()
        {
            var timeoutDuringBrowse = 0;
            var fake = new FakeS7CommPlusSession();
            fake.BrowseVariablesHandler = () =>
            {
                timeoutDuringBrowse = fake.RequestTimeoutMilliseconds;
                Task.Delay(60).Wait();
                return (0, new List<VarInfo>());
            };
            var client = CreateClient(fake, requestTimeoutMs: 20, browseTimeoutMs: 200);

            await client.GetTagsBySymbolsAsync(new[] { "DB.Value" });

            Assert.Equal(200, timeoutDuringBrowse);
            Assert.Equal(20, fake.RequestTimeoutMilliseconds);
            Assert.Contains(200, fake.RequestTimeoutHistory);
        }

        [Fact]
        public async Task ExecuteWithTimeoutOverridesOneRequestAndRestoresConfiguredTimeout()
        {
            var timeoutDuringRequest = 0;
            var fake = new FakeS7CommPlusSession();
            fake.CpuInfoHandler = () =>
            {
                timeoutDuringRequest = fake.RequestTimeoutMilliseconds;
                Task.Delay(60).Wait();
                return (0, new S7CommPlusCpuInfo { PlcName = "TestCpu" });
            };
            var client = CreateClient(fake, requestTimeoutMs: 20);

            var cpuInfo = await client.ExecuteWithTimeoutAsync(
                TimeSpan.FromMilliseconds(200),
                cancellationToken => client.GetCpuInfoAsync(cancellationToken));

            Assert.Equal("TestCpu", cpuInfo.PlcName);
            Assert.Equal(200, timeoutDuringRequest);
            Assert.Equal(20, fake.RequestTimeoutMilliseconds);
        }

        [Fact]
        public async Task ConcurrentTimeoutScopesKeepIndependentDeadlines()
        {
            var observedTimeouts = new List<int>();
            var observedTimeoutsLock = new object();
            var fake = new FakeS7CommPlusSession();
            fake.CpuInfoHandler = () =>
            {
                lock (observedTimeoutsLock)
                {
                    observedTimeouts.Add(fake.RequestTimeoutMilliseconds);
                }
                return (0, new S7CommPlusCpuInfo { PlcName = "TestCpu" });
            };
            var client = CreateClient(fake, requestTimeoutMs: 20);

            await Task.WhenAll(
                client.ExecuteWithTimeoutAsync(
                    TimeSpan.FromMilliseconds(111),
                    cancellationToken => client.GetCpuInfoAsync(cancellationToken)),
                client.ExecuteWithTimeoutAsync(
                    TimeSpan.FromMilliseconds(222),
                    cancellationToken => client.GetCpuInfoAsync(cancellationToken)));

            Assert.Contains(111, observedTimeouts);
            Assert.Contains(222, observedTimeouts);
            Assert.Equal(20, fake.RequestTimeoutMilliseconds);
        }

        [Fact]
        public async Task ExecuteWithTimeoutSupportsRequestsWithoutResults()
        {
            var timeoutDuringRequest = 0;
            var fake = new FakeS7CommPlusSession();
            fake.SetCpuOperatingStateHandler = _ =>
            {
                timeoutDuringRequest = fake.RequestTimeoutMilliseconds;
                return 0;
            };
            var client = CreateClient(fake, requestTimeoutMs: 20, writeEnabled: true);

            await client.ExecuteWithTimeoutAsync(
                TimeSpan.FromMilliseconds(200),
                cancellationToken => client.StartCpuAsync(cancellationToken));

            Assert.Equal(200, timeoutDuringRequest);
            Assert.Equal(20, fake.RequestTimeoutMilliseconds);
        }

        [Fact]
        public async Task ReadReconnectsOnceAfterTransientDisconnect()
        {
            var fake = new FakeS7CommPlusSession();
            fake.ReadHandler = _ =>
            {
                if (fake.ReadCount == 1)
                {
                    return (S7Consts.errTCPDataReceive, new List<object?>(), new List<ulong>());
                }
                return (0, new List<object?> { new ValueInt(7) }, new List<ulong> { 0 });
            };
            var client = CreateClient(fake);

            var result = await client.ReadAsync(new[] { new ItemAddress("8A0E0001.F") });

            Assert.Equal(2, fake.ConnectCount);
            Assert.Equal(2, fake.ReadCount);
            Assert.True(result.Items[0].IsSuccess);
        }

        [Fact]
        public async Task ProtocolErrorDoesNotReconnect()
        {
            var fake = new FakeS7CommPlusSession
            {
                ReadHandler = _ => (S7Consts.errIsoInvalidPDU3, new List<object?>(), new List<ulong>())
            };
            var client = CreateClient(fake);

            var ex = await Assert.ThrowsAsync<S7CommPlusConnectionException>(() => client.ReadAsync(new[] { new ItemAddress("8A0E0001.F") }));

            Assert.Equal(S7Consts.errIsoInvalidPDU3, ex.ErrorCode);
            Assert.False(ex.IsTransient);
            Assert.Equal(1, fake.ConnectCount);
        }

        [Fact]
        public async Task PartialBatchReadReturnsPerItemErrors()
        {
            var fake = new FakeS7CommPlusSession
            {
                ReadHandler = addresses => (0,
                    new List<object?> { new ValueInt(1), null },
                    new List<ulong> { 0, 0xDEAD })
            };
            var client = CreateClient(fake);

            var result = await client.ReadAsync(new[] { new ItemAddress("8A0E0001.F"), new ItemAddress("8A0E0001.10") });

            Assert.True(result.Items[0].IsSuccess);
            Assert.False(result.Items[1].IsSuccess);
            Assert.Equal((ulong)0xDEAD, result.Items[1].ItemError);
        }

        [Fact]
        public async Task EmptyReadsReturnEmptyResultsWithoutConnecting()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            var addressRead = await client.ReadAsync(Array.Empty<ItemAddress>());
            var tagRead = await client.ReadAsync(Array.Empty<PlcTag>());

            Assert.Empty(addressRead.Items);
            Assert.Empty(tagRead.Items);
            Assert.Equal(0, fake.ConnectCount);
        }

        [Fact]
        public async Task EmptyWritesReturnEmptyResultsWithoutConnecting()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            var addressWrite = await client.WriteAsync(Array.Empty<ItemAddress>(), Array.Empty<PValue>());
            var tagWrite = await client.WriteAsync(Array.Empty<PlcTag>());

            Assert.Empty(addressWrite.Items);
            Assert.Empty(tagWrite.Items);
            Assert.Equal(0, fake.ConnectCount);
        }

        [Fact]
        public void ItemAddressRejectsMalformedAccessStrings()
        {
            Assert.Throws<ArgumentException>(() => new ItemAddress(""));
            Assert.Throws<ArgumentException>(() => new ItemAddress("8A0E0001"));
            Assert.Throws<ArgumentException>(() => new ItemAddress("8A0E0001.NOTHEX"));
        }

        [Fact]
        public void ItemAddressLegacyLocalIdListRemainsMutable()
        {
            var address = new ItemAddress("8A0E0001.F.10");

            address.LID.Add(0x2);
            address.LID.RemoveAt(0);

            Assert.Equal("8A0E0001.10.2", address.GetAccessString());
            Assert.Equal(6u, address.GetNumberOfFields());
        }

        [Fact]
        public void PlcTagCharRejectsCharactersThatDoNotFitOnePlcByte()
        {
            var tag = new PlcTagChar("CharValue", new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_CHAR);

            tag.Value = 'A';

            Assert.Equal('A', tag.Value);
            Assert.Throws<ArgumentOutOfRangeException>(() => tag.Value = '\u20AC');
        }

        [Fact]
        public void VlqDecodersRejectTruncatedValues()
        {
            Assert.Throws<InvalidDataException>(() => S7p.DecodeUInt32Vlq(new MemoryStream(new byte[] { 0x80 }), out _));
            Assert.Throws<InvalidDataException>(() => S7p.DecodeUInt64Vlq(new MemoryStream(new byte[] { 0x80 }), out _));
            Assert.Throws<InvalidDataException>(() => S7p.DecodeInt32Vlq(new MemoryStream(new byte[] { 0x80 }), out _));
            Assert.Throws<InvalidDataException>(() => S7p.DecodeInt64Vlq(new MemoryStream(new byte[] { 0x80 }), out _));
        }

        [Fact]
        public void BlobDecompressorReturnsPayloadWhenOnlyZlibTrailerIsMissing()
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><PlcContentInfo><Entity Id=\"Target\" /></PlcContentInfo>";
            using var compressed = new MemoryStream();
#if NETFRAMEWORK
            compressed.WriteByte(0x78);
            compressed.WriteByte(0x9C);
            using (var zlib = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
#else
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
#endif
            using (var writer = new StreamWriter(zlib))
            {
                writer.Write(xml);
            }

#if NETFRAMEWORK
            compressed.Write(new byte[4], 0, 4);
#endif
            var compressedBytes = compressed.ToArray();
            var truncated = compressedBytes.Take(compressedBytes.Length - 4).ToArray();
            var decompressor = new BlobDecompressor();

            var result = decompressor.decompress(truncated, 0);

            Assert.Equal(xml, result);
        }

        [Fact]
        public async Task BrowseBlockStructureParsesPlcStructureXml()
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <PlcContentInfo>
                  <Entity Id="Target">
                    <GroupStructureV2>
                      <Unit Name="Software unit">
                        <ProgramBlocks>
                          <Group Name="Main folder">
                            <OnlineId>123</OnlineId>
                          </Group>
                        </ProgramBlocks>
                        <PlcTagTables>
                          <Group Name="Tag folders">
                            <OnlineId>Default tag table</OnlineId>
                          </Group>
                        </PlcTagTables>
                        <PlcDataTypes />
                      </Unit>
                    </GroupStructureV2>
                  </Entity>
                  <Entity Rid="123">
                    <Header Name="MainBlock" Number="1" Type="FB" SubType="FB" ProgrammingLanguage="SCL" LastModified="2026-05-22T00:00:00Z" />
                  </Entity>
                  <Entity>
                    <TagTables>
                      <TagTables>
                        <Container>
                          <TagTable Name="Default tag table" LoadRelevantModifiedTime="2026-05-22T00:01:00Z" />
                        </Container>
                      </TagTables>
                    </TagTables>
                  </Entity>
                </PlcContentInfo>
                """;
            var fake = new FakeS7CommPlusSession
            {
                PlcStructureXmlHandler = () => (0, xml)
            };
            var client = CreateClient(fake);

            var snapshot = await client.GetPlcStructureXmlAsync();
            var structure = await client.BrowseBlockStructureAsync();

            Assert.Equal(xml, snapshot.Xml);
            Assert.Equal(ExpectedSha256Hex(xml), snapshot.ProgramChangeMarker.StructureHash);
            Assert.Equal(new DateTime(2026, 5, 22, 0, 1, 0, DateTimeKind.Utc), snapshot.ProgramChangeMarker.LastModified);
            Assert.Equal(1, snapshot.ProgramChangeMarker.BlockCount);
            Assert.Equal(1, snapshot.ProgramChangeMarker.TagTableCount);
            var unit = Assert.Single(structure);
            Assert.Equal(S7CommPlusPlcStructureNodeKind.Unit, unit.Kind);
            Assert.Equal("Software unit", unit.Name);
            var programBlocks = unit.Children.Single(child => child.Name == "Program blocks");
            var mainFolder = Assert.Single(programBlocks.Children);
            var block = Assert.Single(mainFolder.Children);
            Assert.Equal(S7CommPlusPlcStructureNodeKind.Block, block.Kind);
            Assert.Equal((uint)123, block.RelationId);
            Assert.Equal("MainBlock", block.Name);
            Assert.Equal(1, block.Number);
            Assert.Equal("FB", block.BlockType);
            Assert.Equal("SCL", block.BlockLanguage);
            var tagTable = Assert.Single(unit.Children.Single(child => child.Name == "PLC tags").Children.Single().Children);
            Assert.Equal(S7CommPlusPlcStructureNodeKind.Item, tagTable.Kind);
            Assert.Equal("Default tag table", tagTable.Name);
            Assert.Equal("TagTable", tagTable.BlockType);
        }

        private static string ExpectedSha256Hex(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
        }

        [Fact]
        public async Task ConcurrentReadsAreSerialized()
        {
            var fake = new FakeS7CommPlusSession
            {
                ReadHandler = _ =>
                {
                    Task.Delay(50).Wait();
                    return (0, new List<object?> { new ValueInt(1) }, new List<ulong> { 0 });
                }
            };
            var client = CreateClient(fake);
            var addresses = new[] { new ItemAddress("8A0E0001.F") };

            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.ReadAsync(addresses)));

            Assert.Equal(1, fake.MaxConcurrentReads);
            Assert.Equal(5, fake.ReadCount);
        }

        [Fact]
        public async Task WritesAreBlockedByDefault()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            await Assert.ThrowsAsync<S7CommPlusWriteDisabledException>(() =>
                client.WriteAsync(new[] { new ItemAddress("8A0E0001.F") }, new PValue[] { new ValueInt(1) }));
        }

        [Fact]
        public async Task CpuStartStopAreBlockedByDefault()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            await Assert.ThrowsAsync<S7CommPlusWriteDisabledException>(() => client.StopCpuAsync());
            await Assert.ThrowsAsync<S7CommPlusWriteDisabledException>(() => client.StartCpuAsync());

            Assert.Equal(0, fake.CpuOperatingStateWriteCount);
        }

        [Fact]
        public async Task CpuStartStopUseOperatingStateRequests()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake, writeEnabled: true);

            await client.StopCpuAsync();
            await client.StartCpuAsync();

            Assert.Equal(new[] { 1, 3 }, fake.CpuOperatingStateRequests);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task SecurityModeIsPassedToConnectionAndNegotiatedModeIsExposed()
        {
            var fake = new FakeS7CommPlusSession
            {
                ConnectHandler = options =>
                {
                    Assert.Equal(S7CommPlusSecurityMode.LegacyChallenge, options.SecurityMode);
                    Assert.Equal(S7CommPlusTlsBackend.BouncyCastle, options.TlsBackend);
                    options.NegotiatedSecurityMode = S7CommPlusSecurityMode.LegacyChallenge;
                    return 0;
                }
            };
            var client = new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    SecurityMode = S7CommPlusSecurityMode.LegacyChallenge,
                    TlsBackend = S7CommPlusTlsBackend.BouncyCastle,
                    ConnectTimeout = TimeSpan.FromMilliseconds(500),
                    RequestTimeout = TimeSpan.FromMilliseconds(500),
                    DisconnectTimeout = TimeSpan.FromMilliseconds(100)
                },
                () => fake);

            await client.ConnectAsync();

            Assert.Equal(S7CommPlusSecurityMode.LegacyChallenge, client.Options.NegotiatedSecurityMode);
            Assert.Equal(S7CommPlusTlsBackend.BouncyCastle, client.Options.TlsBackend);
        }

        [Fact]
        public async Task AutoSecurityModeAllowsOneConnectTimeoutPerSecurityAttempt()
        {
            var fake = new FakeS7CommPlusSession
            {
                ConnectHandler = options =>
                {
                    Assert.Equal(TimeSpan.FromMilliseconds(200), options.ConnectTimeout);
                    Thread.Sleep(300);
                    return 0;
                }
            };
            var client = new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    SecurityMode = S7CommPlusSecurityMode.Auto,
                    ConnectTimeout = TimeSpan.FromMilliseconds(200),
                    RequestTimeout = TimeSpan.FromMilliseconds(500),
                    DisconnectTimeout = TimeSpan.FromMilliseconds(100)
                },
                () => fake);

            await client.ConnectAsync();

            Assert.True(client.IsConnected);
        }

        [Fact]
        public void BouncyCastleIsDefaultTlsBackend()
        {
            var options = new S7CommPlusClientOptions();

            Assert.Equal(S7CommPlusTlsBackend.BouncyCastle, options.TlsBackend);
        }

        [Fact]
        public void AutoIsDefaultSecurityMode()
        {
            var options = new S7CommPlusClientOptions();

            Assert.Equal(S7CommPlusSecurityMode.Auto, options.SecurityMode);
        }

        [Fact]
        public void LegacySessionKeyRefreshDefaultsToTwentyFiveMinutes()
        {
            var options = new S7CommPlusClientOptions();

            Assert.True(options.LegacySessionKeyRefreshEnabled);
            Assert.Equal(TimeSpan.FromMinutes(25), options.LegacySessionKeyRefreshInterval);
        }

        [Fact]
        public void LegacyPublicKeyIdQualifiesOnlyFamilyOnlyFingerprint()
        {
            var options = new S7CommPlusClientOptions
            {
                LegacyPublicKeyId = " 181B7B0847D11694 "
            };

            Assert.Equal("00:181B7B0847D11694", options.GetLegacyPublicKeyFingerprint("00"));
            Assert.Equal(
                "00:AAAAAAAAAAAAAAAA",
                options.GetLegacyPublicKeyFingerprint("00:AAAAAAAAAAAAAAAA"));
        }

        [Fact]
        public void LegacyPublicKeyFallbackIsEnabledAndCatalogIsDeterministic()
        {
            var options = new S7CommPlusClientOptions();
            var fingerprints = LegacyPublicKeyCatalog.GetFingerprints("00");

            Assert.True(options.LegacyPublicKeyFallbackEnabled);
            Assert.NotEmpty(fingerprints);
            Assert.Contains("00:181B7B0847D11694", fingerprints);
            Assert.Equal(
                fingerprints.OrderBy(value => value, StringComparer.Ordinal),
                fingerprints);
        }

        [Fact]
        public async Task DiscoveredLegacyPublicKeySurvivesProtocolSessionReplacement()
        {
            const string fingerprint = "00:181B7B0847D11694";
            var firstSession = new FakeS7CommPlusSession
            {
                ConnectHandler = options =>
                {
                    options.LegacyPublicKeyFingerprintOverride = fingerprint;
                    return 0;
                }
            };
            string? secondSessionFingerprint = null;
            var secondSession = new FakeS7CommPlusSession
            {
                ConnectHandler = options =>
                {
                    secondSessionFingerprint =
                        options.LegacyPublicKeyFingerprintOverride;
                    return 0;
                }
            };
            var sessions = new Queue<IS7CommPlusSession>(
                new IS7CommPlusSession[] { firstSession, secondSession });
            await using var client = new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    SecurityMode = S7CommPlusSecurityMode.LegacyChallenge
                },
                () => sessions.Dequeue());

            await client.ConnectAsync();
            await client.DisconnectAsync();
            await client.ConnectAsync();

            Assert.Equal(fingerprint, secondSessionFingerprint);
        }

        [Theory]
        [InlineData("181B7B0847D1169")]
        [InlineData("181B7B0847D1169XX")]
        public void LegacyPublicKeyIdMustBeSixteenHexCharacters(string keyId)
        {
            var ex = Assert.Throws<ArgumentException>(() => new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    LegacyPublicKeyId = keyId
                },
                () => new FakeS7CommPlusSession()));

            Assert.Equal("LegacyPublicKeyId", ex.ParamName);
        }

        [Fact]
        public void EnabledLegacySessionKeyRefreshRequiresPositiveInterval()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    LegacySessionKeyRefreshInterval = TimeSpan.Zero
                },
                () => new FakeS7CommPlusSession()));

            Assert.Equal("LegacySessionKeyRefreshInterval", ex.ParamName);
        }

        [Fact]
        public async Task ConnectErrorIncludesSessionErrorDetail()
        {
            var fake = new FakeS7CommPlusSession
            {
                LastErrorDetail = "Could not load native OpenSSL dependency 'runtimes/win-arm64/native/libcrypto-3-arm64.dll'."
            };
            fake.ConnectHandler = _ => S7Consts.errOpenSSL;
            var client = CreateClient(fake);

            var ex = await Assert.ThrowsAsync<S7CommPlusConnectionException>(() => client.ConnectAsync());

            Assert.Contains("OPENSSL", ex.Message);
            Assert.Contains("libcrypto-3-arm64.dll", ex.Message);
        }

        [Fact]
        public void RemoteTsapMustBeAscii()
        {
            var ex = Assert.Throws<ArgumentException>(() => new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    RemoteTsap = "SIMATIC-ROOT-HMI-\u00C4"
                },
                () => new FakeS7CommPlusSession()));

            Assert.Equal("RemoteTsap", ex.ParamName);
        }

        [Fact]
        public void RemoteTsapMustFitCotpParameterLength()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    RemoteTsap = new string('A', 256)
                },
                () => new FakeS7CommPlusSession()));

            Assert.Equal("RemoteTsap", ex.ParamName);
        }

        [Fact]
        public void SiemensTsapDefaultsAreAvailable()
        {
            Assert.Equal(102, S7CommPlusDefaults.IsoTcpPort);
            Assert.Equal((ushort)0x0600, S7CommPlusDefaults.LocalTsap);
            Assert.Equal("SIMATIC-ROOT-HMI", S7CommPlusDefaults.RemoteTsapHmi);
            Assert.Equal("SIMATIC-ROOT-ES", S7CommPlusDefaults.RemoteTsapEs);
        }

        [Fact]
        public async Task LegitimateAsyncUsesTypedLegitimationErrorAndDoesNotRequireWriteEnablement()
        {
            var fake = new FakeS7CommPlusSession
            {
                LegitimateHandler = (password, username) =>
                {
                    Assert.Equal("secret", password);
                    Assert.Equal("operator", username);
                    return S7Consts.errCliAccessDenied;
                }
            };
            var client = CreateClient(fake);

            var ex = await Assert.ThrowsAsync<S7CommPlusLegitimationException>(() =>
                client.LegitimateAsync("secret", "operator"));

            Assert.Equal("Legitimate", ex.Operation);
            Assert.False(ex.IsTransient);
        }

        [Fact]
        public async Task GetCommunicationResourcesUsesReadPipeline()
        {
            var fake = new FakeS7CommPlusSession();
            var client = CreateClient(fake);

            var resources = await client.GetCommunicationResourcesAsync();

            Assert.Equal(20, resources.TagsPerReadRequestMax);
            Assert.Equal(20, resources.TagsPerWriteRequestMax);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task GetCpuStateUsesReadPipeline()
        {
            var fake = new FakeS7CommPlusSession
            {
                CpuStateHandler = () => (0, new S7CommPlusCpuState(5, S7CommPlusCpuOperatingState.Run, 2, "Run"))
            };
            var client = CreateClient(fake);

            var cpuState = await client.GetCpuStateAsync();

            Assert.Equal(5, cpuState.RawOperatingState);
            Assert.Equal(S7CommPlusCpuOperatingState.Run, cpuState.OperatingState);
            Assert.Equal(2, cpuState.RawStateSwitch);
            Assert.Equal("Run", cpuState.StateSwitch);
            Assert.True(cpuState.IsRun);
            Assert.False(cpuState.IsStop);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task GetCpuCycleTimeUsesReadPipeline()
        {
            var fake = new FakeS7CommPlusSession
            {
                CpuCycleTimeHandler = () => (0, new S7CommPlusCpuCycleTime(0, 150, 50.007, 50.012, 50.654))
            };
            var client = CreateClient(fake);

            var cycleTime = await client.GetCpuCycleTimeAsync();

            Assert.Equal(0, cycleTime.ConfiguredMinimumMilliseconds);
            Assert.Equal(150, cycleTime.ConfiguredMaximumMilliseconds);
            Assert.Equal(50.007, cycleTime.ShortestMilliseconds);
            Assert.Equal(50.012, cycleTime.CurrentMilliseconds);
            Assert.Equal(50.654, cycleTime.LongestMilliseconds);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task GetCpuMemoryUsageUsesReadPipeline()
        {
            var fake = new FakeS7CommPlusSession
            {
                CpuMemoryUsageHandler = () => (0, new S7CommPlusCpuMemoryUsage(new[]
                {
                    new S7CommPlusCpuMemoryArea("load", "Load memory", 1000, 120),
                    new S7CommPlusCpuMemoryArea("retain", "Retain memory", 500, 25)
                }))
            };
            var client = CreateClient(fake);

            var memoryUsage = await client.GetCpuMemoryUsageAsync();

            Assert.Collection(
                memoryUsage.Areas,
                area =>
                {
                    Assert.Equal("load", area.Key);
                    Assert.Equal("Load memory", area.Name);
                    Assert.Equal(1000, area.TotalBytes);
                    Assert.Equal(120, area.UsedBytes);
                    Assert.Equal(880, area.FreeBytes);
                    Assert.Equal(12, area.UsedPercent);
                    Assert.Equal(88, area.FreePercent);
                },
                area =>
                {
                    Assert.Equal("retain", area.Key);
                    Assert.Equal(500, area.TotalBytes);
                    Assert.Equal(25, area.UsedBytes);
                });
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task GetCpuCultureInfoUsesReadPipeline()
        {
            var fake = new FakeS7CommPlusSession
            {
                CpuCultureInfoHandler = () => (0, new S7CommPlusCpuCultureInfo(new[] { 1031, 1033 }))
            };
            var client = CreateClient(fake);

            var cultureInfo = await client.GetCpuCultureInfoAsync();

            Assert.Equal(new[] { 1031, 1033 }, cultureInfo.LanguageIds);
            Assert.Equal("de-DE", cultureInfo.PrimaryCulture.Name);
            Assert.Contains(cultureInfo.Cultures, culture => culture.Name == "en-US");
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public void CpuCultureInfoPreservesUnresolvedLanguageIds()
        {
            var cultureInfo = new S7CommPlusCpuCultureInfo(new[] { 1031, 0xffff });

            Assert.Equal(new[] { 1031, 0xffff }, cultureInfo.LanguageIds);
            Assert.Equal(new[] { 0xffff }, cultureInfo.UnresolvedLanguageIds);
            Assert.Equal("de-DE", cultureInfo.PrimaryCulture.Name);
        }

        [Fact]
        public async Task GetActiveAlarmsUsesReadPipeline()
        {
            var fake = new FakeS7CommPlusSession
            {
                ActiveAlarmsHandler = () => (0, new List<S7CommPlusAlarm>())
            };
            var client = CreateClient(fake);

            var alarms = await client.GetActiveAlarmsAsync(languageId: 1033);

            Assert.Empty(alarms);
            Assert.Equal(1033, fake.LastActiveAlarmsLanguageId);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task GetActiveAlarmsWithoutLanguageIdRequestsAllLanguages()
        {
            var fake = new FakeS7CommPlusSession
            {
                ActiveAlarmsHandler = () => (0, new List<S7CommPlusAlarm>())
            };
            var client = CreateClient(fake);

            var alarms = await client.GetActiveAlarmsAsync();

            Assert.Empty(alarms);
            Assert.Equal(0, fake.LastActiveAlarmsLanguageId);
        }

        [Fact]
        public void AlarmExposesSourceRelationAndAlarmIds()
        {
            var alarm = new S7CommPlusAlarm
            {
                CpuAlarmId = ((ulong)0x12345678 << 32) | ((ulong)0x9abc << 16)
            };

            Assert.Equal((uint)0x12345678, alarm.SourceRelationId);
            Assert.Equal((ushort)0x9abc, alarm.SourceAlarmId);
        }

        [Fact]
        public void AlarmTextsCanReadAllNotificationLanguages()
        {
            var blob = new ValueBlobSparseArray(new Dictionary<uint, ValueBlobSparseArray.BlobEntry>
            {
                { ((uint)1031 << 16) | 2, BlobText("Alarm deutsch") },
                { ((uint)1033 << 16) | 2, BlobText("Alarm english") },
                { ((uint)1033 << 16) | 1, BlobText("Info english") }
            });

            var texts = S7CommPlusAlarmTexts.FromNotificationBlobAllLanguages(blob);

            Assert.Equal("Alarm deutsch", texts[1031].AlarmText);
            Assert.Equal("Alarm english", texts[1033].AlarmText);
            Assert.Equal("Info english", texts[1033].Infotext);
        }

        [Fact]
        public async Task TagSubscriptionPublishesValuesAndDeletesOnDispose()
        {
            var allowNotifications = new ManualResetEventSlim(false);
            var fake = new FakeS7CommPlusSession();
            fake.WaitForTagSubscriptionHandler = (_, _) =>
            {
                allowNotifications.Wait(TimeSpan.FromSeconds(1));
                if (fake.TagSubscriptionWaitCount == 1)
                {
                    return (0, new List<Notification>
                    {
                        new Notification(2)
                        {
                            Add1Timestamp = DateTime.UtcNow,
                            NotificationCreditTick = 1,
                            NotificationSequenceNumber = 7,
                            Values = { [1] = new ValueInt(42) }
                        }
                    });
                }

                Thread.Sleep(10);
                return (S7Consts.errCliJobTimeout, new List<Notification>());
            };
            var client = CreateClient(fake);
            var tag = PlcTags.TagFactory("DB.Value", new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);
            var received = new TaskCompletionSource<S7CommPlusTagNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = await client.SubscribeTagsAsync(new[] { tag }, FastSubscriptionOptions());
            subscription.NotificationReceived += (_, args) => received.TrySetResult(args.Notification);
            allowNotifications.Set();

            var notification = await RuntimeCompatibility.WaitAsync(received.Task, TimeSpan.FromSeconds(2));
            await subscription.StopAsync();

            Assert.Equal((uint)7, notification.SequenceNumber);
            Assert.Single(notification.Items);
            Assert.True(notification.Items[0].IsSuccess);
            Assert.Same(tag, notification.Items[0].Tag);
            Assert.Equal(1, fake.TagSubscriptionCreateCount);
            Assert.Equal(1, fake.TagSubscriptionDeleteCount);
        }

        [Fact]
        public void TisResultParserUsesRequestModelOffsets()
        {
            var model = new S7CommPlusTisResultModel();
            var watchPoint = new S7CommPlusTisWatchPointModel
            {
                NetworkId = "cu-1",
                Uid = "21",
                Pin = "operand",
                RloOffset = 20
            };
            watchPoint.Values.Add(new S7CommPlusTisValueModel
            {
                NetworkId = "cu-1",
                Uid = "21",
                Pin = "operand",
                ValueOffset = 30,
                ByteLength = 1,
                ValidityOffset = 31,
                ValidCountOffset = 32
            });
            model.WatchPoints.Add(watchPoint);
            var result = new byte[40];
            result[20] = 0x80;
            result[23] = 0x05;
            result[30] = 1;
            result[31] = 0x7f;
            result[33] = 2;

            var parsed = S7CommPlusTisResultParser.Parse(result, model);

            var parsedWatchPoint = Assert.Single(parsed);
            Assert.True(parsedWatchPoint.Rlo);
            Assert.Equal((uint)5, parsedWatchPoint.ExecutionCount);
            var parsedValue = Assert.Single(parsedWatchPoint.Values);
            Assert.True(parsedValue.BoolValue);
            Assert.Equal((byte)0x7f, parsedValue.Validity);
            Assert.Equal((ushort)2, parsedValue.ValidCount);
            Assert.Equal("TRUE", parsedValue.DisplayValue);
        }

        [Fact]
        public void NotificationParsesCapturedOnlineStatusTableItems()
        {
            using var stream = CreateNotificationBody();
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.OnlineStatusTable);
            stream.WriteByte(0x74);
            stream.WriteByte(0x00);
            stream.WriteByte(0x08);
            stream.WriteByte(0x02);
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.OnlineStatusTable);
            stream.WriteByte(0x70);
            stream.WriteByte(0x00);
            stream.WriteByte(0x08);
            stream.WriteByte(0x01);
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.EndOfList);
            stream.WriteByte(0);
            stream.Position = 0;

            var notification = new Notification(2);
            notification.Deserialize(stream);

            Assert.Equal(0x74000802u, notification.OnlineStatusTableValues[0x74]);
            Assert.Equal(0x70000801u, notification.OnlineStatusTableValues[0x70]);
            Assert.Empty(notification.Values);
        }

        [Fact]
        public void NotificationParsesCapturedTisResultAsBlobValue()
        {
            var result = new byte[116];
            result[104] = 0x80;
            result[107] = 0x01;
            result[108] = 0x00;
            result[111] = 0x02;
            result[112] = 0x80;
            result[115] = 0x03;
            using var stream = CreateNotificationBody();
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.ValueWithUInt32Reference);
            WriteUInt32(stream, 9);
            new ValueBlob(0, result).Serialize(stream);
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.ValueWithUInt32Reference);
            WriteUInt32(stream, 10);
            new ValueUSInt(1).Serialize(stream);
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.ValueWithUInt32Reference);
            WriteUInt32(stream, 11);
            new ValueBool(true).Serialize(stream);
            stream.WriteByte((byte)S7CommPlusNotificationReturnCode.EndOfList);
            stream.WriteByte(0);
            stream.Position = 0;

            var notification = new Notification(2);
            notification.Deserialize(stream);
            var model = new S7CommPlusTisResultModel();
            model.WatchPoints.Add(new S7CommPlusTisWatchPointModel { RloOffset = 104 });
            model.WatchPoints.Add(new S7CommPlusTisWatchPointModel { RloOffset = 108 });
            model.WatchPoints.Add(new S7CommPlusTisWatchPointModel { RloOffset = 112 });

            var blob = Assert.IsType<ValueBlob>(notification.Values[9]);
            var parsed = S7CommPlusTisResultParser.Parse(blob.GetValue(), model);

            Assert.True(parsed[0].Rlo);
            Assert.False(parsed[1].Rlo);
            Assert.True(parsed[2].Rlo);
            Assert.Equal((byte)1, Assert.IsType<ValueUSInt>(notification.Values[10]).GetValue());
            Assert.True(Assert.IsType<ValueBool>(notification.Values[11]).GetValue());
        }

        [Fact]
        public async Task TisWatchSubscriptionPublishesUpdatesAndDeletesOnDispose()
        {
            var allowNotifications = new ManualResetEventSlim(false);
            var fake = new FakeS7CommPlusSession();
            var notification = new S7CommPlusTisWatchNotification(
                DateTime.UtcNow,
                123,
                1,
                true,
                1,
                new byte[] { 1, 2, 3 },
                new[]
                {
                    new S7CommPlusTisWatchPointResult("cu-1", "21", "operand", true, 0x80000001, 1, Array.Empty<S7CommPlusTisValueResult>())
                });
            fake.WaitForTisWatchSubscriptionHandler = _ =>
            {
                allowNotifications.Wait(TimeSpan.FromSeconds(1));
                if (fake.TisWatchSubscriptionWaitCount == 1)
                {
                    return (0, new List<S7CommPlusTisWatchNotification> { notification });
                }

                Thread.Sleep(10);
                return (S7Consts.errCliJobTimeout, new List<S7CommPlusTisWatchNotification>());
            };
            var client = CreateClient(fake);
            var received = new TaskCompletionSource<S7CommPlusTisWatchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = await client.OpenBlockOnlineViewAsync(new S7CommPlusTisWatchRequest
            {
                RequestBlob = new byte[] { 0x40, 0x80, 0x32, 0x06 },
                TriggerBlob = new byte[] { 0x10, 0x06 },
            }, FastSubscriptionOptions());
            subscription.NotificationReceived += (_, args) => received.TrySetResult(args.Notification);
            allowNotifications.Set();

            var update = await RuntimeCompatibility.WaitAsync(received.Task, TimeSpan.FromSeconds(10));
            await subscription.StopAsync();

            Assert.Equal((uint)123, update.SequenceNumber);
            Assert.Single(update.WatchPoints);
            Assert.True(update.WatchPoints[0].Rlo);
            Assert.Equal(1, fake.TisWatchSubscriptionCreateCount);
            Assert.Equal(1, fake.TisWatchSubscriptionDeleteCount);
        }

        [Fact]
        public async Task MultipleTisWatchSubscriptionsUseOneConnectionAndDistinctHandles()
        {
            var fake = new FakeS7CommPlusSession
            {
                WaitForTisWatchSubscriptionByIdHandler = (_, _) =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<S7CommPlusTisWatchNotification>());
                }
            };
            var client = CreateClient(fake);
            var options = FastSubscriptionOptions();

            await using var first = await client.OpenBlockOnlineViewAsync(CreateTisWatchRequest(1), options);
            await using var second = await client.OpenBlockOnlineViewAsync(CreateTisWatchRequest(2), options);
            await using var third = await client.OpenBlockOnlineViewAsync(CreateTisWatchRequest(3), options);

            await first.StopAsync();
            await second.StopAsync();
            await third.StopAsync();

            Assert.Equal(1, fake.ConnectCount);
            Assert.Equal(3, fake.TisWatchSubscriptionCreateCount);
            Assert.Equal(3, fake.TisWatchSubscriptionDeleteCount);
            Assert.Equal(3, fake.CreatedTisWatchSubscriptionIds.Distinct().Count());
            Assert.Equal(
                fake.CreatedTisWatchSubscriptionIds.OrderBy(id => id),
                fake.DeletedTisWatchSubscriptionIds.OrderBy(id => id));
        }

        [Fact]
        public async Task TagSubscriptionPublishesPerItemErrors()
        {
            var allowNotifications = new ManualResetEventSlim(false);
            var fake = new FakeS7CommPlusSession();
            fake.WaitForTagSubscriptionHandler = (_, _) =>
            {
                allowNotifications.Wait(TimeSpan.FromSeconds(1));
                if (fake.TagSubscriptionWaitCount == 1)
                {
                    return (0, new List<Notification>
                    {
                        new Notification(2)
                        {
                            ReturnValues = { [1] = 0x13 }
                        }
                    });
                }

                Thread.Sleep(10);
                return (S7Consts.errCliJobTimeout, new List<Notification>());
            };
            var client = CreateClient(fake);
            var tag = PlcTags.TagFactory("DB.Value", new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);
            var received = new TaskCompletionSource<S7CommPlusTagNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = await client.SubscribeTagsAsync(new[] { tag }, FastSubscriptionOptions());
            subscription.NotificationReceived += (_, args) => received.TrySetResult(args.Notification);
            allowNotifications.Set();

            var notification = await RuntimeCompatibility.WaitAsync(received.Task, TimeSpan.FromSeconds(2));
            await subscription.StopAsync();

            Assert.Single(notification.Items);
            Assert.False(notification.Items[0].IsSuccess);
            Assert.Equal((ulong)0x13, notification.Items[0].ItemError);
            Assert.Equal(1, fake.TagSubscriptionDeleteCount);
        }

        [Fact]
        public async Task SubscriptionIdleTimeoutDoesNotFaultByDefault()
        {
            var fake = new FakeS7CommPlusSession
            {
                WaitForTagSubscriptionHandler = (_, _) =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<Notification>());
                }
            };
            var client = CreateClient(fake);
            var tag = PlcTags.TagFactory("DB.Value", new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);

            await using var subscription = await client.SubscribeTagsAsync(new[] { tag }, FastSubscriptionOptions());
            await Task.Delay(40);
            await subscription.StopAsync();

            Assert.Equal(S7CommPlusSubscriptionState.Stopped, subscription.State);
            Assert.Equal(1, fake.TagSubscriptionDeleteCount);
        }

        [Fact]
        public async Task ReadCanRunWhileSubscriptionIsActive()
        {
            var fake = new FakeS7CommPlusSession
            {
                WaitForTagSubscriptionHandler = (_, _) =>
                {
                    Thread.Sleep(10);
                    return (S7Consts.errCliJobTimeout, new List<Notification>());
                }
            };
            var client = CreateClient(fake);
            var tag = PlcTags.TagFactory("DB.Value", new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);

            await using var subscription = await client.SubscribeTagsAsync(new[] { tag }, FastSubscriptionOptions());
            var readTask = client.ReadAsync(new[] { tag });
            await Task.Delay(40);

            var read = await RuntimeCompatibility.WaitAsync(readTask, TimeSpan.FromSeconds(2));
            await subscription.StopAsync();

            Assert.Single(read.Items);
            Assert.Equal(1, fake.ReadCount);
        }

        [Fact]
        public async Task AlarmAndTisSubscriptionsCanBeCreatedOnSameClientConnection()
        {
            var fake = new FakeS7CommPlusSession
            {
                WaitForAlarmSubscriptionHandler = (_, _) =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<Notification>());
                },
                WaitForTisWatchSubscriptionHandler = _ =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<S7CommPlusTisWatchNotification>());
                }
            };
            var client = CreateClient(fake);

            await using var alarmSubscription = await client.SubscribeAlarmsAsync(
                options: new S7CommPlusSubscriptionOptions
                {
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });
            await using var tisSubscription = await client.OpenBlockOnlineViewAsync(new S7CommPlusTisWatchRequest
            {
                RequestBlob = new byte[] { 0x40, 0x80, 0x32, 0x06 },
                TriggerBlob = new byte[] { 0x10, 0x06 },
            }, FastSubscriptionOptions());

            await alarmSubscription.StopAsync();
            await tisSubscription.StopAsync();

            Assert.Equal(1, fake.ConnectCount);
            Assert.Equal(1, fake.AlarmSubscriptionCreateCount);
            Assert.Equal(1, fake.TisWatchSubscriptionCreateCount);
            Assert.Equal(1, fake.AlarmSubscriptionDeleteCount);
            Assert.Equal(1, fake.TisWatchSubscriptionDeleteCount);
        }

        [Fact]
        public async Task ActiveAlarmsCanBeReadAfterAlarmSubscriptionStartsOnSameConnection()
        {
            var fake = new FakeS7CommPlusSession
            {
                ActiveAlarmsHandler = () => (0, new List<S7CommPlusAlarm>()),
                WaitForAlarmSubscriptionHandler = (_, _) =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<Notification>());
                }
            };
            var client = CreateClient(fake);

            await using var subscription = await client.SubscribeAlarmsAsync(
                1033,
                new S7CommPlusSubscriptionOptions
                {
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });

            var activeAlarms = await client.GetActiveAlarmsAsync(1033);
            await subscription.StopAsync();

            Assert.Empty(activeAlarms);
            Assert.Equal(1, fake.ConnectCount);
            Assert.Equal(1, fake.AlarmSubscriptionCreateCount);
            Assert.Equal(1, fake.AlarmSubscriptionDeleteCount);
            Assert.Equal(1033, fake.LastActiveAlarmsLanguageId);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task SubscriptionCommunicationFailureFaultsSubscriptionAndClient()
        {
            var fake = new FakeS7CommPlusSession
            {
                WaitForTagSubscriptionHandler = (_, _) => (S7Consts.errTCPDataReceive, new List<Notification>())
            };
            var client = CreateClient(fake);
            var tag = PlcTags.TagFactory("DB.Value", new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);
            var error = new TaskCompletionSource<S7CommPlusException>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = await client.SubscribeTagsAsync(new[] { tag }, FastSubscriptionOptions());
            subscription.CommunicationError += (_, args) => error.TrySetResult(args.Exception);

            var ex = await RuntimeCompatibility.WaitAsync(error.Task, TimeSpan.FromSeconds(2));

            Assert.Equal("WaitForTagSubscriptionNotifications", ex.Operation);
            Assert.Equal(S7CommPlusSubscriptionState.Faulted, subscription.State);
            Assert.Equal(S7CommPlusConnectionState.Faulted, client.State);
            await Assert.ThrowsAsync<S7CommPlusConnectionException>(() => subscription.Completion);
            Assert.Equal(1, fake.TagSubscriptionDeleteCount);
        }

        [Fact]
        public async Task AlarmSubscriptionCreatesWithLanguagesAndDeletesOnDispose()
        {
            uint[]? capturedLanguages = null;
            var fake = new FakeS7CommPlusSession
            {
                CreateAlarmSubscriptionHandler = (languages, creditLimit) =>
                {
                    capturedLanguages = languages;
                    Assert.Equal((short)3, creditLimit);
                    return 0;
                },
                WaitForAlarmSubscriptionHandler = (_, _) =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<Notification>());
                }
            };
            var client = CreateClient(fake);

            await using var subscription = await client.SubscribeAlarmsAsync(
                new[] { 1031, 1033 },
                alarmTextLanguageId: 1033,
                options: new S7CommPlusSubscriptionOptions
                {
                    InitialCreditLimit = 3,
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });
            await subscription.StopAsync();

            Assert.Equal(new uint[] { 1031, 1033 }, capturedLanguages);
            Assert.Equal(1033, subscription.AlarmTextLanguageId);
            Assert.Equal(new[] { 1031, 1033 }, subscription.LanguageIds);
            Assert.Equal(0, fake.GetCpuCultureInfoCount);
            Assert.Equal(1, fake.AlarmSubscriptionCreateCount);
            Assert.Equal(1, fake.AlarmSubscriptionDeleteCount);
            Assert.Equal(0, fake.DisconnectCount);
            Assert.Equal(S7CommPlusConnectionState.Connected, client.State);
        }

        [Fact]
        public async Task AlarmSubscriptionWithoutLanguageIdUsesFirstThreeCpuLanguages()
        {
            uint[]? capturedLanguages = null;
            var fake = new FakeS7CommPlusSession
            {
                CpuCultureInfoHandler = () => (0, new S7CommPlusCpuCultureInfo(new[] { 1031, 2057, 1036, 1040 })),
                CreateAlarmSubscriptionHandler = (languages, _) =>
                {
                    capturedLanguages = languages;
                    return 0;
                },
                WaitForAlarmSubscriptionHandler = (_, _) =>
                {
                    Thread.Sleep(5);
                    return (S7Consts.errCliJobTimeout, new List<Notification>());
                }
            };
            var client = CreateClient(fake);

            await using var subscription = await client.SubscribeAlarmsAsync(
                options: new S7CommPlusSubscriptionOptions
                {
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });
            await subscription.StopAsync();

            Assert.Equal(new uint[] { 1031, 2057, 1036 }, capturedLanguages);
            Assert.Equal(new[] { 1031, 2057, 1036 }, subscription.LanguageIds);
            Assert.False(subscription.ReceivesAllAlarmTextLanguages);
            Assert.Equal(1031, subscription.AlarmTextLanguageId);
            Assert.Equal(1, fake.GetCpuCultureInfoCount);
        }

        [Fact]
        public async Task AlarmSubscriptionConnectionLossSkipsProtocolDelete()
        {
            var fake = new FakeS7CommPlusSession
            {
                WaitForAlarmSubscriptionHandler = (_, _) => (S7Consts.errTCPNotConnected, new List<Notification>())
            };
            var client = CreateClient(fake);
            var error = new TaskCompletionSource<S7CommPlusException>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = await client.SubscribeAlarmsAsync(
                languageId: 1031,
                options: new S7CommPlusSubscriptionOptions
                {
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });
            subscription.CommunicationError += (_, args) => error.TrySetResult(args.Exception);

            var ex = await RuntimeCompatibility.WaitAsync(error.Task, TimeSpan.FromSeconds(2));

            Assert.Equal("WaitForAlarmNotifications", ex.Operation);
            Assert.Equal(S7Consts.errTCPNotConnected, ex.ErrorCode);
            Assert.Equal(S7CommPlusSubscriptionState.Faulted, subscription.State);
            Assert.Equal(S7CommPlusConnectionState.Faulted, client.State);
            await Assert.ThrowsAsync<S7CommPlusConnectionException>(() => subscription.Completion);
            Assert.Equal(1, fake.AlarmSubscriptionCreateCount);
            Assert.Equal(0, fake.AlarmSubscriptionDeleteCount);
        }

        [Fact]
        public async Task AlarmSnapshotSubscriptionSubscribesBeforeReadingActiveAlarms()
        {
            var order = new List<string>();
            var subscriptionFake = new FakeS7CommPlusSession
            {
                CreateAlarmSubscriptionHandler = (_, _) =>
                {
                    order.Add("subscribe");
                    return 0;
                },
                WaitForAlarmSubscriptionHandler = (_, _) => (S7Consts.errCliJobTimeout, new List<Notification>())
            };
            var snapshotFake = new FakeS7CommPlusSession
            {
                ActiveAlarmsHandler = () =>
                {
                    order.Add("snapshot");
                    return (0, new List<S7CommPlusAlarm>());
                }
            };
            var subscriptionClient = CreateClient(subscriptionFake);
            var snapshotClient = CreateClient(snapshotFake);

            await using var result = await subscriptionClient.SubscribeAlarmsWithSnapshotAsync(
                snapshotClient,
                languageId: 1031,
                options: new S7CommPlusSubscriptionOptions
                {
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });
            await result.Subscription.StopAsync();

            Assert.Same(result.Subscription, result.Subscription);
            Assert.Empty(result.ActiveAlarms);
            Assert.Equal(new[] { "subscribe", "snapshot" }, order);
            Assert.Equal(1031, snapshotFake.LastActiveAlarmsLanguageId);
            Assert.Equal(1, subscriptionFake.AlarmSubscriptionCreateCount);
            Assert.Equal(1, snapshotFake.ConnectCount);
        }

        [Fact]
        public async Task AlarmSnapshotWithoutLanguageIdUsesCpuPrimaryLanguage()
        {
            uint[]? capturedLanguages = null;
            var subscriptionFake = new FakeS7CommPlusSession
            {
                CpuCultureInfoHandler = () => (0, new S7CommPlusCpuCultureInfo(new[] { 1031, 2057 })),
                CreateAlarmSubscriptionHandler = (languages, _) =>
                {
                    capturedLanguages = languages;
                    return 0;
                },
                WaitForAlarmSubscriptionHandler = (_, _) => (S7Consts.errCliJobTimeout, new List<Notification>())
            };
            var snapshotFake = new FakeS7CommPlusSession
            {
                ActiveAlarmsHandler = () => (0, new List<S7CommPlusAlarm>())
            };
            var subscriptionClient = CreateClient(subscriptionFake);
            var snapshotClient = CreateClient(snapshotFake);

            await using var result = await subscriptionClient.SubscribeAlarmsWithSnapshotAsync(
                snapshotClient,
                options: new S7CommPlusSubscriptionOptions
                {
                    NotificationTimeout = TimeSpan.FromMilliseconds(20)
                });
            await result.Subscription.StopAsync();

            Assert.Equal(new uint[] { 1031, 2057 }, capturedLanguages);
            Assert.Equal(1031, snapshotFake.LastActiveAlarmsLanguageId);
            Assert.Equal(new[] { 1031, 2057 }, result.Subscription.LanguageIds);
            Assert.False(result.Subscription.ReceivesAllAlarmTextLanguages);
        }

        private static S7CommPlusClient CreateClient(
            FakeS7CommPlusSession fake,
            int requestTimeoutMs = 5000,
            bool writeEnabled = false,
            int? browseTimeoutMs = null)
        {
            return new S7CommPlusClient(
                new S7CommPlusClientOptions
                {
                    Address = "127.0.0.1",
                    WriteEnabled = writeEnabled,
                    RequestTimeout = TimeSpan.FromMilliseconds(requestTimeoutMs),
                    BrowseTimeout = TimeSpan.FromMilliseconds(browseTimeoutMs ?? requestTimeoutMs),
                    ConnectTimeout = TimeSpan.FromMilliseconds(500),
                    DisconnectTimeout = TimeSpan.FromMilliseconds(100)
                },
                () => fake);
        }

        /// <summary>
        /// Creates one synthetic aggregate UDINT tag with sequential element access IDs.
        /// </summary>
        /// <param name="elementCount">Number of scalar element tags to attach.</param>
        /// <returns>A parent array tag ready for aggregate read or write tests.</returns>
        private static PlcTagUDIntArray CreateAggregateUnsignedIntegerTag(int elementCount)
        {
            var aggregate = new PlcTagUDIntArray(
                "DB.Values",
                new ItemAddress("8A0E0001.F"),
                Softdatatype.S7COMMP_SOFTDATATYPE_UDINT);
            aggregate.SetAggregateElements(Enumerable.Range(0, elementCount)
                .Select(index => (PlcTag)new PlcTagUDInt(
                    $"DB.Values[{index}]",
                    new ItemAddress($"8A0E0001.F.{index:X}"),
                    Softdatatype.S7COMMP_SOFTDATATYPE_UDINT))
                .ToArray());
            return aggregate;
        }

        private static PlcTag CreateAggregateTag(uint softdatatype, string name, string accessSequence)
        {
            return S7CommPlusProtocolSession.CreateResolvedPlcTag(new VarInfo
            {
                Name = name,
                AccessSequence = accessSequence,
                Softdatatype = softdatatype,
                ArrayElementCount = 2,
                ArrayDimensions = new[] { new S7CommPlusArrayDimension(1, 2) },
            });
        }

        private static ValueUSIntArray CreateStringValue(string value)
        {
            var bytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(value);
            return new ValueUSIntArray(new[] { checked((byte)bytes.Length), checked((byte)bytes.Length) }.Concat(bytes).ToArray());
        }

        private static ValueUSIntArray CreateDateAndTimeValue(DateTime value)
        {
            var year = value.Year < 2000 ? value.Year - 1900 : value.Year - 2000;
            return new ValueUSIntArray(new[]
            {
                ToBcd(year),
                ToBcd(value.Month),
                ToBcd(value.Day),
                ToBcd(value.Hour),
                ToBcd(value.Minute),
                ToBcd(value.Second),
                ToBcd(value.Millisecond / 10),
                checked((byte)((value.Millisecond % 10) << 4)),
            });
        }

        private static ValueStruct CreateDtlValue(DateTime value, uint nanoseconds, ulong interfaceTimestamp)
        {
            var bytes = new byte[12];
            bytes[0] = (byte)(value.Year >> 8);
            bytes[1] = (byte)value.Year;
            bytes[2] = (byte)value.Month;
            bytes[3] = (byte)value.Day;
            bytes[5] = (byte)value.Hour;
            bytes[6] = (byte)value.Minute;
            bytes[7] = (byte)value.Second;
            bytes[8] = (byte)(nanoseconds >> 24);
            bytes[9] = (byte)(nanoseconds >> 16);
            bytes[10] = (byte)(nanoseconds >> 8);
            bytes[11] = (byte)nanoseconds;
            var result = new ValueStruct(0x02000043)
            {
                PackedStructInterfaceTimestamp = interfaceTimestamp,
            };
            result.AddStructElement(0x02000043, new ValueByteArray(bytes));
            return result;
        }

        private static byte ToBcd(int value)
        {
            return checked((byte)(((value / 10) << 4) | (value % 10)));
        }

        private static S7CommPlusSubscriptionOptions FastSubscriptionOptions()
        {
            return new S7CommPlusSubscriptionOptions
            {
                CycleTimeMilliseconds = 100,
                NotificationTimeout = TimeSpan.FromMilliseconds(20)
            };
        }

        private static S7CommPlusTisWatchRequest CreateTisWatchRequest(byte seed)
        {
            return new S7CommPlusTisWatchRequest
            {
                RequestBlob = new byte[] { 0x40, 0x80, 0x32, seed },
                TriggerBlob = new byte[] { 0x10, seed },
            };
        }

        private static MemoryStream CreateNotificationBody()
        {
            var stream = new MemoryStream();
            WriteUInt32(stream, 0x70000FFB);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            stream.WriteByte(1);
            stream.WriteByte(1);
            stream.WriteByte(1);
            return stream;
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static ValueBlobSparseArray.BlobEntry BlobText(string text)
        {
            return new ValueBlobSparseArray.BlobEntry
            {
                blobRootId = 0,
                value = Encoding.UTF8.GetBytes(text)
            };
        }
    }
}
