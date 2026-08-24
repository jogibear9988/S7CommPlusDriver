using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace S7CommPlusDriver
{
    internal sealed class S7CommPlusTisTraceSubscriptionService
    {
        private const uint TisResultReferenceId = 9;
        private const uint TisNotificationCreditReferenceId = 10;
        private const uint TisEnabledActualReferenceId = 11;

        private readonly IS7CommPlusProtocolSession _session;
        private readonly S7CommPlusProtocolRequests _requests;
        private readonly Dictionary<uint, TraceState> _subscriptions = new Dictionary<uint, TraceState>();

        private sealed class TraceState
        {
            public uint JobObjectId { get; set; }
            public uint SubscriptionObjectId { get; set; }
            public uint SubscriptionRefObjectId { get; set; }
            public uint PollSequenceNumber { get; set; }
        }

        public S7CommPlusTisTraceSubscriptionService(IS7CommPlusProtocolSession session)
        {
            _session = session;
            _requests = new S7CommPlusProtocolRequests(session);
        }

        public string LastDiagnostic { get; private set; } = "";

        public int Create(S7CommPlusTisTraceRequest request, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            if (request == null)
                return S7Consts.errCliInvalidParams;

            LastDiagnostic = "";
            request.LastLifecycleStage = "create TIS trace job";
            var state = new TraceState();
            var job = new PObject
            {
                ClassId = (uint)(request.UseContinuingJob ? Ids.TisContinuingJob_Class_Rid : Ids.TisTraceJob_Class_Rid),
                RelationId = Ids.GetNewRIDOnServer
            };
            job.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString(request.JobName));
            if (!request.UseContinuingJob)
                job.AddAttribute(Ids.TisTraceJob_Interpretation, new ValueBlob(0, request.InterpretationBlob));
            job.AddAttribute(Ids.AbstractTisJob_LargeBufferMemorySize, new ValueUDInt(request.LargeBufferSizeUsed));
            if (request.ClientData != null)
                job.AddAttribute(Ids.TisTraceJob_ClientData, new ValueBlob(0, request.ClientData));
            job.AddAttribute(Ids.AbstractTisJob_Request, new ValueBlob(0, request.RequestBlob));
            job.AddAttribute(Ids.AbstractTisJob_Trigger, new ValueBlob(0, request.TriggerBlob));
            job.AddAttribute(Ids.AbstractTisJob_ModifyingJob, new ValueBool(true));

            var createJob = new CreateObjectRequest(ProtocolVersion.V2, 0, true)
            {
                TransportFlags = S7CommPlusProtocolConstants.RequestWithResponseTransportFlags,
                RequestId = request.UseContinuingJob ? 7u : _session.SessionId,
                RequestValue = new ValueUDInt(0)
            };
            createJob.SetRequestObject(job);
            var result = _requests.CreateObject(createJob, out var response);
            if (result != 0)
            {
                _session.DisconnectTransport();
                return result;
            }
            if (response.ReturnValue != 0 || response.ObjectIds.Count == 0)
            {
                LastDiagnostic = $"Create TIS trace job rejected: return=0x{response.ReturnValue:X16}, objectIds={response.ObjectIds.Count}";
                request.LastLifecycleStage = LastDiagnostic;
                if (response.ObjectIds.Count > 0)
                {
                    state.JobObjectId = response.ObjectIds[0];
                    Cleanup(state);
                }
                return S7Consts.errCliInvalidParams;
            }

            state.JobObjectId = response.ObjectIds[0];
            request.LastLifecycleStage = "create TIS trace subscription";
            result = CreateSubscription(request.JobName, state);
            if (result != 0)
            {
                Cleanup(state);
                return result;
            }

            request.LastLifecycleStage = "enable TIS trace job and add notification credit";
            result = _requests.SetMultiVariablesRaw(
                0,
                new uint[]
                {
                    0, state.JobObjectId, 1, Ids.AbstractTisJob_TisJobEnabledConf,
                    0, state.SubscriptionRefObjectId, 1, Ids.TisSubscriptionRef_IncrementNotificationCredit
                },
                new PValue[] { new ValueBool(true), new ValueUSInt(1) });
            if (result != 0)
            {
                Cleanup(state);
                return result;
            }

            request.LastLifecycleStage = "started";
            subscriptionObjectId = state.SubscriptionObjectId;
            _subscriptions[subscriptionObjectId] = state;
            return 0;
        }

        private int CreateSubscription(string jobName, TraceState state)
        {
            var subscription = new PObject
            {
                ClassId = Ids.ClassSubscription,
                RelationId = S7CommPlusProtocolConstants.SubscriptionRelationIdStart
            };
            subscription.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString("Subscription_" + jobName));
            subscription.AddAttribute(Ids.SubscriptionFunctionClassId, new ValueUSInt((byte)SubscriptionFunctionClass.Tis));
            subscription.AddAttribute(Ids.SubscriptionMissedSendings, new ValueUInt(0));
            subscription.AddAttribute(Ids.SubscriptionSubsystemError, new ValueLInt(0));
            subscription.AddAttribute(Ids.SubscriptionRouteMode, new ValueUSInt((byte)SubscriptionRouteMode.Tis));
            subscription.AddAttribute(Ids.SubscriptionActive, new ValueBool(true));
            subscription.AddAttribute(Ids.SubscriptionReferenceList, CreateReferenceList(state));
            subscription.AddAttribute(Ids.SubscriptionCycleTime, new ValueUDInt(0));
            subscription.AddAttribute(Ids.SubscriptionDisabled, new ValueUSInt(0));
            subscription.AddAttribute(Ids.SubscriptionCount, new ValueUSInt(0));
            subscription.AddAttribute(Ids.SubscriptionCreditLimit, new ValueInt(-1));
            subscription.AddAttribute(Ids.SubscriptionTicks, new ValueUInt(S7CommPlusProtocolConstants.SubscriptionTicksUnlimited));
            subscription.AddAttribute(S7CommPlusProtocolConstants.SubscriptionDefaultAttribute1055, new ValueUSInt(0));

            var subscriptionRef = new PObject
            {
                ClassId = Ids.TisSubscriptionRef_Class_Rid,
                RelationId = Ids.GetNewRIDOnServer
            };
            subscriptionRef.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString("TisSubscriptionRef_" + jobName));
            subscriptionRef.AddAttribute(Ids.SubscriptionReferenceMode, new ValueUSInt(1));
            subscriptionRef.AddAttribute(Ids.TisSubscriptionRef_IncrementNotificationCredit, new ValueUSInt(0));
            subscriptionRef.AddRelation(Ids.TisSubscriptionRef_itsAssumingJob, state.JobObjectId);
            subscription.AddObject(subscriptionRef);

            var create = new CreateObjectRequest(ProtocolVersion.V2, 0, true)
            {
                TransportFlags = S7CommPlusProtocolConstants.RequestWithResponseTransportFlags,
                RequestId = _session.SessionId2,
                RequestValue = new ValueUDInt(0)
            };
            create.SetRequestObject(subscription);
            var result = _requests.CreateObject(create, out var response);
            if (result != 0)
            {
                _session.DisconnectTransport();
                return result;
            }
            if (response.ReturnValue != 0 || response.ObjectIds.Count == 0)
                return S7Consts.errCliInvalidParams;
            state.SubscriptionObjectId = response.ObjectIds[0];
            state.SubscriptionRefObjectId = response.ObjectIds.Count > 1 ? response.ObjectIds[1] : 0;
            return state.SubscriptionRefObjectId == 0 ? S7Consts.errCliInvalidParams : 0;
        }

        private ValueUDIntArray CreateReferenceList(TraceState state)
        {
            return new ValueUDIntArray(new[]
            {
                0x80010000u, 0u, 3u,
                0x80120001u, TisResultReferenceId, _session.SessionId, state.JobObjectId, 0u, (uint)Ids.AbstractTisJob_Result,
                0x80120001u, TisNotificationCreditReferenceId, _session.SessionId, state.JobObjectId, 0u, (uint)Ids.AbstractTisJob_NotificationCredit,
                0x80120001u, TisEnabledActualReferenceId, _session.SessionId, state.JobObjectId, 0u, (uint)Ids.AbstractTisJob_TisJobEnabledActual
            }, S7CommPlusProtocolConstants.ValueAddressArrayFlag);
        }

        public int WaitForNotifications(uint subscriptionObjectId, int waitTimeout, out List<S7CommPlusTisTraceNotification> notifications)
        {
            notifications = new List<S7CommPlusTisTraceNotification>();
            if (!_subscriptions.TryGetValue(subscriptionObjectId, out var state))
                return S7Consts.errCliInvalidParams;

            LastDiagnostic = $"waiting for TIS trace notification timeout={waitTimeout}ms";
            var result = _requests.WaitNotification(subscriptionObjectId, waitTimeout, out var notification);
            if (result != 0)
            {
                if (TryPoll(state, out var polled))
                {
                    notifications.Add(polled);
                    _requests.SetVariable(state.SubscriptionRefObjectId, Ids.TisSubscriptionRef_IncrementNotificationCredit, new ValueUSInt(1));
                    return 0;
                }
                LastDiagnostic = $"WaitNotification returned {result}";
                return result;
            }

            notification.Values.TryGetValue(TisEnabledActualReferenceId, out var enabledValue);
            notification.Values.TryGetValue(TisNotificationCreditReferenceId, out var creditValue);
            notification.Values.TryGetValue(TisResultReferenceId, out var resultValue);
            var rawResult = ExtractBlob(resultValue);
            var rawLargeBuffer = ReadBlob(state.JobObjectId, Ids.TisTraceJob_LargeBuffer);
            notifications.Add(new S7CommPlusTisTraceNotification(
                notification.Add1Timestamp,
                notification.NotificationSequenceNumber,
                notification.NotificationCreditTick,
                ExtractBool(enabledValue),
                ExtractByte(creditValue),
                rawResult,
                rawLargeBuffer));
            return _requests.SetVariable(state.SubscriptionRefObjectId, Ids.TisSubscriptionRef_IncrementNotificationCredit, new ValueUSInt(1));
        }

        private bool TryPoll(TraceState state, out S7CommPlusTisTraceNotification notification)
        {
            notification = null;
            var rawResult = ReadBlob(state.JobObjectId, Ids.AbstractTisJob_Result);
            var rawLargeBuffer = ReadBlob(state.JobObjectId, Ids.TisTraceJob_LargeBuffer);
            if (rawResult.Length == 0 && rawLargeBuffer.Length == 0)
                return false;
            notification = new S7CommPlusTisTraceNotification(
                DateTime.UtcNow, ++state.PollSequenceNumber, 0, null, null, rawResult, rawLargeBuffer);
            return true;
        }

        private byte[] ReadBlob(uint objectId, int attribute)
        {
            var result = _requests.GetVariable(objectId, (uint)attribute, out var value);
            if (result != 0)
                result = _requests.GetVarSubstreamed(objectId, (ushort)attribute, out value);
            if (result != 0)
            {
                LastDiagnostic = $"reading trace attribute {attribute} failed with {result}";
                return Array.Empty<byte>();
            }
            return ExtractBlob(value);
        }

        public int Delete(uint subscriptionObjectId)
        {
            if (!_subscriptions.TryGetValue(subscriptionObjectId, out var state))
                return 0;
            _subscriptions.Remove(subscriptionObjectId);
            return Cleanup(state);
        }

        private int Cleanup(TraceState state)
        {
            var result = 0;
            if (state.SubscriptionObjectId != 0)
            {
                _requests.SetVariable(state.SubscriptionObjectId, Ids.SubscriptionDisabled, new ValueUSInt(1));
                result = _session.DeleteObject(state.SubscriptionObjectId);
                state.SubscriptionObjectId = 0;
                state.SubscriptionRefObjectId = 0;
            }
            if (state.JobObjectId != 0)
            {
                var deleteResult = _session.DeleteObject(state.JobObjectId);
                if (result == 0)
                    result = deleteResult;
                state.JobObjectId = 0;
            }
            return result;
        }

        private static bool? ExtractBool(PValue value) => value is ValueBool typed ? typed.GetValue() : null;
        private static byte? ExtractByte(PValue value) => value is ValueUSInt typed ? typed.GetValue() : null;

        private static byte[] ExtractBlob(PValue value)
        {
            if (value is ValueBlob blob)
                return blob.GetValue() ?? Array.Empty<byte>();
            if (value is ValueBlobSparseArray sparse)
            {
                foreach (var key in sparse.GetValue().Keys.OrderBy(x => x))
                {
                    var data = sparse.GetValue()[key].value;
                    if (data != null && data.Length > 0)
                        return data;
                }
            }
            return Array.Empty<byte>();
        }
    }
}
