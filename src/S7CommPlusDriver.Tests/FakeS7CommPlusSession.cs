using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Alarming;
using S7CommPlusDriver.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace S7CommPlusDriver.Tests
{
    internal sealed class FakeS7CommPlusSession : IS7CommPlusSession
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public int ReadCount { get; private set; }
        public int BrowseVariablesCount { get; private set; }
        public int GetSymbolCommentsCount { get; private set; }
        public int GetCpuCultureInfoCount { get; private set; }
        public int MaxConcurrentReads { get; private set; }
        public int ActiveReads;
        public int CpuOperatingStateWriteCount { get; private set; }
        public int? LastCpuOperatingStateRequest { get; private set; }
        public bool? LastBrowseExpandedPrimitiveArrayElements { get; private set; }
        public List<int> CpuOperatingStateRequests { get; } = new List<int>();
        public int TagSubscriptionCreateCount { get; private set; }
        public int TagSubscriptionWaitCount { get; private set; }
        public int TagSubscriptionDeleteCount { get; private set; }
        public int AlarmSubscriptionCreateCount { get; private set; }
        public int AlarmSubscriptionWaitCount { get; private set; }
        public int AlarmSubscriptionDeleteCount { get; private set; }
        public int TisWatchSubscriptionCreateCount { get; private set; }
        public int TisWatchSubscriptionWaitCount { get; private set; }
        public int TisWatchSubscriptionDeleteCount { get; private set; }
        public int TisTraceSubscriptionCreateCount { get; private set; }
        public int TisTraceSubscriptionWaitCount { get; private set; }
        public int TisTraceSubscriptionDeleteCount { get; private set; }
        public int LastActiveAlarmsLanguageId { get; private set; }
        public int RequestTimeoutMilliseconds { get; private set; }
        public List<int> RequestTimeoutHistory { get; } = new List<int>();
        public string LastErrorDetail { get; set; } = "";
        public List<uint> CreatedTagSubscriptionIds { get; } = new List<uint>();
        public List<uint> CreatedAlarmSubscriptionIds { get; } = new List<uint>();
        public List<uint> CreatedTisWatchSubscriptionIds { get; } = new List<uint>();
        public List<uint> CreatedTisTraceSubscriptionIds { get; } = new List<uint>();
        public List<uint> WaitedTagSubscriptionIds { get; } = new List<uint>();
        public List<uint> WaitedAlarmSubscriptionIds { get; } = new List<uint>();
        public List<uint> WaitedTisWatchSubscriptionIds { get; } = new List<uint>();
        public List<uint> WaitedTisTraceSubscriptionIds { get; } = new List<uint>();
        public List<uint> DeletedTagSubscriptionIds { get; } = new List<uint>();
        public List<uint> DeletedAlarmSubscriptionIds { get; } = new List<uint>();
        public List<uint> DeletedTisWatchSubscriptionIds { get; } = new List<uint>();
        public List<uint> DeletedTisTraceSubscriptionIds { get; } = new List<uint>();
        private uint _nextSubscriptionObjectId = 1;

        public Func<S7CommPlusClientOptions, int>? ConnectHandler { get; set; }
        public Func<int, int>? DisconnectHandler { get; set; }
        public Func<string, string, int>? LegitimateHandler { get; set; }
        public Func<(int Error, List<VarInfo> Variables)>? BrowseVariablesHandler { get; set; }
        public Func<(int Error, List<S7CommPlusBlockInfo> Blocks)>? BrowseBlocksHandler { get; set; }
        public Func<(int Error, string StructureXml)>? PlcStructureXmlHandler { get; set; }
        public Func<uint, (int Error, S7CommPlusClientBlockContent Block)>? GetBlockHandler { get; set; }
        public Func<uint, (int Error, S7CommPlusSymbolCommentCatalog Comments)>? GetSymbolCommentsHandler { get; set; }
        public Func<(int Error, List<S7CommPlusAlarm> Alarms)>? ActiveAlarmsHandler { get; set; }
        public Func<string, PlcTag>? GetTagHandler { get; set; }
        public Func<(int Error, S7CommPlusCpuInfo CpuInfo)>? CpuInfoHandler { get; set; }
        public Func<(int Error, byte[] Capabilities)>? OnlineCapabilitiesHandler { get; set; }
        public Func<(int Error, S7CommPlusCpuState CpuState)>? CpuStateHandler { get; set; }
        public Func<(int Error, S7CommPlusCpuCycleTime CycleTime)>? CpuCycleTimeHandler { get; set; }
        public Func<(int Error, S7CommPlusCpuMemoryUsage MemoryUsage)>? CpuMemoryUsageHandler { get; set; }
        public Func<int, int>? SetCpuOperatingStateHandler { get; set; }
        public Func<(int Error, S7CommPlusCpuCultureInfo CultureInfo)>? CpuCultureInfoHandler { get; set; }
        public Func<IEnumerable<int>, (int Error, S7CommPlusTextListCatalog TextLists)>? TextListsHandler { get; set; }
        public Func<(int Error, S7CommPlusCommunicationResourceSnapshot Resources)>? CommunicationResourcesHandler { get; set; }
        public Func<List<ItemAddress>, (int Error, List<object?> Values, List<ulong> Errors)>? ReadHandler { get; set; }
        public Func<List<ItemAddress>, List<PValue>, (int Error, List<ulong> Errors)>? WriteHandler { get; set; }
        public Func<List<PlcTag>, ushort, short, int>? CreateTagSubscriptionHandler { get; set; }
        public Func<int, short, (int Error, List<Notification> Notifications)>? WaitForTagSubscriptionHandler { get; set; }
        public Func<uint, int, short, (int Error, List<Notification> Notifications)>? WaitForTagSubscriptionByIdHandler { get; set; }
        public Func<int>? DeleteTagSubscriptionHandler { get; set; }
        public Func<uint[], short, int>? CreateAlarmSubscriptionHandler { get; set; }
        public Func<int, short, (int Error, List<Notification> Notifications)>? WaitForAlarmSubscriptionHandler { get; set; }
        public Func<uint, int, short, (int Error, List<Notification> Notifications)>? WaitForAlarmSubscriptionByIdHandler { get; set; }
        public Func<int>? DeleteAlarmSubscriptionHandler { get; set; }
        public Func<S7CommPlusTisWatchRequest, int>? CreateTisWatchSubscriptionHandler { get; set; }
        public Func<int, (int Error, List<S7CommPlusTisWatchNotification> Notifications)>? WaitForTisWatchSubscriptionHandler { get; set; }
        public Func<uint, int, (int Error, List<S7CommPlusTisWatchNotification> Notifications)>? WaitForTisWatchSubscriptionByIdHandler { get; set; }
        public Func<int>? DeleteTisWatchSubscriptionHandler { get; set; }
        public Func<S7CommPlusTisTraceRequest, int>? CreateTisTraceSubscriptionHandler { get; set; }
        public Func<int, (int Error, List<S7CommPlusTisTraceNotification> Notifications)>? WaitForTisTraceSubscriptionHandler { get; set; }
        public Func<uint, int, (int Error, List<S7CommPlusTisTraceNotification> Notifications)>? WaitForTisTraceSubscriptionByIdHandler { get; set; }
        public Func<int>? DeleteTisTraceSubscriptionHandler { get; set; }
        public string LastTisWatchDiagnostic { get; set; } = "";
        public string LastTisTraceDiagnostic { get; set; } = "";
        public string LastAlarmSubscriptionDiagnostic { get; set; } = "";

        public int Connect(S7CommPlusClientOptions options)
        {
            ConnectCount++;
            SetRequestTimeout(options.RequestTimeoutMilliseconds);
            var error = ConnectHandler?.Invoke(options) ?? 0;
            IsConnected = error == 0;
            return error;
        }

        public void SetRequestTimeout(int timeoutMilliseconds)
        {
            RequestTimeoutMilliseconds = timeoutMilliseconds;
            RequestTimeoutHistory.Add(timeoutMilliseconds);
        }

        public int Disconnect(int timeoutMilliseconds)
        {
            DisconnectCount++;
            IsConnected = false;
            return DisconnectHandler?.Invoke(timeoutMilliseconds) ?? 0;
        }

        public int CloseTransport(int timeoutMilliseconds)
        {
            DisconnectCount++;
            IsConnected = false;
            return DisconnectHandler?.Invoke(timeoutMilliseconds) ?? 0;
        }

        public int Legitimate(string password, string username)
        {
            return LegitimateHandler?.Invoke(password, username) ?? 0;
        }

        public int BrowseVariables(bool expandPrimitiveArrayElements, out List<VarInfo> variables)
        {
            BrowseVariablesCount++;
            LastBrowseExpandedPrimitiveArrayElements = expandPrimitiveArrayElements;
            var result = BrowseVariablesHandler?.Invoke() ?? (0, new List<VarInfo>());
            variables = result.Variables;
            return result.Error;
        }

        public int BrowseBlocks(out List<S7CommPlusBlockInfo> blocks)
        {
            var result = BrowseBlocksHandler?.Invoke() ?? (0, new List<S7CommPlusBlockInfo>());
            blocks = result.Blocks;
            return result.Error;
        }

        public int GetPlcStructureXml(out S7CommPlusPlcStructureSnapshot plcStructure)
        {
            var result = PlcStructureXmlHandler?.Invoke() ?? (0, string.Empty);
            plcStructure = PlcStructureXmlParser.CreateSnapshot(result.StructureXml);
            return result.Error;
        }

        public int GetBlockContent(uint relationId, out S7CommPlusClientBlockContent blockContent)
        {
            var result = GetBlockHandler?.Invoke(relationId) ?? (0, new S7CommPlusClientBlockContent(relationId, $"Block_{relationId}", S7CommPlusProgrammingLanguage.SCL, relationId & 0xffff, S7CommPlusBlockType.FC, "", new Dictionary<uint, string>(), "", Array.Empty<string>(), "", "", Array.Empty<string>(), Array.Empty<string>()));
            blockContent = result.Block;
            return result.Error;
        }

        public int GetSymbolComments(uint relationId, out S7CommPlusSymbolCommentCatalog comments)
        {
            GetSymbolCommentsCount++;
            var result = GetSymbolCommentsHandler?.Invoke(relationId)
                ?? (0, S7CommPlusSymbolCommentParser.Parse(relationId, string.Empty, string.Empty, string.Empty));
            comments = result.Comments;
            return result.Error;
        }

        public int GetActiveAlarms(out List<S7CommPlusAlarm> alarmList, int languageId, Func<string, long, int, string>? textListResolver)
        {
            LastActiveAlarmsLanguageId = languageId;
            var result = ActiveAlarmsHandler?.Invoke() ?? (0, new List<S7CommPlusAlarm>());
            alarmList = result.Alarms;
            return result.Error;
        }

        public PlcTag GetPlcTagBySymbol(string symbol)
        {
            return GetTagHandler?.Invoke(symbol) ?? PlcTags.TagFactory(symbol, new ItemAddress("8A0E0001.F"), Softdatatype.S7COMMP_SOFTDATATYPE_INT);
        }

        public int GetCpuInfo(out S7CommPlusCpuInfo cpuInfo)
        {
            var result = CpuInfoHandler?.Invoke() ?? (0, new S7CommPlusCpuInfo { PlcName = "TestCpu" });
            cpuInfo = result.CpuInfo;
            return result.Error;
        }

        public int GetOnlineCapabilities(out byte[] capabilities)
        {
            var result = OnlineCapabilitiesHandler?.Invoke() ?? (0, Array.Empty<byte>());
            capabilities = result.Capabilities;
            return result.Error;
        }

        public int GetCpuState(out S7CommPlusCpuState cpuState)
        {
            var result = CpuStateHandler?.Invoke() ?? (0, new S7CommPlusCpuState(8, S7CommPlusCpuOperatingState.Run));
            cpuState = result.CpuState;
            return result.Error;
        }

        public int GetCpuCycleTime(out S7CommPlusCpuCycleTime cycleTime)
        {
            var result = CpuCycleTimeHandler?.Invoke() ?? (0, new S7CommPlusCpuCycleTime(0, 150, 50.007, 50.012, 50.654));
            cycleTime = result.CycleTime;
            return result.Error;
        }

        public int GetCpuMemoryUsage(out S7CommPlusCpuMemoryUsage memoryUsage)
        {
            var result = CpuMemoryUsageHandler?.Invoke() ?? (0, new S7CommPlusCpuMemoryUsage(new[]
            {
                new S7CommPlusCpuMemoryArea("load", "Load memory", 1000, 120),
                new S7CommPlusCpuMemoryArea("work-code", "Work memory code", 2000, 400)
            }));
            memoryUsage = result.MemoryUsage;
            return result.Error;
        }

        public int SetCpuOperatingState(int operatingStateRequest)
        {
            CpuOperatingStateWriteCount++;
            LastCpuOperatingStateRequest = operatingStateRequest;
            CpuOperatingStateRequests.Add(operatingStateRequest);
            return SetCpuOperatingStateHandler?.Invoke(operatingStateRequest) ?? 0;
        }

        public int GetCpuCultureInfo(out S7CommPlusCpuCultureInfo cultureInfo)
        {
            GetCpuCultureInfoCount++;
            var result = CpuCultureInfoHandler?.Invoke() ?? (0, new S7CommPlusCpuCultureInfo(new[] { 1033 }));
            cultureInfo = result.CultureInfo;
            return result.Error;
        }

        public int GetTextLists(IEnumerable<int> languageIds, out S7CommPlusTextListCatalog textLists)
        {
            var result = TextListsHandler?.Invoke(languageIds) ?? (0, S7CommPlusTextListCatalog.Empty);
            textLists = result.TextLists;
            return result.Error;
        }

        public int GetCommunicationResources(out S7CommPlusCommunicationResourceSnapshot resources)
        {
            var result = CommunicationResourcesHandler?.Invoke() ?? (0, new S7CommPlusCommunicationResourceSnapshot());
            resources = result.Resources;
            return result.Error;
        }

        public int ReadValues(List<ItemAddress> addresslist, out List<object> values, out List<ulong> errors)
        {
            ReadCount++;
            var active = Interlocked.Increment(ref ActiveReads);
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, active);
            try
            {
                var result = ReadHandler?.Invoke(addresslist)
                    ?? (0, new List<object?> { new ValueInt(123) }, new List<ulong> { 0 });
                values = result.Values.Cast<object>().ToList();
                errors = result.Errors;
                if (result.Error != 0)
                {
                    IsConnected = false;
                }
                return result.Error;
            }
            finally
            {
                Interlocked.Decrement(ref ActiveReads);
            }
        }

        public int WriteValues(List<ItemAddress> addresslist, List<PValue> values, out List<ulong> errors)
        {
            var result = WriteHandler?.Invoke(addresslist, values)
                ?? (0, new List<ulong>(new ulong[addresslist.Count]));
            errors = result.Errors;
            return result.Error;
        }

        public int CreateTagSubscription(List<PlcTag> tags, ushort cycleTimeMilliseconds, short initialCreditLimit, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            TagSubscriptionCreateCount++;
            var result = CreateTagSubscriptionHandler?.Invoke(tags, cycleTimeMilliseconds, initialCreditLimit) ?? 0;
            if (result == 0)
            {
                subscriptionObjectId = _nextSubscriptionObjectId++;
                CreatedTagSubscriptionIds.Add(subscriptionObjectId);
            }
            return result;
        }

        public int WaitForTagSubscriptionNotifications(uint subscriptionObjectId, int timeoutMilliseconds, short creditLimitStep, out List<Notification> notifications)
        {
            TagSubscriptionWaitCount++;
            WaitedTagSubscriptionIds.Add(subscriptionObjectId);
            var result = WaitForTagSubscriptionByIdHandler?.Invoke(subscriptionObjectId, timeoutMilliseconds, creditLimitStep)
                ?? WaitForTagSubscriptionHandler?.Invoke(timeoutMilliseconds, creditLimitStep)
                ?? (S7Consts.errCliJobTimeout, new List<Notification>());
            notifications = result.Notifications;
            return result.Error;
        }

        public int DeleteTagSubscription(uint subscriptionObjectId)
        {
            TagSubscriptionDeleteCount++;
            DeletedTagSubscriptionIds.Add(subscriptionObjectId);
            return DeleteTagSubscriptionHandler?.Invoke() ?? 0;
        }

        public int CreateAlarmSubscription(uint[] languageIds, short initialCreditLimit, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            AlarmSubscriptionCreateCount++;
            var result = CreateAlarmSubscriptionHandler?.Invoke(languageIds, initialCreditLimit) ?? 0;
            if (result == 0)
            {
                subscriptionObjectId = _nextSubscriptionObjectId++;
                CreatedAlarmSubscriptionIds.Add(subscriptionObjectId);
            }
            return result;
        }

        public int WaitForAlarmNotifications(uint subscriptionObjectId, int timeoutMilliseconds, short creditLimitStep, out List<Notification> notifications)
        {
            AlarmSubscriptionWaitCount++;
            WaitedAlarmSubscriptionIds.Add(subscriptionObjectId);
            var result = WaitForAlarmSubscriptionByIdHandler?.Invoke(subscriptionObjectId, timeoutMilliseconds, creditLimitStep)
                ?? WaitForAlarmSubscriptionHandler?.Invoke(timeoutMilliseconds, creditLimitStep)
                ?? (S7Consts.errCliJobTimeout, new List<Notification>());
            notifications = result.Notifications;
            return result.Error;
        }

        public int DeleteAlarmSubscription(uint subscriptionObjectId)
        {
            AlarmSubscriptionDeleteCount++;
            DeletedAlarmSubscriptionIds.Add(subscriptionObjectId);
            return DeleteAlarmSubscriptionHandler?.Invoke() ?? 0;
        }

        public int CreateTisWatchSubscription(S7CommPlusTisWatchRequest request, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            TisWatchSubscriptionCreateCount++;
            var result = CreateTisWatchSubscriptionHandler?.Invoke(request) ?? 0;
            if (result == 0)
            {
                subscriptionObjectId = _nextSubscriptionObjectId++;
                CreatedTisWatchSubscriptionIds.Add(subscriptionObjectId);
            }
            return result;
        }

        public int WaitForTisWatchNotifications(uint subscriptionObjectId, int timeoutMilliseconds, out List<S7CommPlusTisWatchNotification> notifications)
        {
            TisWatchSubscriptionWaitCount++;
            WaitedTisWatchSubscriptionIds.Add(subscriptionObjectId);
            var result = WaitForTisWatchSubscriptionByIdHandler?.Invoke(subscriptionObjectId, timeoutMilliseconds)
                ?? WaitForTisWatchSubscriptionHandler?.Invoke(timeoutMilliseconds)
                ?? (S7Consts.errCliJobTimeout, new List<S7CommPlusTisWatchNotification>());
            notifications = result.Notifications;
            return result.Error;
        }

        public int DeleteTisWatchSubscription(uint subscriptionObjectId)
        {
            TisWatchSubscriptionDeleteCount++;
            DeletedTisWatchSubscriptionIds.Add(subscriptionObjectId);
            return DeleteTisWatchSubscriptionHandler?.Invoke() ?? 0;
        }

        public int CreateTisTraceSubscription(S7CommPlusTisTraceRequest request, out uint subscriptionObjectId)
        {
            subscriptionObjectId = 0;
            TisTraceSubscriptionCreateCount++;
            var result = CreateTisTraceSubscriptionHandler?.Invoke(request) ?? 0;
            if (result == 0)
            {
                subscriptionObjectId = _nextSubscriptionObjectId++;
                CreatedTisTraceSubscriptionIds.Add(subscriptionObjectId);
            }
            return result;
        }

        public int WaitForTisTraceNotifications(uint subscriptionObjectId, int timeoutMilliseconds, out List<S7CommPlusTisTraceNotification> notifications)
        {
            TisTraceSubscriptionWaitCount++;
            WaitedTisTraceSubscriptionIds.Add(subscriptionObjectId);
            var result = WaitForTisTraceSubscriptionByIdHandler?.Invoke(subscriptionObjectId, timeoutMilliseconds)
                ?? WaitForTisTraceSubscriptionHandler?.Invoke(timeoutMilliseconds)
                ?? (S7Consts.errCliJobTimeout, new List<S7CommPlusTisTraceNotification>());
            notifications = result.Notifications;
            return result.Error;
        }

        public int DeleteTisTraceSubscription(uint subscriptionObjectId)
        {
            TisTraceSubscriptionDeleteCount++;
            DeletedTisTraceSubscriptionIds.Add(subscriptionObjectId);
            return DeleteTisTraceSubscriptionHandler?.Invoke() ?? 0;
        }
    }
}
