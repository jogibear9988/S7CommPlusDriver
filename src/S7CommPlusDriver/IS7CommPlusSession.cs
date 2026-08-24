using S7CommPlusDriver.Alarming;
using S7CommPlusDriver.ClientApi;
using System;
using System.Collections.Generic;

namespace S7CommPlusDriver
{
    internal interface IS7CommPlusSession
    {
        bool IsConnected { get; }
        string LastErrorDetail { get; }
        int RequestTimeoutMilliseconds { get; }
        void SetRequestTimeout(int timeoutMilliseconds);
        int Connect(S7CommPlusClientOptions options);
        int Disconnect(int timeoutMilliseconds);
        int CloseTransport(int timeoutMilliseconds);
        int Legitimate(string password, string username);
        int BrowseVariables(bool expandPrimitiveArrayElements, out List<VarInfo> variables);
        int BrowseBlocks(out List<S7CommPlusBlockInfo> blocks);
        int GetPlcStructureXml(out S7CommPlusPlcStructureSnapshot plcStructure);
        int GetBlockContent(uint relationId, out S7CommPlusClientBlockContent blockContent);

        /// <summary>
        /// Retrieves the engineering comment catalog for one browsed DB or absolute I/Q/M area.
        /// </summary>
        /// <param name="relationId">The PLC object relation ID that owns the declarations.</param>
        /// <param name="comments">Receives a catalog that resolves the session's browsed <see cref="VarInfo"/> instances.</param>
        /// <returns>A native driver error code, or zero when retrieval and parsing succeed.</returns>
        int GetSymbolComments(uint relationId, out S7CommPlusSymbolCommentCatalog comments);
        PlcTag GetPlcTagBySymbol(string symbol);
        int GetCpuInfo(out S7CommPlusCpuInfo cpuInfo);
        int GetOnlineCapabilities(out byte[] capabilities);
        int GetCpuState(out S7CommPlusCpuState cpuState);
        int GetCpuCycleTime(out S7CommPlusCpuCycleTime cycleTime);
        int GetCpuMemoryUsage(out S7CommPlusCpuMemoryUsage memoryUsage);
        int SetCpuOperatingState(int operatingStateRequest);
        int GetCpuCultureInfo(out S7CommPlusCpuCultureInfo cultureInfo);
        int GetTextLists(IEnumerable<int> languageIds, out S7CommPlusTextListCatalog textLists);
        int GetCommunicationResources(out S7CommPlusCommunicationResourceSnapshot resources);
        int GetActiveAlarms(out List<S7CommPlusAlarm> alarmList, int languageId, Func<string, long, int, string> textListResolver);
        int ReadValues(List<ItemAddress> addresses, out List<object> values, out List<ulong> errors);
        int WriteValues(List<ItemAddress> addresses, List<PValue> values, out List<ulong> errors);
        int CreateTagSubscription(List<PlcTag> tags, ushort cycleTimeMilliseconds, short initialCreditLimit, out uint subscriptionObjectId);
        int WaitForTagSubscriptionNotifications(uint subscriptionObjectId, int timeoutMilliseconds, short creditLimitStep, out List<Notification> notifications);
        int DeleteTagSubscription(uint subscriptionObjectId);
        int CreateAlarmSubscription(uint[] languageIds, short initialCreditLimit, out uint subscriptionObjectId);
        int WaitForAlarmNotifications(uint subscriptionObjectId, int timeoutMilliseconds, short creditLimitStep, out List<Notification> notifications);
        int DeleteAlarmSubscription(uint subscriptionObjectId);
        int CreateTisWatchSubscription(S7CommPlusTisWatchRequest request, out uint subscriptionObjectId);
        int WaitForTisWatchNotifications(uint subscriptionObjectId, int timeoutMilliseconds, out List<S7CommPlusTisWatchNotification> notifications);
        string LastTisWatchDiagnostic { get; }
        string LastAlarmSubscriptionDiagnostic { get; }
        int DeleteTisWatchSubscription(uint subscriptionObjectId);
        int CreateTisTraceSubscription(S7CommPlusTisTraceRequest request, out uint subscriptionObjectId);
        int WaitForTisTraceNotifications(uint subscriptionObjectId, int timeoutMilliseconds, out List<S7CommPlusTisTraceNotification> notifications);
        string LastTisTraceDiagnostic { get; }
        int DeleteTisTraceSubscription(uint subscriptionObjectId);
    }
}
