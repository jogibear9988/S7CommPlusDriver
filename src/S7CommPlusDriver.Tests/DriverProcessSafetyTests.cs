using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using S7CommPlusDriver.Tls;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class DriverProcessSafetyTests
    {
        [Fact]
        public void FinalizerDoesNotTouchManagedTransportOrThread()
        {
            var transport = new FakeS7Transport
            {
                CloseException = new ThreadStateException("Unable to retrieve thread information.")
            };
            var client = new S7Client(() => transport);
            SetField(client, "m_runThread", new Thread(() => { }));

            // Invoke directly so a regression fails the assertion without killing the test host.
            Invoke(client, "Finalize");

            Assert.Equal(0, transport.CloseCount);
            transport.CloseException = null;
            client.Dispose();
        }

        [Fact]
        public void AbandonedClientsSurviveActualGarbageCollection()
        {
            var transport = new FakeS7Transport
            {
                CloseException = new ThreadStateException("Finalization must not close managed transports.")
            };
            var references = new WeakReference[100];
            for (int i = 0; i < references.Length; i++)
                references[i] = AbandonClient(transport);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.All(references, reference => Assert.False(reference.IsAlive));
            Assert.Equal(0, transport.CloseCount);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonClient(FakeS7Transport transport)
        {
            return new WeakReference(new S7Client(() => transport));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SessionShutdownDisposesAndReleasesItsClient(bool abort)
        {
            var transport = new FakeS7Transport { Connected = true };
            var client = new S7Client(() => transport);
            var session = new S7CommPlusProtocolSession();
            client.OnDataReceived = (_, _) => { };
            client.OnReceiveError = _ => { };
            SetField(session, "m_client", client);

            Assert.Equal(0, abort ? session.CloseTransport() : session.TryDisconnect());

            Assert.Null(GetField(session, "m_client"));
            Assert.Null(client.OnDataReceived);
            Assert.Null(client.OnReceiveError);
            Assert.Equal(1, transport.CloseCount);
        }

        [Fact]
        public void PreparingReconnectDisposesPreviousClient()
        {
            var transport = new FakeS7Transport { Connected = true };
            var session = new S7CommPlusProtocolSession();
            SetField(session, "m_client", new S7Client(() => transport));
            try
            {
                session.DebugApplyRequestTimeoutForTests(10, 20);
                Assert.Equal(1, transport.CloseCount);
                Assert.False(transport.Connected);
            }
            finally
            {
                session.TryDisconnect();
            }
        }

        [Fact]
        public void ReceiveThreadContainsTransportAndErrorCallbackExceptions()
        {
            var transport = new FakeS7Transport
            {
                Connected = true,
                ReceiveException = new InvalidOperationException("receive failure"),
                CloseException = new InvalidOperationException("close failure")
            };
            using var client = new S7Client(() => transport);
            int reportedError = 0;
            client.OnReceiveError = error =>
            {
                reportedError = error;
                throw new InvalidOperationException("error callback failure");
            };

            var thread = StartReceiver(client);
            Assert.True(thread.Join(5000));
            Assert.Equal(S7Consts.errTCPDataReceive, reportedError);
            Assert.Contains("receive failure", client.LastErrorDetail);
            Assert.Contains("error callback failure", client.LastErrorDetail);
            Assert.Contains("close failure", client.LastErrorDetail);
            Assert.False(client.Connected);
        }

        [Fact]
        public void ReceiveThreadContainsDataCallbackException()
        {
            var transport = CreateDataTransport();
            using var client = new S7Client(() => transport);
            int reportedError = 0;
            client.OnDataReceived = (_, _) => throw new FormatException("malformed response");
            client.OnReceiveError = error => reportedError = error;

            Assert.True(StartReceiver(client).Join(5000));
            Assert.Equal(S7Consts.errTCPDataReceive, reportedError);
            Assert.Contains("malformed response", client.LastErrorDetail);
        }

        [Fact]
        public void DisconnectFromReceiveCallbackDoesNotJoinItself()
        {
            var transport = CreateDataTransport();
            using var client = new S7Client(() => transport);
            int result = -1;
            client.OnDataReceived = (_, _) => result = client.Disconnect(10000);

            Assert.True(StartReceiver(client).Join(5000));
            Assert.Equal(0, result);
            Assert.Equal(1, transport.CloseCount);
        }

        [Fact]
        public void TimedOutDisconnectDefersTlsDisposalUntilReceiveReturns()
        {
            var transport = CreateDataTransport();
            using var client = new S7Client(() => transport);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var tls = new TrackingTlsConnector();
            SetField(client, "m_sslconn", tls);
            client.OnDataReceived = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            var thread = StartReceiver(client);
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(S7Consts.errCliDestroying, client.Disconnect(1));
                Assert.Equal(0, tls.DisposeCount);
                Assert.Equal(S7Consts.errCliDestroying, client.Connect());
            }
            finally
            {
                release.Set();
                Assert.True(thread.Join(5000));
            }
            Assert.Equal(1, tls.DisposeCount);
        }

        [Fact]
        public void NativeKeyLoggingCallbackContainsFileErrors()
        {
            using var client = new S7Client();
            // NUL is invalid in a path on all supported platforms.
            string previous = S7Client.WriteSslKeyPath;
            try
            {
                S7Client.WriteSslKeyPath = "\0";
                client.SSL_CTX_keylog_cb(IntPtr.Zero, "test");
                Assert.Contains("TLS key logging failed", client.LastErrorDetail);
            }
            finally
            {
                S7Client.WriteSslKeyPath = previous;
            }
        }

        [Fact]
        public void TlsReaderContainsThrowingErrorHandler()
        {
            var callback = new ThrowingTlsCallback();
            using var connector = new BouncyCastleTlsConnector(callback);

            // An unconnected TLS protocol fails immediately at the read boundary.
            Invoke(connector, "ReadDecryptedData");

            Assert.Equal(1, callback.ErrorCount);
        }

        [Fact]
        public void RefreshTimerContainsLoggerFailureAndClosesSession()
        {
            var transport = new FakeS7Transport { Connected = true };
            var session = new S7CommPlusProtocolSession();
            SetField(session, "m_client", new S7Client(() => transport));
            SetField(session, "m_LegacyDigestActive", true);
            SetField(session, "m_LegacySessionPublicKey", new byte[0]);
            SetField(session, "m_LegacySessionKeyRefreshLogger", new ThrowingLogger());

            // The invalid authentication state causes renewal to fail; logging that
            // failure must not throw out of the ThreadPool timer callback either.
            session.GetType().GetMethod("RefreshLegacySessionKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, new object[] { 0 });

            Assert.False(transport.Connected);
            Assert.Null(GetField(session, "m_client"));
            Assert.Contains("logger failure", (string)GetField(session, "m_LastErrorDetail")!);
        }

        private static FakeS7Transport CreateDataTransport()
        {
            var transport = new FakeS7Transport { Connected = true };
            transport.EnqueueReceive(new byte[] { 3, 0, 0, 8 });
            transport.EnqueueReceive(new byte[] { 2, 0xf0, 0x80 });
            transport.EnqueueReceive(new byte[] { 0x72 });
            return transport;
        }

        private static Thread StartReceiver(S7Client client)
        {
            Invoke(client, "StartThread");
            return (Thread)GetField(client, "m_runThread")!;
        }

        private static object? GetField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        private static void Invoke(object target, string name) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

        private sealed class TrackingTlsConnector : IS7TlsConnector
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
            public void Write(byte[] data, int dataLength) => throw new NotSupportedException();
            public void ReadCompleted(byte[] data, int dataLength) => throw new NotSupportedException();
            public int Receive(ref byte[] buffer, int bufferSize) => throw new NotSupportedException();
            public byte[] GetOmsExporterSecret() => throw new NotSupportedException();
        }

        private sealed class ThrowingTlsCallback : IS7TlsConnectorCallback
        {
            public int ErrorCount;
            public void WriteData(byte[] data, int length) { }
            public void OnDataAvailable() { }
            public void OnSslError(int error, string state)
            {
                ErrorCount++;
                throw new InvalidOperationException("TLS error callback failure");
            }
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("logger failure");
        }
    }
}
