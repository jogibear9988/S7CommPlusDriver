using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace S7CommPlusDriver
{
    internal sealed class S7CommPlusTextListService
    {
        private const uint TextListLibraryBaseRelationId = 0x8a370000;
        private readonly S7CommPlusMetadataService _metadata;
        private readonly S7CommPlusProtocolRequests _requests;

        public S7CommPlusTextListService(IS7CommPlusProtocolSession session, S7CommPlusMetadataService metadata)
        {
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _requests = new S7CommPlusProtocolRequests(session);
        }

        public int GetTextLists(IEnumerable<int> languageIds, out S7CommPlusTextListCatalog catalog)
        {
            catalog = S7CommPlusTextListCatalog.Empty;
            var requestedLanguages = languageIds?.Distinct().ToList();
            if (requestedLanguages == null || requestedLanguages.Count == 0)
            {
                var cultureResult = _metadata.GetCpuCultureInfo(out var cultureInfo);
                if (cultureResult != 0)
                {
                    return cultureResult;
                }

                requestedLanguages = cultureInfo.LanguageIds.ToList();
            }

            if (requestedLanguages.Any(languageId => languageId < 0 || languageId > UInt16.MaxValue))
            {
                return S7Consts.errCliInvalidParams;
            }

            var lists = new List<S7CommPlusTextList>();
            var result = TryReadLibrary(0, TextListLibraryBaseRelationId, S7CommPlusTextListScope.LanguageIndependent, lists);
            if (result != 0)
            {
                return result;
            }

            foreach (var languageId in requestedLanguages)
            {
                if (languageId == 0)
                {
                    continue;
                }

                result = TryReadLibrary(
                    languageId,
                    TextListLibraryBaseRelationId + (ushort)languageId,
                    S7CommPlusTextListScope.LanguageSpecific,
                    lists);
                if (result != 0)
                {
                    return result;
                }
            }

            catalog = new S7CommPlusTextListCatalog(requestedLanguages, lists);
            return 0;
        }

        private int TryReadLibrary(int languageId, uint relationId, S7CommPlusTextListScope scope, List<S7CommPlusTextList> lists)
        {
            var result = _requests.Explore(relationId, null, out var response, exploreChildsRecursive: 0);
            if (result != 0)
            {
                return result;
            }

            var textLibrary = response?.Objects?.FirstOrDefault(obj => obj.ClassId == Ids.TextLibraryClassRID);
            if (textLibrary == null)
            {
                return 0;
            }

            if (!textLibrary.Attributes.TryGetValue(Ids.TextLibraryOffsetArea, out var offsetAreaValue) ||
                !textLibrary.Attributes.TryGetValue(Ids.TextLibraryStringArea, out var stringAreaValue) ||
                offsetAreaValue is not ValueBlobArray offsetArea ||
                stringAreaValue is not ValueBlob stringArea)
            {
                return S7Consts.errIsoInvalidPDU;
            }

            var offsets = offsetArea.GetValue();
            var strings = stringArea.GetValue();
            if (offsets == null || offsets.Length < 2 || strings == null)
            {
                return S7Consts.errIsoInvalidPDU;
            }

            return DecodeTextListLibrary(offsets[0].GetValue(), offsets[1].GetValue(), strings, languageId, scope, lists);
        }

        /// <summary>
        /// Decodes the three binary tables carried by a PLC text-library object into public catalog entries.
        /// </summary>
        /// <param name="listTable">Maps each 16-bit runtime list identifier to an offset in <paramref name="entryTable"/>.</param>
        /// <param name="entryTable">Contains 32-bit list values and offsets into <paramref name="stringTable"/>.</param>
        /// <param name="stringTable">Contains length-prefixed UTF-8 text values.</param>
        /// <param name="languageId">The LCID represented by the library, or zero for language-independent lists.</param>
        /// <param name="scope">Identifies whether the source library is language-independent or language-specific.</param>
        /// <param name="lists">Receives successfully decoded lists.</param>
        /// <returns>Zero when every referenced table entry is valid; otherwise the S7 invalid-PDU error.</returns>
        internal static int DecodeTextListLibrary(
            byte[] listTable,
            byte[] entryTable,
            byte[] stringTable,
            int languageId,
            S7CommPlusTextListScope scope,
            List<S7CommPlusTextList> lists)
        {
            if (listTable == null || entryTable == null || stringTable == null || listTable.Length < 20)
            {
                return S7Consts.errIsoInvalidPDU;
            }

            var pos = 16u;
            if (!TryReadUInt32(listTable, pos, out var listCount))
            {
                return S7Consts.errIsoInvalidPDU;
            }
            pos += 4;
            var decodedTexts = new Dictionary<uint, string>();

            for (var i = 0; i < listCount; i++)
            {
                if (!TryReadUInt16(listTable, pos, out var listId) ||
                    !TryReadUInt32(listTable, pos + 2, out var entryOffset))
                {
                    return S7Consts.errIsoInvalidPDU;
                }
                pos += 6;

                if (!TryReadEntries(entryTable, stringTable, entryOffset, decodedTexts, out var entries))
                {
                    return S7Consts.errIsoInvalidPDU;
                }

                lists.Add(new S7CommPlusTextList(listId, languageId, scope, ClassifyTextList(listId), entries));
            }

            return 0;
        }

        private static S7CommPlusTextListType ClassifyTextList(int listId)
        {
            // The online text-library payload only carries runtime list ids.
            // Keep the public API unified and expose this as a best-effort
            // classification for callers that want to display/filter it.
            return listId >= 512 && listId < 32768
                ? S7CommPlusTextListType.User
                : S7CommPlusTextListType.System;
        }

        private static bool TryReadEntries(
            byte[] entryTable,
            byte[] stringTable,
            uint entryOffset,
            Dictionary<uint, string> decodedTexts,
            out S7CommPlusTextListEntry[] entries)
        {
            if (TryReadEntries(entryTable, stringTable, entryOffset, decodedTexts, use32BitValues: true, out entries))
            {
                return true;
            }

            // Older S7-1200/1500 firmware stores each list entry as a 16-bit
            // value followed by a 32-bit string offset. Current firmware uses
            // a signed 32-bit value and therefore an eight-byte record.
            return TryReadEntries(entryTable, stringTable, entryOffset, decodedTexts, use32BitValues: false, out entries);
        }

        private static bool TryReadEntries(
            byte[] entryTable,
            byte[] stringTable,
            uint entryOffset,
            Dictionary<uint, string> decodedTexts,
            bool use32BitValues,
            out S7CommPlusTextListEntry[] entries)
        {
            entries = null;
            if (!TryReadUInt32(entryTable, entryOffset, out var entryCount))
            {
                return false;
            }

            if (entryCount > Int32.MaxValue)
            {
                return false;
            }
            entries = new S7CommPlusTextListEntry[(int)entryCount];
            var pos = entryOffset + 4;
            for (var i = 0; i < entryCount; i++)
            {
                int value;
                uint stringOffset;
                if (use32BitValues)
                {
                    if (!TryReadUInt32(entryTable, pos, out var value32) ||
                        !TryReadUInt32(entryTable, pos + 4, out stringOffset))
                    {
                        return false;
                    }
                    value = unchecked((int)value32);
                    pos += 8;
                }
                else
                {
                    if (!TryReadUInt16(entryTable, pos, out var value16) ||
                        !TryReadUInt32(entryTable, pos + 2, out stringOffset))
                    {
                        return false;
                    }
                    value = value16;
                    pos += 6;
                }

                if (!TryReadText(stringTable, stringOffset, decodedTexts, out var text))
                {
                    return false;
                }
                entries[i] = new S7CommPlusTextListEntry(value, value, text);
            }

            return true;
        }

        private static bool TryReadText(
            byte[] stringTable,
            uint offset,
            Dictionary<uint, string> decodedTexts,
            out string text)
        {
            if (decodedTexts.TryGetValue(offset, out text))
            {
                return true;
            }
            if (!TryReadUInt16(stringTable, offset, out var length))
            {
                return false;
            }

            var start = offset + 2;
            if (start > stringTable.Length || start + length > stringTable.Length)
            {
                return false;
            }

            text = Utils.GetUtfString(stringTable, start, length);
            decodedTexts.Add(offset, text);
            return true;
        }

        private static bool TryReadUInt16(byte[] data, uint offset, out ushort value)
        {
            value = 0;
            if (data == null || offset + 2 > data.Length)
            {
                return false;
            }

            value = Utils.GetUInt16LE(data, offset);
            return true;
        }

        private static bool TryReadUInt32(byte[] data, uint offset, out uint value)
        {
            value = 0;
            if (data == null || offset + 4 > data.Length)
            {
                return false;
            }

            value = Utils.GetUInt32LE(data, offset);
            return true;
        }
    }
}
