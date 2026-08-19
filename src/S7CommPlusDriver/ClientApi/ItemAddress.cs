#region License
/******************************************************************************
 * S7CommPlusDriver
 * 
 * Copyright (C) 2023 Thomas Wiens, th.wiens@gmx.de
 *
 * This file is part of S7CommPlusDriver.
 *
 * S7CommPlusDriver is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 /****************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace S7CommPlusDriver
{
    public class ItemAddress : IS7pSerialize
    {
        private const int InlineLocalIdCapacity = 4;

        private UInt32 _localId0;
        private UInt32 _localId1;
        private UInt32 _localId2;
        private UInt32 _localId3;
        private int _compactLocalIdCount;
        private UInt32[] _overflowLocalIds;
        private List<UInt32> _materializedLocalIds;

        public UInt32 SymbolCrc;
        public UInt32 AccessArea;
        public UInt32 AccessSubArea;

        /// <summary>
        /// Gets or sets the mutable local-ID list used by legacy callers.
        /// </summary>
        /// <remarks>
        /// Runtime accessors keep up to four IDs inline. The list is allocated only when a caller explicitly requests this legacy API.
        /// </remarks>
        public List<UInt32> LID
        {
            get
            {
                if (_materializedLocalIds == null)
                {
                    _materializedLocalIds = new List<UInt32>(_compactLocalIdCount);
                    for (var index = 0; index < _compactLocalIdCount; index++)
                    {
                        _materializedLocalIds.Add(GetCompactLocalId(index));
                    }
                }
                return _materializedLocalIds;
            }
            set
            {
                _materializedLocalIds = value ?? throw new ArgumentNullException(nameof(value));
                _compactLocalIdCount = 0;
                _overflowLocalIds = null;
            }
        }

        public ItemAddress() : this(0, Ids.DB_ValueActual)
        {
        }

        public ItemAddress(UInt32 area, UInt32 subArea)
        {
            SymbolCrc = 0;
            AccessArea = area;
            AccessSubArea = subArea;
        }

        public ItemAddress(string variableAccessString)
        {
            if (string.IsNullOrWhiteSpace(variableAccessString))
            {
                throw new ArgumentException("Variable access string is required.", nameof(variableAccessString));
            }

            var fieldStart = 0;
            var fieldIndex = 0;
            while (fieldStart < variableAccessString.Length)
            {
                var fieldEnd = variableAccessString.IndexOf('.', fieldStart);
                if (fieldEnd < 0)
                {
                    fieldEnd = variableAccessString.Length;
                }
                if (!TryParseHexField(variableAccessString, fieldStart, fieldEnd - fieldStart, out var id))
                {
                    throw new ArgumentException("Variable access string contains an invalid hexadecimal field.", nameof(variableAccessString));
                }
                if (fieldIndex == 0)
                {
                    AccessArea = id;
                }
                else
                {
                    AddLocalId(id);
                }
                fieldIndex++;
                fieldStart = fieldEnd + 1;
            }
            if (fieldIndex < 2)
            {
                throw new ArgumentException("Variable access string must contain an access area and at least one local ID field.", nameof(variableAccessString));
            }
            SymbolCrc = 0;
            if (AccessArea >= 0x8A0E0000)
            {
                AccessSubArea = Ids.DB_ValueActual;
            }
            else if ((AccessArea == Ids.NativeObjects_theS7Timers_Rid) ||
                     (AccessArea == Ids.NativeObjects_theS7Counters_Rid) ||
                     (AccessArea == Ids.NativeObjects_theIArea_Rid) ||
                     (AccessArea == Ids.NativeObjects_theQArea_Rid) ||
                     (AccessArea == Ids.NativeObjects_theMArea_Rid))
            {
                AccessSubArea = Ids.ControllerArea_ValueActual;
            }
        }

        public string GetAccessString()
        {
            var result = new StringBuilder(16 + LocalIdCount * 9);
            result.AppendFormat("{0:X}", AccessArea);
            for (var index = 0; index < LocalIdCount; index++)
            {
                result.AppendFormat(".{0:X}", GetLocalId(index));
            }
            return result.ToString();
        }

        public UInt32 GetNumberOfFields()
        {
            return (UInt32)(4 + LocalIdCount);
        }

        public void SetAccessAreaToDatablock(UInt32 number)
        {
            AccessArea = (UInt16)number + 0x8a0e0000;
        }

        public int Serialize(Stream buffer)
        {
            int ret = 0;
            ret += S7p.EncodeUInt32Vlq(buffer, SymbolCrc);
            ret += S7p.EncodeUInt32Vlq(buffer, AccessArea);
            ret += S7p.EncodeUInt32Vlq(buffer, (UInt32)LocalIdCount + 1);
            ret += S7p.EncodeUInt32Vlq(buffer, AccessSubArea);
            for (var index = 0; index < LocalIdCount; index++)
            {
                ret += S7p.EncodeUInt32Vlq(buffer, GetLocalId(index));
            }
            return ret;
        }

        public override string ToString()
        {
            string s = "";
            s += "<ItemAddress>" + Environment.NewLine;
            s += "<SymbolCrc>" + SymbolCrc.ToString() + "</SymbolCrc>" + Environment.NewLine;
            s += "<AccessArea>" + AccessArea.ToString() + "</AccessArea>" + Environment.NewLine;
            s += "<NumberOfIDs>" + (LocalIdCount + 1).ToString() + "</NumberOfIDs>" + Environment.NewLine;
            s += "<AccessSubArea>" + AccessSubArea.ToString() + "</AccessSubArea>" + Environment.NewLine;
            for (var index = 0; index < LocalIdCount; index++)
            {
                s += "<LIDvalue>" + GetLocalId(index).ToString() + "</LIDvalue>" + Environment.NewLine;
            }
            s += "</ItemAddress>" + Environment.NewLine;
            return s;
        }

        internal int LocalIdCount => _materializedLocalIds?.Count ?? _compactLocalIdCount;

        internal void AddLocalId(UInt32 localId)
        {
            if (_materializedLocalIds != null)
            {
                _materializedLocalIds.Add(localId);
                return;
            }

            var index = _compactLocalIdCount++;
            switch (index)
            {
                case 0: _localId0 = localId; return;
                case 1: _localId1 = localId; return;
                case 2: _localId2 = localId; return;
                case 3: _localId3 = localId; return;
            }

            var overflowIndex = index - InlineLocalIdCapacity;
            if (_overflowLocalIds == null)
            {
                _overflowLocalIds = new UInt32[4];
            }
            else if (overflowIndex == _overflowLocalIds.Length)
            {
                Array.Resize(ref _overflowLocalIds, _overflowLocalIds.Length * 2);
            }
            _overflowLocalIds[overflowIndex] = localId;
        }

        internal UInt32 GetLocalId(int index)
        {
            if (index < 0 || index >= LocalIdCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            if (_materializedLocalIds != null)
            {
                return _materializedLocalIds[index];
            }
            return GetCompactLocalId(index);
        }

        internal UInt32[] CopyLocalIds()
        {
            var result = new UInt32[LocalIdCount];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = GetLocalId(index);
            }
            return result;
        }

        private UInt32 GetCompactLocalId(int index)
        {
            switch (index)
            {
                case 0: return _localId0;
                case 1: return _localId1;
                case 2: return _localId2;
                case 3: return _localId3;
                default: return _overflowLocalIds[index - InlineLocalIdCapacity];
            }
        }

        private static bool TryParseHexField(string value, int start, int length, out UInt32 result)
        {
            result = 0;
            if (length <= 0)
            {
                return false;
            }
            for (var index = start; index < start + length; index++)
            {
                var character = value[index];
                var digit = character >= '0' && character <= '9'
                    ? character - '0'
                    : character >= 'A' && character <= 'F'
                        ? character - 'A' + 10
                        : character >= 'a' && character <= 'f'
                            ? character - 'a' + 10
                            : -1;
                if (digit < 0 || result > (UInt32.MaxValue - (UInt32)digit) / 16)
                {
                    return false;
                }
                result = result * 16 + (UInt32)digit;
            }
            return true;
        }
    }
}
