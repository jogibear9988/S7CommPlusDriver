using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class S7CommPlusProtocolSessionReceiveTests
    {
        [Fact]
        public void RequestTimeoutReplacesHandshakeTimeoutAfterConnection()
        {
            var connection = new S7CommPlusProtocolSession();

            var timeouts = connection.DebugApplyRequestTimeoutForTests(5000, 120000);

            Assert.Equal(120000, timeouts.ProtocolReadTimeout);
            Assert.Equal(120000, timeouts.TransportReceiveTimeout);
            Assert.Equal(120000, timeouts.TransportSendTimeout);
        }

        [Fact]
        public void MalformedPduPublishesReceiveError()
        {
            var connection = new S7CommPlusProtocolSession();
            connection.DebugResetReceiveDispatcherForTests();

            connection.DebugOnDataReceivedForTests(new byte[] { 0x00, ProtocolVersion.V1, 0x00, 0x00 });
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out var pdu);

            Assert.Equal(S7Consts.errIsoInvalidPDU1, error);
            Assert.Null(pdu);
        }

        [Fact]
        public void InvalidProtocolVersionPublishesReceiveError()
        {
            var connection = new S7CommPlusProtocolSession();
            connection.DebugResetReceiveDispatcherForTests();

            connection.DebugOnDataReceivedForTests(new byte[] { 0x72, 0x42, 0x00, 0x00 });
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out var pdu);

            Assert.Equal(S7Consts.errIsoInvalidPDU2, error);
            Assert.Null(pdu);
        }

        [Fact]
        public void CompletePduIsDispatchedWithoutPolling()
        {
            var connection = new S7CommPlusProtocolSession();
            connection.DebugResetReceiveDispatcherForTests();

            connection.DebugOnDataReceivedForTests(new byte[] { 0x72, ProtocolVersion.V1, 0x00, 0x01, 0xAA, 0x72, ProtocolVersion.V1, 0x00, 0x00 });
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out MemoryStream pdu);

            Assert.Equal(0, error);
            Assert.NotNull(pdu);
            Assert.Equal(new byte[] { ProtocolVersion.V1, 0xAA }, pdu.ToArray());
        }

        [Fact]
        public void ReceiveTimeoutReturnsJobTimeout()
        {
            var connection = new S7CommPlusProtocolSession();
            connection.DebugResetReceiveDispatcherForTests();

            var error = connection.DebugReceiveNextS7plusPduForTests(10, out var pdu);

            Assert.Equal(S7Consts.errCliJobTimeout, error);
            Assert.Null(pdu);
        }

        [Fact]
        public void LegacyDigestMismatchPublishesReceiveError()
        {
            var connection = new S7CommPlusProtocolSession();
            connection.DebugResetReceiveDispatcherForTests();
            connection.DebugEnableLegacyDigestForTests(new byte[24]);

            var pdu = new byte[4 + 1 + 32 + 1 + 4];
            pdu[0] = 0x72;
            pdu[1] = ProtocolVersion.V3;
            pdu[3] = 34;
            pdu[4] = 0x20;
            pdu[37] = 0x31;
            pdu[38] = 0x72;
            pdu[39] = ProtocolVersion.V3;

            connection.DebugOnDataReceivedForTests(pdu);
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out var received);

            Assert.Equal(S7Consts.errS7CommPlusDigestMismatch, error);
            Assert.Null(received);
        }

        [Fact]
        public void LegacyFragmentedPduUsesAccumulatedDigestInput()
        {
            var connection = new S7CommPlusProtocolSession();
            var sessionKey = new byte[24];
            for (var i = 0; i < sessionKey.Length; i++)
            {
                sessionKey[i] = (byte)(i + 1);
            }

            var firstBody = new byte[] { 0x31, 0x01, 0x02 };
            var secondBody = new byte[] { 0x03, 0x04 };
            var accumulatedBody = new byte[firstBody.Length + secondBody.Length];
            firstBody.CopyTo(accumulatedBody, 0);
            secondBody.CopyTo(accumulatedBody, firstBody.Length);

            connection.DebugResetReceiveDispatcherForTests();
            connection.DebugEnableLegacyDigestForTests(sessionKey);

            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(firstBody, firstBody, sessionKey, hasTrailer: false));
            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(secondBody, accumulatedBody, sessionKey, hasTrailer: true));
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out MemoryStream pdu);

            Assert.Equal(0, error);
            Assert.NotNull(pdu);
            Assert.Equal(new byte[] { ProtocolVersion.V3, 0x31, 0x01, 0x02, 0x03, 0x04 }, pdu.ToArray());
        }

        [Fact]
        public void LegacyFragmentedPduKeepsAccumulatingDigestInputAcrossMiddleFragments()
        {
            var connection = new S7CommPlusProtocolSession();
            var sessionKey = new byte[24];
            for (var i = 0; i < sessionKey.Length; i++)
            {
                sessionKey[i] = (byte)(0x30 + i);
            }

            var firstBody = new byte[] { 0x31, 0xAA };
            var secondBody = new byte[] { 0xBB, 0xCC };
            var thirdBody = new byte[] { 0xDD, 0xEE };
            var secondDigestInput = new byte[firstBody.Length + secondBody.Length];
            firstBody.CopyTo(secondDigestInput, 0);
            secondBody.CopyTo(secondDigestInput, firstBody.Length);
            var thirdDigestInput = new byte[secondDigestInput.Length + thirdBody.Length];
            secondDigestInput.CopyTo(thirdDigestInput, 0);
            thirdBody.CopyTo(thirdDigestInput, secondDigestInput.Length);

            connection.DebugResetReceiveDispatcherForTests();
            connection.DebugEnableLegacyDigestForTests(sessionKey);

            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(firstBody, firstBody, sessionKey, hasTrailer: false));
            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(secondBody, secondDigestInput, sessionKey, hasTrailer: false));
            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(thirdBody, thirdDigestInput, sessionKey, hasTrailer: true));
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out MemoryStream pdu);

            Assert.Equal(0, error);
            Assert.NotNull(pdu);
            Assert.Equal(new byte[] { ProtocolVersion.V3, 0x31, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, pdu.ToArray());
        }

        [Fact]
        public void LegacyContinuationFragmentDigestMismatchDoesNotAbortReassembly()
        {
            var connection = new S7CommPlusProtocolSession();
            var sessionKey = new byte[24];
            for (var i = 0; i < sessionKey.Length; i++)
            {
                sessionKey[i] = (byte)(0x80 + i);
            }

            var firstBody = new byte[] { 0x32, 0x10 };
            var secondBody = new byte[] { 0x20, 0x30 };

            connection.DebugResetReceiveDispatcherForTests();
            connection.DebugEnableLegacyDigestForTests(sessionKey);

            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(firstBody, firstBody, sessionKey, hasTrailer: false));
            connection.DebugOnDataReceivedForTests(CreateLegacyFragment(secondBody, secondBody, sessionKey, hasTrailer: true));
            var error = connection.DebugReceiveNextS7plusPduForTests(100, out MemoryStream pdu);

            Assert.Equal(0, error);
            Assert.NotNull(pdu);
            Assert.Equal(new byte[] { ProtocolVersion.V3, 0x32, 0x10, 0x20, 0x30 }, pdu.ToArray());
        }

        [Fact]
        public void LargeGetMultiVariablesRequestExceedsLegacySingleFrameLimit()
        {
            var connection = new S7CommPlusProtocolSession();
            var addresses = Enumerable.Range(1, 100)
                .Select(i => new ItemAddress($"8A0E{i:X4}.1"))
                .ToArray();

            var exceedsLimit = connection.DebugGetMultiVariablesRequestExceedsLegacySingleFrameForTests(addresses);

            Assert.True(exceedsLimit);
        }

        [Fact]
        public void SmallGetMultiVariablesRequestFitsLegacySingleFrameLimit()
        {
            var connection = new S7CommPlusProtocolSession();
            var addresses = Enumerable.Range(1, 8)
                .Select(i => new ItemAddress($"8A0E{i:X4}.1"))
                .ToArray();

            var exceedsLimit = connection.DebugGetMultiVariablesRequestExceedsLegacySingleFrameForTests(addresses);

            Assert.False(exceedsLimit);
        }

        [Fact]
        public void WriteBatchStopsBeforeSerializedPayloadExceedsLimit()
        {
            var connection = new S7CommPlusProtocolSession();
            var addresses = Enumerable.Range(1, 3)
                .Select(i => new ItemAddress($"8A0E{i:X4}.1"))
                .ToArray();
            var values = Enumerable.Range(1, 3)
                .Select(_ => (PValue)new ValueBlob(0, new byte[600]))
                .ToArray();

            var batch = connection.DebugCreateWriteRequestBatchForTests(addresses, values, 3, 987);

            Assert.Equal(1, batch.ItemCount);
            Assert.InRange(batch.SerializedLength, 1, 987);
        }

        [Fact]
        public void WriteBatchStillAllowsOneIntrinsicallyOversizedItem()
        {
            var connection = new S7CommPlusProtocolSession();
            var addresses = new[] { new ItemAddress("8A0E0001.1") };
            var values = new PValue[] { new ValueBlob(0, new byte[600]) };

            var batch = connection.DebugCreateWriteRequestBatchForTests(addresses, values, 20, 128);

            Assert.Equal(1, batch.ItemCount);
            Assert.True(batch.SerializedLength > 128);
        }

        [Fact]
        public void WriteBatchAlsoHonorsPlcItemLimit()
        {
            var connection = new S7CommPlusProtocolSession();
            var addresses = Enumerable.Range(1, 5)
                .Select(i => new ItemAddress($"8A0E{i:X4}.1"))
                .ToArray();
            var values = Enumerable.Range(1, 5)
                .Select(i => (PValue)new ValueDInt(i))
                .ToArray();

            var batch = connection.DebugCreateWriteRequestBatchForTests(addresses, values, 3, 987);

            Assert.Equal(3, batch.ItemCount);
            Assert.InRange(batch.SerializedLength, 1, 987);
        }

        [Fact]
        public void LargeLegacyPayloadIsProtectedOnceBeforeTransportFragmentation()
        {
            var connection = new S7CommPlusProtocolSession();
            var sessionKey = Enumerable.Range(1, 24).Select(value => (byte)value).ToArray();
            var payload = Enumerable.Range(0, 2000).Select(value => (byte)value).ToArray();
            connection.DebugEnableLegacyDigestForTests(sessionKey);

            var result = connection.DebugBuildLegacyProtectedPduForTests(payload);

            Assert.Equal(0, result.Error);
            Assert.Equal(4 + 33 + payload.Length + 4, result.Packet.Length);
            Assert.Equal(0x72, result.Packet[0]);
            Assert.Equal(ProtocolVersion.V3, result.Packet[1]);
            Assert.Equal(payload.Length + 33, (result.Packet[2] << 8) | result.Packet[3]);
            Assert.Equal(32, result.Packet[4]);
            Assert.Equal(payload, result.Packet.Skip(37).Take(payload.Length));
            Assert.Equal(
                new byte[] { 0x72, ProtocolVersion.V3, 0x00, 0x00 },
                result.Packet.Skip(result.Packet.Length - 4).ToArray());
        }

        private static byte[] CreateLegacyFragment(byte[] body, byte[] digestInput, byte[] sessionKey, bool hasTrailer)
        {
            var pdu = new byte[4 + 1 + 32 + body.Length + (hasTrailer ? 4 : 0)];
            var dataLength = 1 + 32 + body.Length;
            pdu[0] = 0x72;
            pdu[1] = ProtocolVersion.V3;
            pdu[2] = (byte)(dataLength >> 8);
            pdu[3] = (byte)dataLength;
            pdu[4] = 32;
            RuntimeCompatibility.HmacSha256(sessionKey, digestInput, pdu.AsSpan(5, 32));
            body.CopyTo(pdu, 37);
            if (hasTrailer)
            {
                pdu[^4] = 0x72;
                pdu[^3] = ProtocolVersion.V3;
            }

            return pdu;
        }
    }
}
