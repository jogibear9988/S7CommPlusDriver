using S7CommPlusDriver.Alarming;
using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.IO;

namespace S7CommPlusDriver
{
    internal partial class S7CommPlusProtocolSession : IS7CommPlusSession
    {
        private IS7CommPlusProtocolSession _protocolSession;
        private S7CommPlusTagSubscriptionService _tagSubscriptions;
        private S7CommPlusAlarmSubscriptionService _alarmSubscriptions;
        private S7CommPlusTisWatchSubscriptionService _tisWatchSubscriptions;
        private S7CommPlusTisTraceSubscriptionService _tisTraceSubscriptions;
        private S7CommPlusAlarmBrowseService _alarmBrowser;
        private S7CommPlusMetadataService _metadata;
        private S7CommPlusTextListService _textLists;

        private IS7CommPlusProtocolSession ProtocolSession => _protocolSession ??= new ProtocolSessionAdapter(this);
        private S7CommPlusTagSubscriptionService TagSubscriptions => _tagSubscriptions ??= new S7CommPlusTagSubscriptionService(ProtocolSession);
        private S7CommPlusAlarmSubscriptionService AlarmSubscriptions => _alarmSubscriptions ??= new S7CommPlusAlarmSubscriptionService(ProtocolSession);
        private S7CommPlusTisWatchSubscriptionService TisWatchSubscriptions => _tisWatchSubscriptions ??= new S7CommPlusTisWatchSubscriptionService(ProtocolSession);
        private S7CommPlusTisTraceSubscriptionService TisTraceSubscriptions => _tisTraceSubscriptions ??= new S7CommPlusTisTraceSubscriptionService(ProtocolSession);
        private S7CommPlusAlarmBrowseService AlarmBrowser => _alarmBrowser ??= new S7CommPlusAlarmBrowseService(ProtocolSession);
        private S7CommPlusMetadataService Metadata => _metadata ??= new S7CommPlusMetadataService(ProtocolSession);
        private S7CommPlusTextListService TextLists => _textLists ??= new S7CommPlusTextListService(ProtocolSession, Metadata);

        int IS7CommPlusSession.Connect(S7CommPlusClientOptions options)
        {
            return Connect(options);
        }

        string IS7CommPlusSession.LastErrorDetail => m_LastErrorDetail;

        int IS7CommPlusSession.RequestTimeoutMilliseconds => m_ReadTimeout;

        void IS7CommPlusSession.SetRequestTimeout(int timeoutMilliseconds)
        {
            ApplyRequestTimeout(timeoutMilliseconds);
        }

        int IS7CommPlusSession.Disconnect(int timeoutMilliseconds)
        {
            return TryDisconnect(timeoutMilliseconds);
        }

        int IS7CommPlusSession.CloseTransport(int timeoutMilliseconds)
        {
            return CloseTransport(timeoutMilliseconds);
        }

        int IS7CommPlusSession.Legitimate(string password, string username)
        {
            return Legitimate(password, username);
        }

        int IS7CommPlusSession.BrowseVariables(bool expandPrimitiveArrayElements, out List<VarInfo> variables)
        {
            return Browse(expandPrimitiveArrayElements, out variables);
        }

        int IS7CommPlusSession.BrowseBlocks(out List<S7CommPlusBlockInfo> blocks)
        {
            return Metadata.BrowseBlocks(out blocks);
        }

        int IS7CommPlusSession.GetPlcStructureXml(out S7CommPlusPlcStructureSnapshot plcStructure)
        {
            return Metadata.GetPlcStructureXml(out plcStructure);
        }

        int IS7CommPlusSession.GetBlockContent(uint relationId, out S7CommPlusClientBlockContent blockContent)
        {
            return Metadata.GetBlockContent(relationId, out blockContent);
        }

        /// <summary>
        /// Retrieves and parses only the engineering comment metadata for one block or absolute area.
        /// </summary>
        /// <param name="relationId">The DB, I, Q, or M relation ID.</param>
        /// <param name="comments">Receives the multilingual declaration comments.</param>
        /// <returns>A native driver error code, or zero on success.</returns>
        int IS7CommPlusSession.GetSymbolComments(uint relationId, out S7CommPlusSymbolCommentCatalog comments)
        {
            return Metadata.GetSymbolComments(relationId, out comments);
        }

