using System;

namespace S7CommPlusDriver
{
    /// <summary>Controls which raw PLC-side trace attributes are downloaded during discovery.</summary>
    public sealed class S7CommPlusTraceQueryOptions
    {
        /// <summary>
        /// Downloads the current result header and sample buffer for every installed trace. Disabled by default because
        /// these buffers can be large.
        /// </summary>
        public bool IncludeResultData { get; set; }
    }

    /// <summary>Low-level state inferred from the PLC's enable flag and raw result header.</summary>
    public enum S7CommPlusTraceState
    {
        Unknown,
        Inactive,
        WaitingForTrigger,
        Recording,
        Completed,
        Saving,
        Faulted,
        Deleted
    }

    /// <summary>Stable reference to a raw PLC trace job.</summary>
    public sealed class S7CommPlusTraceReference : IEquatable<S7CommPlusTraceReference>
    {
        public S7CommPlusTraceReference(string persistentId, string name, uint objectId, DateTime? creationTimestamp = null)
        {
            if (String.IsNullOrWhiteSpace(persistentId))
                throw new ArgumentException("Persistent trace ID is required.", nameof(persistentId));
            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Trace name is required.", nameof(name));
            PersistentId = persistentId;
            Name = name;
            ObjectId = objectId;
            CreationTimestamp = creationTimestamp;
        }

        public string PersistentId { get; }
        public string Name { get; }
        public uint ObjectId { get; }
        public DateTime? CreationTimestamp { get; }

        public bool Equals(S7CommPlusTraceReference other) =>
            other != null && String.Equals(PersistentId, other.PersistentId, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as S7CommPlusTraceReference);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(PersistentId);
        public override string ToString() => $"{Name} ({PersistentId})";
    }

    /// <summary>
    /// Raw point-in-time snapshot of an installed TIS trace job. Payload and client-data blobs are intentionally not
    /// interpreted by this package.
    /// </summary>
    public sealed class S7CommPlusInstalledTrace
    {
        public S7CommPlusInstalledTrace(
            S7CommPlusTraceReference reference,
            S7CommPlusTraceState state,
            byte[] rawRequest,
            byte[] rawTrigger,
            byte[] rawInterpretation,
            byte[] rawResult = null,
            byte[] rawLargeBuffer = null,
            byte[] rawClientData = null,
            bool? enabled = null,
            bool resultDataIncluded = false,
            uint? allocatedBufferSize = null,
            uint classId = 0,
            uint classFlags = 0,
            uint attributeId = 0,
            bool? continuingJob = null)
        {
            Reference = reference ?? throw new ArgumentNullException(nameof(reference));
            State = state;
            RawRequest = rawRequest ?? Array.Empty<byte>();
            RawTrigger = rawTrigger ?? Array.Empty<byte>();
            RawInterpretation = rawInterpretation ?? Array.Empty<byte>();
            RawResult = rawResult ?? Array.Empty<byte>();
            RawLargeBuffer = rawLargeBuffer ?? Array.Empty<byte>();
            RawClientData = rawClientData ?? Array.Empty<byte>();
            Enabled = enabled;
            ResultDataIncluded = resultDataIncluded;
            AllocatedBufferSize = allocatedBufferSize;
            ClassId = classId;
            ClassFlags = classFlags;
            AttributeId = attributeId;
            ContinuingJob = continuingJob;
        }

        public S7CommPlusTraceReference Reference { get; }
        public S7CommPlusTraceState State { get; }
        public byte[] RawRequest { get; }
        public byte[] RawTrigger { get; }
        public byte[] RawInterpretation { get; }
        public byte[] RawResult { get; }
        public byte[] RawLargeBuffer { get; }
        public byte[] RawClientData { get; }
        public bool? Enabled { get; }
        public bool ResultDataIncluded { get; }
        public uint? AllocatedBufferSize { get; }
        public uint ClassId { get; }
        public uint ClassFlags { get; }
        public uint AttributeId { get; }
        public bool? ContinuingJob { get; }
        public bool HasResultData => RawResult.Length != 0 || RawLargeBuffer.Length != 0;
    }

    /// <summary>Raw memory-card trace measurement discovered on the PLC.</summary>
    public sealed class S7CommPlusStoredTraceMeasurement
    {
        public S7CommPlusStoredTraceMeasurement(
            uint objectId,
            string name,
            DateTime? activationTime,
            DateTime? savingTime,
            uint? sequenceNumber,
            uint? allocatedBufferSize,
            byte[] request,
            byte[] trigger,
            byte[] interpretation,
            byte[] result,
            byte[] largeBuffer)
        {
            ObjectId = objectId;
            Name = name ?? String.Empty;
            ActivationTime = activationTime;
            SavingTime = savingTime;
            SequenceNumber = sequenceNumber;
            AllocatedBufferSize = allocatedBufferSize;
            Request = request ?? Array.Empty<byte>();
            Trigger = trigger ?? Array.Empty<byte>();
            Interpretation = interpretation ?? Array.Empty<byte>();
            Result = result ?? Array.Empty<byte>();
            LargeBuffer = largeBuffer ?? Array.Empty<byte>();
        }

        public uint ObjectId { get; }
        public string Name { get; }
        public DateTime? ActivationTime { get; }
        public DateTime? SavingTime { get; }
        public uint? SequenceNumber { get; }
        public uint? AllocatedBufferSize { get; }
        public byte[] Request { get; }
        public byte[] Trigger { get; }
        public byte[] Interpretation { get; }
        public byte[] Result { get; }
        public byte[] LargeBuffer { get; }
    }
}
