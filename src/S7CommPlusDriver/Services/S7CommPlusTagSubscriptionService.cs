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
using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Internal;

namespace S7CommPlusDriver
{
    internal sealed class S7CommPlusTagSubscriptionService
    {
        private readonly IS7CommPlusProtocolSession _session;
        private readonly S7CommPlusProtocolRequests _requests;

        public S7CommPlusTagSubscriptionService(IS7CommPlusProtocolSession session)
        {
            _session = session;
            _requests = new S7CommPlusProtocolRequests(session);
        }

        private readonly Dictionary<uint, TagSubscriptionState> _subscriptions = new Dictionary<uint, TagSubscriptionState>();
        byte m_SubcriptionChangeCounter = 1;
        uint m_SubscriptionRelationId = S7CommPlusProtocolConstants.SubscriptionRelationIdStart;

        private sealed class TagSubscriptionState
        {
            public Dictionary<uint, PlcTag> SubscribedTags { get; } = new Dictionary<uint, PlcTag>();
            public short NextCreditLimit { get; set; }
        }

        /// <summary>
        /// Creates a subscription
        /// </summary>
        /// <param name="plcTags">The list of tags to add to the subscription</param>
        /// <param name="cycleTime">Cycle time for update in milliseconds. Lowest value seems to be 100 ms (if it's not dependant on the CPU).</param>
        /// <returns></returns>
        public int Create(List<PlcTag> plcTags, ushort cycleTime)
        {
            return Create(plcTags, cycleTime, S7CommPlusProtocolConstants.DefaultSubscriptionCreditLimit, out _);
        }

        public int Create(List<PlcTag> plcTags, ushort cycleTime, short initialCreditLimit, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            int res;
            var state = new TagSubscriptionState();
            var relationId = m_SubscriptionRelationId++;
            PObject subsobj = new PObject();
            subsobj.ClassId = Ids.ClassSubscription;
            subsobj.RelationId = relationId;
            subsobj.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString("Subscription_" + relationId.ToString()));
            subsobj.AddAttribute(Ids.SubscriptionFunctionClassId, new ValueUSInt((byte)SubscriptionFunctionClass.Variables));
            subsobj.AddAttribute(Ids.SubscriptionMissedSendings, new ValueUInt(0));
            subsobj.AddAttribute(Ids.SubscriptionSubsystemError, new ValueLInt(0));
            subsobj.AddAttribute(Ids.SubscriptionRouteMode, new ValueUSInt((byte)SubscriptionRouteMode.CyclicAndChangedValues));

            // Testresults of some RouteModes (0x04, 0x14, 0x20) some applications are using, together with credit limits:
            // For Alarm Subscription RouteMode 0x02 is used.
            //-----------+-------------+-----------------------------------------------------------------------------------------------------------------------------------------------------------------
            // RouteMode | CreditLimit | Behaviour
            //-----------+-------------+-----------------------------------------------------------------------------------------------------------------------------------------------------------------
            // 0x00      |  0          | No notification at all
            // 0x00      | -1          | All values on create; then values that have changed, empty Notification each cycle; unlimited without retriggering; CreditTick always 0
            // 0x00      | n>0         | All values on create; then values that have changed, empty Notification each cycle; stops after CreditTick reaches difference of n when not set to new value
            // 0x04      | 0           | Identical to 0x00 / 0
            // 0x04      | -1          | Identical to 0x00 / -1
            // 0x04      | n>0         | Identical to 0x00 / n>0
            // 0x14      | 0           | Identical to 0x00 / 0
            // 0x14      | -1          | Identical to 0x00 / -1
            // 0x14      | n>0         | Identical to 0x00 / n>0
            // 0x20      | 0           | Identical to 0x00 / 0
            // 0x20      | -1          | All values on create; then values that have changed, on cycle without change no notification; unlimited without retriggering; CreditTick always 0
            // 0x20      | n>0         | All values on create; then values that have changed, on cycle without change no notification; stops after CreditTick reaches difference of n when not set to new value