        PlcTag IS7CommPlusSession.GetPlcTagBySymbol(string symbol)
        {
            return getPlcTagBySymbol(symbol);
        }

        int IS7CommPlusSession.GetCpuInfo(out S7CommPlusCpuInfo cpuInfo)
        {
            return Metadata.GetCpuInfo(out cpuInfo);
        }

        int IS7CommPlusSession.GetOnlineCapabilities(out byte[] capabilities)
        {
            capabilities = null;
            var requests = new S7CommPlusProtocolRequests(ProtocolSession);
            var result = requests.GetVariable(50u, 4196u, out var value);
            if (result != 0)
            {
                return result;
            }

            if (value is ValueBlob blob)
            {
                var blobValue = blob.GetValue();
                capabilities = blobValue == null
                    ? Array.Empty<byte>()
                    : (byte[])blobValue.Clone();
                return 0;
            }

            if (value is ValueByteArray byteArray)
            {
                capabilities = (byte[])byteArray.GetValue().Clone();
                return 0;
            }

            return S7Consts.errIsoInvalidPDU;
        }

        int IS7CommPlusSession.GetCpuState(out S7CommPlusCpuState cpuState)
        {
            return Metadata.GetCpuState(out cpuState);
        }

        int IS7CommPlusSession.GetCpuCycleTime(out S7CommPlusCpuCycleTime cycleTime)
        {
            return Metadata.GetCpuCycleTime(out cycleTime);
        }

        int IS7CommPlusSession.GetCpuMemoryUsage(out S7CommPlusCpuMemoryUsage memoryUsage)
        {
            return Metadata.GetCpuMemoryUsage(out memoryUsage);
        }

        int IS7CommPlusSession.SetCpuOperatingState(int operatingStateRequest)
        {
            return SetPlcOperatingState(operatingStateRequest);
        }

        int IS7CommPlusSession.GetCpuCultureInfo(out S7CommPlusCpuCultureInfo cultureInfo)
        {
            return Metadata.GetCpuCultureInfo(out cultureInfo);
        }

        int IS7CommPlusSession.GetTextLists(IEnumerable<int> languageIds, out S7CommPlusTextListCatalog textLists)
        {
            return TextLists.GetTextLists(languageIds, out textLists);
        }

        int IS7CommPlusSession.GetCommunicationResources(out S7CommPlusCommunicationResourceSnapshot resources)
        {
            return GetCommunicationResources(out resources);
        }

        int IS7CommPlusSession.GetActiveAlarms(out List<S7CommPlusAlarm> alarmList, int languageId, Func<string, long, int, string> textListResolver)
        {
            return AlarmBrowser.GetActiveAlarms(out alarmList, languageId, textListResolver);
        }

        int IS7CommPlusSession.ReadValues(List<ItemAddress> addresses, out List<object> values, out List<ulong> errors)
        {
            return ReadValues(addresses, out values, out errors);
        }

        int IS7CommPlusSession.WriteValues(List<ItemAddress> addresses, List<PValue> values, out List<ulong> errors)
        {
            return WriteValues(addresses, values, out errors);
        }

        int IS7CommPlusSession.CreateTagSubscription(List<PlcTag> tags, ushort cycleTimeMilliseconds, short initialCreditLimit, out uint subscriptionObjectId)
        {
            return TagSubscriptions.Create(tags, cycleTimeMilliseconds, initialCreditLimit, out subscriptionObjectId);
        }

        int IS7CommPlusSession.WaitForTagSubscriptionNotifications(uint subscriptionObjectId, int timeoutMilliseconds, short creditLimitStep, out List<Notification> notifications)
        {
            return TagSubscriptions.WaitForNotifications(subscriptionObjectId, timeoutMilliseconds, creditLimitStep, out notifications);
        }

        int IS7CommPlusSession.DeleteTagSubscription(uint subscriptionObjectId)
        {
            return TagSubscriptions.Delete(subscriptionObjectId);
        }

        int IS7CommPlusSession.CreateAlarmSubscription(uint[] languageIds, short initialCreditLimit, out uint subscriptionObjectId)
        {
            return AlarmSubscriptions.Create(languageIds, initialCreditLimit, out subscriptionObjectId);
        }

