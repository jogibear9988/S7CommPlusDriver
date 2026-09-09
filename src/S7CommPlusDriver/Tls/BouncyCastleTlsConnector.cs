using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using TlsProtocolVersion = Org.BouncyCastle.Tls.ProtocolVersion;

namespace S7CommPlusDriver.Tls
{
    internal sealed class BouncyCastleTlsConnector : IS7TlsConnector
    {
        private const string OmsExporterLabel = "EXPERIMENTAL_OMS";
        private const int OmsExporterSecretLength = 32;

        private readonly IS7TlsConnectorCallback _dataSink;
        private readonly BlockingTlsRecordStream _recordStream;
        private readonly TlsClientProtocol _protocol;
        private readonly PlcTlsClient _tlsClient;
        private readonly BlockingCollection<byte[]> _decryptedData = new BlockingCollection<byte[]>();
        private readonly Thread _readerThread;
        private volatile bool _disposed;
        private bool _handshakeStarted;

        public BouncyCastleTlsConnector(IS7TlsConnectorCallback dataSink)
        {
            _dataSink = dataSink ?? throw new ArgumentNullException(nameof(dataSink));
            _recordStream = new BlockingTlsRecordStream(_dataSink);
            _protocol = new TlsClientProtocol(_recordStream);
            _tlsClient = new PlcTlsClient();

            _readerThread = new Thread(ReadDecryptedData)
            {
                IsBackground = true,
                Name = "S7CommPlus BouncyCastle TLS reader"
            };
        }

        public void StartHandshake()
        {
            ThrowIfDisposed();
            _handshakeStarted = true;
            _protocol.Connect(_tlsClient);
            _readerThread.Start();
        }

        public void Write(byte[] data, int dataLength)
        {
            ThrowIfDisposed();
            _protocol.Stream.Write(data, 0, dataLength);
            _protocol.Stream.Flush();
        }

        public void ReadCompleted(byte[] data, int dataLength)
        {
            if (_disposed)
            {
                return;
            }

            _recordStream.AddIncoming(data, dataLength);
        }

        public int Receive(ref byte[] buffer, int bufferSize)
        {
            ThrowIfDisposed();

            if (!_decryptedData.TryTake(out var data))
            {
                return 0;
            }

            var bytesToCopy = Math.Min(bufferSize, data.Length);
            Buffer.BlockCopy(data, 0, buffer, 0, bytesToCopy);
            return bytesToCopy;
        }

        public byte[] GetOmsExporterSecret()
        {
            ThrowIfDisposed();
            return _tlsClient.GetOmsExporterSecret();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _recordStream.Complete();
            _decryptedData.CompleteAdding();
            _tlsClient.ClearOmsExporterSecret();
            // Bouncy Castle cannot send a close alert before Connect initialized its peer.
            if (_handshakeStarted)
                _protocol.Close();
        }

        private void ReadDecryptedData()
        {
            var buffer = new byte[8192];
            try
            {
                while (!_disposed)
                {
                    var bytesRead = _protocol.Stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    var data = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                    _decryptedData.Add(data);
                    _dataSink.OnDataAvailable();
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    try
                    {
                        _dataSink.OnSslError(-1, ex.Message);
                    }
                    catch (Exception)
                    {
                        // An error callback must not escape this background thread.
                    }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BouncyCastleTlsConnector));
            }
        }

        private sealed class PlcTlsClient : DefaultTlsClient
        {
            private byte[] _omsExporterSecret;

            public PlcTlsClient()
                : base(new BcTlsCrypto(new SecureRandom()))
            {
            }

            public TlsClientContext Context { get; private set; }

            public byte[] GetOmsExporterSecret()
            {
                if (_omsExporterSecret == null)
                {
                    throw new InvalidOperationException("OMS exporter secret is unavailable before the TLS handshake completes.");
                }

                return (byte[])_omsExporterSecret.Clone();
            }

            public void ClearOmsExporterSecret()
            {
                if (_omsExporterSecret != null)
                {
                    Array.Clear(_omsExporterSecret, 0, _omsExporterSecret.Length);
                    _omsExporterSecret = null;
                }
            }

            public override void Init(TlsClientContext context)
            {
                Context = context;
                base.Init(context);
            }

            public override int[] GetCipherSuites()
            {
                return new[]
                {
                    CipherSuite.TLS_AES_256_GCM_SHA384,
                    CipherSuite.TLS_AES_128_GCM_SHA256
                };
            }

            protected override TlsProtocolVersion[] GetSupportedVersions()
            {
                return TlsProtocolVersion.TLSv13.Only();
            }

            public override TlsAuthentication GetAuthentication()
            {
                return new AcceptAnyServerCertificateAuthentication();
            }

            public override void NotifyHandshakeComplete()
            {
                base.NotifyHandshakeComplete();

                // Bouncy Castle clears the TLS exporter master secret as soon as this callback returns.
                // Export and retain the OMS material while the handshake secret is still available.
                _omsExporterSecret = Context.ExportKeyingMaterial(
                    OmsExporterLabel,
                    null,
                    OmsExporterSecretLength);
            }
        }

        private sealed class AcceptAnyServerCertificateAuthentication : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
            }

            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                return null;
            }
        }

        private sealed class BlockingTlsRecordStream : Stream
        {
            private readonly IS7TlsConnectorCallback _dataSink;
            private readonly BlockingCollection<byte[]> _incoming = new BlockingCollection<byte[]>();
            private byte[] _currentReadBuffer;
            private int _currentReadOffset;
            private bool _completed;

            public BlockingTlsRecordStream(IS7TlsConnectorCallback dataSink)
            {
                _dataSink = dataSink;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public void AddIncoming(byte[] data, int dataLength)
            {
                if (_completed)
                {
                    return;
                }

                var copy = new byte[dataLength];
                Buffer.BlockCopy(data, 0, copy, 0, dataLength);
                _incoming.Add(copy);
            }

            public void Complete()
            {
                _completed = true;
                _incoming.CompleteAdding();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (_currentReadBuffer == null || _currentReadOffset >= _currentReadBuffer.Length)
                {
                    try
                    {
                        _currentReadBuffer = _incoming.Take();
                        _currentReadOffset = 0;
                    }
                    catch (InvalidOperationException)
                    {
                        return 0;
                    }
                }

                var bytesToCopy = Math.Min(count, _currentReadBuffer.Length - _currentReadOffset);
                Buffer.BlockCopy(_currentReadBuffer, _currentReadOffset, buffer, offset, bytesToCopy);
                _currentReadOffset += bytesToCopy;
                return bytesToCopy;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);
                _dataSink.WriteData(data, data.Length);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }
        }
    }
}