            subsobj.AddAttribute(Ids.SubscriptionActive, new ValueBool(true));
            subsobj.AddAttribute(Ids.SubscriptionReferenceList, GetSubscriptionListArray(plcTags, state.SubscribedTags));
            subsobj.AddAttribute(Ids.SubscriptionCycleTime, new ValueUDInt(cycleTime));
            subsobj.AddAttribute(Ids.SubscriptionDisabled, new ValueUSInt(0));
            subsobj.AddAttribute(Ids.SubscriptionCount, new ValueUSInt(0));
            state.NextCreditLimit = initialCreditLimit;
            subsobj.AddAttribute(Ids.SubscriptionCreditLimit, new ValueInt(state.NextCreditLimit)); // -1=unlimited, 255 = max
            subsobj.AddAttribute(Ids.SubscriptionTicks, new ValueUInt(S7CommPlusProtocolConstants.SubscriptionTicksUnlimited));
            subsobj.AddAttribute(S7CommPlusProtocolConstants.SubscriptionDefaultAttribute1055, new ValueUSInt(0));

            // Build the request object
            var createObjReq = new CreateObjectRequest(ProtocolVersion.V2, 0, true);
            createObjReq.TransportFlags = S7CommPlusProtocolConstants.RequestWithResponseTransportFlags;
            createObjReq.RequestId = _session.SessionId2;
            createObjReq.RequestValue = new ValueUDInt(0);
            createObjReq.SetRequestObject(subsobj);

            // Send it
            res = _requests.CreateObject(createObjReq, out var createObjRes);
            if (res != 0)
            {
                _session.DisconnectTransport();
                return res;
            }

            if (createObjRes.ReturnValue == 0)
            {
                subscriptionObjectId = createObjRes.ObjectIds[0];
                _subscriptions[subscriptionObjectId] = state;
            }
            else
            {
                // If creating a subscription fails, the object is still created and should be deleted.
                // At least deleting it, gives no error.
                System.Diagnostics.Trace.WriteLine(String.Format("Subscription - Create: Failed with Returnvalue = 0x{0:X8}", createObjRes.ReturnValue));
                res = S7Consts.errCliInvalidParams;
            }
            return res;
        }

        private int SubscriptionSetCreditLimit(uint subscriptionObjectId, short limit)
        {
            return _requests.SetSubscriptionCreditLimit(subscriptionObjectId, limit);
        }

        private ValueUDIntArray GetSubscriptionListArray(List<PlcTag> plcTags, Dictionary<uint, PlcTag> subscribedTags)
        {
            var la = new List<uint>();
            // 0x8?ssxxxx = 8 = create/update flag, ss = subscription change counter.
            la.Add(S7CommPlusProtocolConstants.SubscriptionListCreateFlag | ((uint)(m_SubcriptionChangeCounter) << 16));
            la.Add(0);                     // Number of items to unsubscribe
            la.Add((uint)plcTags.Count);   // Number of items to subscribe

            uint tagReferenceId = 1;
            uint head;
            foreach (var tag in plcTags)
            {
                // Save the reference Id in the dictionary. In the notification we get this reference Id back
                // and know to which tag the value belongs to.
                subscribedTags.Add(tagReferenceId, tag);
                // Write the Item address
                head = S7CommPlusProtocolConstants.SubscriptionItemAddressHeaderFlag;
                // It's not known where 0x8004 stands for -> 4 was a guess it's for the number of fields
                // before the LIDs, but that's wrong (coincidentally fits here in this special case).
                // Get the number of IDs in advance, Sub-Area counts as one, and then count each LID.
                // 0x8aaabbbb = aaa = unknown value, bbbb = number of fields in the 2nd part.
                head |= (uint)(1 + tag.Address.LocalIdCount);
                la.Add(head);
                la.Add(tagReferenceId);
                la.Add(0); // Unknown 1
                la.Add(tag.Address.AccessArea);
                la.Add(tag.Address.SymbolCrc);
                // Count value in head starts from here
                la.Add(tag.Address.AccessSubArea);
                for (var localIdIndex = 0; localIdIndex < tag.Address.LocalIdCount; localIdIndex++)
                {
                    la.Add(tag.Address.GetLocalId(localIdIndex));
                }
                tagReferenceId++;
            }
            // Convert all data to protocol UDInt Array (VLQ encoded)
            return new ValueUDIntArray(la.ToArray(), S7CommPlusProtocolConstants.ValueAddressArrayFlag);
        }

