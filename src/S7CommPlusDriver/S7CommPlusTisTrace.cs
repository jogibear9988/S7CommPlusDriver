using System;
using System.Collections.Generic;

namespace S7CommPlusDriver
{
    public sealed class S7CommPlusTisTraceRequest
    {
        public byte[] RequestBlob { get; set; } = Array.Empty<byte>();
        public byte[] TriggerBlob { get; set; } = Array.Empty<byte>();
        public byte[] InterpretationBlob { get; set; } = Array.Empty<byte>();
        public uint LargeBufferSizeUsed { get; set; }
        /// <summary>Requests that the PLC keep the raw trace job running after this online connection closes.</summary>
        public bool UseContinuingJob { get; set; } = true;
        public byte[] ClientData { get; set; }
        public string JobName { get; set; } = "S7pDriver_TisTraceJob";
        public string LastLifecycleStage { get; internal set; } = "";

        internal S7CommPlusTisTraceRequest Clone()
        {
            return new S7CommPlusTisTraceRequest
            {
                RequestBlob = (byte[])(RequestBlob ?? Array.Empty<byte>()).Clone(),
                TriggerBlob = (byte[])(TriggerBlob ?? Array.Empty<byte>()).Clone(),
                InterpretationBlob = (byte[])(InterpretationBlob ?? Array.Empty<byte>()).Clone(),
                LargeBufferSizeUsed = LargeBufferSizeUsed,
                UseContinuingJob = UseContinuingJob,
                ClientData = ClientData == null ? null : (byte[])ClientData.Clone(),
                JobName = String.IsNullOrWhiteSpace(JobName) ? "S7pDriver_TisTraceJob" : JobName,
                LastLifecycleStage = LastLifecycleStage ?? ""
            };
        }

        internal void Validate()
        {
            if (RequestBlob == null || RequestBlob.Length == 0)
                throw new ArgumentException("A TIS trace request blob (2693) is required.", nameof(RequestBlob));
            if (TriggerBlob == null || TriggerBlob.Length == 0)
                throw new ArgumentException("A TIS trace trigger blob (2694) is required.", nameof(TriggerBlob));
            if (InterpretationBlob == null || InterpretationBlob.Length == 0)
                throw new ArgumentException("A TIS trace interpretation blob (8140) is required.", nameof(InterpretationBlob));
        }
    }

    public sealed class S7CommPlusTisTraceNotificationEventArgs : EventArgs
    {
        public S7CommPlusTisTraceNotificationEventArgs(S7CommPlusTisTraceNotification notification)
        {
            Notification = notification ?? throw new ArgumentNullException(nameof(notification));
        }

        public S7CommPlusTisTraceNotification Notification { get; }
    }

    public sealed class S7CommPlusTisTraceNotification
    {
        public S7CommPlusTisTraceNotification(
            DateTime timestamp,
            uint sequenceNumber,
            byte creditTick,
            bool? jobEnabled,
            byte? notificationCredit,
            byte[] rawResult,
            byte[] rawLargeBuffer)
        {
            Timestamp = timestamp;
            SequenceNumber = sequenceNumber;
            CreditTick = creditTick;
            JobEnabled = jobEnabled;
            NotificationCredit = notificationCredit;
            RawResult = rawResult ?? Array.Empty<byte>();
            RawLargeBuffer = rawLargeBuffer ?? Array.Empty<byte>();
        }

        public DateTime Timestamp { get; }
        public uint SequenceNumber { get; }
        public byte CreditTick { get; }
        public bool? JobEnabled { get; }
        public byte? NotificationCredit { get; }
        public byte[] RawResult { get; }
        public byte[] RawLargeBuffer { get; }
        /// <summary>
        /// Gets whether the PLC result header reports that the trigger fired and recording completed.
        /// </summary>
        public bool IsCompleted => S7CommPlusTisTraceResultStatus.IsCompleted(RawResult);
        /// <summary>
        /// Gets whether result attributes contain bytes. A waiting trace can already expose its ring/pretrigger buffer,
        /// so this property does not by itself indicate completion; use <see cref="IsCompleted"/>.
        /// </summary>
        public bool HasResultData => RawResult.Length != 0 || RawLargeBuffer.Length != 0;
    }

    public sealed class S7CommPlusTisTraceSubscription : S7CommPlusSubscription
    {
        private readonly object _referenceLock = new object();
        private readonly object _resultLock = new object();
        private S7CommPlusTraceReference _traceReference;
        private S7CommPlusTisTraceNotification _latestResult;

        internal S7CommPlusTisTraceSubscription(S7CommPlusTraceReference traceReference)
        {
            _traceReference = traceReference ?? throw new ArgumentNullException(nameof(traceReference));
        }

        /// <summary>
        /// Identifies the PLC-side trace job. The reference remains usable for explicit trace management after this
        /// local notification subscription has been disposed.
        /// </summary>
        public S7CommPlusTraceReference TraceReference
        {
            get
            {
                lock (_referenceLock)
                    return _traceReference;
            }
        }

        public event EventHandler<S7CommPlusTisTraceNotificationEventArgs> NotificationReceived;

        /// <summary>Gets the latest notification containing trace result data, if one has been observed.</summary>
        public S7CommPlusTisTraceNotification LatestResult
        {
            get
            {
                lock (_resultLock)
                    return _latestResult;
            }
        }

        internal void Publish(S7CommPlusTisTraceNotification notification)
        {
            if (notification == null)
                return;
            if (notification.IsCompleted)
            {
                lock (_resultLock)
                    _latestResult = notification;
            }
            NotificationReceived?.Invoke(this, new S7CommPlusTisTraceNotificationEventArgs(notification));
        }


        internal void UpdateTraceReference(S7CommPlusTraceReference traceReference)
        {
            if (traceReference == null)
                throw new ArgumentNullException(nameof(traceReference));
            lock (_referenceLock)
                _traceReference = traceReference;
        }
    }

    internal static class S7CommPlusTisTraceResultStatus
    {
        private const int ValidOffset = 16;
        private const int TriggeredOffset = 17;
        private const int CompletedOffset = 18;

        public static bool IsCompleted(byte[] rawResult)
        {
            // TIS trace result headers observed on S7-1200/1500 use 0x0c followed by three status bytes at
            // offsets 16..18. Buffers are allocated and populated before a trigger; only a result with all three
            // flags set represents a triggered, completed measurement. Unknown/truncated formats are conservative.
            return rawResult != null
                && rawResult.Length > CompletedOffset
                && rawResult[0] == 0x0c
                && rawResult[ValidOffset] != 0
                && rawResult[TriggeredOffset] != 0
                && rawResult[CompletedOffset] != 0;
        }
    }
}
