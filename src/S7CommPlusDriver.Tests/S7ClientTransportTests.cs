using System;
using System.Linq;
using S7CommPlusDriver.Internal;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    /// <summary>
    /// Verifies transport creation, timeout propagation, negotiation, and cleanup behavior of the low-level S7 client.
    /// </summary>
    public sealed class S7ClientTransportTests
    {
        [Fact]
        public void ConnectUsesInjectedTransportAndTimeouts()
        {
            var transport = new FakeS7Transport { ConnectError = S7Consts.errTCPConnectionFailed };
            var client = new S7Client(() => transport)
            {
                PLCPort = 120,
                ConnTimeout = 111,
                RecvTimeout = 222,
                SendTimeout = 333
            };
            client.SetConnectionParams("1.2.3.4", 0x0600, new byte[] { 1, 2, 3 });

            var error = client.Connect();

            Assert.Equal(S7Consts.errTCPConnectionFailed, error);
            Assert.Equal(1, transport.ConnectCount);
            Assert.Equal(("1.2.3.4", 120, 111, 222, 333), transport.LastConnect);
        }

        [Fact]
        public void UpdatingTimeoutsPropagatesToExistingTransport()
        {
            var transport = new FakeS7Transport();
            var client = new S7Client(() => transport)
            {
                RecvTimeout = 222,
                SendTimeout = 333
            };

            client.SetTransportTimeouts(120000, 120000);

            Assert.Equal(120000, client.RecvTimeout);
            Assert.Equal(120000, client.SendTimeout);
            Assert.Equal((120000, 120000), transport.UpdatedTimeouts);
        }

        [Fact]
        public void ConnectAcceptsConnectionConfirmForShortRemoteTsap()
        {
            var transport = new FakeS7Transport { EmptyReceiveDelayMilliseconds = 500 };
            transport.EnqueueReceive(new byte[] { 0x03, 0x00, 0x00, 0x23 });
            transport.EnqueueReceive(new byte[] { 0x1E, 0xD0, 0x00 });
            transport.EnqueueReceive(new byte[28]);

            var client = new S7Client(() => transport);
            client.SetConnectionParams("1.2.3.4", 0x0600, System.Text.Encoding.ASCII.GetBytes(S7CommPlusDefaults.RemoteTsapEs));

            var error = client.Connect();
            client.Disconnect(50);

            Assert.Equal(0, error);
            Assert.Single(transport.Sent);
            Assert.Equal(35, transport.Sent[0].Length);
            Assert.Equal(S7CommPlusProtocolConstants.DefaultIsoTpduSize, client.PduSizeNegotiated);
        }

        [Fact]
        public void ConnectReadsNegotiatedTpduSizeFromConnectionConfirm()
        {
            var transport = new FakeS7Transport { EmptyReceiveDelayMilliseconds = 500 };
            transport.EnqueueReceive(new byte[] { 0x03, 0x00, 0x00, 0x0E });
            transport.EnqueueReceive(new byte[] { 0x09, 0xD0, 0x00 });
            transport.EnqueueReceive(new byte[] { 0x00, 0x00, 0x01, 0x00, 0xC0, 0x01, 0x09 });

            var client = new S7Client(() => transport);
            client.SetConnectionParams("1.2.3.4", 0x0600, System.Text.Encoding.ASCII.GetBytes(S7CommPlusDefaults.RemoteTsapEs));

            var error = client.Connect();
            client.Disconnect(50);

            Assert.Equal(0, error);
            Assert.Equal(512, client.PduSizeNegotiated);
        }

        /// <summary>
        /// Ensures repeated disconnect and disposal paths share one idempotent transport cleanup.
        /// </summary>
        [Fact]
        public void DisconnectClosesInjectedTransportOnlyOnce()
        {
            var transport = new FakeS7Transport { Connected = true };
            var client = new S7Client(() => transport);

            var error = client.Disconnect(50);
            var repeatedError = client.Disconnect(50);
            client.Dispose();

            Assert.Equal(0, error);
            Assert.Equal(0, repeatedError);
            Assert.Equal(1, transport.CloseCount);
            Assert.False(transport.Connected);
        }

        [Fact]
        public void SendFragmentsPayloadAtNegotiatedCotpBoundary()
        {
            var transport = new FakeS7Transport { EmptyReceiveDelayMilliseconds = 500 };
            transport.EnqueueReceive(new byte[] { 0x03, 0x00, 0x00, 0x23 });
            transport.EnqueueReceive(new byte[] { 0x1E, 0xD0, 0x00 });
            transport.EnqueueReceive(new byte[28]);
            var client = new S7Client(() => transport);
            client.SetConnectionParams(
                "1.2.3.4",
                0x0600,
                System.Text.Encoding.ASCII.GetBytes(S7CommPlusDefaults.RemoteTsapEs));
            Assert.Equal(0, client.Connect());
            transport.Sent.Clear();
            var payload = Enumerable.Range(0, 2000).Select(value => (byte)value).ToArray();

            client.Send(payload);
            client.Disconnect(50);

            Assert.Equal(2, transport.Sent.Count);
            Assert.Equal(1024, transport.Sent[0].Length);
            Assert.Equal(990, transport.Sent[1].Length);
            Assert.Equal(0x00, transport.Sent[0][6]);
            Assert.Equal(0x80, transport.Sent[1][6]);
            Assert.Equal(payload, transport.Sent.SelectMany(packet => packet.Skip(7)).ToArray());
        }
    }
}
