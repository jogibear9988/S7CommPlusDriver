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
using System.Globalization;
using System.Text;

namespace S7CommPlusDriver.Alarming
{
    public class S7CommPlusAlarmAssociatedValues
    {
        public S7CommPlusAlarmAssociatedValue SD_1;
        public S7CommPlusAlarmAssociatedValue SD_2;
        public S7CommPlusAlarmAssociatedValue SD_3;
        public S7CommPlusAlarmAssociatedValue SD_4;
        public S7CommPlusAlarmAssociatedValue SD_5;
        public S7CommPlusAlarmAssociatedValue SD_6;
        public S7CommPlusAlarmAssociatedValue SD_7;
        public S7CommPlusAlarmAssociatedValue SD_8;
        public S7CommPlusAlarmAssociatedValue SD_9;
        public S7CommPlusAlarmAssociatedValue SD_10;

        private byte[] PackedStandardAssociatedValues;

        public override string ToString()
        {
            string s = "<S7CommPlusAlarmAssociatedValues>" + Environment.NewLine;
            s += "<SD_1>" + (SD_1 is null ? String.Empty : SD_1.ToString()) + "</SD_1>" + Environment.NewLine;
            s += "<SD_2>" + (SD_2 is null ? String.Empty : SD_2.ToString()) + "</SD_2>" + Environment.NewLine;
            s += "<SD_3>" + (SD_3 is null ? String.Empty : SD_3.ToString()) + "</SD_3>" + Environment.NewLine;
            s += "<SD_4>" + (SD_4 is null ? String.Empty : SD_4.ToString()) + "</SD_4>" + Environment.NewLine;
            s += "<SD_5>" + (SD_5 is null ? String.Empty : SD_5.ToString()) + "</SD_5>" + Environment.NewLine;
            s += "<SD_6>" + (SD_6 is null ? String.Empty : SD_6.ToString()) + "</SD_6>" + Environment.NewLine;
            s += "<SD_7>" + (SD_7 is null ? String.Empty : SD_7.ToString()) + "</SD_7>" + Environment.NewLine;
            s += "<SD_8>" + (SD_8 is null ? String.Empty : SD_8.ToString()) + "</SD_8>" + Environment.NewLine;
            s += "<SD_9>" + (SD_9 is null ? String.Empty : SD_9.ToString()) + "</SD_9>" + Environment.NewLine;
            s += "<SD_10>" + (SD_10 is null ? String.Empty : SD_10.ToString()) + "</SD_10>" + Environment.NewLine;
            s += "</S7CommPlusAlarmAssociatedValues>" + Environment.NewLine;
            return s;
        }

        public S7CommPlusAlarmAssociatedValue GetValue(int sdIndex)
        {
            switch(sdIndex)
            {
                case 1: return SD_1;
                case 2: return SD_2;
                case 3: return SD_3;
                case 4: return SD_4;
                case 5: return SD_5;
                case 6: return SD_6;
                case 7: return SD_7;
                case 8: return SD_8;
                case 9: return SD_9;
                case 10: return SD_10;
                default: return null;
            }
        }

        internal bool TryGetPackedStandardInteger(int position, char elementType, out int value)
        {
            value = 0;
            if (PackedStandardAssociatedValues == null || position <= 0)
            {
                return false;
            }

            var elementTypeLength = GetElementTypeLength(elementType);
            if (elementTypeLength <= 0)
            {
                return false;
            }

            var end = (position * elementTypeLength) - 1;
            var start = end - elementTypeLength + 1;
            if (end >= PackedStandardAssociatedValues.Length)
            {
                return false;
            }

            for (var i = start; i <= end; i++)
            {
                value = (value << 8) | PackedStandardAssociatedValues[i];
            }
            return true;
        }

        internal bool TryGetPackedStandardReal(int position, char elementType, out double value)
        {
            value = 0.0;
            if (PackedStandardAssociatedValues == null || position <= 0)
            {
                return false;
            }

            var elementTypeLength = GetElementTypeLength(elementType);
            if (elementTypeLength != 4 && elementTypeLength != 8)
            {
                return false;
            }

            var end = (position * elementTypeLength) - 1;
            var start = end - elementTypeLength + 1;
            if (end >= PackedStandardAssociatedValues.Length)
            {
                return false;
            }

            value = elementTypeLength == 8
                ? Utils.GetDouble(PackedStandardAssociatedValues, (uint)start)
                : Utils.GetFloat(PackedStandardAssociatedValues, (uint)start);
            return true;
        }

