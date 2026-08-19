using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace S7CommPlusDriver
{
    public enum S7CommPlusTextListScope
    {
        LanguageIndependent,
        LanguageSpecific
    }

    public enum S7CommPlusTextListType
    {
        Unknown,
        User,
        System
    }

    public readonly struct S7CommPlusTextListEntry
    {
        public S7CommPlusTextListEntry(int from, int to, string text)
        {
            From = from;
            To = to;
            Text = text ?? String.Empty;
        }

        public int From { get; }
        public int To { get; }
        public string Text { get; }
        public bool IsRange => From != To;
    }

    public sealed class S7CommPlusTextList
    {
        private readonly S7CommPlusTextListEntry[] _entries;

        public S7CommPlusTextList(int listId, int languageId, S7CommPlusTextListScope scope, IEnumerable<S7CommPlusTextListEntry> entries)
            : this(listId, languageId, scope, S7CommPlusTextListType.Unknown, entries)
        {
        }

        public S7CommPlusTextList(int listId, int languageId, S7CommPlusTextListScope scope, S7CommPlusTextListType textListType, IEnumerable<S7CommPlusTextListEntry> entries)
        {
            ListId = listId;
            LanguageId = languageId;
            Scope = scope;
            TextListType = textListType;
            _entries = CreateEntries(entries);
            Entries = Array.AsReadOnly(_entries);
        }

        internal S7CommPlusTextList(
            int listId,
            int languageId,
            S7CommPlusTextListScope scope,
            S7CommPlusTextListType textListType,
            S7CommPlusTextListEntry[] ownedEntries)
        {
            ListId = listId;
            LanguageId = languageId;
            Scope = scope;
            TextListType = textListType;
            _entries = ownedEntries ?? Array.Empty<S7CommPlusTextListEntry>();
            Entries = Array.AsReadOnly(_entries);
        }

        public int ListId { get; }
        public int LanguageId { get; }
        public S7CommPlusTextListScope Scope { get; }
        public S7CommPlusTextListType TextListType { get; }
        public IReadOnlyList<S7CommPlusTextListEntry> Entries { get; }

        public bool TryResolve(long value, out string text)
        {
            text = null;
            if (value < Int32.MinValue || value > Int32.MaxValue)
            {
                return false;
            }

            var intValue = (int)value;
            foreach (var entry in _entries)
            {
                if (!entry.IsRange)
                {
                    if (entry.From == intValue)
                    {
                        text = entry.Text;
                        return true;
                    }
                }
            }

            foreach (var entry in _entries)
            {
                if (entry.From <= intValue && entry.IsRange && entry.To >= intValue)
                {
                    text = entry.Text;
                    return true;
                }
            }
            return false;
        }

        private static S7CommPlusTextListEntry[] CreateEntries(IEnumerable<S7CommPlusTextListEntry> entries)
        {
            if (entries == null)
            {
                return Array.Empty<S7CommPlusTextListEntry>();
            }
            var result = entries is ICollection<S7CommPlusTextListEntry> collection
                ? new List<S7CommPlusTextListEntry>(collection.Count)
                : new List<S7CommPlusTextListEntry>();
            result.AddRange(entries);
            return result.ToArray();
        }

    }

    public sealed class S7CommPlusTextListCatalog
    {
        public static readonly S7CommPlusTextListCatalog Empty = new S7CommPlusTextListCatalog(Array.Empty<int>(), Array.Empty<S7CommPlusTextList>());

        private readonly Dictionary<long, S7CommPlusTextList> _listsByLanguageAndId;

        public S7CommPlusTextListCatalog(IEnumerable<int> languageIds, IEnumerable<S7CommPlusTextList> textLists)
        {
            var distinctLanguageIds = CreateDistinctLanguageIds(languageIds);
            var materializedTextLists = textLists == null
                ? new List<S7CommPlusTextList>()
                : new List<S7CommPlusTextList>(textLists);
            LanguageIds = new ReadOnlyCollection<int>(distinctLanguageIds);
            TextLists = new ReadOnlyCollection<S7CommPlusTextList>(materializedTextLists);
            _listsByLanguageAndId = new Dictionary<long, S7CommPlusTextList>(materializedTextLists.Count);
            foreach (var textList in materializedTextLists)
            {
                var key = CreateTextListKey(textList.LanguageId, textList.ListId);
                if (!_listsByLanguageAndId.ContainsKey(key))
                {
                    _listsByLanguageAndId.Add(key, textList);
                }
            }
        }

        public IReadOnlyList<int> LanguageIds { get; }
        public IReadOnlyList<S7CommPlusTextList> TextLists { get; }

        public string ResolveText(string textListName, long value, int languageId)
        {
            return TryResolve(textListName, value, languageId, out var text) ? text : null;
        }

        public bool TryResolve(string textListName, long value, int languageId, out string text)
        {
            text = null;
            if (!TryParseTextListId(textListName, out var listId, out var hasLegacySuffix))
            {
                return false;
            }

            if (TryResolve(listId, value, languageId, out text))
            {
                return true;
            }

            if (hasLegacySuffix && listId > 0)
            {
                return TryResolve(listId - 1, value, languageId, out text);
            }

            return false;
        }

        public bool TryResolve(int listId, long value, int languageId, out string text)
        {
            text = null;
            if (TryResolveInLanguage(listId, value, languageId, out text))
            {
                return true;
            }

            if (languageId != 0 && TryResolveInLanguage(listId, value, 0, out text))
            {
                return true;
            }

            return false;
        }

        private bool TryResolveInLanguage(int listId, long value, int languageId, out string text)
        {
            text = null;
            if (!_listsByLanguageAndId.TryGetValue(CreateTextListKey(languageId, listId), out var list))
            {
                return false;
            }

            return list.TryResolve(value, out text);
        }

        private static List<int> CreateDistinctLanguageIds(IEnumerable<int> languageIds)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (languageIds == null)
            {
                return result;
            }
            foreach (var languageId in languageIds)
            {
                if (seen.Add(languageId))
                {
                    result.Add(languageId);
                }
            }
            return result;
        }

        private static long CreateTextListKey(int languageId, int listId)
        {
            return ((long)(uint)languageId << 32) | (uint)listId;
        }

        private static bool TryParseTextListId(string textListName, out int listId, out bool hasLegacySuffix)
        {
            listId = 0;
            hasLegacySuffix = false;
            if (String.IsNullOrWhiteSpace(textListName))
            {
                return false;
            }

            var position = 0;
            while (position < textListName.Length && Char.IsDigit(textListName[position]))
            {
                var digit = textListName[position] - '0';
                if (listId > (Int32.MaxValue - digit) / 10)
                {
                    return false;
                }
                listId = listId * 10 + digit;
                position++;
            }

            if (position == 0)
            {
                return false;
            }
            hasLegacySuffix = position + 1 == textListName.Length &&
                (textListName[position] == 'W' || textListName[position] == 'w');
            return true;
        }
    }
}