        int IS7CommPlusSession.WaitForAlarmNotifications(uint subscriptionObjectId, int timeoutMilliseconds, short creditLimitStep, out List<Notification> notifications)
        {
            return AlarmSubscriptions.WaitForNotifications(subscriptionObjectId, timeoutMilliseconds, creditLimitStep, out notifications);
        }

        int IS7CommPlusSession.DeleteAlarmSubscription(uint subscriptionObjectId)
        {
            return AlarmSubscriptions.Delete(subscriptionObjectId);
        }

        int IS7CommPlusSession.CreateTisWatchSubscription(S7CommPlusTisWatchRequest request, out uint subscriptionObjectId)
        {
            return TisWatchSubscriptions.Create(request, out subscriptionObjectId);
        }

        int IS7CommPlusSession.WaitForTisWatchNotifications(uint subscriptionObjectId, int timeoutMilliseconds, out List<S7CommPlusTisWatchNotification> notifications)
        {
            return TisWatchSubscriptions.WaitForNotifications(subscriptionObjectId, timeoutMilliseconds, out notifications);
        }

        string IS7CommPlusSession.LastTisWatchDiagnostic => TisWatchSubscriptions.LastDiagnostic;

        string IS7CommPlusSession.LastAlarmSubscriptionDiagnostic => AlarmSubscriptions.LastDiagnostic;

        int IS7CommPlusSession.DeleteTisWatchSubscription(uint subscriptionObjectId)
        {
            return TisWatchSubscriptions.Delete(subscriptionObjectId);
        }

        int IS7CommPlusSession.CreateTisTraceSubscription(S7CommPlusTisTraceRequest request, out uint subscriptionObjectId)
        {
            return TisTraceSubscriptions.Create(request, out subscriptionObjectId);
        }

        int IS7CommPlusSession.WaitForTisTraceNotifications(uint subscriptionObjectId, int timeoutMilliseconds, out List<S7CommPlusTisTraceNotification> notifications)
        {
            return TisTraceSubscriptions.WaitForNotifications(subscriptionObjectId, timeoutMilliseconds, out notifications);
        }

        string IS7CommPlusSession.LastTisTraceDiagnostic => TisTraceSubscriptions.LastDiagnostic;

        int IS7CommPlusSession.DeleteTisTraceSubscription(uint subscriptionObjectId)
        {
            return TisTraceSubscriptions.Delete(subscriptionObjectId);
        }

        private sealed class ProtocolSessionAdapter : IS7CommPlusProtocolSession
        {
            private readonly S7CommPlusProtocolSession _connection;

            public ProtocolSessionAdapter(S7CommPlusProtocolSession connection)
            {
                _connection = connection;
            }

            public int LastError
            {
                get => _connection.m_LastError;
                set => _connection.m_LastError = value;
            }

            public int ReadTimeout => _connection.m_ReadTimeout;
            public uint SessionId => _connection.m_SessionId;
            public uint SessionId2 => _connection.m_SessionId2;
            public MemoryStream ReceivedPdu => _connection.m_ReceivedPDU;

            public int SendFunction(IS7pRequest request)
            {
                return _connection.SendS7plusFunctionObjectSerialized(request);
            }

            public int SendFunctionAndWait(IS7pRequest request)
            {
                return _connection.SendS7plusFunctionObjectAndWait(request, _connection.m_ReadTimeout);
            }

            public void WaitForPdu(int timeoutMilliseconds)
            {
                _connection.WaitForNewS7plusReceived(timeoutMilliseconds);
            }

            public int WaitForNotification(uint subscriptionObjectId, int timeoutMilliseconds, out Notification notification)
            {
                return _connection.WaitForNotification(subscriptionObjectId, timeoutMilliseconds, out notification);
            }

            public int CheckResponse(IS7pRequest request, IS7pResponse response)
            {
                return _connection.checkResponseWithIntegrity(request, response);
            }

            public int DeleteObject(uint objectId)
            {
                return _connection.DeleteObject(objectId);
            }

            public void DisconnectTransport()
            {
                _connection.m_client?.Disconnect();
            }

            public void ClearSessionIds()
            {
                _connection.m_SessionId = 0;
                _connection.m_SessionId2 = 0;
            }
        }
    }
}
