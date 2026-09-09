using System;
using System.Collections.Generic;
using System.IO;
using S7CommPlusDriver.Internal;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class S7CommPlusProtocolRequestsTests
    {
        [Fact]
        public void AcknowledgedSetVariableWaitsForAndValidatesTheResponse()
        {
            var session = new RespondingProtocolSession
            {
                ResponseFactory = request => CreateSetVariableResponse(request, 0)
            };
            var requests = new S7CommPlusProtocolRequests(session);

            var result = requests.SetVariableAcknowledged(123, 456, new ValueBool(true));

            Assert.Equal(0, result);
            var request = Assert.IsType<SetVariableRequest>(session.Requests[0]);
            Assert.Equal(S7CommPlusProtocolConstants.RequestWithResponseTransportFlags, request.TransportFlags);
            Assert.Equal((uint)123, request.InObjectId);
            Assert.Equal((uint)456, request.Address);
            Assert.True(Assert.IsType<ValueBool>(request.Value).GetValue());
            Assert.Equal(1, session.ResponseValidationCount);
            Assert.Equal(0, session.FireAndForgetCount);
        }

        [Fact]
        public void AcknowledgedSetVariableReportsAPlcRefusal()
        {
            var session = new RespondingProtocolSession
            {
                ResponseFactory = request => CreateSetVariableResponse(request, 0x8000B10700008011)
            };
            var requests = new S7CommPlusProtocolRequests(session);

            var result = requests.SetVariableAcknowledged(123, 456, new ValueBool(true));

            Assert.Equal(S7Consts.errCliFunctionRefused, result);
        }

        [Fact]
        public void SetMultiVariablesReportsAnItemLevelPlcRefusal()
        {
            var session = new RespondingProtocolSession
            {
                ResponseFactory = request => CreateSetMultiVariablesResponse(request, 0, 0x8000B10700008011)
            };
            var requests = new S7CommPlusProtocolRequests(session);

            var result = requests.SetMultiVariablesRaw(
                123,
                new uint[] { 456 },
                new PValue[] { new ValueBool(true) });

            Assert.Equal(S7Consts.errCliFunctionRefused, result);
        }

        [Fact]
        public void SetMultiVariablesReportsATopLevelPlcRefusal()
        {
            var session = new RespondingProtocolSession
            {
                ResponseFactory = request => CreateSetMultiVariablesResponse(request, 0x8000B10700008011, 0)
            };
            var requests = new S7CommPlusProtocolRequests(session);

            var result = requests.SetMultiVariablesRaw(
                123,
                new uint[] { 456 },
                new PValue[] { new ValueBool(true) });

            Assert.Equal(S7Consts.errCliFunctionRefused, result);
        }

        private static byte[] CreateSetVariableResponse(IS7pRequest request, ulong returnValue)
        {
            using var stream = CreateResponseHeader(request, Functioncode.SetVariable);
            S7p.EncodeUInt64Vlq(stream, returnValue);
            S7p.EncodeUInt32Vlq(stream, request.IntegrityId);
            return stream.ToArray();
        }

        private static byte[] CreateSetMultiVariablesResponse(
            IS7pRequest request,
            ulong returnValue,
            ulong itemReturnValue)
        {
            using var stream = CreateResponseHeader(request, Functioncode.SetMultiVariables);
            S7p.EncodeUInt64Vlq(stream, returnValue);
            if (itemReturnValue != 0)
            {
                S7p.EncodeUInt32Vlq(stream, 1);
                S7p.EncodeUInt64Vlq(stream, itemReturnValue);
            }
            S7p.EncodeUInt32Vlq(stream, 0);
            S7p.EncodeUInt32Vlq(stream, request.IntegrityId);
            return stream.ToArray();
        }

        private static MemoryStream CreateResponseHeader(IS7pRequest request, ushort function)
        {
            var stream = new MemoryStream();
            S7p.EncodeByte(stream, request.ProtocolVersion);
            S7p.EncodeByte(stream, Opcode.Response);
            S7p.EncodeUInt16(stream, 0);
            S7p.EncodeUInt16(stream, function);
            S7p.EncodeUInt16(stream, 0);
            S7p.EncodeUInt16(stream, request.SequenceNumber);
            S7p.EncodeByte(stream, S7CommPlusProtocolConstants.RequestWithResponseTransportFlags);
            return stream;
        }

        private sealed class RespondingProtocolSession : IS7CommPlusProtocolSession
        {
            public Func<IS7pRequest, byte[]> ResponseFactory { get; set; } = null!;
            public List<IS7pRequest> Requests { get; } = new List<IS7pRequest>();
            public int ResponseValidationCount { get; private set; }
            public int FireAndForgetCount { get; private set; }
            public int LastError { get; set; }
            public int ReadTimeout => 1000;
            public uint SessionId => 7;
            public uint SessionId2 => 8;
            public MemoryStream ReceivedPdu { get; } = new MemoryStream();

            public int SendFunction(IS7pRequest request)
            {
                FireAndForgetCount++;
                Requests.Add(request);
                return 0;
            }

            public int SendFunctionAndWait(IS7pRequest request)
            {
                Requests.Add(request);
                var response = ResponseFactory?.Invoke(request) ?? Array.Empty<byte>();
                ReceivedPdu.SetLength(0);
                ReceivedPdu.Write(response, 0, response.Length);
                ReceivedPdu.Position = 0;
                return 0;
            }

            public void WaitForPdu(int timeoutMilliseconds) => throw new NotSupportedException();

            public int WaitForNotification(
                uint subscriptionObjectId,
                int timeoutMilliseconds,
                out Notification notification)
            {
                notification = null!;
                throw new NotSupportedException();
            }

            public int CheckResponse(IS7pRequest request, IS7pResponse response)
            {
                ResponseValidationCount++;
                return 0;
            }

            public int DeleteObject(uint objectId) => throw new NotSupportedException();
            public void DisconnectTransport() => throw new NotSupportedException();
            public void ClearSessionIds() => throw new NotSupportedException();
        }
    }
}
