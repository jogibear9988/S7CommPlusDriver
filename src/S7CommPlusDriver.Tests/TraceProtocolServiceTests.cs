using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using S7CommPlusDriver.Internal;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class TraceProtocolServiceTests
    {
        [Fact]
        public void CreateUsesThePersistentTraceJobWireShape()
        {
            var session = new CaptureFirstRequestSession();
            var service = new S7CommPlusTisTraceSubscriptionService(session);
            var request = CreateRequest(useContinuingJob: true);

            var result = service.Create(request, out _, out _);

            Assert.NotEqual(0, result);
            var create = Assert.IsType<CreateObjectRequest>(session.Request);
            Assert.Equal(S7CommPlusProtocolConstants.CreateObjectTransportFlags, create.TransportFlags);
            Assert.Equal((uint)Ids.NativeObjects_theTisSubsystem_Rid, create.RequestId);
            Assert.Equal((uint)Ids.TisTraceJob_Class_Rid, create.RequestObject.ClassId);
            Assert.Equal((uint)Ids.GetNewRIDOnServer, create.RequestObject.RelationId);
            Assert.Equal((uint)0x20, create.RequestObject.ClassFlags);
            Assert.Equal(
                new uint[]
                {
                    Ids.ObjectVariableTypeName,
                    Ids.AbstractTisJob_Request,
                    Ids.AbstractTisJob_Trigger,
                    Ids.AbstractTisJob_LargeBufferMemorySize,
                    Ids.TisTraceJob_Interpretation,
                    Ids.TisTraceJob_ClientData
                },
                create.RequestObject.Attributes.Keys.ToArray());
            Assert.DoesNotContain((uint)Ids.AbstractTisJob_ModifyingJob, create.RequestObject.Attributes.Keys);
            Assert.DoesNotContain((uint)Ids.AbstractTisJob_ContinuingJob, create.RequestObject.Attributes.Keys);
        }

        [Fact]
        public void CreateSendsTheContinuingOptOutOnlyWhenRequested()
        {
            var session = new CaptureFirstRequestSession();
            var service = new S7CommPlusTisTraceSubscriptionService(session);

            service.Create(CreateRequest(useContinuingJob: false), out _, out _);

            var create = Assert.IsType<CreateObjectRequest>(session.Request);
            var continuing = Assert.IsType<ValueBool>(
                create.RequestObject.Attributes[(uint)Ids.AbstractTisJob_ContinuingJob]);
            Assert.False(continuing.GetValue());
            Assert.DoesNotContain((uint)Ids.AbstractTisJob_ModifyingJob, create.RequestObject.Attributes.Keys);
        }

        [Fact]
        public void CreateAddsNotificationCreditBeforeAcknowledgedActivation()
        {
            var session = new SuccessfulCreateSession();
            var service = new S7CommPlusTisTraceSubscriptionService(session);
            var traceRequest = CreateRequest(useContinuingJob: true);

            var result = service.Create(traceRequest, out var jobId, out var subscriptionId);

            Assert.Equal(0, result);
            Assert.Equal((uint)100, jobId);
            Assert.Equal((uint)200, subscriptionId);
            Assert.Collection(
                session.Requests,
                request => Assert.IsType<CreateObjectRequest>(request),
                request => Assert.IsType<CreateObjectRequest>(request),
                request => AssertSetVariable(request, 201, Ids.TisSubscriptionRef_IncrementNotificationCredit, 1),
                request => AssertSetVariable(request, 100, Ids.AbstractTisJob_TisJobEnabledConf, 1));
            Assert.Equal(2, session.ValidatedResponses);
            Assert.Equal("started", traceRequest.LastLifecycleStage);
        }

        [Fact]
        public void CreateReportsWhenActivationAndPartialJobCleanupBothFail()
        {
            var session = new SuccessfulCreateSession
            {
                RejectActivation = true,
                DeleteError = S7Consts.errIsoConnect
            };
            var service = new S7CommPlusTisTraceSubscriptionService(session);
            var traceRequest = CreateRequest(useContinuingJob: true);

            var result = service.Create(traceRequest, out var jobId, out var subscriptionId);

            Assert.Equal(S7Consts.errCliFunctionRefused, result);
            Assert.Equal(0u, jobId);
            Assert.Equal(0u, subscriptionId);
            Assert.Equal(new uint[] { 200, 100 }, session.DeletedObjectIds);
            Assert.Contains("activate TIS trace job failed", traceRequest.LastLifecycleStage);
            Assert.Contains("cleanup of partial trace job 0x00000064 failed", traceRequest.LastLifecycleStage);
            Assert.Contains("may remain installed", traceRequest.LastLifecycleStage);
        }

        [Fact]
        public void CreateCleansUpObjectsReturnedWithRejectedSubscription()
        {
            var session = new SuccessfulCreateSession { RejectSubscription = true };
            var service = new S7CommPlusTisTraceSubscriptionService(session);
            var traceRequest = CreateRequest(useContinuingJob: true);

            var result = service.Create(traceRequest, out var jobId, out var subscriptionId);

            Assert.Equal(S7Consts.errCliInvalidParams, result);
            Assert.Equal(0u, jobId);
            Assert.Equal(0u, subscriptionId);
            Assert.Equal(new uint[] { 200, 100 }, session.DeletedObjectIds);
            Assert.Contains("partial trace job 0x00000064 was removed", traceRequest.LastLifecycleStage);
        }

        private static void AssertSetVariable(IS7pRequest request, uint objectId, uint address, byte expectedValue)
        {
            var set = Assert.IsType<SetVariableRequest>(request);
            Assert.Equal(S7CommPlusProtocolConstants.RequestWithResponseTransportFlags, set.TransportFlags);
            Assert.Equal(objectId, set.InObjectId);
            Assert.Equal(address, set.Address);
            if (set.Value is ValueBool boolean)
                Assert.Equal(expectedValue != 0, boolean.GetValue());
            else
                Assert.Equal(expectedValue, Assert.IsType<ValueUSInt>(set.Value).GetValue());
        }

        private static S7CommPlusTisTraceRequest CreateRequest(bool useContinuingJob) =>
            new S7CommPlusTisTraceRequest
            {
                JobName = "Trace",
                RequestBlob = new byte[] { 1 },
                TriggerBlob = new byte[] { 2 },
                InterpretationBlob = new byte[] { 3 },
                ClientData = new byte[] { 4 },
                LargeBufferSizeUsed = 64,
                UseContinuingJob = useContinuingJob
            };

        private sealed class CaptureFirstRequestSession : IS7CommPlusProtocolSession
        {
            public IS7pRequest Request { get; private set; } = null!;
            public int LastError { get; set; }
            public int ReadTimeout => 1;
            public uint SessionId => 1;
            public uint SessionId2 => 2;
            public MemoryStream ReceivedPdu { get; } = new MemoryStream();
            public int SendFunction(IS7pRequest request) => throw new NotSupportedException();

            public int SendFunctionAndWait(IS7pRequest request)
            {
                Request = request;
                return S7Consts.errIsoConnect;
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

            public int CheckResponse(IS7pRequest request, IS7pResponse response) => throw new NotSupportedException();
            public int DeleteObject(uint objectId) => throw new NotSupportedException();
            public void DisconnectTransport() { }
            public void ClearSessionIds() => throw new NotSupportedException();
        }

        private sealed class SuccessfulCreateSession : IS7CommPlusProtocolSession
        {
            private int _createCount;
            public List<IS7pRequest> Requests { get; } = new List<IS7pRequest>();
            public List<uint> DeletedObjectIds { get; } = new List<uint>();
            public int ValidatedResponses { get; private set; }
            public bool RejectActivation { get; set; }
            public bool RejectSubscription { get; set; }
            public int DeleteError { get; set; }
            public int LastError { get; set; }
            public int ReadTimeout => 1000;
            public uint SessionId => 7;
            public uint SessionId2 => 8;
            public MemoryStream ReceivedPdu { get; } = new MemoryStream();
            public int SendFunction(IS7pRequest request) => throw new NotSupportedException();

            public int SendFunctionAndWait(IS7pRequest request)
            {
                Requests.Add(request);
                byte[] response;
                if (request is CreateObjectRequest)
                {
                    response = ++_createCount == 1
                        ? CreateObjectResponse(request, 0, 100)
                        : CreateObjectResponse(request, RejectSubscription ? 1UL : 0UL, 200, 201);
                }
                else if (request is SetVariableRequest)
                {
                    var set = (SetVariableRequest)request;
                    var returnValue = RejectActivation
                        && set.Address == Ids.AbstractTisJob_TisJobEnabledConf
                        ? 1UL
                        : 0UL;
                    response = CreateSetVariableResponse(request, returnValue);
                }
                else
                {
                    throw new NotSupportedException();
                }

                ReceivedPdu.SetLength(0);
                ReceivedPdu.Write(response, 0, response.Length);
                ReceivedPdu.Position = 0;
                return 0;
            }

            public int CheckResponse(IS7pRequest request, IS7pResponse response)
            {
                ValidatedResponses++;
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

            public int DeleteObject(uint objectId)
            {
                DeletedObjectIds.Add(objectId);
                return DeleteError;
            }
            public void DisconnectTransport() => throw new NotSupportedException();
            public void ClearSessionIds() => throw new NotSupportedException();

            private static byte[] CreateObjectResponse(IS7pRequest request, ulong returnValue, params uint[] objectIds)
            {
                using var stream = CreateResponseHeader(request, Functioncode.CreateObject);
                S7p.EncodeUInt64Vlq(stream, returnValue);
                S7p.EncodeByte(stream, checked((byte)objectIds.Length));
                foreach (var objectId in objectIds)
                    S7p.EncodeUInt32Vlq(stream, objectId);
                S7p.EncodeByte(stream, ElementID.TerminatingObject);
                return stream.ToArray();
            }

            private static byte[] CreateSetVariableResponse(IS7pRequest request, ulong returnValue)
            {
                using var stream = CreateResponseHeader(request, Functioncode.SetVariable);
                S7p.EncodeUInt64Vlq(stream, returnValue);
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
        }
    }
}
