using System;
using System.Collections.Generic;

namespace S7CommPlusDriver.Tests
{
    internal sealed class FakeS7Transport : IS7Transport
    {
        private readonly Queue<byte[]> _receiveChunks = new Queue<byte[]>();

        public bool Connected { get; set; }
        public int ConnectCount { get; private set; }
        public int CloseCount { get; private set; }
        public int ConnectError { get; set; }
        public int SendError { get; set; }
        public int EmptyReceiveDelayMilliseconds { get; set; }
        public Exception? ReceiveException { get; set; }
        public Exception? CloseException { get; set; }
        public List<byte[]> Sent { get; } = new List<byte[]>();
        public (string Address, int Port, int ConnectTimeout, int ReceiveTimeout, int SendTimeout) LastConnect { get; private set; }
        public (int ReceiveTimeout, int SendTimeout)? UpdatedTimeouts { get; private set; }

        public void EnqueueReceive(byte[] data)
        {
            _receiveChunks.Enqueue(data);
        }

        public int Connect(string address, int port, int connectTimeoutMilliseconds, int receiveTimeoutMilliseconds, int sendTimeoutMilliseconds)
        {
            ConnectCount++;
            LastConnect = (address, port, connectTimeoutMilliseconds, receiveTimeoutMilliseconds, sendTimeoutMilliseconds);
            Connected = ConnectError == 0;
            return ConnectError;
        }

        public void SetTimeouts(int receiveTimeoutMilliseconds, int sendTimeoutMilliseconds)
        {
            UpdatedTimeouts = (receiveTimeoutMilliseconds, sendTimeoutMilliseconds);
        }

        public int Send(byte[] buffer)
        {
            return Send(buffer, buffer.Length);
        }

        public int Send(byte[] buffer, int size)
        {
            if (!Connected)
            {
                return S7Consts.errTCPNotConnected;
            }
            if (SendError != 0)
            {
                return SendError;
            }
            var sent = new byte[size];
            Array.Copy(buffer, sent, size);
            Sent.Add(sent);
            return 0;
        }

        public int Receive(byte[] buffer, int start, int size)
        {
            if (ReceiveException != null)
                throw ReceiveException;
            if (!Connected)
            {
                return S7Consts.errTCPNotConnected;
            }
            if (_receiveChunks.Count == 0)
            {
                if (EmptyReceiveDelayMilliseconds > 0)
                {
                    System.Threading.Thread.Sleep(EmptyReceiveDelayMilliseconds);
                }
                Connected = false;
                return S7Consts.errTCPConnectionReset;
            }
            var chunk = _receiveChunks.Dequeue();
            if (chunk.Length != size)
            {
                return S7Consts.errIsoInvalidDataSize;
            }
            Array.Copy(chunk, 0, buffer, start, size);
            return 0;
        }

        public int Close()
        {
            CloseCount++;
            Connected = false;
            if (CloseException != null)
                throw CloseException;
            return 0;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
