using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class ProtocolValueAndTextListTests
    {
        [Fact]
        public void ObjectDecoderAcceptsRepeatedInheritedAttributeAndKeepsLastValue()
        {
            using var stream = new MemoryStream();
            S7p.EncodeByte(stream, ElementID.Attribute);
            S7p.EncodeUInt32Vlq(stream, Ids.ObjectVariableTypeName);
            new ValueWString("Base name").Serialize(stream);
            S7p.EncodeByte(stream, ElementID.Attribute);
            S7p.EncodeUInt32Vlq(stream, Ids.ObjectVariableTypeName);
            new ValueWString("Effective name").Serialize(stream);
            S7p.EncodeByte(stream, ElementID.TerminatingObject);
            stream.Position = 0;
            var obj = new PObject();

            S7p.DecodeObject(stream, ref obj);

            var name = Assert.IsType<ValueWString>(obj.GetAttribute(Ids.ObjectVariableTypeName));
            Assert.Equal("Effective name", name.GetValue());
            Assert.Equal(stream.Length, stream.Position);
        }

        [Fact]
        public void AddressArrayOfVariantsRoundTripsCapturedWireShape()
        {
            var bytes = new byte[]
            {
                0xA0, Datatype.Variant, 0x02, 0x01, 0x02
            };

            using var input = new MemoryStream(bytes);
            var value = Assert.IsType<ValueVariantArray>(PValue.Deserialize(input));

            Assert.Equal(new uint[] { 1, 2 }, Array.ConvertAll(value.GetValue(), item => item.GetValue()));
            Assert.Equal(input.Length, input.Position);
            using var output = new MemoryStream();
            value.Serialize(output);
            Assert.Equal(bytes, output.ToArray());
        }

        [Fact]
        public void AddressArrayOfStructsDecodesNestedValuesWithoutRepeatedHeaders()
        {
            var bytes = new byte[]
            {
                0x20, Datatype.Struct, 0x02,
                0x00, 0x00, 0x01, 0x00,
                0x01, 0x00, Datatype.UDInt, 0x07, 0x00,
                0x00, 0x00, 0x01, 0x01,
                0x00
            };

            using var input = new MemoryStream(bytes);
            var value = Assert.IsType<ValueStructArray>(PValue.Deserialize(input));
            var structs = value.GetValue();

            Assert.Equal(2, structs.Length);
            Assert.Equal(0x100u, structs[0].GetValue());
            Assert.Equal(7u, Assert.IsType<ValueUDInt>(structs[0].GetStructElement(1)).GetValue());
            Assert.Equal(0x101u, structs[1].GetValue());
            Assert.Equal(input.Length, input.Position);
            using var output = new MemoryStream();
            value.Serialize(output);
            Assert.Equal(bytes, output.ToArray());
        }

        [Fact]
        public void NullArrayConsumesOnlyItsCount()
        {
            var bytes = new byte[] { 0x10, Datatype.Null, 0x03 };

            using var input = new MemoryStream(bytes);
            var value = Assert.IsType<ValueNullArray>(PValue.Deserialize(input));

            Assert.Equal(3u, value.Count);
            Assert.Equal(input.Length, input.Position);
        }

        [Fact]
        public void CpuProjectNameIsOptionalForOlderFirmware()
        {
            Assert.Null(S7CommPlusMetadataService.GetOptionalCpuProjectName(new ValueNull()));
            Assert.Null(S7CommPlusMetadataService.GetOptionalCpuProjectName(new ValueWStringArray(Array.Empty<string>())));
            Assert.Equal(
                "Project",
                S7CommPlusMetadataService.GetOptionalCpuProjectName(
                    new ValueWStringArray(new[] { "Version 1", "Version 2", "Project" })));
        }

        [Fact]
        public void AddressArrayOfS7StringDescriptorsConsumesEveryLength()
        {
            var bytes = new byte[] { 0x20, Datatype.S7String, 0x02, 0x0A, 0x14 };

            using var input = new MemoryStream(bytes);
            var value = Assert.IsType<ValueS7StringArray>(PValue.Deserialize(input));

            Assert.Equal(new uint[] { 10, 20 }, Array.ConvertAll(value.GetValue(), item => item.MaximumLength));
            Assert.Equal(input.Length, input.Position);
        }

        [Fact]
        public void TextListEntriesUseSigned32BitValuesAndEightByteRecords()
        {
            var listTable = new byte[26];
            BinaryPrimitives.WriteUInt32LittleEndian(listTable.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(listTable.AsSpan(20), 0x1234);
            BinaryPrimitives.WriteUInt32LittleEndian(listTable.AsSpan(22), 16);

            var entryTable = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(16), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(20), 70_000);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(24), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(28), UInt32.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(32), 15);

            var stringTable = new byte[21];
            WriteText(stringTable, 8, "Alpha");
            WriteText(stringTable, 15, "Last");
            var lists = new List<S7CommPlusTextList>();

            var result = S7CommPlusTextListService.DecodeTextListLibrary(
                listTable,
                entryTable,
                stringTable,
                1031,
                S7CommPlusTextListScope.LanguageSpecific,
                lists);

            Assert.Equal(0, result);
            var list = Assert.Single(lists);
            Assert.Equal(0x1234, list.ListId);
            Assert.Collection(
                list.Entries,
                entry =>
                {
                    Assert.Equal(70_000, entry.From);
                    Assert.Equal("Alpha", entry.Text);
                },
                entry =>
                {
                    Assert.Equal(-1, entry.From);
                    Assert.Equal("Last", entry.Text);
                });
        }

        [Fact]
        public void TextListEntriesSupportLegacySixByteRecords()
        {
            var listTable = new byte[26];
            BinaryPrimitives.WriteUInt32LittleEndian(listTable.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(listTable.AsSpan(20), 0x0042);
            BinaryPrimitives.WriteUInt32LittleEndian(listTable.AsSpan(22), 16);

            var entryTable = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(16), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(entryTable.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(22), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(entryTable.AsSpan(26), UInt16.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(28), 15);

            var stringTable = new byte[21];
            WriteText(stringTable, 8, "Alpha");
            WriteText(stringTable, 15, "Last");
            var lists = new List<S7CommPlusTextList>();

            var result = S7CommPlusTextListService.DecodeTextListLibrary(
                listTable,
                entryTable,
                stringTable,
                1031,
                S7CommPlusTextListScope.LanguageSpecific,
                lists);

            Assert.Equal(0, result);
            var list = Assert.Single(lists);
            Assert.Equal(0x0042, list.ListId);
            Assert.Collection(
                list.Entries,
                entry =>
                {
                    Assert.Equal(1, entry.From);
                    Assert.Equal("Alpha", entry.Text);
                },
                entry =>
                {
                    Assert.Equal(UInt16.MaxValue, entry.From);
                    Assert.Equal("Last", entry.Text);
                });
        }

        [Fact]
        public void TextListEntriesReuseStringsReferencedByTheSameLibraryOffset()
        {
            var listTable = new byte[26];
            BinaryPrimitives.WriteUInt32LittleEndian(listTable.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(listTable.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(listTable.AsSpan(22), 16);
            var entryTable = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(16), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(24), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(28), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(entryTable.AsSpan(32), 8);
            var stringTable = new byte[16];
            WriteText(stringTable, 8, "Same");
            var lists = new List<S7CommPlusTextList>();

            var result = S7CommPlusTextListService.DecodeTextListLibrary(
                listTable,
                entryTable,
                stringTable,
                1031,
                S7CommPlusTextListScope.LanguageSpecific,
                lists);

            Assert.Equal(0, result);
            var list = Assert.Single(lists);
            Assert.Same(list.Entries[0].Text, list.Entries[1].Text);
        }

        [Fact]
        public void TextListResolutionUsesExactValuesBeforeRangesWithoutChangingEntryOrder()
        {
            var entries = new[]
            {
                new S7CommPlusTextListEntry(10, 20, "Range"),
                new S7CommPlusTextListEntry(15, 15, "Exact"),
                new S7CommPlusTextListEntry(1, 1, "First")
            };
            var list = new S7CommPlusTextList(1, 1031, S7CommPlusTextListScope.LanguageSpecific, entries);

            Assert.True(list.TryResolve(15, out var exactText));
            Assert.True(list.TryResolve(16, out var rangeText));
            Assert.Equal("Exact", exactText);
            Assert.Equal("Range", rangeText);
            Assert.Equal(entries, list.Entries);
        }

        private static void WriteText(byte[] destination, int offset, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset), (ushort)bytes.Length);
            bytes.CopyTo(destination.AsSpan(offset + 2));
        }
    }
}
