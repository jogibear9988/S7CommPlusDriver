using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    /// <summary>
    /// Explicitly enabled PLC write tests. These tests never create or delete jobs and restore the selected job to its
    /// original inactive state.
    /// </summary>
    public sealed class LiveTraceLifecycleTests
    {
        [Fact]
        public async Task ExistingInactiveTraceCanBeActivatedAndDeactivated()
        {
            if (!ReadBoolean("S7COMMPLUS_LIVE_TRACE_LIFECYCLE"))
                return;

            var host = RequiredEnvironmentVariable("S7COMMPLUS_LIVE_HOST");
            var traceName = RequiredEnvironmentVariable("S7COMMPLUS_LIVE_TRACE_NAME");
            var securityMode = S7CommPlusSecurityMode.Tls;
            var configuredSecurityMode = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_SECURITY_MODE");
            if (!String.IsNullOrWhiteSpace(configuredSecurityMode) &&
                !Enum.TryParse(configuredSecurityMode, true, out securityMode))
            {
                throw new InvalidOperationException("S7COMMPLUS_LIVE_SECURITY_MODE is invalid.");
            }

            await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
            {
                Address = host,
                Port = ReadPort(),
                SecurityMode = securityMode,
                WriteEnabled = true,
                RequestTimeout = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            });
            await client.ConnectAsync();

            var installed = await client.GetInstalledTracesAsync();
            var selected = installed.Single(item =>
                String.Equals(item.Reference.Name, traceName, StringComparison.Ordinal));
            Assert.Equal(false, selected.Enabled);

            var activationAttempted = false;
            try
            {
                activationAttempted = true;
                await client.ActivateTraceAsync(selected.Reference);
                await WaitForEnabledStateAsync(client, selected.Reference, true);

                await client.DeactivateTraceAsync(selected.Reference);
                await WaitForEnabledStateAsync(client, selected.Reference, false);
                activationAttempted = false;
            }
            finally
            {
                if (activationAttempted && client.State == S7CommPlusConnectionState.Connected)
                    await client.DeactivateTraceAsync(selected.Reference);
                await client.DisconnectAsync();
            }
        }

        private static async Task WaitForEnabledStateAsync(
            S7CommPlusClient client,
            S7CommPlusTraceReference reference,
            bool expected)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var current = await client.GetInstalledTraceAsync(reference);
                if (current == null)
                    throw new InvalidOperationException("The selected trace disappeared during the lifecycle test.");
                if (current.Enabled == expected)
                    return;
                await Task.Delay(100);
            }
            throw new TimeoutException($"The trace did not reach enabled={expected} within the expected time.");
        }

        private static bool ReadBoolean(string name) =>
            Boolean.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value;

        private static int ReadPort()
        {
            var value = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_PORT");
            return String.IsNullOrWhiteSpace(value)
                ? S7CommPlusDefaults.IsoTcpPort
                : Int32.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static string RequiredEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(name + " is required when the live trace lifecycle test is enabled.");
            return value.Trim();
        }
    }
}
