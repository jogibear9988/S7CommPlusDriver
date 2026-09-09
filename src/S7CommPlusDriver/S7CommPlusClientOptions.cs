using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using S7CommPlusDriver.Internal;
using System;
using System.Text;

namespace S7CommPlusDriver
{
    public sealed class S7CommPlusClientOptions
    {
        private string _remoteTsap;

        public string Address { get; set; } = string.Empty;
        public int Port { get; set; } = S7CommPlusDefaults.IsoTcpPort;
        public ushort LocalTsap { get; set; } = S7CommPlusDefaults.LocalTsap;
        /// <summary>
        /// Gets or sets the client role used for TLS and legacy challenge connections.
        /// </summary>
        /// <remarks>
        /// HMI is the default so ordinary driver connections do not claim an engineering session on the PLC.
        /// The role selects the matching standard remote TSAP unless <see cref="RemoteTsap"/> is explicitly set.
        /// </remarks>
        public S7CommPlusSessionRole SessionRole { get; set; } = S7CommPlusSessionRole.Hmi;
        /// <summary>
        /// Gets or sets a custom remote TSAP. When not explicitly set, the TSAP is derived from <see cref="SessionRole"/>.
        /// Assign <see langword="null"/> to return to the role-derived TSAP.
        /// </summary>
        public string RemoteTsap
        {
            get => _remoteTsap ?? GetDefaultRemoteTsap(SessionRole);
            set => _remoteTsap = value;
        }
        public string Password { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public TimeSpan ConnectTimeout { get; set; } = S7CommPlusDefaults.ConnectTimeout;
        public TimeSpan RequestTimeout { get; set; } = S7CommPlusDefaults.RequestTimeout;
        public TimeSpan DisconnectTimeout { get; set; } = S7CommPlusDefaults.DisconnectTimeout;
        public TimeSpan BrowseTimeout { get; set; } = S7CommPlusDefaults.BrowseTimeout;
        public bool AutoReconnect { get; set; } = true;
        public bool WriteEnabled { get; set; } = false;
        /// <summary>
        /// Enables periodic renewal of the integrity key for legacy challenge-authenticated sessions.
        /// </summary>
        public bool LegacySessionKeyRefreshEnabled { get; set; } = true;
        /// <summary>
        /// Time between successful legacy session-key renewals. Siemens TIA renews after roughly 30 to 35 minutes;
        /// the shorter default leaves margin before PLC-side expiration.
        /// </summary>
        public TimeSpan LegacySessionKeyRefreshInterval { get; set; } = TimeSpan.FromMinutes(25);
#if HARPOS7_LEGACY_AUTH
        public S7CommPlusSecurityMode SecurityMode { get; set; } = S7CommPlusSecurityMode.Auto;
#else
        public S7CommPlusSecurityMode SecurityMode { get; set; } = S7CommPlusSecurityMode.Tls;
#endif
        public S7CommPlusTlsBackend TlsBackend { get; set; } = S7CommPlusTlsBackend.BouncyCastle;
        public S7CommPlusSecurityMode? NegotiatedSecurityMode { get; internal set; }
        /// <summary>
        /// Gets or sets the optional 16-character Siemens public-key identifier used when a legacy PLC reports
        /// only its two-character key-family fingerprint, for example <c>00</c>.
        /// </summary>
        /// <remarks>
        /// Most PLCs report a complete fingerprint such as <c>00:181B7B0847D11694</c> and do not require this
        /// option. The identifier is not a password or private key.
        /// </remarks>
        public string LegacyPublicKeyId { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether the driver automatically tries compatible public keys from the
        /// bundled HarpoS7 catalog when a legacy PLC reports only its two-character key-family fingerprint.
        /// </summary>
        /// <remarks>
        /// Each candidate is attempted on a fresh connection. The successful candidate is remembered for reconnects.
        /// Disable this only when deterministic failure is preferred over automatic key discovery.
        /// </remarks>
        public bool LegacyPublicKeyFallbackEnabled { get; set; } = true;
        /// <summary>
        /// Gets or sets an optional custom resolver for legacy Siemens public keys.
        /// </summary>
        /// <remarks>
        /// The resolver receives the fingerprint exactly as reported by the PLC. It takes precedence over
        /// <see cref="LegacyPublicKeyId"/> and the built-in HarpoS7 public-key store.
        /// </remarks>
        public Func<string, byte[]> LegacyPublicKeyResolver { get; set; }
        public ILogger Logger { get; set; } = NullLogger.Instance;

        internal int ConnectTimeoutMilliseconds => ToPositiveMilliseconds(ConnectTimeout, nameof(ConnectTimeout));
        internal int RequestTimeoutMilliseconds => ToPositiveMilliseconds(RequestTimeout, nameof(RequestTimeout));
        internal int DisconnectTimeoutMilliseconds => ToPositiveMilliseconds(DisconnectTimeout, nameof(DisconnectTimeout));
        internal int BrowseTimeoutMilliseconds => ToPositiveMilliseconds(BrowseTimeout, nameof(BrowseTimeout));
        internal int LegacySessionKeyRefreshIntervalMilliseconds => ToPositiveMilliseconds(LegacySessionKeyRefreshInterval, nameof(LegacySessionKeyRefreshInterval));
        internal byte[] RemoteTsapBytes => Encoding.ASCII.GetBytes(RemoteTsap ?? string.Empty);
        internal LegacyServerSessionRole LegacySessionRole => SessionRole == S7CommPlusSessionRole.Hmi
            ? LegacyServerSessionRole.Hmi
            : LegacyServerSessionRole.EngineeringSystem;
        internal string LegacyPublicKeyFingerprintOverride { get; set; }

        internal S7CommPlusClientOptions Clone()
        {
            return (S7CommPlusClientOptions)MemberwiseClone();
        }

        internal string GetLegacyPublicKeyFingerprint(string plcFingerprint)
        {
            if (!string.IsNullOrWhiteSpace(LegacyPublicKeyFingerprintOverride))
            {
                return LegacyPublicKeyFingerprintOverride;
            }
            if (string.IsNullOrWhiteSpace(LegacyPublicKeyId) || plcFingerprint == null || plcFingerprint.Length != 2)
            {
                return plcFingerprint;
            }

            return $"{plcFingerprint}:{LegacyPublicKeyId.Trim()}";
        }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Address))
            {
                throw new ArgumentException("PLC address is required.", nameof(Address));
            }
            if (Port <= 0 || Port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");
            }
            if (string.IsNullOrWhiteSpace(RemoteTsap))
            {
                throw new ArgumentException("Remote TSAP is required.", nameof(RemoteTsap));
            }
            if (!Enum.IsDefined(typeof(S7CommPlusSessionRole), SessionRole))
            {
                throw new ArgumentOutOfRangeException(nameof(SessionRole), "Session role is not supported.");
            }
            foreach (var character in RemoteTsap)
            {
                if (character > 0x7F)
                {
                    throw new ArgumentException("Remote TSAP must contain only ASCII characters.", nameof(RemoteTsap));
                }
            }
            if (RemoteTsapBytes.Length > S7CommPlusProtocolConstants.MaxCotpParameterLength)
            {
                throw new ArgumentOutOfRangeException(nameof(RemoteTsap), $"Remote TSAP must be {S7CommPlusProtocolConstants.MaxCotpParameterLength} bytes or shorter.");
            }
            if (!Enum.IsDefined(typeof(S7CommPlusSecurityMode), SecurityMode))
            {
                throw new ArgumentOutOfRangeException(nameof(SecurityMode), "Security mode is not supported.");
            }
            if (!Enum.IsDefined(typeof(S7CommPlusTlsBackend), TlsBackend))
            {
                throw new ArgumentOutOfRangeException(nameof(TlsBackend), "TLS backend is not supported.");
            }
            if (!string.IsNullOrWhiteSpace(LegacyPublicKeyId) && !IsLegacyPublicKeyId(LegacyPublicKeyId.Trim()))
            {
                throw new ArgumentException(
                    "Legacy public-key id must contain exactly 16 hexadecimal characters.",
                    nameof(LegacyPublicKeyId));
            }
#if NETFRAMEWORK
            if (TlsBackend == S7CommPlusTlsBackend.OpenSsl)
            {
                throw new PlatformNotSupportedException(
                    "The native OpenSSL backend is not supported on .NET Framework. Use the BouncyCastle backend.");
            }
#endif
#if !HARPOS7_LEGACY_AUTH
            if (SecurityMode != S7CommPlusSecurityMode.Tls)
            {
                throw new S7CommPlusUnsupportedSecurityModeException(
                    SecurityMode,
                    $"{Address}:{Port}",
                    "Legacy S7CommPlus challenge authentication is available only on net8.0 and later builds.");
            }
#endif
            _ = ConnectTimeoutMilliseconds;
            _ = RequestTimeoutMilliseconds;
            _ = DisconnectTimeoutMilliseconds;
            _ = BrowseTimeoutMilliseconds;
            if (LegacySessionKeyRefreshEnabled)
            {
                _ = LegacySessionKeyRefreshIntervalMilliseconds;
            }
            Logger ??= NullLogger.Instance;
        }

        private static string GetDefaultRemoteTsap(S7CommPlusSessionRole sessionRole)
        {
            return sessionRole == S7CommPlusSessionRole.EngineeringSystem
                ? S7CommPlusDefaults.RemoteTsapEs
                : S7CommPlusDefaults.RemoteTsapHmi;
        }

        private static bool IsLegacyPublicKeyId(string value)
        {
            if (value.Length != 16)
            {
                return false;
            }

            foreach (var character in value)
            {
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F')
                    || (character >= 'a' && character <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private static int ToPositiveMilliseconds(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, "Timeout must be greater than zero.");
            }
            if (value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(name, "Timeout is too large.");
            }
            return Math.Max(1, (int)value.TotalMilliseconds);
        }
    }
}