        public int TestWaitForNotifications(int untilNumberOfNotifications)
        {
            int res = 0;
            short creditLimitStep = S7CommPlusProtocolConstants.DefaultSubscriptionCreditLimitStep;
            var subscriptionObjectId = 0u;
            foreach (var subscription in _subscriptions)
            {
                subscriptionObjectId = subscription.Key;
                break;
            }

            if (subscriptionObjectId == 0 || !_subscriptions.TryGetValue(subscriptionObjectId, out var state))
            {
                return S7Consts.errCliInvalidParams;
            }

            for (int i = 1; i <= untilNumberOfNotifications; i++)
            {
                System.Diagnostics.Trace.WriteLine(Environment.NewLine + "WaitForNotifications(): *** Loop #" + i.ToString() + " ***");
                var result = _requests.WaitNotification(subscriptionObjectId, 5000, out var noti);
                if (result != 0)
                {
                    return result;
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine("Notification: CreditTick=" + noti.NotificationCreditTick + " SequenceNumber=" + noti.NotificationSequenceNumber);
                    System.Diagnostics.Trace.WriteLine(String.Format("PLC-Timestamp={0}.{1:D03} ValuesCount={2}", noti.Add1Timestamp.ToString(), noti.Add1Timestamp.Millisecond, noti.Values.Count));
                    foreach(var v in noti.Values)
                    {
                        System.Diagnostics.Trace.WriteLine("---> key=" + v.Key + " value=" + v.Value.ToString());
                        // Notification item errors are one-byte return codes; tag read errors use the 64-bit PLC return value space.
                        state.SubscribedTags[v.Key].ProcessReadResult(v.Value, 0);
                    }

                    if (noti.NotificationCreditTick >= state.NextCreditLimit - 1) // Set new limit one tick before it expires, to get a constant flow of data
                    {
                        // CreditTick in Notification is only one byte
                        state.NextCreditLimit = (short)((state.NextCreditLimit + creditLimitStep) % 255);
                        System.Diagnostics.Trace.WriteLine("--> Credit limit of " + noti.NotificationCreditTick + " reached. SetCreditLimit to " + state.NextCreditLimit.ToString());
                        SubscriptionSetCreditLimit(subscriptionObjectId, state.NextCreditLimit);
                    }
                }
            }
            return res;
        }

        public int WaitForNotifications(uint subscriptionObjectId, int waitTimeout, short creditLimitStep, out List<Notification> notifications)
        {
            notifications = new List<Notification>();
            if (!_subscriptions.TryGetValue(subscriptionObjectId, out var state))
            {
                return S7Consts.errCliInvalidParams;
            }

            var result = _requests.WaitNotification(subscriptionObjectId, waitTimeout, out var noti);
            if (result != 0)
            {
                return result;
            }

            notifications.Add(noti);

            foreach (var value in noti.Values)
            {
                if (state.SubscribedTags.TryGetValue(value.Key, out var tag))
                {
                    tag.ProcessReadResult(value.Value, 0);
                }
            }

            foreach (var returnValue in noti.ReturnValues)
            {
                if (state.SubscribedTags.TryGetValue(returnValue.Key, out var tag))
                {
                    tag.ProcessReadResult(null, returnValue.Value);
                }
            }

            if (creditLimitStep > 0 && noti.NotificationCreditTick >= state.NextCreditLimit - 1)
            {
                state.NextCreditLimit = (short)((state.NextCreditLimit + creditLimitStep) % 255);
                if (state.NextCreditLimit == 0)
                {
                    state.NextCreditLimit = creditLimitStep;
                }
                return SubscriptionSetCreditLimit(subscriptionObjectId, state.NextCreditLimit);
            }

            return 0;
        }

        public int Delete(uint subscriptionObjectId)
        {
            int res;
            if (subscriptionObjectId == 0)
            {
                return 0;
            }

            _subscriptions.Remove(subscriptionObjectId);
            System.Diagnostics.Trace.WriteLine(String.Format("SubscriptionDelete: Calling DeleteObject for SubscriptionObjectId={0:X8}", subscriptionObjectId));
            res = _session.DeleteObject(subscriptionObjectId);
            return res;
        }
    }
}