        internal static S7CommPlusAlarmAssociatedValues FromValueBlob(ValueBlobArray blob)
        {
            var av = new S7CommPlusAlarmAssociatedValues();
            var blobs = blob.GetValue();
            av.TrySetPackedStandardAssociatedValues(blobs);

            // Comes as Array[17], with indices:
            // 0 = Unknown Typeinformation, 4 Bytes
            // 1..10 = SD_1..SD_10
            //
            // The typeinformation at index 0 has a BlobRootId of 3476 = AS_CGS.AssociatedValues
            // When browsing 0x2000113 we get the result:
            // Type   Name
            // ---------------
            // UInt   Syntax
            // Byte   Aap
            int i = 0;
            S7CommPlusAlarmAssociatedValue pv;
            foreach(var b in blobs)
            {
                var bytes = b.GetValue();
                switch (b.BlobRootId)
                {
                    case (Ids.TI_BOOL):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetBool(bytes[0] != 0);
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_BYTE):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(bytes[0]);
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_CHAR):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetString(Encoding.GetEncoding("ISO-8859-1").GetString(bytes, 0, 1));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_WORD):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(Utils.GetUInt16(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_INT):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(Utils.GetInt16(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_DWORD):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(Utils.GetUInt32(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_DINT):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(Utils.GetInt32(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_REAL):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetReal(Utils.GetFloat(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_LREAL):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetReal(Utils.GetDouble(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_USINT):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(bytes[0]);
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_UINT):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(Utils.GetUInt16(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_UDINT):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt(Utils.GetUInt32(bytes, 0));
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_SINT):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetInt((sbyte)bytes[0]);
                        av.SetSDValue(pv, i);
                        break;
                    case (Ids.TI_WCHAR):
                        pv = new S7CommPlusAlarmAssociatedValue(b.BlobRootId);
                        pv.SetString(((char)Utils.GetUInt16(bytes, 0)).ToString());
                        av.SetSDValue(pv, i);
                        break;
                    default:
                        if (b.BlobRootId > Ids.TI_STRING_START && b.BlobRootId <= Ids.TI_STRING_END)
                        {
                            //byte s_maxlen = bytes[0]; // Don't need this value
                            byte s_actlen = bytes[1];
                            pv = new S7CommPlusAlarmAssociatedValue(Ids.TI_STRING);
                            pv.SetString(Encoding.GetEncoding("ISO-8859-1").GetString(bytes, 2, s_actlen));
                            av.SetSDValue(pv, i);
                        }
                        else if (b.BlobRootId > Ids.TI_WSTRING_START && b.BlobRootId <= Ids.TI_WSTRING_END)
                        {
                            //int ws_maxlen = Utils.GetUInt16(bytes, 0); // Don't need this value
                            int ws_actlen = Utils.GetUInt16(bytes, 2);
                            pv = new S7CommPlusAlarmAssociatedValue(Ids.TI_WSTRING);
                            pv.SetString(Encoding.BigEndianUnicode.GetString(bytes, 4, ws_actlen * 2));
                            av.SetSDValue(pv, i);
                        }
                        break;
                }
                i++;
                // All other elements have no value
                if (i > 10)
                {
                    break;
                }
            }
            return av;
        }

        private void TrySetPackedStandardAssociatedValues(ValueBlob[] blobs)
        {
            if (blobs == null || blobs.Length < 2)
            {
                return;
            }

            // Standard associated-value payloads use untyped blobs; the second blob
            // contains packed values addressed by placeholder position and element width.
            if (blobs[0].BlobRootId != 0 || blobs[1].BlobRootId != 0)
            {
                return;
            }

            for (var i = 2; i < blobs.Length; i++)
            {
                if (blobs[i].BlobRootId != 0)
                {
                    return;
                }
            }

            var bytes = blobs[1].GetValue();
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            PackedStandardAssociatedValues = new byte[bytes.Length];
            Array.Copy(bytes, PackedStandardAssociatedValues, bytes.Length);
        }

        private void SetSDValue(S7CommPlusAlarmAssociatedValue v, int index)
        {
            switch(index)
            {
                case 1: SD_1 = v; break;
                case 2: SD_2 = v; break;
                case 3: SD_3 = v; break;
                case 4: SD_4 = v; break;
                case 5: SD_5 = v; break;
                case 6: SD_6 = v; break;
                case 7: SD_7 = v; break;
                case 8: SD_8 = v; break;
                case 9: SD_9 = v; break;
                case 10: SD_10 = v; break;
            }
        }

        private static int GetElementTypeLength(char elementType)
        {
            switch (elementType)
            {
                case 'B':
                case 'Y':
                case 'C':
                    return 1;
                case 'W':
                case 'I':
                    return 2;
                case 'X':
                case 'D':
                case 'R':
                    return 4;
                case 'O':
                    return 8;
                default:
                    return 0;
            }
        }
    }

    public class S7CommPlusAlarmAssociatedValue
    {
        bool ValueBool;
        Int64 ValueInt;
        double ValueReal;
        string ValueString;

        public uint TypeInfo;
        // Allowed types in plc program: Bool, Byte, Char, DInt, DWord, Int, LReal, Real, SInt, String, UDInt, UInt, WChar, Word, WString
        // Break down to .Net types which can handle all these values: Bool, Int64, double, string

        public S7CommPlusAlarmAssociatedValue(uint typeinfo)
        {
            TypeInfo = typeinfo;
        }

        public void SetBool(bool value)
        {
            ValueBool = value;
        }

        public void SetInt(Int64 value)
        {
            ValueInt = value;
        }

        public void SetReal(double value)
        {
            ValueReal = value;
        }

        public void SetString(string value)
        {
            ValueString = value;
        }

        public bool IsString
        {
            get
            {
                return TypeInfo == Ids.TI_CHAR
                    || TypeInfo == Ids.TI_WCHAR
                    || TypeInfo == Ids.TI_STRING
                    || TypeInfo == Ids.TI_WSTRING;
            }
        }

        public bool IsReal
        {
            get
            {
                return TypeInfo == Ids.TI_REAL || TypeInfo == Ids.TI_LREAL;
            }
        }

        public string GetString()
        {
            return ValueString ?? String.Empty;
        }

        public double GetReal()
        {
            return IsReal ? ValueReal : GetSignedInteger();
        }

        internal int GetIntegerByElementType(char elementType)
        {
            switch (elementType)
            {
                case 'B':
                    return (int)(ValueBool ? 1 : GetUnsignedInteger() & 0x1);
                case 'Y':
                case 'C':
                    return (int)(GetUnsignedInteger() & 0xFF);
                case 'W':
                case 'I':
                    return (int)(GetUnsignedInteger() & 0xFFFF);
                case 'X':
                case 'D':
                case 'R':
                    return unchecked((int)(GetUnsignedInteger() & 0xFFFFFFFF));
                default:
                    return unchecked((int)GetUnsignedInteger());
            }
        }

        internal double GetRealByElementType(char elementType)
        {
            if (elementType == 'O' || IsReal)
            {
                return ValueReal;
            }

            return GetIntegerByElementType(elementType);
        }

        public Int64 GetSignedInteger()
        {
            switch (TypeInfo)
            {
                case (Ids.TI_BOOL):
                    return ValueBool ? 1 : 0;
                case (Ids.TI_BYTE):
                case (Ids.TI_USINT):
                    return unchecked((sbyte)(byte)ValueInt);
                case (Ids.TI_WORD):
                case (Ids.TI_UINT):
                    return unchecked((short)(ushort)ValueInt);
                case (Ids.TI_DWORD):
                case (Ids.TI_UDINT):
                    return unchecked((int)(uint)ValueInt);
                case (Ids.TI_INT):
                case (Ids.TI_DINT):
                case (Ids.TI_SINT):
                    return ValueInt;
                case (Ids.TI_REAL):
                case (Ids.TI_LREAL):
                    return Convert.ToInt64(ValueReal, CultureInfo.InvariantCulture);
                default:
                    return ValueInt;
            }
        }

        public UInt64 GetUnsignedInteger()
        {
            switch (TypeInfo)
            {
                case (Ids.TI_BOOL):
                    return ValueBool ? 1u : 0u;
                case (Ids.TI_BYTE):
                case (Ids.TI_USINT):
                case (Ids.TI_SINT):
                    return (byte)ValueInt;
                case (Ids.TI_WORD):
                case (Ids.TI_UINT):
                case (Ids.TI_INT):
                    return (ushort)ValueInt;
                case (Ids.TI_DWORD):
                case (Ids.TI_UDINT):
                case (Ids.TI_DINT):
                    return (uint)ValueInt;
                case (Ids.TI_REAL):
                case (Ids.TI_LREAL):
                    return Convert.ToUInt64(ValueReal, CultureInfo.InvariantCulture);
                default:
                    return (UInt64)Math.Max(0, ValueInt);
            }
        }

        public override string ToString()
        {
            string s = String.Empty;
            switch (TypeInfo)
            {
                case (Ids.TI_BOOL):
                    s = ValueBool.ToString();
                    break;
                case (Ids.TI_BYTE):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_CHAR):
                    s = ValueString;
                    break;
                case (Ids.TI_WORD):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_INT):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_DWORD):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_DINT):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_REAL):
                    s = ValueReal.ToString();
                    break;
                case (Ids.TI_LREAL):
                    s = ValueReal.ToString();
                    break;
                case (Ids.TI_USINT):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_UINT):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_UDINT):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_SINT):
                    s = ValueInt.ToString();
                    break;
                case (Ids.TI_WCHAR):
                    s = ValueString;
                    break;
                case (Ids.TI_STRING):
                    s = ValueString;
                    break;
                case (Ids.TI_WSTRING):
                    s = ValueString;
                    break;
            }
            return s;
        }
    }
}
