using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
            public byte[] LastPolledDataHash { get; set; }
        }

        public S7CommPlusTisTraceSubscriptionService(IS7CommPlusProtocolSession session)
        {
            _session = session;
            _requests = new S7CommPlusProtocolRequests(session);
        }

        public string LastDiagnostic { get; private set; } = "";

        public int GetInstalledTraces(bool includeResultData, out List<S7CommPlusInstalledTrace> traces)
        {
            traces = new List<S7CommPlusInstalledTrace>();
            var attributes = new List<uint>
            {
                Ids.ObjectVariableTypeName,
                Ids.AbstractTisJob_TisJobEnabledConf,
                Ids.AbstractTisJob_TisJobEnabledActual,
                Ids.AbstractTisJob_ContinuingJob,
                Ids.AbstractTisJob_CreationTimestamp,
                Ids.AbstractTisJob_ModifyingJob,
                Ids.AbstractTisJob_LargeBufferMemorySize,
                Ids.AbstractTisJob_Request,
                Ids.AbstractTisJob_Trigger,
                Ids.TisTraceJob_ClientData,
                Ids.TisTraceJob_Interpretation
            };
            if (includeResultData)
            {
                attributes.Add(Ids.AbstractTisJob_Result);
                attributes.Add(Ids.TisTraceJob_LargeBuffer);
            }
            var result = _requests.Explore(Ids.NativeObjects_theTisSubsystem_Rid, attributes.ToArray(), out var response);
            if (result != 0)
                return result;
            if (response?.ReturnValue != 0)
                return S7Consts.errCliFunctionRefused;

            var objects = Flatten(response.Objects).ToList();
            var traceObjects = objects.Where(IsTraceJob).ToList();
            LastDiagnostic = $"explored TIS subsystem {Ids.NativeObjects_theTisSubsystem_Rid}: " +
                $"{objects.Count} objects, {traceObjects.Count} trace jobs";
            foreach (var obj in traceObjects)
                traces.Add(ConvertInstalledTrace(obj, includeResultData));
            return 0;
        }

        public int GetStoredMeasurements(
            bool includeResultData,
            out List<S7CommPlusStoredTraceMeasurement> measurements)
        {
            measurements = new List<S7CommPlusStoredTraceMeasurement>();
            var attributes = new List<uint>
            {
                Ids.ObjectVariableTypeName,
                Ids.TisMeasurement_ActivationTime,
                Ids.TisMeasurement_SavingTime,
                Ids.TisMeasurement_SequenceNumber,
                Ids.TisMeasurement_LargeBufferMemorySize,
                Ids.TisMeasurement_Request,
                Ids.TisMeasurement_Trigger,
                Ids.TisMeasurement_Interpretation
            };
            if (includeResultData)
            {
                attributes.Add(Ids.TisMeasurement_Result);
                attributes.Add(Ids.TisMeasurement_LargeBuffer);
            }

            var result = _requests.Explore(
                Ids.NativeObjects_theMeasurementContainer_Rid,
                attributes,
                out var response,
                exploreChildsRecursive: 1);
            if (result != 0)
                return result;
            if (response?.ReturnValue != 0)
                return S7Consts.errCliFunctionRefused;

            var objects = Flatten(response.Objects).ToList();
            var stored = objects.Where(obj => obj.ClassId == Ids.TisMeasurement_Class_Rid).ToList();
            LastDiagnostic = $"explored measurement container {Ids.NativeObjects_theMeasurementContainer_Rid}: " +
                $"{objects.Count} objects, {stored.Count} stored trace measurements";
            foreach (var obj in stored)
                measurements.Add(ConvertStoredMeasurement(obj));
            return 0;
        }

        internal static bool IsTraceJob(PObject obj)
        {
            if (obj == null)
                return false;
            if (obj.ClassId == Ids.TisTraceJob_Class_Rid || obj.ClassId == Ids.TisContinuingJob_Class_Rid)
                return true;

            // Concrete TIS trace class IDs can vary with the CPU firmware's type schema. The inherited payload shape is
            // stable and distinguishes traces from watch jobs, which have Request and Trigger but no Interpretation.
            return obj.Attributes.ContainsKey(Ids.AbstractTisJob_Request)
                && obj.Attributes.ContainsKey(Ids.AbstractTisJob_Trigger)
                && obj.Attributes.ContainsKey(Ids.TisTraceJob_Interpretation);
        }

        private static IEnumerable<PObject> Flatten(IEnumerable<PObject> objects)
        {
            if (objects == null)
                yield break;
            foreach (var obj in objects)
            {
                yield return obj;
                foreach (var child in Flatten(obj.GetObjects()))
                    yield return child;
            }
        }

        internal static S7CommPlusInstalledTrace ConvertInstalledTrace(PObject obj, bool resultDataIncluded)
        {
            var name = GetString(obj, Ids.ObjectVariableTypeName);
            if (String.IsNullOrWhiteSpace(name))
                name = $"Trace_{obj.RelationId:X8}";
            var createdAt = GetTimestamp(obj, Ids.AbstractTisJob_CreationTimestamp);
            var request = GetBlob(obj, Ids.AbstractTisJob_Request);
            var trigger = GetBlob(obj, Ids.AbstractTisJob_Trigger);
            var interpretation = GetBlob(obj, Ids.TisTraceJob_Interpretation);
            var clientData = GetBlob(obj, Ids.TisTraceJob_ClientData);
            var enabled = GetBool(obj, Ids.AbstractTisJob_TisJobEnabledActual)
                ?? GetBool(obj, Ids.AbstractTisJob_TisJobEnabledConf);
            var result = GetBlob(obj, Ids.AbstractTisJob_Result);
            var largeBuffer = GetBlob(obj, Ids.TisTraceJob_LargeBuffer);
            var allocatedBufferSize = GetUInt32(obj, Ids.AbstractTisJob_LargeBufferMemorySize);
            var continuingJob = GetBool(obj, Ids.AbstractTisJob_ContinuingJob);
            var state = !resultDataIncluded
                ? enabled == false
                    ? S7CommPlusTraceState.Inactive
                    : S7CommPlusTraceState.Unknown
                : S7CommPlusTisTraceResultStatus.IsCompleted(result)
                    ? S7CommPlusTraceState.Completed
                    : enabled == false
                        ? S7CommPlusTraceState.Inactive
                        : enabled == true
                            ? S7CommPlusTraceState.WaitingForTrigger
                            : S7CommPlusTraceState.Unknown;

            var persistentId = CreateExternalPersistentId(name, createdAt, request, trigger, interpretation, clientData);
            var reference = new S7CommPlusTraceReference(persistentId, name, obj.RelationId, createdAt);
            return new S7CommPlusInstalledTrace(
                reference,
                state,
                request,
                trigger,
                interpretation,
                result,
                largeBuffer,
                clientData,
                enabled,
                resultDataIncluded,
                allocatedBufferSize,
                obj.ClassId,
                obj.ClassFlags,
                obj.AttributeId,
                continuingJob);
        }

        internal static S7CommPlusStoredTraceMeasurement ConvertStoredMeasurement(PObject obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));
            return new S7CommPlusStoredTraceMeasurement(
                obj.RelationId,
                GetString(obj, Ids.ObjectVariableTypeName),
                GetTimestamp(obj, Ids.TisMeasurement_ActivationTime),
                GetTimestamp(obj, Ids.TisMeasurement_SavingTime),
                GetUInt32(obj, Ids.TisMeasurement_SequenceNumber),
                GetUInt32(obj, Ids.TisMeasurement_LargeBufferMemorySize),
                GetBlob(obj, Ids.TisMeasurement_Request),
                GetBlob(obj, Ids.TisMeasurement_Trigger),
                GetBlob(obj, Ids.TisMeasurement_Interpretation),
                GetBlob(obj, Ids.TisMeasurement_Result),
                GetBlob(obj, Ids.TisMeasurement_LargeBuffer));
        }

        private static string GetString(PObject obj, uint attribute) =>
            obj.Attributes.TryGetValue(attribute, out var value) && value is ValueWString text ? text.GetValue() : null;

        private static bool? GetBool(PObject obj, uint attribute) =>
            obj.Attributes.TryGetValue(attribute, out var value) && value is ValueBool boolean ? boolean.GetValue() : (bool?)null;

        private static uint? GetUInt32(PObject obj, uint attribute) =>
            obj.Attributes.TryGetValue(attribute, out var value) && value is ValueUDInt unsigned ? unsigned.GetValue() : (uint?)null;

        private static byte[] GetBlob(PObject obj, uint attribute) =>
            obj.Attributes.TryGetValue(attribute, out var value)
                ? ExtractBlob(value)
                : Array.Empty<byte>();

        private static DateTime? GetTimestamp(PObject obj, uint attribute)
        {
            if (!obj.Attributes.TryGetValue(attribute, out var value) || !(value is ValueTimestamp timestamp))
                return null;
            try
            {
                return RuntimeCompatibility.UnixEpoch.AddTicks(checked((long)(timestamp.GetValue() / 100UL))).UtcDateTime;
            }
            catch
            {
                return null;
            }
        }

        private static string CreateExternalPersistentId(string name, DateTime? createdAt, params byte[][] blobs)
        {
            var bytes = new List<byte>(Encoding.UTF8.GetBytes(name));
            bytes.AddRange(BitConverter.GetBytes(createdAt?.Ticks ?? 0));
            foreach (var blob in blobs)
            {
                bytes.AddRange(BitConverter.GetBytes(blob?.Length ?? 0));
                if (blob != null)
                    bytes.AddRange(blob);
            }
            return "external:" + RuntimeCompatibility.ToHexString(RuntimeCompatibility.Sha256(bytes.ToArray()));
        }

        public int Create(S7CommPlusTisTraceRequest request, out uint jobObjectId, out uint subscriptionObjectId)
        {
            jobObjectId = 0;
            subscriptionObjectId = 0;
            if (request == null)
                return S7Consts.errCliInvalidParams;

            LastDiagnostic = "";
            request.LastLifecycleStage = "create TIS trace job";
            var state = new TraceState();
            var job = new PObject
            {
                // TisTraceJob is the concrete trace class. Its normal default is to continue independently of the
                // creating connection, so only the opt-out needs to be sent explicitly.
                ClassId = Ids.TisTraceJob_Class_Rid,
                RelationId = Ids.GetNewRIDOnServer,
                ClassFlags = 0x20
            };
            job.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString(request.JobName));
            job.AddAttribute(Ids.AbstractTisJob_Request, new ValueBlob(0, request.RequestBlob));
            job.AddAttribute(Ids.AbstractTisJob_Trigger, new ValueBlob(0, request.TriggerBlob));
            job.AddAttribute(Ids.AbstractTisJob_LargeBufferMemorySize, new ValueUDInt(request.LargeBufferSizeUsed));
            job.AddAttribute(Ids.TisTraceJob_Interpretation, new ValueBlob(0, request.InterpretationBlob));
            if (request.ClientData != null)
                job.AddAttribute(Ids.TisTraceJob_ClientData, new ValueBlob(0, request.ClientData));
            if (!request.UseContinuingJob)
                job.AddAttribute(Ids.AbstractTisJob_ContinuingJob, new ValueBool(false));

            var createJob = new CreateObjectRequest(ProtocolVersion.V2, 0, true)
            {
                TransportFlags = S7CommPlusProtocolConstants.CreateObjectTransportFlags,
                RequestId = Ids.NativeObjects_theTisSubsystem_Rid,
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
                    var cleanupResult = Cleanup(state);
                    if (cleanupResult != 0)
                    {
                        LastDiagnostic += $"; cleanup failed with {cleanupResult}; the partial PLC trace job may remain installed";
                        request.LastLifecycleStage = LastDiagnostic;
                    }
                }
                return S7Consts.errCliInvalidParams;
            }

            state.JobObjectId = response.ObjectIds[0];
            request.LastLifecycleStage = "create TIS trace subscription";
            result = CreateSubscription(request.JobName, state);
            if (result != 0)
                return FailCreation(request, state, result);

            request.LastLifecycleStage = "add TIS trace notification credit";
            result = _requests.SetVariableAcknowledged(
                state.SubscriptionRefObjectId,
                Ids.TisSubscriptionRef_IncrementNotificationCredit,
                new ValueUSInt(1));
            if (result != 0)
                return FailCreation(request, state, result);

            request.LastLifecycleStage = "activate TIS trace job";
            result = _requests.SetVariableAcknowledged(
                state.JobObjectId,
                Ids.AbstractTisJob_TisJobEnabledConf,
                new ValueBool(true));
            if (result != 0)
                return FailCreation(request, state, result);

            request.LastLifecycleStage = "started";
            jobObjectId = state.JobObjectId;
            subscriptionObjectId = state.SubscriptionObjectId;
            _subscriptions[subscriptionObjectId] = state;
            return 0;
        }

        private int FailCreation(S7CommPlusTisTraceRequest request, TraceState state, int failure)
        {
            var failedStage = request.LastLifecycleStage;
            var jobObjectId = state.JobObjectId;
            var cleanupResult = Cleanup(state);
            LastDiagnostic = $"{failedStage} failed with {failure}";
            if (cleanupResult != 0)
            {
                LastDiagnostic += $"; cleanup of partial trace job 0x{jobObjectId:X8} failed with {cleanupResult}; " +
                    "the PLC object may remain installed";
            }
            else if (jobObjectId != 0)
            {
                LastDiagnostic += $"; partial trace job 0x{jobObjectId:X8} was removed";
            }
            request.LastLifecycleStage = LastDiagnostic;
            return failure;
        }

        public int Attach(uint jobObjectId, string jobName, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            if (jobObjectId == 0)
                return S7Consts.errCliInvalidParams;

            LastDiagnostic = "create TIS trace subscription for existing job";
            var state = new TraceState { JobObjectId = jobObjectId };
            var result = CreateSubscription(
                String.IsNullOrWhiteSpace(jobName) ? $"Trace_{jobObjectId:X8}" : jobName,
                state);
            if (result != 0)
            {
                CleanupSubscription(state);
                return result;
            }

            result = _requests.SetVariableAcknowledged(
                state.SubscriptionRefObjectId,
                Ids.TisSubscriptionRef_IncrementNotificationCredit,
                new ValueUSInt(1));
            if (result != 0)
            {
                CleanupSubscription(state);
                return result;
            }

            subscriptionObjectId = state.SubscriptionObjectId;
            _subscriptions[subscriptionObjectId] = state;
            LastDiagnostic = "attached";
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
                TransportFlags = S7CommPlusProtocolConstants.CreateObjectTransportFlags,
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
            state.SubscriptionObjectId = response.ObjectIds.Count > 0 ? response.ObjectIds[0] : 0;
            state.SubscriptionRefObjectId = response.ObjectIds.Count > 1 ? response.ObjectIds[1] : 0;
            if (response.ReturnValue != 0 || state.SubscriptionObjectId == 0)
                return S7Consts.errCliInvalidParams;
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
                    return _requests.SetVariableAcknowledged(
                        state.SubscriptionRefObjectId,
                        Ids.TisSubscriptionRef_IncrementNotificationCredit,
                        new ValueUSInt(1));
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
            return _requests.SetVariableAcknowledged(state.SubscriptionRefObjectId, Ids.TisSubscriptionRef_IncrementNotificationCredit, new ValueUSInt(1));
        }

        private bool TryPoll(TraceState state, out S7CommPlusTisTraceNotification notification)
        {
            notification = null;
            var rawResult = ReadBlob(state.JobObjectId, Ids.AbstractTisJob_Result);
            var rawLargeBuffer = ReadBlob(state.JobObjectId, Ids.TisTraceJob_LargeBuffer);
            if (!S7CommPlusTisTraceResultStatus.IsCompleted(rawResult))
                return false;
            var fingerprintSource = new byte[8 + rawResult.Length + rawLargeBuffer.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(rawResult.Length), 0, fingerprintSource, 0, 4);
            Buffer.BlockCopy(rawResult, 0, fingerprintSource, 4, rawResult.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(rawLargeBuffer.Length), 0, fingerprintSource, 4 + rawResult.Length, 4);
            Buffer.BlockCopy(rawLargeBuffer, 0, fingerprintSource, 8 + rawResult.Length, rawLargeBuffer.Length);
            var fingerprint = RuntimeCompatibility.Sha256(fingerprintSource);
            if (state.LastPolledDataHash != null && state.LastPolledDataHash.SequenceEqual(fingerprint))
                return false;
            state.LastPolledDataHash = fingerprint;
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
            return CleanupSubscription(state);
        }

        public int DeleteJob(uint jobObjectId)
        {
            if (jobObjectId == 0)
                return S7Consts.errCliInvalidParams;
            return _session.DeleteObject(jobObjectId);
        }

        public int DeleteStoredMeasurement(uint measurementObjectId)
        {
            if (measurementObjectId == 0)
                return S7Consts.errCliInvalidParams;
            return _session.DeleteObject(measurementObjectId);
        }

        public int SetEnabled(uint jobObjectId, bool enabled)
        {
            if (jobObjectId == 0)
                return S7Consts.errCliInvalidParams;
            return _requests.SetVariableAcknowledged(jobObjectId, Ids.AbstractTisJob_TisJobEnabledConf, new ValueBool(enabled));
        }

        private int Cleanup(TraceState state)
        {
            var result = CleanupSubscription(state);
            if (state.JobObjectId != 0)
            {
                var deleteResult = _session.DeleteObject(state.JobObjectId);
                if (result == 0)
                    result = deleteResult;
                state.JobObjectId = 0;
            }
            return result;
        }

        private int CleanupSubscription(TraceState state)
        {
            var result = 0;
            if (state.SubscriptionObjectId != 0)
            {
                result = _requests.SetVariableAcknowledged(
                    state.SubscriptionObjectId,
                    Ids.SubscriptionDisabled,
                    new ValueUSInt(1));
                var deleteResult = _session.DeleteObject(state.SubscriptionObjectId);
                if (result == 0)
                    result = deleteResult;
                state.SubscriptionObjectId = 0;
                state.SubscriptionRefObjectId = 0;
            }
            return result;
        }

        private static bool? ExtractBool(PValue value) => value is ValueBool typed ? typed.GetValue() : null;
        private static byte? ExtractByte(PValue value) => value is ValueUSInt typed ? typed.GetValue() : null;

        internal static byte[] ExtractBlob(PValue value)
        {
            if (value is ValueBlob blob)
                return blob.GetValue() ?? Array.Empty<byte>();
            if (value is ValueBlobSparseArray sparse)
            {
                using var combined = new MemoryStream();
                foreach (var key in sparse.GetValue().Keys.OrderBy(x => x))
                {
                    var data = sparse.GetValue()[key].value;
                    if (data != null && data.Length > 0)
                        combined.Write(data, 0, data.Length);
                }
                return combined.ToArray();
            }
            return Array.Empty<byte>();
        }
    }
}
