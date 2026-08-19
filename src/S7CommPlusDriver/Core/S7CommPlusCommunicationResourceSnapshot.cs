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
using S7CommPlusDriver.Internal;

namespace S7CommPlusDriver
{
    internal sealed class S7CommPlusCommunicationResourceSnapshot
    {
        public int TagsPerReadRequestMax { get; private set; } = 20;
        public int TagsPerWriteRequestMax { get; private set; } = 20;
        public int PlcAttributesMax { get; private set; }
        public int PlcAttributesFree { get; private set; }
        public int PlcSubscriptionsMax { get; private set; }
        public int PlcSubscriptionsFree { get; private set; }
        public int SubscriptionMemoryMax { get; private set; }
        public int SubscriptionMemoryFree { get; private set; }

        public int ReadMax(S7CommPlusProtocolSession conn)
        {
            // Read SystemLimits
            // Assumption (so far, because for all CPUs which have be seen both values were the same):
            // Siemens SystemLimits LIDs:
            // 1000 = number of tags per read request
            // 1001 = number of tags per write request
            int res;
            var readlist = new List<ItemAddress>();
            var values = new List<object>();
            var errors = new List<UInt64>();

            var adrTagsPerReadRequestMax = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.SystemLimits
            };
            adrTagsPerReadRequestMax.AddLocalId(S7CommPlusProtocolConstants.SystemLimitTagsPerReadRequest);

            var adrTagsPerWriteRequestMax = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.SystemLimits
            };
            adrTagsPerWriteRequestMax.AddLocalId(S7CommPlusProtocolConstants.SystemLimitTagsPerWriteRequest);

            var adrPlcSubscriptionsMax = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.SystemLimits
            };
            adrPlcSubscriptionsMax.AddLocalId(S7CommPlusProtocolConstants.SystemLimitPlcSubscriptions);

            var adrPlcAttributesMax = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.SystemLimits
            };
            adrPlcAttributesMax.AddLocalId(S7CommPlusProtocolConstants.SystemLimitPlcAttributes);

            var adrSubscriptionMemoryMax = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.SystemLimits
            };
            adrSubscriptionMemoryMax.AddLocalId(S7CommPlusProtocolConstants.SystemLimitSubscriptionMemory);

            readlist.Add(adrTagsPerReadRequestMax);
            readlist.Add(adrTagsPerWriteRequestMax);
            readlist.Add(adrPlcSubscriptionsMax);
            readlist.Add(adrPlcAttributesMax);
            readlist.Add(adrSubscriptionMemoryMax);

            res = conn.ReadValues(readlist, out values, out errors);
            int i = 0;
            for (i = 0; i < values.Count; i++)
            {
                if (i < errors.Count && errors[i] == 0 && values[i] is ValueDInt value)
                {
                    int v = value.GetValue();
                    switch (i)
                    {
                        case 0:
                            TagsPerReadRequestMax = v;
                            break;
                        case 1:
                            TagsPerWriteRequestMax = v;
                            break;
                        case 2:
                            PlcSubscriptionsMax = v;
                            break;
                        case 3:
                            PlcAttributesMax = v;
                            break;
                        case 4:
                            SubscriptionMemoryMax = v;
                            break;
                    }
                }
            }
            return res;
        }

        public int ReadFree(S7CommPlusProtocolSession conn)
        {
            int res;
            var readlist = new List<ItemAddress>();
            var values = new List<object>();
            var errors = new List<UInt64>();

            var adrPlcSubscriptionsFree = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.FreeItems
            };
            adrPlcSubscriptionsFree.AddLocalId(S7CommPlusProtocolConstants.SystemLimitPlcSubscriptions);

            var adrPlcAttributesFree = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.FreeItems
            };
            adrPlcAttributesFree.AddLocalId(S7CommPlusProtocolConstants.SystemLimitPlcAttributes);

            var adrSubscriptionMemoryFree = new ItemAddress
            {
                AccessArea = Ids.ObjectRoot,
                AccessSubArea = Ids.FreeItems
            };
            adrSubscriptionMemoryFree.AddLocalId(S7CommPlusProtocolConstants.SystemLimitSubscriptionMemory);

            readlist.Add(adrPlcSubscriptionsFree);
            readlist.Add(adrPlcAttributesFree);
            readlist.Add(adrSubscriptionMemoryFree);

            res = conn.ReadValues(readlist, out values, out errors);
            int i = 0;
            for (i = 0; i < values.Count; i++)
            {
                if (i < errors.Count && errors[i] == 0 && values[i] is ValueDInt value)
                {
                    int v = value.GetValue();
                    switch (i)
                    {
                        case 0:
                            PlcSubscriptionsFree = v;
                            break;
                        case 1:
                            PlcAttributesFree = v;
                            break;
                        case 2:
                            SubscriptionMemoryFree = v;
                            break;
                    }
                }
            }
            return res;
        }

        public override string ToString()
        {
            string s = "<S7CommPlusCommunicationResourceSnapshot>" + Environment.NewLine;
            s += "<TagsPerReadRequestMax>" + TagsPerReadRequestMax.ToString() + "</TagsPerReadRequestMax>" + Environment.NewLine;
            s += "<TagsPerWriteRequestMax>" + TagsPerWriteRequestMax.ToString() + "</TagsPerWriteRequestMax>" + Environment.NewLine;
            s += "<PlcAttributesMax>" + PlcAttributesMax.ToString() + "</PlcAttributesMax>" + Environment.NewLine;
            s += "<PlcAttributesFree>" + PlcAttributesFree.ToString() + "</PlcAttributesFree>" + Environment.NewLine;
            s += "<PlcSubscriptionsMax>" + PlcSubscriptionsMax.ToString() + "</PlcSubscriptionsMax>" + Environment.NewLine;
            s += "<PlcSubscriptionsFree>" + PlcSubscriptionsFree.ToString() + "</PlcSubscriptionsFree>" + Environment.NewLine;
            s += "<SubscriptionMemoryMax>" + SubscriptionMemoryMax.ToString() + "</SubscriptionMemoryMax>" + Environment.NewLine;
            s += "<SubscriptionMemoryFree>" + SubscriptionMemoryFree.ToString() + "</SubscriptionMemoryFree>" + Environment.NewLine;
            s += "</S7CommPlusCommunicationResourceSnapshot>" + Environment.NewLine;
            return s;
        }
    }
}
