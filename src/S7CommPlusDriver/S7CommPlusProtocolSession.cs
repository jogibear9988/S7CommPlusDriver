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
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Internal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace S7CommPlusDriver
{
    internal partial class S7CommPlusProtocolSession
    {
        #region Private Members
        private S7Client m_client;
        private MemoryStream m_ReceivedPDU;
        private MemoryStream m_ReceivedTempPDU;
        private Channel<ReceivedS7PlusPdu> m_ReceivedPDUs = CreateReceiveChannel();
        private readonly object m_RequestLock = new object();
        private readonly object m_ReceiveDispatchLock = new object();
        private readonly object m_NotificationQueueLock = new object();
        private readonly Dictionary<uint, Queue<Notification>> m_NotificationQueues = new Dictionary<uint, Queue<Notification>>();

        private bool m_ReceivedNeedMoreDataForCompletePDU;
        private bool m_NewS7CommPlusReceived;
        private IS7pRequest m_LastSentRequestForWait;
        private UInt32 m_SessionId;
        private UInt32 m_SessionId2;
        public UInt32 SessionId2
        {
            get { return m_SessionId2; }
            private set { m_SessionId2 = value; }
        }

        private int m_ReadTimeout = 5000;
        private UInt16 m_SequenceNumber = 0;
        private UInt32 m_IntegrityId = 0;
        private UInt32 m_IntegrityId_Set = 0;
        private bool m_LegacyDigestActive;
        private byte[] m_LegacySessionKey;
#if HARPOS7_LEGACY_AUTH
        private long m_LegacyDigestTraceSequence;
#endif
        private ValueStruct m_ServerSessionVersion;
        private S7CommPlusSecurityMode m_NegotiatedSecurityMode = S7CommPlusSecurityMode.Tls;
        private S7CommPlusCommunicationResourceSnapshot m_CommunicationResources = new S7CommPlusCommunicationResourceSnapshot();
        private string m_LastErrorDetail = string.Empty;

        private List<DatablockInfo> dbInfoList;
        private List<PObject> typeInfoList = new List<PObject>();
        #endregion

        #region Public Members
        public int m_LastError = 0;

        #endregion

        #region Private Methods
        private sealed class ReceivedS7PlusPdu
        {
            public ReceivedS7PlusPdu(MemoryStream pdu, int errorCode = 0)
            {
                Pdu = pdu;
                ErrorCode = errorCode;
            }

            public MemoryStream Pdu { get; }
            public int ErrorCode { get; }
        }

        private static Channel<ReceivedS7PlusPdu> CreateReceiveChannel()
        {
            return Channel.CreateUnbounded<ReceivedS7PlusPdu>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        private UInt16 GetNextSequenceNumber()
        {
            if (m_SequenceNumber == UInt16.MaxValue)
            {
                m_SequenceNumber = 1;
            }
            else
            {
                m_SequenceNumber++;
            }
            return m_SequenceNumber;
        }

        // We must count the IntegrityId for different functions of the protocol.
        // As a first guess functions for setting variables need separate counters.
        // Use the functioncode to differ between the which sequence/integrity counter values.
        private UInt32 GetNextIntegrityId(ushort functioncode)
        {
            UInt32 ret;
            switch (functioncode)
            {
                case Functioncode.SetMultiVariables:
                case Functioncode.SetVariable:
                case Functioncode.SetVarSubStreamed:
                case Functioncode.DeleteObject:
                case Functioncode.CreateObject:
                    if (m_IntegrityId_Set == UInt32.MaxValue)
                    {
                        m_IntegrityId_Set = 0;
                    }
                    else
                    {
                        m_IntegrityId_Set++;
                    }
                    ret = m_IntegrityId_Set;
                    break;
                default:
                    if (m_IntegrityId == UInt32.MaxValue)
                    {
                        m_IntegrityId = 0;
                    }
                    else
                    {
                        m_IntegrityId++;
                    }
                    ret = m_IntegrityId;
                    break;
            }
            return ret;
        }

        private void WaitForNewS7plusReceived(int Timeout)
        {
            var expectedRequest = m_LastSentRequestForWait;
            m_LastError = expectedRequest == null
                ? ReceiveNextS7plusPdu(Timeout, out m_ReceivedPDU)
                : WaitForExpectedResponse(expectedRequest, Timeout);
            if (m_LastError != 0)
            {
                Trace.WriteLine("S7CommPlusProtocolSession - WaitForNewS7plusReceived: ERROR: " + S7Client.ErrorText(m_LastError));
            }
        }

        private int ReceiveNextS7plusPdu(int timeout, out MemoryStream pdu)
        {
            pdu = null;
            try
            {
                using var cts = new CancellationTokenSource(Math.Max(1, timeout));
                var received = m_ReceivedPDUs.Reader.ReadAsync(cts.Token).AsTask().GetAwaiter().GetResult();
                if (received.ErrorCode != 0)
                {
                    return received.ErrorCode;
                }
                pdu = received.Pdu;
                return 0;
            }
            catch (OperationCanceledException)
            {
                return S7Consts.errCliJobTimeout;
            }
            catch (ChannelClosedException)
            {
                return S7Consts.errTCPNotConnected;
            }
        }

        private int SendS7plusFunctionObject(IS7pRequest funcObj)
        {
            if (m_LegacyDigestActive && funcObj.ProtocolVersion != ProtocolVersion.V1)
            {
                funcObj.ProtocolVersion = ProtocolVersion.V3;
            }

            // If we don't have a SessionId, this must be the first CreateObjectRequest, where we use the Id for NullServerSession
            if (m_SessionId == 0)
            {
                funcObj.SessionId = Ids.ObjectNullServerSession;
            }
            else
            {
                funcObj.SessionId = m_SessionId;
            }

            // Insert SequenceNumber and IntegrityId, if neccessary for object type and state of communication
            funcObj.SequenceNumber = GetNextSequenceNumber();
            if (funcObj.WithIntegrityId)
            {
                funcObj.IntegrityId = GetNextIntegrityId(funcObj.FunctionCode);
            }
            m_LastSentRequestForWait = funcObj;

            MemoryStream stream = new MemoryStream();
            funcObj.Serialize(stream);
            return SendS7plusPDUdata(stream.ToArray(), (int)stream.Length, funcObj.ProtocolVersion);
        }

        private int SendS7plusFunctionObjectSerialized(IS7pRequest funcObj)
        {
            lock (m_RequestLock)
            {
                try
                {
                    return SendS7plusFunctionObject(funcObj);
                }
                finally
                {
                    if (ReferenceEquals(m_LastSentRequestForWait, funcObj))
                    {
                        m_LastSentRequestForWait = null;
                    }
                }
            }
        }

        private int SendS7plusFunctionObjectAndWait(IS7pRequest funcObj, int timeout)
        {
            lock (m_RequestLock)
            {
                try
                {
                    var result = SendS7plusFunctionObject(funcObj);
                    if (result != 0)
                    {
                        return result;
                    }

                    m_LastError = 0;
                    return WaitForExpectedResponse(funcObj, timeout);
                }
                finally
                {
                    if (ReferenceEquals(m_LastSentRequestForWait, funcObj))
                    {
                        m_LastSentRequestForWait = null;
                    }
                }
            }
        }

        private int SendRawS7plusPduAndWait(byte[] sendPduData, int bytesToSend, byte protoVersion, int timeout)
        {
            lock (m_RequestLock)
            {
                var result = SendS7plusPDUdata(sendPduData, bytesToSend, protoVersion);
                if (result != 0)
                {
                    return result;
                }

                m_LastSentRequestForWait = null;
                m_LastError = ReceiveNextS7plusPdu(timeout, out m_ReceivedPDU);
                if (m_LastError != 0)
                {
                    Trace.WriteLine("S7CommPlusProtocolSession - SendRawS7plusPduAndWait: ERROR: " + S7Client.ErrorText(m_LastError));
                }
                return m_LastError;
            }
        }

        private int WaitForExpectedResponse(IS7pRequest request, int timeout)
        {
            var deadline = RuntimeCompatibility.TickCount64 + Math.Max(1, timeout);
            while (RuntimeCompatibility.TickCount64 < deadline)
            {
                var remaining = (int)Math.Min(50, Math.Max(1, deadline - RuntimeCompatibility.TickCount64));
                var result = DispatchOneReceivedPdu(remaining, request, 0, out var responsePdu, out _);
                if (result == S7Consts.errCliJobTimeout || result == S7Consts.errTCPReceiveTimeout)
                {
                    continue;
                }
                if (result != 0)
                {
                    return result;
                }
                if (responsePdu != null)
                {
                    m_ReceivedPDU = responsePdu;
                    return 0;
                }
            }

            return S7Consts.errCliJobTimeout;
        }

        private int WaitForNotification(uint subscriptionObjectId, int timeout, out Notification notification)
        {
            notification = null;
            var deadline = RuntimeCompatibility.TickCount64 + Math.Max(1, timeout);
            while (RuntimeCompatibility.TickCount64 < deadline)
            {
                if (TryDequeueNotification(subscriptionObjectId, out notification))
                {
                    return 0;
                }

                var remaining = (int)Math.Min(50, Math.Max(1, deadline - RuntimeCompatibility.TickCount64));
                var result = DispatchOneReceivedPdu(remaining, null, subscriptionObjectId, out _, out notification);
                if (result == S7Consts.errCliJobTimeout || result == S7Consts.errTCPReceiveTimeout)
                {
                    continue;
                }
                if (result != 0)
                {
                    return result;
                }
                if (notification != null && (subscriptionObjectId == 0 || NotificationMatches(notification, subscriptionObjectId)))
                {
                    return 0;
                }
            }

            notification = null;
            return S7Consts.errCliJobTimeout;
        }

        private int DispatchOneReceivedPdu(int timeout, IS7pRequest expectedResponse, uint notificationSubscriptionObjectId, out MemoryStream responsePdu, out Notification matchingNotification)
        {
            responsePdu = null;
            matchingNotification = null;

            if (!Monitor.TryEnter(m_ReceiveDispatchLock, timeout))
            {
                return S7Consts.errCliJobTimeout;
            }

            try
            {
                var result = ReceiveNextS7plusPdu(timeout, out var pdu);
                if (result != 0)
                {
                    return result;
                }

                if (TryPeekS7PlusPdu(pdu, out var opcode, out var function, out var sequenceNumber))
                {
                    if (opcode == Opcode.Response)
                    {
                        if (expectedResponse != null
                            && function == expectedResponse.FunctionCode
                            && sequenceNumber == expectedResponse.SequenceNumber)
                        {
                            pdu.Position = 0;
                            responsePdu = pdu;
                            return 0;
                        }

                        Trace.WriteLine($"S7CommPlusProtocolSession - Dispatch: discarded unexpected response function=0x{function:X4} seq={sequenceNumber}.");
                        return 0;
                    }

                    if (opcode == Opcode.Notification)
                    {
                        pdu.Position = 0;
                        var notification = Notification.DeserializeFromPdu(pdu);
                        if (notification == null)
                        {
                            return S7Consts.errIsoInvalidPDU;
                        }

                        if (expectedResponse == null
                            && (notificationSubscriptionObjectId == 0 || NotificationMatches(notification, notificationSubscriptionObjectId)))
                        {
                            if (notificationSubscriptionObjectId != 0)
                            {
                                EnqueueNotification(notification, notificationSubscriptionObjectId);
                            }
                            matchingNotification = notification;
                            return 0;
                        }

                        EnqueueNotification(notification);
                        return 0;
                    }
                }

                return 0;
            }
            finally
            {
                Monitor.Exit(m_ReceiveDispatchLock);
            }
        }

        private static bool TryPeekS7PlusPdu(MemoryStream pdu, out byte opcode, out ushort function, out ushort sequenceNumber)
        {
            opcode = 0;
            function = 0;
            sequenceNumber = 0;
            if (pdu == null)
            {
                return false;
            }

            var position = pdu.Position;
            try
            {
                pdu.Position = 0;
                if (pdu.Length < 2)
                {
                    return false;
                }

                S7p.DecodeByte(pdu, out var protocolVersion);
                if (protocolVersion == ProtocolVersion.SystemEvent)
                {
                    return false;
                }

                S7p.DecodeByte(pdu, out opcode);
                if (opcode == Opcode.Response)
                {
                    if (pdu.Length < 10)
                    {
                        return false;
                    }
                    S7p.DecodeUInt16(pdu, out _);
                    S7p.DecodeUInt16(pdu, out function);
                    S7p.DecodeUInt16(pdu, out _);
                    S7p.DecodeUInt16(pdu, out sequenceNumber);
                }

                return opcode == Opcode.Response || opcode == Opcode.Notification;
            }
            finally
            {
                pdu.Position = position;
            }
        }

        private void EnqueueNotification(Notification notification)
        {
            EnqueueNotification(notification, 0);
        }

        private void EnqueueNotification(Notification notification, uint excludedSubscriptionObjectId)
        {
            lock (m_NotificationQueueLock)
            {
                if (notification.SubscriptionObjectId != excludedSubscriptionObjectId)
                {
                    EnqueueNotification(notification.SubscriptionObjectId, notification);
                }
                if (notification.P2SubscriptionObjectId != 0 && notification.P2SubscriptionObjectId != notification.SubscriptionObjectId)
                {
                    if (notification.P2SubscriptionObjectId != excludedSubscriptionObjectId)
                    {
                        EnqueueNotification(notification.P2SubscriptionObjectId, notification);
                    }
                }
                Monitor.PulseAll(m_NotificationQueueLock);
            }
        }

        private void EnqueueNotification(uint subscriptionObjectId, Notification notification)
        {
            if (subscriptionObjectId == 0)
            {
                return;
            }

            if (!m_NotificationQueues.TryGetValue(subscriptionObjectId, out var queue))
            {
                queue = new Queue<Notification>();
                m_NotificationQueues.Add(subscriptionObjectId, queue);
            }
            queue.Enqueue(notification);
        }

        private bool TryDequeueNotification(uint subscriptionObjectId, out Notification notification)
        {
            lock (m_NotificationQueueLock)
            {
                if (subscriptionObjectId != 0)
                {
                    if (m_NotificationQueues.TryGetValue(subscriptionObjectId, out var queue) && queue.Count > 0)
                    {
                        notification = queue.Dequeue();
                        return true;
                    }
                }
                else
                {
                    foreach (var queue in m_NotificationQueues.Values)
                    {
                        if (queue.Count > 0)
                        {
                            notification = queue.Dequeue();
                            return true;
                        }
                    }
                }
            }

            notification = null;
            return false;
        }

        private static bool NotificationMatches(Notification notification, uint subscriptionObjectId)
        {
            return notification.SubscriptionObjectId == subscriptionObjectId
                || notification.P2SubscriptionObjectId == subscriptionObjectId;
        }

        private void ClearNotificationQueues()
        {
            lock (m_NotificationQueueLock)
            {
                m_NotificationQueues.Clear();
            }
        }

        private int SendS7plusPDUdata(byte[] sendPduData, int bytesToSend, byte protoVersion)
        {
            m_LastError = 0;

            int curSize;
            int sourcePos = 0;
            int sendLen;
            bool useLegacyDigest = ShouldUseLegacyDigest(protoVersion);
            int legacyDigestLength = useLegacyDigest ? LegacyDigestFieldLength : 0;
            int MaxSize = GetMaxS7CommPlusPayloadSize(legacyDigestLength);
            if (MaxSize <= 0)
            {
                return S7Consts.errIsoInvalidPDU;
            }
            if (useLegacyDigest)
            {
                int buildError = BuildLegacyProtectedPdu(
                    sendPduData,
                    bytesToSend,
                    protoVersion,
                    out byte[] protectedPacket);
                if (buildError != 0)
                    return buildError;
                m_client.Send(protectedPacket);
                return m_client._LastError;
            }
            byte[] packet = new byte[MaxSize + S7CommPlusProtocolConstants.S7CommPlusHeaderLength + legacyDigestLength];

            while (bytesToSend > 0)
            {
                if (bytesToSend > MaxSize)
                {
                    curSize = MaxSize;
                    bytesToSend -= MaxSize;
                }
                else
                {
                    curSize = bytesToSend;
                    bytesToSend -= curSize;
                }
                // Header
                packet[0] = S7CommPlusProtocolConstants.FrameMarker;
                packet[1] = protoVersion;
                int s7DataLength = curSize + legacyDigestLength;
                packet[2] = (byte)(s7DataLength >> 8);
                packet[3] = (byte)(s7DataLength & 0x00FF);
                // Data part
                if (useLegacyDigest)
                {
                    if (!TryWriteLegacyDigest(packet, 4, sendPduData, 0, sourcePos + curSize))
                    {
                        return S7Consts.errS7CommPlusDigestMismatch;
                    }
                    Array.Copy(sendPduData, sourcePos, packet, 4 + legacyDigestLength, curSize);
                }
                else
                {
                    Array.Copy(sendPduData, sourcePos, packet, 4, curSize);
                }
                sourcePos += curSize;
                sendLen = S7CommPlusProtocolConstants.S7CommPlusHeaderLength + s7DataLength;

                // Trailer only in last packet
                if (bytesToSend == 0)
                {
                    Array.Resize(ref packet, sendLen + S7CommPlusProtocolConstants.S7CommPlusTrailerLength);
                    packet[sendLen] = S7CommPlusProtocolConstants.FrameMarker;
                    sendLen++;
                    packet[sendLen] = protoVersion;
                    sendLen++;
                    packet[sendLen] = 0;
                    sendLen++;
                    packet[sendLen] = 0;
                    sendLen++;
                }
                m_client.Send(packet);
                if (m_client._LastError != 0)
                {
                    return m_client._LastError;
                }
            }
            return m_LastError;
        }

        private int BuildLegacyProtectedPdu(
            byte[] sendPduData,
            int bytesToSend,
            byte protoVersion,
            out byte[] protectedPacket)
        {
            protectedPacket = null;
            if (sendPduData == null || bytesToSend < 0 || bytesToSend > sendPduData.Length
                || bytesToSend > UInt16.MaxValue - LegacyDigestFieldLength)
            {
                return S7Consts.errS7CommPlusLegacyRequestTooLarge;
            }

            int dataLength = bytesToSend + LegacyDigestFieldLength;
            protectedPacket = new byte[
                S7CommPlusProtocolConstants.S7CommPlusHeaderLength
                + dataLength
                + S7CommPlusProtocolConstants.S7CommPlusTrailerLength];
            protectedPacket[0] = S7CommPlusProtocolConstants.FrameMarker;
            protectedPacket[1] = protoVersion;
            protectedPacket[2] = (byte)(dataLength >> 8);
            protectedPacket[3] = (byte)dataLength;
            if (!TryWriteLegacyDigest(protectedPacket, 4, sendPduData, 0, bytesToSend))
            {
                protectedPacket = null;
                return S7Consts.errS7CommPlusDigestMismatch;
            }
            Array.Copy(sendPduData, 0, protectedPacket, 4 + LegacyDigestFieldLength, bytesToSend);
            int trailerOffset = 4 + dataLength;
            protectedPacket[trailerOffset] = S7CommPlusProtocolConstants.FrameMarker;
            protectedPacket[trailerOffset + 1] = protoVersion;
            return 0;
        }

        private int GetMaxS7CommPlusPayloadSize(int legacyDigestLength)
        {
            int negotiatedTpduSize = m_client?.PduSizeNegotiated > 0
                ? m_client.PduSizeNegotiated
                : S7CommPlusProtocolConstants.DefaultIsoTpduSize;
            return negotiatedTpduSize
                - S7CommPlusProtocolConstants.TpktHeaderLength
                - S7CommPlusProtocolConstants.CotpHeaderLength
                - S7CommPlusProtocolConstants.TlsRecordHeaderLength
                - S7CommPlusProtocolConstants.TlsAesGcmRecordOverhead
                - legacyDigestLength
                - S7CommPlusProtocolConstants.S7CommPlusHeaderLength
                - S7CommPlusProtocolConstants.S7CommPlusTrailerLength;
        }

        private int GetSingleFramePayloadLimit(byte protocolVersion)
        {
            int legacyDigestLength = ShouldUseLegacyDigest(protocolVersion) ? LegacyDigestFieldLength : 0;
            return GetMaxS7CommPlusPayloadSize(legacyDigestLength);
        }

        private static bool ExceedsSingleFramePayload(IS7pRequest request, int maxPayloadSize)
        {
            if (maxPayloadSize <= 0)
            {
                return true;
            }

            return GetSerializedRequestLengthForBatching(request) > maxPayloadSize;
        }

        private static long GetSerializedRequestLengthForBatching(IS7pRequest request)
        {
            uint sessionId = request.SessionId;
            ushort sequenceNumber = request.SequenceNumber;
            uint integrityId = request.IntegrityId;
            try
            {
                // The integrity id is VLQ encoded, so use its widest representation when
                // deciding whether an item still fits before the real request is numbered.
                request.SessionId = UInt32.MaxValue;
                request.SequenceNumber = UInt16.MaxValue;
                request.IntegrityId = UInt32.MaxValue;

                using var stream = new MemoryStream();
                request.Serialize(stream);
                return stream.Length;
            }
            finally
            {
                request.SessionId = sessionId;
                request.SequenceNumber = sequenceNumber;
                request.IntegrityId = integrityId;
            }
        }

        private void OnDataReceived(byte[] PDU, int len)
        {
            // In this method, we've got always a complete TPDU (from protocol layer above) without fragmentation
            // At this point, we can detect if we receive a fragmented S7CommPlus PDU.
            // If not fragmented, then TPKT.Length - 15 is equal of the length in S7CommPlus.Header.
            // 15 bytes because: 4 Bytes TPKT.Header.len + 3 Bytes ISO.Header.Len + 4 Bytes S7CommPlus.Header.len + 4 Bytes S7CommPlus.trailer.Len.
            // Since the pure userdata of the TPDU comes in here, that is only minus 4 bytes header + 4 bytes trailer.
            //
            // Special handling for SystemEvents with ProtocolVersion = 0xfe:
            // Here's only a header.
            // Because of this, the first byte for the ProtocolVersion must be written in then stream at first.
            // The datalength must not be written into the stream, because it's not valid on fragmented PDUs
            // for the complete length, only for the single fragment.

            // This method is called from a different thread.
            // If we use subscriptions or alarming, we may get new data before the last PDU was processed completely.
            // First step we push the complete PDU to a queue.
            // Receive errors are published through the receive queue so request waiters fail immediately.

            if (!m_ReceivedNeedMoreDataForCompletePDU)
            {
                m_ReceivedTempPDU = new MemoryStream();
            }
            // S7comm-plus
            byte protoVersion;
            int pos = 0;
            int s7HeaderDataLen = 0;
            // Check header
            if (PDU[pos] != S7CommPlusProtocolConstants.FrameMarker)
            {
                m_ReceivedNeedMoreDataForCompletePDU = false;
                PublishReceiveError(S7Consts.errIsoInvalidPDU1);
                return;
            }
            pos++;
            protoVersion = PDU[pos];
            if (protoVersion != ProtocolVersion.V1 && protoVersion != ProtocolVersion.V2 && protoVersion != ProtocolVersion.V3 && protoVersion != ProtocolVersion.SystemEvent)
            {
                m_ReceivedNeedMoreDataForCompletePDU = false;
                PublishReceiveError(S7Consts.errIsoInvalidPDU2);
                return;
            }
            // For the first fragment, write the ProtocolVersion into the stream in advance
            if (!m_ReceivedNeedMoreDataForCompletePDU)
            {
                m_ReceivedTempPDU.Write(PDU, pos, 1);
            }
            pos++;

            // Read the length of the data-part from header
            s7HeaderDataLen = GetWordAt(PDU, pos);
            pos += 2;
            if (s7HeaderDataLen > 0)
            {
                // Special handling for SystemEvent 0xfe PDUs:
                // This only confirms a few data, but also reports major protocol errors (e.g. incorrect sequence numbers).
                // The confirms can be discarded (for now), but the errors are relevant, because a connection termination is neccessary.
                // As we don't have a trailer on this types, it's not possible that they are transmitted as fragments.
                if (protoVersion == ProtocolVersion.SystemEvent)
                {
                    Trace.WriteLine("S7CommPlusProtocolSession - OnDataReceived: ProtocolVersion 0xfe SystemEvent received");
                    m_ReceivedTempPDU.Write(PDU, pos, s7HeaderDataLen);
                    pos += s7HeaderDataLen;
                    // Create SystemEventObject
                    m_ReceivedNeedMoreDataForCompletePDU = false;
                    m_ReceivedTempPDU.Position = 0;
                    m_NewS7CommPlusReceived = false;

                    var sysevt = SystemEvent.DeserializeFromPdu(m_ReceivedTempPDU);
                    if (sysevt.IsFatalError())
                    {
                        Trace.WriteLine("S7CommPlusProtocolSession - OnDataReceived: SystemEvent has fatal error");
                        // Termination neccessary
                        PublishReceiveError(S7Consts.errIsoInvalidPDU3);
                    }
                    else
                    {
                        Trace.WriteLine("S7CommPlusProtocolSession - OnDataReceived: SystemEvent with non fatal error, do nothing");
                    }
                }
                else
                {
                    var dataPos = pos;
                    var dataLen = s7HeaderDataLen;
                    if (ShouldUseLegacyDigest(protoVersion))
                    {
                        if (dataLen < LegacyDigestFieldLength)
                        {
                            m_ReceivedNeedMoreDataForCompletePDU = false;
                            PublishReceiveError(S7Consts.errS7CommPlusDigestMismatch);
                            return;
                        }
                        var legacyBodyPos = dataPos + LegacyDigestFieldLength;
                        var legacyBodyLen = dataLen - LegacyDigestFieldLength;
                        var previousBodyLen = Math.Max(0, (int)m_ReceivedTempPDU.Length - 1);
                        var digestData = previousBodyLen == 0
                            ? PDU
                            : BuildLegacyAccumulatedDigestData(PDU, legacyBodyPos, legacyBodyLen, previousBodyLen);
                        var digestDataOffset = previousBodyLen == 0 ? legacyBodyPos : 0;
                        var digestDataLen = previousBodyLen + legacyBodyLen;
                        // Siemens OMS verifies the first protected large frame, then sets an internal
                        // "MAC already verified" flag for continuation fragments in the same logical PDU.
                        var legacyDigestVerified = previousBodyLen == 0;
                        var legacyDigestMatched = !legacyDigestVerified || TryVerifyLegacyDigest(
                                PDU,
                                dataPos,
                                digestData,
                                digestDataOffset,
                                digestDataLen);
                        TraceLegacyDigestReceive(
                            PDU,
                            len,
                            dataPos,
                            legacyBodyPos,
                            legacyBodyLen,
                            previousBodyLen,
                            digestData,
                            digestDataOffset,
                            digestDataLen,
                            legacyDigestVerified,
                            legacyDigestMatched);
                        if (legacyDigestVerified && !legacyDigestMatched)
                        {
                            m_ReceivedNeedMoreDataForCompletePDU = false;
                            m_ReceivedTempPDU = null;
                            PublishReceiveError(S7Consts.errS7CommPlusDigestMismatch);
                            return;
                        }
                        dataPos += LegacyDigestFieldLength;
                        dataLen -= LegacyDigestFieldLength;
                    }
                    // Copy data part to destination stream
                    m_ReceivedTempPDU.Write(PDU, dataPos, dataLen);
                    pos += s7HeaderDataLen;
                    // If this is a fragmented PDU, then at this point no trailer
                    if ((len - 4 - 4) == s7HeaderDataLen)
                    {
                        m_ReceivedNeedMoreDataForCompletePDU = false;
                        m_ReceivedTempPDU.Position = 0;    // Set position back to zero, ready for readout
                        m_NewS7CommPlusReceived = true;
                    }
                    else
                    {
                        m_ReceivedNeedMoreDataForCompletePDU = true;
                    }
                }
            }

            // If a complete (usable) PDU is received, add to the queue (threadsafe) for readout
            if (m_NewS7CommPlusReceived)
            {
                m_ReceivedPDUs.Writer.TryWrite(new ReceivedS7PlusPdu(m_ReceivedTempPDU));
                m_NewS7CommPlusReceived = false;
            }
        }

        private void PublishReceiveError(int errorCode)
        {
            m_LastError = errorCode;
            m_ReceivedPDUs.Writer.TryWrite(new ReceivedS7PlusPdu(null, errorCode));
        }

        private byte[] BuildLegacyAccumulatedDigestData(byte[] pdu, int bodyPos, int bodyLen, int previousBodyLen)
        {
            var digestData = new byte[previousBodyLen + bodyLen];
            var temp = m_ReceivedTempPDU.GetBuffer();
            Array.Copy(temp, 1, digestData, 0, previousBodyLen);
            Array.Copy(pdu, bodyPos, digestData, previousBodyLen, bodyLen);
            return digestData;
        }

        internal void DebugOnDataReceivedForTests(byte[] pdu)
        {
            OnDataReceived(pdu, pdu.Length);
        }

        internal int DebugReceiveNextS7plusPduForTests(int timeout, out MemoryStream pdu)
        {
            return ReceiveNextS7plusPdu(timeout, out pdu);
        }

        internal void DebugResetReceiveDispatcherForTests()
        {
            m_ReceivedPDUs.Writer.TryComplete();
            m_ReceivedPDUs = CreateReceiveChannel();
            m_ReceivedTempPDU = null;
            m_ReceivedPDU = null;
            m_ReceivedNeedMoreDataForCompletePDU = false;
            m_NewS7CommPlusReceived = false;
            m_LastSentRequestForWait = null;
            ClearNotificationQueues();
            m_LastError = 0;
        }

        internal void DebugEnableLegacyDigestForTests(byte[] sessionKey)
        {
            m_LegacySessionKey = sessionKey;
            m_LegacyDigestActive = true;
        }

        internal bool DebugGetMultiVariablesRequestExceedsLegacySingleFrameForTests(IEnumerable<ItemAddress> addresses)
        {
            var request = new GetMultiVariablesRequest(ProtocolVersion.V3);
            request.AddressList.AddRange(addresses);
            return ExceedsSingleFramePayload(request, GetMaxS7CommPlusPayloadSize(LegacyDigestFieldLength));
        }

        internal (int ItemCount, long SerializedLength) DebugCreateWriteRequestBatchForTests(
            IReadOnlyList<ItemAddress> addresses,
            IReadOnlyList<PValue> values,
            int maxItems,
            int maxPayloadSize)
        {
            var request = CreateWriteRequestBatch(addresses, values, 0, maxItems, maxPayloadSize, out int itemCount);
            return (itemCount, GetSerializedRequestLengthForBatching(request));
        }

        internal int DebugSendLegacyPayloadForTests(byte[] payload)
        {
            return SendS7plusPDUdata(payload, payload.Length, ProtocolVersion.V3);
        }

        internal (int Error, byte[] Packet) DebugBuildLegacyProtectedPduForTests(byte[] payload)
        {
            int error = BuildLegacyProtectedPdu(payload, payload.Length, ProtocolVersion.V3, out byte[] packet);
            return (error, packet);
        }

        private UInt16 GetWordAt(byte[] Buffer, int Pos)
        {
            return (UInt16)((Buffer[Pos] << 8) | Buffer[Pos + 1]);
        }

        private void SetWordAt(byte[] Buffer, int Pos, UInt16 Value)
        {
            Buffer[Pos] = (byte)(Value >> 8);
            Buffer[Pos + 1] = (byte)(Value & 0x00FF);
        }

        private void printBuf(byte[] b)
        {
            Trace.WriteLine(BitConverter.ToString(b));
        }

        private int checkResponseWithIntegrity(IS7pRequest request, IS7pResponse response)
        {
            if (response == null)
            {
                //System.Diagnostics.Trace.WriteLine("checkResponseWithIntegrity: ERROR! response == null");
                return S7Consts.errIsoInvalidPDU4;
            }
            if (request.SequenceNumber != response.SequenceNumber)
            {
                //System.Diagnostics.Trace.WriteLine(String.Format("checkResponseWithIntegrity: ERROR! SeqenceNumber of Response ({0}) doesn't match Request ({1})", response.SequenceNumber, request.SequenceNumber));
                return S7Consts.errIsoInvalidPDU5;
            }
            // Overflow is possible and allowed
            UInt32 reqIntegCheck = (UInt32)request.SequenceNumber + request.IntegrityId;
            if (response.IntegrityId != reqIntegCheck)
            {
                Trace.WriteLine(String.Format("checkResponseWithIntegrity: ERROR! IntegrityId of the Response ({0}) doesn't match Request ({1})", response.IntegrityId, reqIntegCheck));
                // Don't return this as error so far
            }
            return 0;
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Establishes a connection to the PLC.
        /// </summary>
        /// <param name="address">PLC IP address</param>
        /// <param name="password">PLC password (if set)</param>
        /// <param name="timeoutMs">read timeout in milliseconds (default: 5000 ms)</param>
        /// <returns></returns>
        public bool IsConnected
        {
            get { return m_client?.Connected == true; }
        }

        public int Connect(string address, string password = "", string username = "", int timeoutMs = 5000, int port = 102, ushort localTsap = 0x0600, byte[] remoteTsap = null)
        {
            return ConnectTls(address, password, username, timeoutMs, port, localTsap, remoteTsap);
        }

        internal int Connect(S7CommPlusClientOptions options)
        {
            if (options == null)
            {
                return S7Consts.errCliInvalidParams;
            }

            switch (options.SecurityMode)
            {
                case S7CommPlusSecurityMode.Tls:
                    return ConnectAndSetNegotiatedMode(options, S7CommPlusSecurityMode.Tls);
                case S7CommPlusSecurityMode.LegacyChallenge:
                    return ConnectAndSetNegotiatedMode(options, S7CommPlusSecurityMode.LegacyChallenge);
                case S7CommPlusSecurityMode.Auto:
                    var tlsResult = ConnectAndSetNegotiatedMode(options, S7CommPlusSecurityMode.Tls);
                    if (tlsResult == 0)
                    {
                        return 0;
                    }
                    options.Logger.LogInformation("TLS connect to PLC {Address}:{Port} failed with {ErrorCode}; trying legacy challenge authentication.", options.Address, options.Port, tlsResult);
                    TryDisconnect(options.DisconnectTimeoutMilliseconds);
                    return ConnectAndSetNegotiatedMode(options, S7CommPlusSecurityMode.LegacyChallenge);
                default:
                    return S7Consts.errCliInvalidParams;
            }
        }

        private int ConnectAndSetNegotiatedMode(S7CommPlusClientOptions options, S7CommPlusSecurityMode securityMode)
        {
            var result = securityMode == S7CommPlusSecurityMode.Tls
                ? ConnectTls(options.Address, options.Password, options.Username, options.ConnectTimeoutMilliseconds, options.Port, options.LocalTsap, options.RemoteTsapBytes, options.TlsBackend)
                : ConnectLegacyChallenge(options);
            if (result == 0)
            {
                ApplyRequestTimeout(options.RequestTimeoutMilliseconds);
                m_NegotiatedSecurityMode = securityMode;
                options.NegotiatedSecurityMode = securityMode;
            }
            return result;
        }

        /// <summary>
        /// Switches the connected protocol and transport from connection-phase deadlines to the configured request deadline.
        /// </summary>
        /// <param name="timeoutMilliseconds">The request timeout used for complete S7CommPlus responses and socket I/O.</param>
        private void ApplyRequestTimeout(int timeoutMilliseconds)
        {
            m_ReadTimeout = timeoutMilliseconds;
            m_client?.SetTransportTimeouts(timeoutMilliseconds, timeoutMilliseconds);
        }

        /// <summary>
        /// Exercises the post-handshake timeout transition without opening a network connection.
        /// </summary>
        /// <param name="connectTimeoutMilliseconds">The initial timeout installed while preparing the transport.</param>
        /// <param name="requestTimeoutMilliseconds">The timeout that must replace it after the handshake.</param>
        /// <returns>The effective protocol, receive, and send timeouts after the transition.</returns>
        internal (int ProtocolReadTimeout, int TransportReceiveTimeout, int TransportSendTimeout) DebugApplyRequestTimeoutForTests(
            int connectTimeoutMilliseconds,
            int requestTimeoutMilliseconds)
        {
            PrepareClient("127.0.0.1", connectTimeoutMilliseconds, 102, 0x0600, null);
            ApplyRequestTimeout(requestTimeoutMilliseconds);
            return (m_ReadTimeout, m_client.RecvTimeout, m_client.SendTimeout);
        }

        private void PrepareClient(string address, int timeoutMs, int port, ushort localTsap, byte[] remoteTsap)
        {
            StopLegacySessionKeyRefresh();
            m_LastError = 0;
            m_LastErrorDetail = string.Empty;
            m_LegacyDigestActive = false;
            m_LegacySessionKey = null;
            m_ServerSessionVersion = null;
            m_ReceivedPDU = null;
            m_ReceivedTempPDU = null;
            m_ReceivedNeedMoreDataForCompletePDU = false;
            m_NewS7CommPlusReceived = false;
            m_LastSentRequestForWait = null;
            ClearNotificationQueues();
            m_SessionId = 0;
            m_SessionId2 = 0;
            m_SequenceNumber = 0;
            m_IntegrityId = 0;
            m_IntegrityId_Set = 0;
            dbInfoList = null;
            typeInfoList.Clear();
            m_ReceivedPDUs.Writer.TryComplete();
            m_ReceivedPDUs = CreateReceiveChannel();
            m_client = new S7Client();
            m_client.OnDataReceived = this.OnDataReceived;
            m_client.OnReceiveError = this.PublishReceiveError;
            m_client.PLCPort = port;
            m_client.ConnTimeout = timeoutMs;
            m_client.RecvTimeout = timeoutMs;
            m_client.SendTimeout = timeoutMs;
            m_client.SetConnectionParams(address, localTsap, remoteTsap ?? Encoding.ASCII.GetBytes("SIMATIC-ROOT-HMI"));
        }

        private int ConnectTls(string address, string password = "", string username = "", int timeoutMs = 5000, int port = 102, ushort localTsap = 0x0600, byte[] remoteTsap = null, S7CommPlusTlsBackend tlsBackend = S7CommPlusTlsBackend.OpenSsl)
        {
            if (timeoutMs > 0) {
                m_ReadTimeout = timeoutMs;
            }

            int res;
            int Elapsed = Environment.TickCount;
            PrepareClient(address, timeoutMs, port, localTsap, remoteTsap);
            res = m_client.Connect();
            if (res != 0)
                return res;

            #region Step 1: Unencrypted InitSSL Request / Response

            InitSslRequest sslReq = new InitSslRequest(ProtocolVersion.V1, 0 , 0);
            res = SendS7plusFunctionObjectAndWait(sslReq, m_ReadTimeout);
            if (res != 0)
            {
                m_client.Disconnect();
                return res;
            }
            InitSslResponse sslRes;
            sslRes = InitSslResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (sslRes == null)
            {
                m_client.Disconnect();
                return S7Consts.errInitSslResponse;
            }

            #endregion

            #region Step 2: Activate TLS. Everything from here onwards is TLS encrypted.

            res = m_client.SslActivate(tlsBackend);
            if (res != 0)
            {
                m_LastErrorDetail = m_client.LastErrorDetail;
                m_client.Disconnect();
                return res;
            }

            #endregion

            #region Step 3: CreateObjectRequest / Response (with TLS)

            var createObjReq = new CreateObjectRequest(ProtocolVersion.V1, 0, false);
            createObjReq.SetNullServerSessionData();
            res = SendS7plusFunctionObjectAndWait(createObjReq, m_ReadTimeout);
            if (res != 0)
            {
                m_LastErrorDetail = m_client.LastErrorDetail;
                m_client.Disconnect();
                return res;
            }

            var createObjRes = CreateObjectResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (createObjRes == null)
            {
                //System.Diagnostics.Trace.WriteLine("S7CommPlusProtocolSession - Connect: CreateObjectResponse with Error!");
                m_client.Disconnect();
                return S7Consts.errIsoInvalidPDU6;
            }
            // There are (always?) at least two IDs in the response.
            // Usually the first is used for polling data, and the 2nd for jobs which use notifications, e.g. alarming, subscriptions.
            m_SessionId = createObjRes.ObjectIds[0];
            m_SessionId2 = createObjRes.ObjectIds[1];
            //System.Diagnostics.Trace.WriteLine("S7CommPlusProtocolSession - Connect: Using SessionId=0x" + String.Format("{0:X04}", m_SessionId));

            // Evaluate Struct 314
            PValue sval = createObjRes.ResponseObject.GetAttribute(Ids.ServerSessionVersion);
            ValueStruct serverSession = (ValueStruct)sval;
            m_ServerSessionVersion = serverSession;

            #endregion

            #region Step 4: SetMultiVariablesRequest / Response

            var setMultiVarReq = new SetMultiVariablesRequest(ProtocolVersion.V2);
            setMultiVarReq.SetSessionSetupData(m_SessionId, serverSession);
            res = SendS7plusFunctionObjectAndWait(setMultiVarReq, m_ReadTimeout);
            if (res != 0)
            {
                m_client.Disconnect();
                return res;
            }

            var setMultiVarRes = SetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (setMultiVarRes == null)
            {
                //System.Diagnostics.Trace.WriteLine("S7CommPlusProtocolSession - Connect: SetMultiVariablesResponse with Error!");
                m_client.Disconnect();
                return S7Consts.errIsoInvalidPDU7;
            }

            #endregion

            #region Step 5: Read SystemLimits
            res = m_CommunicationResources.ReadMax(this);
            if (res != 0)
            {
                m_client.Disconnect();
                return res;
            }
            #endregion

            #region Step 6: Password
            res = legitimate(serverSession, password, username);
            if (res != 0) {
                m_client.Disconnect();
                return res;
            }
            #endregion

            // If everything has been error-free up to this point, then the connection has been established successfully.
            Trace.WriteLine("S7CommPlusProtocolSession - Connect: Time for connection establishment: " + (Environment.TickCount - Elapsed) + " ms.");
            return 0;
        }

        public int Legitimate(string password, string username = "")
        {
            if (!IsConnected)
            {
                return S7Consts.errTCPNotConnected;
            }

            if (m_ServerSessionVersion == null)
            {
                return S7Consts.errCliFirmwareNotSupported;
            }

            return legitimate(m_ServerSessionVersion, password ?? string.Empty, username ?? string.Empty);
        }

        public int GetCommunicationResources(out S7CommPlusCommunicationResourceSnapshot resources)
        {
            resources = m_CommunicationResources ?? new S7CommPlusCommunicationResourceSnapshot();
            if (!IsConnected)
            {
                return S7Consts.errTCPNotConnected;
            }

            var result = resources.ReadMax(this);
            if (result != 0)
            {
                return result;
            }

            return resources.ReadFree(this);
        }

        public void Disconnect()
        {
            TryDisconnect(m_ReadTimeout);
        }

        public int TryDisconnect(int timeoutMs = 2000)
        {
            StopLegacySessionKeyRefresh();
            int res = 0;
            var oldTimeout = m_ReadTimeout;
            if (timeoutMs > 0)
            {
                m_ReadTimeout = timeoutMs;
            }

            try
            {
                if (m_client?.Connected == true && m_SessionId != 0)
                {
                    res = DeleteObject(m_SessionId);
                }
            }
            catch
            {
                res = S7Consts.errTCPDataSend;
            }
            finally
            {
                try
                {
                    var disconnectResult = m_client?.Disconnect(timeoutMs) ?? 0;
                    if (res == 0)
                    {
                        res = disconnectResult;
                    }
                }
                catch
                {
                    if (res == 0)
                    {
                        res = S7Consts.errTCPDataReceive;
                    }
                }

                m_ReadTimeout = oldTimeout;
                m_ReceivedPDU = null;
                m_ReceivedTempPDU = null;
                m_ReceivedPDUs.Writer.TryComplete();
                m_ReceivedPDUs = CreateReceiveChannel();
                m_ReceivedNeedMoreDataForCompletePDU = false;
                m_NewS7CommPlusReceived = false;
                m_LastSentRequestForWait = null;
                ClearNotificationQueues();
                m_SessionId = 0;
                m_SessionId2 = 0;
                m_SequenceNumber = 0;
                m_IntegrityId = 0;
                m_IntegrityId_Set = 0;
                m_LegacyDigestActive = false;
                m_LegacySessionKey = null;
                dbInfoList = null;
                typeInfoList.Clear();
            }

            return res;
        }

        internal int CloseTransport(int timeoutMs = 2000)
        {
            StopLegacySessionKeyRefresh();
            var oldTimeout = m_ReadTimeout;
            if (timeoutMs > 0)
            {
                m_ReadTimeout = timeoutMs;
            }

            try
            {
                return m_client?.Disconnect(timeoutMs) ?? 0;
            }
            catch
            {
                return S7Consts.errTCPDataReceive;
            }
            finally
            {
                m_ReadTimeout = oldTimeout;
                m_ReceivedPDU = null;
                m_ReceivedTempPDU = null;
                m_ReceivedPDUs.Writer.TryComplete();
                m_ReceivedPDUs = CreateReceiveChannel();
                m_ReceivedNeedMoreDataForCompletePDU = false;
                m_NewS7CommPlusReceived = false;
                m_LastSentRequestForWait = null;
                ClearNotificationQueues();
                m_SessionId = 0;
                m_SessionId2 = 0;
                m_SequenceNumber = 0;
                m_IntegrityId = 0;
                m_IntegrityId_Set = 0;
                m_LegacyDigestActive = false;
                m_LegacySessionKey = null;
                dbInfoList = null;
                typeInfoList.Clear();
            }
        }

        /// <summary>
        /// Deletes the object with the given Id.
        /// </summary>
        /// <param name="deleteObjectId">The object Id to delete</param>
        /// <returns>0 on success</returns>
        private int DeleteObject(uint deleteObjectId)
        {
            int res;
            m_LastErrorDetail = string.Empty;
            var delObjReq = new DeleteObjectRequest(ProtocolVersion.V2);
            delObjReq.DeleteObjectId = deleteObjectId;
            res = SendS7plusFunctionObjectAndWait(delObjReq, m_ReadTimeout);
            if (res != 0)
            {
                return res;
            }
            // If we delete our own session id, then there's no IntegrityId in the response.
            // And the error code gives an error, but not a fatal one.
            // If we delete another object, there should be an IntegrityId in the response, and
            // the response gives no error.
            if (deleteObjectId == m_SessionId)
            {
                var delObjRes = DeleteObjectResponse.DeserializeFromPdu(m_ReceivedPDU, false);
                Trace.WriteLine("S7CommPlusProtocolSession - DeleteSession: Deleted our own Session Id object, not checking the response.");
                m_SessionId = 0; // not valid anymore
                m_SessionId2 = 0;
            }
            else
            {
                var delObjRes = DeleteObjectResponse.DeserializeFromPdu(m_ReceivedPDU, true);
                res = checkResponseWithIntegrity(delObjReq, delObjRes);
                if (res != 0)
                {
                    return res;
                }
                if (delObjRes.ReturnValue != 0)
                {
                    Trace.WriteLine("S7CommPlusProtocolSession - DeleteSession: Executed with Error! ReturnValue=" + delObjRes.ReturnValue);
                    m_LastErrorDetail = String.Format(
                        "DeleteObject for object {0} was rejected with return value 0x{1:X16}.{2}",
                        deleteObjectId,
                        delObjRes.ReturnValue,
                        delObjRes.ErrorObject == null ? String.Empty : " " + delObjRes.ErrorObject.ToString());
                    res = -1;
                }
            }
            return res;
        }

        public int ReadValues(List<ItemAddress> addresslist, out List<object> values, out List<UInt64> errors)
        {
            // The requester must pass the internal type with the request, otherwise not all return values can be converted automatically.
            // For example, strings are transmitted as UInt-Array.
            values = new List<object>();
            errors = new List<UInt64>();
            // Initialize error fields to error value
            for (int i = 0; i < addresslist.Count; i++)
            {
                values.Add(null);
                errors.Add(0xffffffffffffffff);
            }
            if (addresslist.Count == 0)
            {
                return 0;
            }

            // Split requests by both the PLC's item limit and the negotiated TPDU payload.
            int chunk_startIndex = 0;
            int count_perChunk = 0;
            int maxTagsPerRequest = Math.Max(1, m_CommunicationResources.TagsPerReadRequestMax);
            int maxPayloadSize = GetSingleFramePayloadLimit(ProtocolVersion.V2);
            do
            {
                int res;
                var getMultiVarReq = new GetMultiVariablesRequest(ProtocolVersion.V2);

                getMultiVarReq.AddressList.Clear();
                count_perChunk = 0;
                while (count_perChunk < maxTagsPerRequest && (chunk_startIndex + count_perChunk) < addresslist.Count)
                {
                    getMultiVarReq.AddressList.Add(addresslist[chunk_startIndex + count_perChunk]);
                    if (count_perChunk > 0 && ExceedsSingleFramePayload(getMultiVarReq, maxPayloadSize))
                    {
                        getMultiVarReq.AddressList.RemoveAt(getMultiVarReq.AddressList.Count - 1);
                        break;
                    }
                    count_perChunk++;
                }

                res = SendS7plusFunctionObjectAndWait(getMultiVarReq, m_ReadTimeout);
                if (res != 0)
                {
                    return res;
                }

                var getMultiVarRes = GetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
                res = checkResponseWithIntegrity(getMultiVarReq, getMultiVarRes);
                if (res != 0)
                {
                    return res;
                }
                // ReturnValue shows also an error, if only one single variable could not be read
                if (getMultiVarRes.ReturnValue != 0)
                {
                    Trace.WriteLine("S7CommPlusProtocolSession - ReadValues: Executed with Error! ReturnValue=" + getMultiVarRes.ReturnValue);
                }

                // If a variable could not be read, there is no value, but there is an ErrorValue.
                // The production client maps this to per-item batch status.
                foreach (var v in getMultiVarRes.Values)
                {
                    values[chunk_startIndex + (int)v.Key - 1] = v.Value;
                    // Initialize error to 0, will be overwritten below if there was an error on an item.
                    errors[chunk_startIndex + (int)v.Key - 1] = 0;
                }

                foreach (var ev in getMultiVarRes.ErrorValues)
                {
                    errors[chunk_startIndex + (int)ev.Key - 1] = ev.Value;
                }
                chunk_startIndex += count_perChunk;

            } while (chunk_startIndex < addresslist.Count);

            return m_LastError;
        }

        public int WriteValues(List<ItemAddress> addresslist, List<PValue> values, out List<UInt64> errors)
        {
            int res;
            errors = new List<UInt64>();
            if (addresslist.Count != values.Count)
            {
                return S7Consts.errCliInvalidParams;
            }
            for (int i = 0; i < addresslist.Count; i++)
            {
                // Initialize to no error value, as there's no explicit value for write success.
                errors.Add(0);
            }
            if (addresslist.Count == 0)
            {
                return 0;
            }

            // Split requests by both the PLC's item limit and the negotiated TPDU payload.
            int chunk_startIndex = 0;
            int count_perChunk = 0;
            int maxTagsPerRequest = Math.Max(1, m_CommunicationResources.TagsPerWriteRequestMax);
            int maxPayloadSize = GetSingleFramePayloadLimit(ProtocolVersion.V2);
            do
            {
                var setMultiVarReq = CreateWriteRequestBatch(
                    addresslist,
                    values,
                    chunk_startIndex,
                    maxTagsPerRequest,
                    maxPayloadSize,
                    out count_perChunk);

                res = SendS7plusFunctionObjectAndWait(setMultiVarReq, m_ReadTimeout);
                if (res != 0)
                {
                    return res;
                }

                var setMultiVarRes = SetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
                res = checkResponseWithIntegrity(setMultiVarReq, setMultiVarRes);
                if (res != 0)
                {
                    return res;
                }
                // ReturnValue shows also an error, if only one single variable could not be written
                if (setMultiVarRes.ReturnValue != 0)
                {
                    Trace.WriteLine("S7CommPlusProtocolSession - WriteValues: Write with errors. ReturnValue=" + setMultiVarRes.ReturnValue);
                }

                foreach (var ev in setMultiVarRes.ErrorValues)
                {
                    errors[chunk_startIndex + (int)ev.Key - 1] = ev.Value;
                }
                chunk_startIndex += count_perChunk;

            } while (chunk_startIndex < addresslist.Count);

            return m_LastError;
        }

        private static SetMultiVariablesRequest CreateWriteRequestBatch(
            IReadOnlyList<ItemAddress> addresses,
            IReadOnlyList<PValue> values,
            int startIndex,
            int maxItems,
            int maxPayloadSize,
            out int itemCount)
        {
            var request = new SetMultiVariablesRequest(ProtocolVersion.V2);
            itemCount = 0;
            while (itemCount < maxItems && startIndex + itemCount < addresses.Count)
            {
                request.AddressListVar.Add(addresses[startIndex + itemCount]);
                request.ValueList.Add(values[startIndex + itemCount]);
                if (itemCount > 0 && ExceedsSingleFramePayload(request, maxPayloadSize))
                {
                    request.AddressListVar.RemoveAt(request.AddressListVar.Count - 1);
                    request.ValueList.RemoveAt(request.ValueList.Count - 1);
                    break;
                }
                itemCount++;
            }

            return request;
        }

        public int SetPlcOperatingState(Int32 state)
        {
            int res;
            var setVarReq = new SetVariableRequest(ProtocolVersion.V2);
            setVarReq.InObjectId = Ids.NativeObjects_theCPUexecUnit_Rid;
            setVarReq.Address = Ids.CPUexecUnit_operatingStateReq;
            setVarReq.Value = new ValueDInt(state);

            res = SendS7plusFunctionObjectAndWait(setVarReq, m_ReadTimeout);
            if (res != 0)
            {
                m_client.Disconnect();
                return res;
            }

            var setVarRes = SetVariableResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (setVarRes == null)
            {
                //System.Diagnostics.Trace.WriteLine("S7CommPlusProtocolSession - Connect: SetVariableResponse with Error!");
                m_client.Disconnect();
                return S7Consts.errIsoInvalidPDU12;
            }

            res = checkResponseWithIntegrity(setVarReq, setVarRes);
            if (res != 0)
            {
                return res;
            }

            if (setVarRes.ReturnValue != 0)
            {
                return S7Consts.errCliInvalidParams;
            }

            return 0;
        }

        /// <summary>
        /// Browses variables using the legacy element-expanded primitive-array representation.
        /// </summary>
        /// <param name="varInfoList">Receives the flattened variables when the browse succeeds.</param>
        /// <returns>A native driver error code, or zero on success.</returns>
        public int Browse(out List<VarInfo> varInfoList)
        {
            return Browse(true, out varInfoList);
        }

        /// <summary>
        /// Browses PLC type information and chooses whether primitive array elements are materialized individually.
        /// </summary>
        /// <param name="expandPrimitiveArrayElements">
        /// <see langword="true"/> for the legacy indexed-element list; <see langword="false"/> for aggregate array items.
        /// </param>
        /// <param name="varInfoList">Receives the browsable variables when the operation succeeds.</param>
        /// <returns>A native driver error code, or zero on success.</returns>
        internal int Browse(bool expandPrimitiveArrayElements, out List<VarInfo> varInfoList)
        {
            int res;
            varInfoList = new List<VarInfo>();
            Browser vars = new Browser(expandPrimitiveArrayElements);
            ExploreRequest exploreReq;
            ExploreResponse exploreRes;

            #region Read all objects

            var exploreData = new List<BrowseData>();

            exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = Ids.NativeObjects_thePLCProgram_Rid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            // We want to know the following attributes
            exploreReq.AddressList.Add(Ids.ObjectVariableTypeName);
            exploreReq.AddressList.Add(Ids.Block_BlockNumber);
            exploreReq.AddressList.Add(Ids.ASObjectES_Comment);

            res = SendS7plusFunctionObjectAndWait(exploreReq, m_ReadTimeout);
            if (res != 0)
            {
                return res;
            }

            exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            #endregion

            #region Evaluate all data blocks that then need to be browsed

            var obj = exploreRes.Objects.First(o => o.ClassId == Ids.PLCProgram_Class_Rid);

            foreach (var ob in obj.GetObjects())
            {
                switch (ob.ClassId)
                {
                    case Ids.DB_Class_Rid:
                        UInt32 relid = ob.RelationId;
                        UInt32 area = (relid >> 16);
                        UInt32 num = relid & 0xffff;
                        if (area == 0x8a0e)
                        {
                            var name = (ValueWString)(ob.GetAttribute(Ids.ObjectVariableTypeName));
                            BrowseData data = new BrowseData();
                            data.db_block_relid = relid;
                            data.db_name = name.GetValue();
                            data.db_number = num;
                            exploreData.Add(data);
                        }
                        break;
                }
            }

            #endregion

            #region Determine the TypeInfo RID to the RelId from the first response
            // By querying LID = 1 from all DBs you get the RID back with which the type information can be queried.
            // This is neccessary because, for example, with instance DBs (e.g. TON), the type information must
            // not be accessed via the RID of the DB, but of the RID of the TON.
            var readlist = new List<ItemAddress>();
            var values = new List<object>();
            var errors = new List<UInt64>();

            foreach (var data in exploreData)
            {
                if (data.db_number > 0) // only process datablocks here, no marker, timer etc.
                {
                    // Insert the variable address
                    var adr1 = new ItemAddress();
                    adr1.AccessArea = data.db_block_relid;
                    adr1.AccessSubArea = Ids.DB_ValueActual;
                    adr1.AddLocalId(1);
                    readlist.Add(adr1);
                }
            }
            res = ReadValues(readlist, out values, out errors);
            if (res != 0)
            {
                return res;
            }
            #endregion

            #region Pass the preliminary information for recombination to ExploreSymbols

            // Add the response information to the list
            for (int i = 0; i < values.Count; i++)
            {
                if (errors[i] == 0)
                {
                    ValueRID rid = (ValueRID)values[i];
                    var data = exploreData[i];
                    data.db_block_ti_relid = rid.GetValue();
                    exploreData[i] = data;
                }
                else
                {
                    // On error, set the relid to zero, will be removed from the list in the next step.
                    Trace.WriteLine(String.Format("Explore: Skipping block type info relation for {0}, item error 0x{1:X}.", exploreData[i].db_name, errors[i]));
                    var data = exploreData[i];
                    data.db_block_ti_relid = 0;
                    exploreData[i] = data;
                }
            }
            // Remove elements with db_block_ti_relid == 0. This occurs e.g. on datablocks only present in load memory.
            // The informations can't be used any further (at least not for variable access).
            exploreData.RemoveAll(item => item.db_block_ti_relid == 0);

            foreach (var ed in exploreData)
            {
                vars.AddBlockNode(eNodeType.Root, ed.db_name, ed.db_block_relid, ed.db_block_ti_relid);
            }

            // Add IQMCT areas manually
            vars.AddBlockNode(eNodeType.Root, "IArea", Ids.NativeObjects_theIArea_Rid, 0x90010000);
            vars.AddBlockNode(eNodeType.Root, "QArea", Ids.NativeObjects_theQArea_Rid, 0x90020000);
            vars.AddBlockNode(eNodeType.Root, "MArea", Ids.NativeObjects_theMArea_Rid, 0x90030000);
            vars.AddBlockNode(eNodeType.Root, "S7Timers", Ids.NativeObjects_theS7Timers_Rid, 0x90050000);
            vars.AddBlockNode(eNodeType.Root, "S7Counters", Ids.NativeObjects_theS7Counters_Rid, 0x90060000);

            #endregion

            #region Read the Type Info Container (as a single big PDU, must be proven to be the way to go in big programs)
            exploreReq = new ExploreRequest(ProtocolVersion.V2);
            // With ObjectOMSTypeInfoContainer we get all in a big PDU (with maybe hundreds of fragments)
            exploreReq.ExploreId = Ids.ObjectOMSTypeInfoContainer;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            res = SendS7plusFunctionObjectAndWait(exploreReq, m_ReadTimeout);
            if (res != 0)
            {
                return res;
            }
            #endregion

            #region Process the response, and build the complete variables list
            exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }
            var objs = exploreRes.Objects.First(o => o.ClassId == Ids.ClassOMSTypeInfoContainer);

            vars.SetTypeInfoContainerObjects(objs.GetObjects());
            vars.BuildTree();
            vars.BuildFlatList();
            varInfoList = vars.GetVarInfoList();
            #endregion

            return 0;
        }

        /// <summary>
        /// Gets the first level of a tag symbol string. Removes the " used to escape special chars.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <returns>The first level of the symbol string</returns>
        /// <exception cref="Exception">Symbol syntax error</exception>
        private string parseSymbolLevel(ref string symbol)
        {
            if (symbol.StartsWith("\""))
            {
                int idx = symbol.IndexOf('"', 1);
                if (idx < 0) throw new Exception("Symbol syntax error");
                string lvl = symbol.Substring(1, idx - 1);
                symbol = symbol.Remove(0, idx + 1);
                if (symbol.StartsWith(".")) symbol = symbol.Remove(0, 1);
                return lvl;
            }
            else
            {
                int idx = symbol.IndexOf('.');
                int idx2 = symbol.IndexOf('[', 1);
                if (idx2 >= 0 && (idx2 < idx || idx < 0)) idx = idx2;
                if (idx >= 0)
                {
                    string lvl = symbol.Substring(0, idx);
                    symbol = symbol.Remove(0, idx);
                    if (symbol.StartsWith(".")) symbol = symbol.Remove(0, 1);
                    return lvl;
                }
                else
                {
                    string lvl = symbol;
                    symbol = "";
                    return lvl;
                }
            }
        }

        /// <summary>
        /// Gets the typeinfo by given ti relid from the internal buffer. If it's not found in the buffer
        /// it's fetched from the PLC and stored in the buffer.
        /// </summary>
        /// <param name="ti_relid">type info relid</param>
        /// <returns>type info</returns>
        /// <exception cref="Exception">Could not get type info</exception>
        public PObject getTypeInfoByRelId(uint ti_relid)
        {
            PObject pObj = typeInfoList.Find(ti => ti.RelationId == ti_relid);
            if (pObj == null)
            {
                // Type info not found in list, request it from plc
                List<PObject> newPObj = new List<PObject>();
                if (GetTypeInformation(ti_relid, out newPObj) != 0) throw new Exception("Could not get type info");
                typeInfoList.AddRange(newPObj);
                // Try again
                pObj = typeInfoList.Find(ti => ti.RelationId == ti_relid);
            }
            return pObj;
        }

        /// <summary>
        /// Calculates the access sequence for 1 dimensional arrays.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <param name="varType">Var type that holds the dim info</param>
        /// <param name="varInfo">used to build access sequence</param>
        /// <exception cref="Exception">Symbol syntax error</exception>
        private void calcAccessSeqFor1DimArray(ref string symbol, PVartypeListElement varType, VarInfo varInfo)
        {
            Regex re = new Regex(@"^\[(-?\d+)\]");
            Match m = re.Match(symbol);
            if (!m.Success) throw new ArgumentException("Expected a one-dimensional array index such as '[1]'.", nameof(symbol));
            parseSymbolLevel(ref symbol); // remove index from symbol string
            if (!int.TryParse(m.Groups[1].Value, out var arrayIndex))
            {
                throw new ArgumentException("The array index is outside the supported integer range.", nameof(symbol));
            }

            var ioit = (IOffsetInfoType_1Dim)varType.OffsetInfoType;
            uint arrayElementCount = ioit.GetArrayElementCount();
            int arrayLowerBounds = ioit.GetArrayLowerBounds();

            var normalizedIndex = (long)arrayIndex - arrayLowerBounds;
            if (normalizedIndex < 0 || normalizedIndex >= arrayElementCount)
            {
                throw new ArgumentOutOfRangeException(nameof(symbol), arrayIndex, "The array index is outside the PLC declaration bounds.");
            }
            varInfo.AccessSequence += "." + String.Format("{0:X}", arrayIndex - arrayLowerBounds);
            varInfo.ContainsIndexedArray = true;
            if (varType.OffsetInfoType.HasRelation()) varInfo.AccessSequence += ".1"; // additional ".1" for array of struct
        }

        /// <summary>
        /// Calculates the access sequence for multi-dimensional arrays.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <param name="varType">Var type that holds the dim info</param>
        /// <param name="varInfo">used to build access sequence</param>
        /// <exception cref="Exception">Symbol syntax error</exception>
        private void calcAccessSeqForMDimArray(ref string symbol, PVartypeListElement varType, VarInfo varInfo)
        {
            Regex re = new Regex(@"^\[( ?-?\d+ ?(, ?-?\d+ ?)+)\]");
            Match m = re.Match(symbol);
            if (!m.Success) throw new ArgumentException("Expected a multidimensional array index such as '[1,2]'.", nameof(symbol));
            parseSymbolLevel(ref symbol); // remove index from symbol string
            string idxs = m.Groups[1].Value.Replace(" ", "");

            var indexTexts = idxs.Split(',');
            var indexes = new int[indexTexts.Length];
            for (var index = 0; index < indexTexts.Length; index++)
            {
                if (!int.TryParse(indexTexts[index], out indexes[index]))
                {
                    throw new ArgumentException("An array index is outside the supported integer range.", nameof(symbol));
                }
            }
            var ioit = (IOffsetInfoType_MDim)varType.OffsetInfoType;
            uint[] MdimArrayElementCount = (uint[])ioit.GetMdimArrayElementCount().Clone();
            int[] MdimArrayLowerBounds = ioit.GetMdimArrayLowerBounds();

            // check dim count
            int dimCount = MdimArrayElementCount.Aggregate(0, (acc, act) => acc += (act > 0) ? 1 : 0);
            if (dimCount != indexes.Length)
            {
                throw new ArgumentException($"The PLC array has {dimCount} dimensions, but {indexes.Length} indices were supplied.", nameof(symbol));
            }
            // check bounds
            for (int i = 0; i < dimCount; ++i)
            {
                indexes[i] = (indexes[i] - MdimArrayLowerBounds[dimCount - i - 1]);
                if (indexes[i] < 0 || indexes[i] >= MdimArrayElementCount[dimCount - i - 1])
                {
                    throw new ArgumentOutOfRangeException(nameof(symbol), "An array index is outside the PLC declaration bounds.");
                }
            }

            // calc dim size
            if (varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL)
            {
                var remainder = MdimArrayElementCount[0] % 8;
                if (remainder != 0)
                {
                    MdimArrayElementCount[0] += 8 - remainder; // for bool must be a multiple of 8!
                }
            }
            uint[] dimSize = new uint[dimCount];
            uint g = 1;
            for (int i = 0; i < dimCount - 1; ++i)
            {
                dimSize[i] = g;
                g *= MdimArrayElementCount[i];
            }
            dimSize[dimCount - 1] = g;

            // calc id
            int arrayIndex = 0;
            for (int i = 0; i < dimCount; ++i)
            {
                arrayIndex += indexes[i] * (int)dimSize[dimCount - i - 1];
            }

            varInfo.AccessSequence += "." + String.Format("{0:X}", arrayIndex);
            varInfo.ContainsIndexedArray = true;
            if (varType.OffsetInfoType.HasRelation()) varInfo.AccessSequence += ".1"; // additional ".1" for array of struct
        }

        /// <summary>
        /// Browses the symbol level by level recursively. Fetches missing type info automatically from the plc.
        /// </summary>
        /// <param name="ti_relid">type info relid</param>
        /// <param name="symbol">plc tag symbol</param>
        /// <param name="varInfo">used to build access sequence</param>
        /// <returns>plc tag or null if not found</returns>
        /// <exception cref="Exception">Symbol syntax error, Out of bounds</exception>
        private PlcTag browsePlcTagBySymbol(uint ti_relid, ref string symbol, VarInfo varInfo)
        {
            PObject pObj = getTypeInfoByRelId(ti_relid);
            if (pObj == null) throw new Exception("Could not get type info");
            string levelName = parseSymbolLevel(ref symbol);
            // find level name of symbol in var list
            int idx = pObj.VarnameList?.Names?.IndexOf(levelName) ?? -1;
            if (idx < 0) return null;
            PVartypeListElement varType = pObj.VartypeList.Elements[idx];
            varInfo.AccessSequence += "." + String.Format("{0:X}", varType.LID);
            AddSymbolCrcSegment(varInfo, levelName, varType);
            AccumulateTraceAddressMetadata(varInfo, varType);
            var isAggregateArray = IsAggregatePrimitiveArray(varType, symbol);
            if (varType.OffsetInfoType.Is1Dim())
            {
                if (!isAggregateArray)
                {
                    calcAccessSeqFor1DimArray(ref symbol, varType, varInfo);
                }
            }
            if (varType.OffsetInfoType.IsMDim())
            {
                if (!isAggregateArray)
                {
                    calcAccessSeqForMDimArray(ref symbol, varType, varInfo);
                }
            }
            if (varType.OffsetInfoType.HasRelation())
            {
                if (symbol.Length <= 0 && varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DTL)
                {
                    return CreateResolvedPlcTag(varInfo, varType, isAggregateArray);
                }
                if (symbol.Length <= 0)
                {
                    return null;
                }
                else
                {
                    var ioit = (IOffsetInfoType_Relation)varType.OffsetInfoType;
                    return browsePlcTagBySymbol(ioit.GetRelationId(), ref symbol, varInfo);
                }
            }
            else
            {
                return CreateResolvedPlcTag(varInfo, varType, isAggregateArray);
            }
        }

        private static void AccumulateTraceAddressMetadata(VarInfo varInfo, PVartypeListElement varType)
        {
            if (varType?.OffsetInfoType == null)
                return;

            varInfo.OptAddress = checked(varInfo.OptAddress + varType.OffsetInfoType.OptimizedAddress);
            varInfo.NonOptAddress = checked(varInfo.NonOptAddress + varType.OffsetInfoType.NonoptimizedAddress);

            if (varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BOOL)
            {
                varInfo.OptBitoffset = varType.GetAttributeBitoffset();
                varInfo.NonOptBitoffset = varType.GetBitoffsetinfoFlagClassic()
                    ? varType.GetBitoffsetinfoNonoptimizedBitoffset()
                    : varType.GetAttributeBitoffset();
            }
            else if (varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL)
            {
                varInfo.OptBitoffset = varType.GetBitoffsetinfoOptimizedBitoffset();
            }
            else
            {
                varInfo.OptBitoffset = 0;
                varInfo.NonOptBitoffset = 0;
            }
        }

        /// <summary>
        /// Determines whether a complete primitive array was requested without an element index.
        /// Arrays of structures still require a member path and are rejected by the relation handling that follows.
        /// </summary>
        /// <param name="varType">The PLC member metadata containing scalar or array dimension information.</param>
        /// <param name="remainingSymbol">The unconsumed symbolic path after the current member name.</param>
        /// <returns><see langword="true"/> when the current member is an aggregate one- or multidimensional array request.</returns>
        internal static bool IsAggregatePrimitiveArray(PVartypeListElement varType, string remainingSymbol)
        {
            if (varType == null) throw new ArgumentNullException(nameof(varType));

            return string.IsNullOrEmpty(remainingSymbol)
                && (varType.OffsetInfoType.Is1Dim() || varType.OffsetInfoType.IsMDim());
        }

        /// <summary>
        /// Creates a resolved scalar or aggregate tag and expands aggregate arrays into PLC-readable element addresses.
        /// </summary>
        /// <param name="varInfo">The accumulated symbolic name, access sequence, and CRC path.</param>
        /// <param name="varType">The resolved member datatype and dimension metadata.</param>
        /// <param name="isAggregateArray">Whether the symbol requested the complete primitive array.</param>
        /// <returns>The concrete tag used by the public client API.</returns>
        private static PlcTag CreateResolvedPlcTag(VarInfo varInfo, PVartypeListElement varType, bool isAggregateArray)
        {
            var address = CreateItemAddress(varInfo);
            var tag = PlcTags.TagFactory(varInfo.Name, address, varType.Softdatatype, isAggregateArray);
            tag?.SetTraceAddressMetadata(varInfo);
            if (!isAggregateArray || tag == null)
            {
                return tag;
            }

            var elementTags = GetAggregateArrayElementAccessIds(varType)
                .Select(accessId =>
                {
                    var elementAddress = CreateAggregateArrayElementAddress(
                        varInfo.AccessSequence,
                        accessId,
                        varType.OffsetInfoType.HasRelation());
                    return PlcTags.TagFactory($"{varInfo.Name}[#{accessId}]", elementAddress, varType.Softdatatype);
                })
                .Where(elementTag => elementTag != null)
                .ToList();
            tag.SetAggregateElements(elementTags);
            return tag;
        }

        /// <summary>
        /// Creates a typed tag directly from one aggregate browse result, avoiding a second walk through the PLC type catalog.
        /// </summary>
        /// <param name="varInfo">Browse metadata containing the exact symbol, access sequence, CRC, datatype, and array bounds.</param>
        /// <returns>A ready-to-read scalar or aggregate tag, or <see langword="null"/> for an unsupported datatype.</returns>
        /// <remarks>
        /// Aggregate primitive arrays are expanded into their individually addressable wire elements because the PLC does not accept
        /// every complete array type at its declaration address. The parent tag still exposes the assembled typed array to callers.
        /// </remarks>
        internal static PlcTag CreateResolvedPlcTag(VarInfo varInfo)
        {
            if (varInfo == null) throw new ArgumentNullException(nameof(varInfo));
            if (string.IsNullOrWhiteSpace(varInfo.Name)) throw new ArgumentException("Browse metadata must contain a symbol name.", nameof(varInfo));
            if (string.IsNullOrWhiteSpace(varInfo.AccessSequence)) throw new ArgumentException("Browse metadata must contain an access sequence.", nameof(varInfo));

            var address = CreateItemAddress(varInfo);
            var isAggregateArray = varInfo.ArrayElementCount > 0;
            var tag = PlcTags.TagFactory(varInfo.Name, address, varInfo.Softdatatype, isAggregateArray);
            tag?.SetTraceAddressMetadata(varInfo);
            if (!isAggregateArray || tag == null)
            {
                return tag;
            }

            var elementTags = GetAggregateArrayElementAccessIds(varInfo)
                .Select(accessId => PlcTags.TagFactory(
                    $"{varInfo.Name}[#{accessId}]",
                    CreateAggregateArrayElementAddress(
                        varInfo.AccessSequence,
                        accessId,
                        RequiresArrayElementRelationSelector(varInfo.Softdatatype)),
                    varInfo.Softdatatype))
                .Where(elementTag => elementTag != null)
                .ToList();
            tag.SetAggregateElements(elementTags);
            return tag;
        }

        /// <summary>
        /// Enumerates the physical PLC access IDs represented by caller-facing browse dimensions.
        /// </summary>
        /// <param name="varInfo">Aggregate browse metadata whose dimensions are ordered as written in a symbolic PLC name.</param>
        /// <returns>Element access IDs in declaration order, excluding multidimensional packed-boolean alignment cells.</returns>
        private static IReadOnlyList<uint> GetAggregateArrayElementAccessIds(VarInfo varInfo)
        {
            if (varInfo.ArrayElementCount == 0)
            {
                return Array.Empty<uint>();
            }

            var dimensions = varInfo.ArrayDimensions ?? Array.Empty<S7CommPlusArrayDimension>();
            if (dimensions.Count <= 1)
            {
                return Enumerable.Range(0, checked((int)varInfo.ArrayElementCount))
                    .Select(index => (uint)index)
                    .ToArray();
            }

            // Browse dimensions are printed left-to-right, while the protocol calculates linear access IDs innermost-first.
            var logicalCounts = dimensions.Reverse().Select(dimension => dimension.ElementCount).ToArray();
            var physicalCounts = (uint[])logicalCounts.Clone();
            if (varInfo.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL)
            {
                var remainder = physicalCounts[0] % 8;
                if (remainder != 0)
                {
                    physicalCounts[0] += 8 - remainder;
                }
            }

            var strides = new uint[logicalCounts.Length];
            strides[0] = 1;
            for (var dimension = 1; dimension < strides.Length; dimension++)
            {
                strides[dimension] = checked(strides[dimension - 1] * physicalCounts[dimension - 1]);
            }

            List<uint> accessIds = new(checked((int)varInfo.ArrayElementCount));
            AddAggregateArrayAccessIds(logicalCounts, strides, logicalCounts.Length - 1, 0, accessIds);
            return accessIds;
        }

        /// <summary>
        /// Enumerates linear S7 access IDs for every declared array element in PLC storage order.
        /// </summary>
        /// <param name="varType">The primitive array member metadata.</param>
        /// <returns>Element access IDs, excluding alignment-only gaps used by packed multidimensional booleans.</returns>
        internal static IReadOnlyList<uint> GetAggregateArrayElementAccessIds(PVartypeListElement varType)
        {
            if (varType == null) throw new ArgumentNullException(nameof(varType));

            if (varType.OffsetInfoType is IOffsetInfoType_1Dim oneDimensional)
            {
                return Enumerable.Range(0, checked((int)oneDimensional.GetArrayElementCount()))
                    .Select(index => (uint)index)
                    .ToArray();
            }
            if (varType.OffsetInfoType is not IOffsetInfoType_MDim multiDimensional)
            {
                return Array.Empty<uint>();
            }

            var logicalCounts = multiDimensional.GetMdimArrayElementCount()
                .TakeWhile(count => count > 0)
                .ToArray();
            if (logicalCounts.Length == 0)
            {
                return Array.Empty<uint>();
            }

            var physicalCounts = (uint[])logicalCounts.Clone();
            if (varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL)
            {
                var remainder = physicalCounts[0] % 8;
                if (remainder != 0)
                {
                    physicalCounts[0] += 8 - remainder;
                }
            }

            var strides = new uint[logicalCounts.Length];
            strides[0] = 1;
            for (var dimension = 1; dimension < strides.Length; dimension++)
            {
                strides[dimension] = checked(strides[dimension - 1] * physicalCounts[dimension - 1]);
            }

            List<uint> accessIds = new(checked((int)multiDimensional.GetArrayElementCount()));
            AddAggregateArrayAccessIds(logicalCounts, strides, logicalCounts.Length - 1, 0, accessIds);
            return accessIds;
        }

        /// <summary>
        /// Creates an address for an element derived from an aggregate array access sequence.
        /// The aggregate symbol CRC is deliberately omitted because it describes the array declaration, not the synthetic
        /// element address, and S7-1500 PLCs reject that combination with an item-level protocol error.
        /// </summary>
        /// <param name="aggregateAccessSequence">Resolved low-level access sequence of the aggregate array.</param>
        /// <param name="accessId">Linear element access ID in PLC storage order.</param>
        /// <param name="requiresRelationSelector">Whether the packed system datatype requires the protocol's literal relation selector.</param>
        /// <returns>An element address with a zero symbol CRC.</returns>
        internal static ItemAddress CreateAggregateArrayElementAddress(
            string aggregateAccessSequence,
            uint accessId,
            bool requiresRelationSelector = false)
        {
            if (string.IsNullOrWhiteSpace(aggregateAccessSequence)) throw new ArgumentException("An aggregate access sequence is required.", nameof(aggregateAccessSequence));

            return new ItemAddress($"{aggregateAccessSequence}.{accessId:X}{(requiresRelationSelector ? ".1" : string.Empty)}");
        }

        private static bool RequiresArrayElementRelationSelector(uint softdatatype)
        {
            return softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DTL;
        }

        /// <summary>
        /// Creates one scalar array-element tag from retained aggregate browse metadata and caller-facing PLC indices.
        /// </summary>
        /// <param name="aggregateVariable">The aggregate primitive-array entry retained in the symbol catalog.</param>
        /// <param name="requestedSymbol">The exact indexed symbol to assign to the resulting tag.</param>
        /// <param name="indicesText">The comma-separated indices between the final square brackets.</param>
        /// <param name="tag">Receives the resolved scalar tag when every index is valid.</param>
        /// <returns><see langword="true"/> when the metadata and indices identify a declared array element.</returns>
        /// <remarks>
        /// Multidimensional Siemens arrays use row-major symbolic indices, while packed multidimensional boolean arrays reserve
        /// physical alignment cells in their innermost dimension. The calculated access ID preserves that wire layout.
        /// </remarks>
        internal static bool TryCreateResolvedPrimitiveArrayElement(
            VarInfo aggregateVariable,
            string requestedSymbol,
            string indicesText,
            out PlcTag tag)
        {
            tag = null;
            if (aggregateVariable == null
                || aggregateVariable.ArrayElementCount == 0
                || string.IsNullOrWhiteSpace(aggregateVariable.AccessSequence)
                || string.IsNullOrWhiteSpace(requestedSymbol))
            {
                return false;
            }

            var dimensions = aggregateVariable.ArrayDimensions ?? Array.Empty<S7CommPlusArrayDimension>();
            var indexParts = indicesText.Split(',');
            if (dimensions.Count == 0 || indexParts.Length != dimensions.Count)
            {
                return false;
            }

            var normalizedIndices = new uint[dimensions.Count];
            for (var dimensionIndex = 0; dimensionIndex < dimensions.Count; dimensionIndex++)
            {
                if (!int.TryParse(indexParts[dimensionIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    return false;
                }

                var dimension = dimensions[dimensionIndex];
                var normalizedIndex = (long)index - dimension.LowerBound;
                if (normalizedIndex < 0 || normalizedIndex >= dimension.ElementCount)
                {
                    return false;
                }
                normalizedIndices[dimensionIndex] = (uint)normalizedIndex;
            }

            uint accessId = 0;
            uint stride = 1;
            for (var dimensionIndex = dimensions.Count - 1; dimensionIndex >= 0; dimensionIndex--)
            {
                accessId = checked(accessId + normalizedIndices[dimensionIndex] * stride);
                var physicalElementCount = dimensions[dimensionIndex].ElementCount;
                if (dimensionIndex == dimensions.Count - 1
                    && dimensions.Count > 1
                    && aggregateVariable.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL)
                {
                    var remainder = physicalElementCount % 8;
                    if (remainder != 0)
                    {
                        physicalElementCount += 8 - remainder;
                    }
                }
                stride = checked(stride * physicalElementCount);
            }

            tag = PlcTags.TagFactory(
                requestedSymbol,
                CreateAggregateArrayElementAddress(
                    aggregateVariable.AccessSequence,
                    accessId,
                    RequiresArrayElementRelationSelector(aggregateVariable.Softdatatype)),
                aggregateVariable.Softdatatype);
            return tag != null;
        }

        /// <summary>
        /// Recursively emits row-major array access IDs while preserving physical strides and omitting padding cells.
        /// </summary>
        /// <param name="logicalCounts">Declared element counts in the protocol's innermost-first order.</param>
        /// <param name="strides">Physical access-ID stride for each dimension.</param>
        /// <param name="dimension">The dimension currently being enumerated.</param>
        /// <param name="baseAccessId">The access ID accumulated by outer dimensions.</param>
        /// <param name="accessIds">Destination list in PLC declaration order.</param>
        private static void AddAggregateArrayAccessIds(
            IReadOnlyList<uint> logicalCounts,
            IReadOnlyList<uint> strides,
            int dimension,
            uint baseAccessId,
            ICollection<uint> accessIds)
        {
            for (uint index = 0; index < logicalCounts[dimension]; index++)
            {
                var accessId = checked(baseAccessId + index * strides[dimension]);
                if (dimension == 0)
                {
                    accessIds.Add(accessId);
                }
                else
                {
                    AddAggregateArrayAccessIds(logicalCounts, strides, dimension - 1, accessId, accessIds);
                }
            }
        }

        private static void AddSymbolCrcSegment(VarInfo varInfo, string levelName, PVartypeListElement varType)
        {
            if (varInfo.SymbolCrcPath == null)
            {
                varInfo.SymbolCrcPath = new List<S7CommPlusSymbolCrc.PathSegment>();
            }

            if (varType.OffsetInfoType.Is1Dim() || varType.OffsetInfoType.IsMDim())
            {
                varInfo.SymbolCrcPath.Add(S7CommPlusSymbolCrc.PathSegment.Array(
                    levelName,
                    varType.Softdatatype,
                    Browser.GetArrayLowerBound(varType.OffsetInfoType)));
            }
            else
            {
                varInfo.SymbolCrcPath.Add(S7CommPlusSymbolCrc.PathSegment.Member(levelName, varType.Softdatatype));
            }
        }

        private static ItemAddress CreateItemAddress(VarInfo varInfo)
        {
            var address = new ItemAddress(varInfo.AccessSequence);
            address.SymbolCrc = varInfo.ContainsIndexedArray
                ? 0
                : varInfo.SymbolCrcPath == null
                    ? varInfo.SymbolCrc
                    : S7CommPlusSymbolCrc.ComputeFromSegments(varInfo.SymbolCrcPath);
            varInfo.SymbolCrc = address.SymbolCrc;
            return address;
        }

        /// <summary>
        /// Get the plc tag for the given plc tag symbol.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <returns>plc tag, returns null if plc tag could not be found</returns>
        public PlcTag getPlcTagBySymbol(string symbol)
        {
            VarInfo varInfo = new VarInfo();
            varInfo.Name = symbol;
            varInfo.SymbolCrcPath = new List<S7CommPlusSymbolCrc.PathSegment>();
            // make sure we have the db list
            if (dbInfoList == null)
            {
                if (GetListOfDatablocks(out dbInfoList) != 0) { return null; }
            }
            string levelName = parseSymbolLevel(ref symbol);
            // find db by first level name of symbol
            DatablockInfo dbInfo = dbInfoList.Find(dbi => dbi.db_name == levelName);
            if (dbInfo != null)
            {
                varInfo.AccessSequence = String.Format("{0:X}", dbInfo.db_block_relid);
                return browsePlcTagBySymbol(dbInfo.db_block_ti_relid, ref symbol, varInfo);
            }
            else
            {
                symbol = varInfo.Name;
                // Merker
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theMArea_Rid);
                varInfo.SymbolCrcPath.Clear();
                PlcTag tag = browsePlcTagBySymbol(0x90030000, ref symbol, varInfo);
                if (tag != null) return tag;
                symbol = varInfo.Name;
                // Outputs
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theQArea_Rid);
                varInfo.SymbolCrcPath.Clear();
                tag = browsePlcTagBySymbol(0x90020000, ref symbol, varInfo);
                if (tag != null) return tag;
                symbol = varInfo.Name;
                // Inputs
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theIArea_Rid);
                varInfo.SymbolCrcPath.Clear();
                tag = browsePlcTagBySymbol(0x90010000, ref symbol, varInfo);
                if (tag != null) return tag;
                symbol = varInfo.Name;
                // S7 timers
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theS7Timers_Rid);
                varInfo.SymbolCrcPath.Clear();
                tag = browsePlcTagBySymbol(0x90050000, ref symbol, varInfo);
                if (tag != null) return tag;
                symbol = varInfo.Name;
                // S7 counters
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theS7Counters_Rid);
                varInfo.SymbolCrcPath.Clear();
                tag = browsePlcTagBySymbol(0x90060000, ref symbol, varInfo);
                if (tag != null) return tag;
            }
            return null;
        }

        public class BrowseEntry
        {
            public string Name;
            public uint Softdatatype;
            public UInt32 LID;
            public UInt32 SymbolCrc;
            public string AccessSequence;
        };

        public class BrowseData
        {
            public string db_name;                                          // Name of the datablock
            public UInt32 db_number;                                        // Number of the datablock
            public UInt32 db_block_relid;                                   // RID of the datablock
            public UInt32 db_block_ti_relid;                                // Type-Info RID of the datablock
            public List<BrowseEntry> variables = new List<BrowseEntry>();   // Variables inside the datablock
        };

        public class DatablockInfo
        {
            public string db_name;                                          // Name of the datablock
            public UInt32 db_number;                                        // Number of the datablock
            public UInt32 db_block_relid;                                   // RID of the datablock
            public UInt32 db_block_ti_relid;                                // Type-Info RID of the datablock
        };

        public int GetListOfDatablocks(out List<DatablockInfo> dbInfoList)
        {
            int res;

            dbInfoList = new List<DatablockInfo>();

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = Ids.NativeObjects_thePLCProgram_Rid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            // Add the attributes we need in the response
            exploreReq.AddressList.Add(Ids.ObjectVariableTypeName);

            // Set filter on Id for Datablock Class RID. With this filter, we only
            // get informations from datablocks, and not other blocks we don't need here.
            var filter = new ValueStruct(Ids.Filter);
            filter.AddStructElement(Ids.FilterOperation, new ValueDInt(8)); // 8 = InstanceIOf
            filter.AddStructElement(Ids.AddressCount, new ValueUDInt(0));
            uint[] faddress = new uint[32]; // Unknown, possible dependant on FilterOperation
            filter.AddStructElement(Ids.Address, new ValueUDIntArray(faddress));
            filter.AddStructElement(Ids.FilterValue, new ValueRID(Ids.DB_Class_Rid));

            exploreReq.FilterData = filter;

            res = SendS7plusFunctionObjectAndWait(exploreReq, m_ReadTimeout);
            if (res != 0)
            {
                return res;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            // Get the datablock information we want further informations from.
            var objList = exploreRes.Objects;

            foreach (var ob in objList)
            {
                // May be this check can be removed, if setting the filter to the DB_Class_Rid is working 100%.
                switch (ob.ClassId)
                {
                    case Ids.DB_Class_Rid:
                        UInt32 relid = ob.RelationId;
                        UInt32 area = (relid >> 16);
                        UInt32 num = relid & 0xffff;
                        if (area == 0x8a0e)
                        {
                            var name = (ValueWString)(ob.GetAttribute(Ids.ObjectVariableTypeName));
                            DatablockInfo data = new DatablockInfo();
                            data.db_block_relid = relid;
                            data.db_name = name.GetValue();
                            data.db_number = num;
                            dbInfoList.Add(data);
                        }
                        break;
                }
            }

            // Get the TypeInfo RID to RelId from the first response

            // With LID=1 we get the RID back. With this number we can explore further
            // informations of this datablock.
            // This is neccessary, because informations about instance DBs (e.g. TON) you
            // don't get by the RID of the DB, instead of exploring the TON Type RID.
            var readlist = new List<ItemAddress>();
            var values = new List<object>();
            var errors = new List<UInt64>();

            foreach (var data in dbInfoList)
            {
                if (data.db_number > 0)
                {
                    // Insert the address
                    var adr1 = new ItemAddress();
                    adr1.AccessArea = data.db_block_relid;
                    adr1.AccessSubArea = Ids.DB_ValueActual;
                    adr1.AddLocalId(1);
                    readlist.Add(adr1);
                }
            }
            res = ReadValues(readlist, out values, out errors);
            if (res != 0)
            {
                return res;
            }

            // Insert response data into the list
            for (int i = 0; i < values.Count; i++)
            {
                if (errors[i] == 0)
                {
                    var rid = (ValueRID)values[i];
                    var data = dbInfoList[i];
                    data.db_block_ti_relid = rid.GetValue();
                    dbInfoList[i] = data;
                }
                else
                {
                    // On error, set relid=0, which is then removed in the next step.
                    // Should we report this for the user?
                    var data = dbInfoList[i];
                    data.db_block_ti_relid = 0;
                    dbInfoList[i] = data;
                }
            }

            // Remove elements with db_block_ti_relid == 0.
            // This can occur on datablocks which are only in load memory and can't be explored.
            dbInfoList.RemoveAll(item => item.db_block_ti_relid == 0);

            return 0;
        }

        public int GetTypeInformation(uint exploreId, out List<PObject> objList)
        {
            int res;
            objList = new List<PObject>();

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = exploreId;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            res = SendS7plusFunctionObjectAndWait(exploreReq, m_ReadTimeout);
            if (res != 0)
            {
                return res;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }
            objList = exploreRes.Objects;

            return 0;
        }

        /// <summary>
        /// Requests the tag and block comments from the Plc, returned as XML strings.
        /// xml_linecomment:
        /// The returned XML format differs between between request of I/Q/M/C/T areas and datablocks:
        /// I/Q/M/C/T: <CommentDictionary>     <TagLineComments>      <Comment RefID="ID"> <DictEntry Lanuage="de-DE"> ....
        /// Datablock: <InterfaceLineComments> <Part Kind="Comments"> <Comment Path="ID">  <DictEntry Lanuage="de-DE"> ....
        /// As "ID" the number for the variable identification is used.
        ///
        /// xml_dbcomment:
        /// The xml-value description generated from our own value xml-serialization for WStringSparseArray. The value key is the language id.
        /// Example:
        /// <Value type ="WStringSparseArray"><Value key="1032">DB Kommentar in german de-DE</Value><Value key="1034">DB comment in english en-US</Value></Value>
        /// </summary>
        /// <param name="relid">The relation ID for the area you want the comments for, e.g. 0x8a0e0000+db_number, or 0x52 for M-area</param>
        /// <param name="xml_linecomment"></param>
        /// <param name="xml_dbcomment"></param>
        /// <returns>0 if no error</returns>
        public int GetCommentsXml(uint relid, out string xml_linecomment, out string xml_dbcomment)
        {
            int res;
            // With requesting DataInterface_InterfaceDescription, whe would be able to get all informations like the access ids and
            // datatype informations, that we get from the other browsing method. Needs to be tested which one is more efficient on network traffic or plc load.
            // If we keep use browsing for the comments, at least we would be able to read all information in one request.
            xml_linecomment = String.Empty;
            xml_dbcomment = String.Empty;

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = relid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            // We want to know the following attributes
            exploreReq.AddressList.Add(Ids.ASObjectES_Comment);
            exploreReq.AddressList.Add(Ids.DataInterface_LineComments);

            res = SendS7plusFunctionObjectAndWait(exploreReq, m_ReadTimeout);
            if (res != 0)
            {
                return res;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            foreach(var obj in exploreRes.Objects)
            {
                foreach(var att in obj.Attributes)
                {
                    switch (att.Key)
                    {
                        case Ids.ASObjectES_Comment:
                            var att_comment = (ValueWStringSparseArray)att.Value;
                            xml_dbcomment = att_comment.ToString();
                            break;
                        case Ids.DataInterface_LineComments:
                            var att_linecomment = (ValueBlobSparseArray)att.Value;
                            BlobDecompressor bd = new BlobDecompressor();
                            var blob_sp = att_linecomment.GetValue();
                            // In DBs we get the data with Sparsearray key = 1, in M-Area with key = 2.
                            // For now, just take the first, don't know where the key ids are for.
                            foreach (var key in blob_sp.Keys)
                            {
                                xml_linecomment = bd.decompress(blob_sp[key].value, 4); // Offset of 4, as we have a header for the zlib dictionary version
                                break;
                            }
                            break;
                    }
                }
            }
            return 0;
        }
    }
    #endregion
}
