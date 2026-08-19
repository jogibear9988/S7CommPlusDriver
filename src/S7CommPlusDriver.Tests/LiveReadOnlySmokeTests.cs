using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace S7CommPlusDriver.Tests
{
    public sealed class LiveReadOnlySmokeTests
    {
        private readonly ITestOutputHelper _output;

        public LiveReadOnlySmokeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task LivePlcReadOnlySmokeTest()
        {
            var host = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_HOST");
            if (string.IsNullOrWhiteSpace(host))
            {
                return;
            }

            var securityModeName = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_SECURITY_MODE");
            var securityMode = new S7CommPlusClientOptions().SecurityMode;
            if (!string.IsNullOrWhiteSpace(securityModeName))
            {
                Assert.True(Enum.TryParse(securityModeName, ignoreCase: true, out securityMode), $"Invalid S7COMMPLUS_LIVE_SECURITY_MODE value '{securityModeName}'.");
            }

            var tlsBackendName = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_TLS_BACKEND");
            var tlsBackend = new S7CommPlusClientOptions().TlsBackend;
            if (!string.IsNullOrWhiteSpace(tlsBackendName))
            {
                Assert.True(Enum.TryParse(tlsBackendName, ignoreCase: true, out tlsBackend), $"Invalid S7COMMPLUS_LIVE_TLS_BACKEND value '{tlsBackendName}'.");
            }

            await using var client = new S7CommPlusClient(new S7CommPlusClientOptions
            {
                Address = host,
                Port = ReadOptionalPort("S7COMMPLUS_LIVE_PORT", S7CommPlusDefaults.IsoTcpPort),
                SecurityMode = securityMode,
                TlsBackend = tlsBackend,
                LegacyPublicKeyId = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_PUBLIC_KEY_ID"),
                Logger = new TestOutputLogger(_output),
                RequestTimeout = ReadOptionalTimeout("S7COMMPLUS_LIVE_REQUEST_TIMEOUT_SECONDS", TimeSpan.FromSeconds(5)),
                ConnectTimeout = ReadOptionalTimeout("S7COMMPLUS_LIVE_CONNECT_TIMEOUT_SECONDS", TimeSpan.FromSeconds(5))
            });
            await client.ConnectAsync();
            var cpuInfo = await client.GetCpuInfoAsync();
            var vars = await client.BrowseAsync();

            Assert.NotNull(cpuInfo);
            Assert.NotEmpty(vars);

            if (ReadOptionalBoolean("S7COMMPLUS_LIVE_EXTENDED_METADATA"))
            {
                var cultureInfo = await client.GetCpuCultureInfoAsync();
                var textLists = await client.GetTextListsAsync();

                Assert.NotNull(cultureInfo);
                Assert.NotNull(cultureInfo.LanguageIds);
                Assert.NotEmpty(cultureInfo.LanguageIds);
                Assert.NotNull(textLists);
                Assert.NotEmpty(textLists.TextLists);
            }

            var tagNames = Environment.GetEnvironmentVariable("S7COMMPLUS_LIVE_TAGS");
            if (!string.IsNullOrWhiteSpace(tagNames))
            {
                var requested = RuntimeCompatibility.SplitAndTrim(tagNames, ';');
                var tagTasks = requested.Select(symbol => client.GetTagBySymbolAsync(symbol)).ToArray();
                var tags = await Task.WhenAll(tagTasks);
                var readResult = await client.ReadAsync(tags);
                Assert.NotEmpty(tags);
                Assert.All(readResult.Items, item => Assert.True(item.IsSuccess, $"Tag {item.Tag.Name} read failed with item error {item.ItemError}."));
            }

            if (ReadOptionalBoolean("S7COMMPLUS_LIVE_RECONNECT"))
            {
                await client.DisconnectAsync();
                await client.ConnectAsync();
                Assert.NotNull(await client.GetCpuInfoAsync());
            }

            await client.DisconnectAsync();
        }

        [Fact]
        public async Task LegacySessionKeyLifetimeReadOnlyTest()
        {
            var hostsValue = Environment.GetEnvironmentVariable("S7COMMPLUS_LEGACY_LIFETIME_HOSTS");
            if (string.IsNullOrWhiteSpace(hostsValue))
            {
                return;
            }

            var hosts = RuntimeCompatibility.SplitAndTrim(hostsValue, ';');
            Assert.NotEmpty(hosts);

            var duration = ReadOptionalTimeout("S7COMMPLUS_LEGACY_LIFETIME_MINUTES", TimeSpan.FromMinutes(40), TimeSpan.FromMinutes);
            var activeReadInterval = ReadOptionalTimeout("S7COMMPLUS_LEGACY_ACTIVE_READ_SECONDS", TimeSpan.FromSeconds(5));
            Assert.True(duration > TimeSpan.FromMinutes(30), "The legacy key lifetime test must run longer than 30 minutes.");

            _output.WriteLine($"Starting read-only legacy key lifetime test for {string.Join(", ", hosts)}; duration={duration}, active interval={activeReadInterval}.");
            await Task.WhenAll(hosts.Select(host => TestLegacyKeyLifetimeAsync(
                host,
                duration,
                activeReadInterval,
                refreshEnabled: false,
                refreshInterval: TimeSpan.FromMinutes(25))));
        }

        [Fact]
        public async Task LegacySessionKeyRefreshReadOnlyTest()
        {
            var hostsValue = Environment.GetEnvironmentVariable("S7COMMPLUS_LEGACY_REFRESH_TEST_HOSTS");
            if (string.IsNullOrWhiteSpace(hostsValue))
            {
                return;
            }

            var hosts = RuntimeCompatibility.SplitAndTrim(hostsValue, ';');
            Assert.NotEmpty(hosts);

            var duration = ReadOptionalTimeout("S7COMMPLUS_LEGACY_REFRESH_TEST_SECONDS", TimeSpan.FromMinutes(3));
            var refreshInterval = ReadOptionalTimeout("S7COMMPLUS_LEGACY_REFRESH_INTERVAL_SECONDS", TimeSpan.FromSeconds(30));
            var activeReadInterval = ReadOptionalTimeout("S7COMMPLUS_LEGACY_ACTIVE_READ_SECONDS", TimeSpan.FromSeconds(5));
            Assert.True(duration >= TimeSpan.FromTicks(refreshInterval.Ticks * 2), "The refresh test must cover at least two key-renewal intervals.");

            _output.WriteLine($"Starting read-only legacy key refresh test for {string.Join(", ", hosts)}; duration={duration}, refresh interval={refreshInterval}.");
            await Task.WhenAll(hosts.Select(host => TestLegacyKeyLifetimeAsync(
                host,
                duration,
                activeReadInterval,
                refreshEnabled: true,
                refreshInterval)));
        }

        private async Task TestLegacyKeyLifetimeAsync(
            string host,
            TimeSpan duration,
            TimeSpan activeReadInterval,
            bool refreshEnabled,
            TimeSpan refreshInterval)
        {
            await using var idleClient = CreateLegacyLifetimeClient(host, refreshEnabled, refreshInterval);
            await using var activeClient = CreateLegacyLifetimeClient(host, refreshEnabled, refreshInterval);

            await idleClient.ConnectAsync();
            await activeClient.ConnectAsync();
            Assert.NotNull(await idleClient.GetCpuInfoAsync());
            Assert.NotNull(await activeClient.GetCpuInfoAsync());

            var stopwatch = Stopwatch.StartNew();
            _output.WriteLine($"{host}: idle and active legacy sessions established.");

            var idleTask = ReadAfterIdleAsync(host, idleClient, stopwatch, duration);
            var activeTask = ReadContinuouslyAsync(host, activeClient, stopwatch, duration, activeReadInterval);
            await Task.WhenAll(idleTask, activeTask);
        }

        private async Task ReadAfterIdleAsync(string host, S7CommPlusClient client, Stopwatch stopwatch, TimeSpan duration)
        {
            await Task.Delay(duration);
            try
            {
                Assert.True(client.IsConnected, $"{host}: original idle session was disconnected before the final read.");
                Assert.NotNull(await client.GetCpuInfoAsync());
                _output.WriteLine($"{host}: idle session read succeeded at {stopwatch.Elapsed}.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{host}: idle session read failed at {stopwatch.Elapsed}.", ex);
            }
        }

        private async Task ReadContinuouslyAsync(
            string host,
            S7CommPlusClient client,
            Stopwatch stopwatch,
            TimeSpan duration,
            TimeSpan activeReadInterval)
        {
            var readCount = 0;
            while (stopwatch.Elapsed < duration)
            {
                var delay = duration - stopwatch.Elapsed < activeReadInterval
                    ? duration - stopwatch.Elapsed
                    : activeReadInterval;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }

                try
                {
                    Assert.True(client.IsConnected, $"{host}: original active session was disconnected before read #{readCount + 1}.");
                    Assert.NotNull(await client.GetCpuInfoAsync());
                    readCount++;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{host}: active session read #{readCount + 1} failed at {stopwatch.Elapsed}.", ex);
                }
            }

            _output.WriteLine($"{host}: active session completed {readCount} reads through {stopwatch.Elapsed}.");
        }

        private S7CommPlusClient CreateLegacyLifetimeClient(
            string host,
            bool refreshEnabled,
            TimeSpan refreshInterval)
        {
            return new S7CommPlusClient(new S7CommPlusClientOptions
            {
                Address = host,
                SecurityMode = S7CommPlusSecurityMode.LegacyChallenge,
                AutoReconnect = false,
                WriteEnabled = false,
                LegacySessionKeyRefreshEnabled = refreshEnabled,
                LegacySessionKeyRefreshInterval = refreshInterval,
                Logger = new TestOutputLogger(_output),
                // This test measures session lifetime, not request latency. Leave
                // enough time for an isolated retransmission on the lab network.
                RequestTimeout = TimeSpan.FromSeconds(15),
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });
        }

        private sealed class TestOutputLogger : ILogger
        {
            private readonly ITestOutputHelper _output;

            public TestOutputLogger(ITestOutputHelper output)
            {
                _output = output;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception == null ? string.Empty : Environment.NewLine + exception)}");
            }

            private sealed class NoopScope : IDisposable
            {
                public static readonly NoopScope Instance = new NoopScope();

                public void Dispose()
                {
                }
            }
        }

        private static int ReadOptionalPort(string environmentVariable, int fallback)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            Assert.True(
                int.TryParse(value, out var port) && port > 0 && port <= 65535,
                $"Invalid {environmentVariable} value '{value}'.");
            return port;
        }

        private static bool ReadOptionalBoolean(string environmentVariable)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Assert.True(
                bool.TryParse(value, out var result),
                $"Invalid {environmentVariable} value '{value}'.");
            return result;
        }

        private static TimeSpan ReadOptionalTimeout(string environmentVariable, TimeSpan fallback)
        {
            return ReadOptionalTimeout(environmentVariable, fallback, TimeSpan.FromSeconds);
        }

        private static TimeSpan ReadOptionalTimeout(
            string environmentVariable,
            TimeSpan fallback,
            Func<double, TimeSpan> fromValue)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            Assert.True(double.TryParse(value, out var seconds) && seconds > 0, $"Invalid {environmentVariable} value '{value}'.");
            return fromValue(seconds);
        }
    }
}
