using S7CommPlusDriver.ClientApi;
using System.Collections.Generic;

namespace S7CommPlusDriver
{
    public readonly struct S7CommPlusReadResult
    {
        public S7CommPlusReadResult(ItemAddress address, object value, ulong itemError)
        {
            Address = address;
            Value = value;
            ItemError = itemError;
        }

        public ItemAddress Address { get; }
        public object Value { get; }
        public ulong ItemError { get; }
        public bool IsSuccess => ItemError == 0;
    }

    public readonly struct S7CommPlusTagReadResult
    {
        public S7CommPlusTagReadResult(PlcTag tag, ulong itemError)
        {
            Tag = tag;
            ItemError = itemError;
        }

        public PlcTag Tag { get; }
        public ulong ItemError { get; }
        public bool IsSuccess => ItemError == 0;
    }

    public readonly struct S7CommPlusWriteResult
    {
        public S7CommPlusWriteResult(ItemAddress address, ulong itemError)
        {
            Address = address;
            ItemError = itemError;
        }

        public ItemAddress Address { get; }
        public ulong ItemError { get; }
        public bool IsSuccess => ItemError == 0;
    }

    public sealed class S7CommPlusBatchResult<T>
    {
        public S7CommPlusBatchResult(IReadOnlyList<T> items)
        {
            Items = items;
        }

        public IReadOnlyList<T> Items { get; }
    }
}
