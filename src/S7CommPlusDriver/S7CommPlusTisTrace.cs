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
        public bool UseContinuingJob { get; set; }
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
    }

    public sealed class S7CommPlusTisTraceSubscription : S7CommPlusSubscription
    {
        public event EventHandler<S7CommPlusTisTraceNotificationEventArgs> NotificationReceived;

        internal void Publish(S7CommPlusTisTraceNotification notification)
        {
            if (notification != null)
                NotificationReceived?.Invoke(this, new S7CommPlusTisTraceNotificationEventArgs(notification));
        }
    }
}
