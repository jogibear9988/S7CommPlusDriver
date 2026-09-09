using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using S7CommPlusDriver.Internal;
using Microsoft.Extensions.Logging;

#if HARPOS7_LEGACY_AUTH
using HarpoS7;
using HarpoS7.Auth;
using HarpoS7.Integrity;
using HarpoS7.PublicKeys.Exceptions;
using HarpoS7.PublicKeys.Impl;
using HarpoS7.Utilities.Auth;
using HarpoS7.Utilities.Extensions;
#endif

namespace S7CommPlusDriver
{
    internal partial class S7CommPlusProtocolSession
    {
        private const int LegacyDigestLength = LegacyOmsConstants.PacketDigestLength;
        private const int LegacyDigestFieldLength = LegacyOmsConstants.PacketDigestFieldLength;

#if HARPOS7_LEGACY_AUTH
        private Timer m_LegacySessionKeyRefreshTimer;
        private int m_LegacySessionKeyRefreshGeneration;
        private int m_LegacySessionKeyRefreshIntervalMilliseconds;
        private EPublicKeyFamily m_LegacySessionKeyFamily;
        private byte[] m_LegacySessionPublicKey;
        private ILogger m_LegacySessionKeyRefreshLogger;
        private string m_LegacySessionKeyRefreshEndpoint;
        private IReadOnlyList<string> m_LegacyPublicKeyFallbackFingerprints;
        private string m_LegacyAttemptedPublicKeyFingerprint;

        private int ConnectLegacyChallenge(S7CommPlusClientOptions options)
        {
            m_LegacyPublicKeyFallbackFingerprints = null;
            m_LegacyAttemptedPublicKeyFingerprint = null;
            var res = ConnectLegacyChallengeWithPublicKeyFallback(options, options.LegacySessionRole);

            if (res == 0 &&
                options.LegacyPublicKeyResolver == null &&
                string.IsNullOrWhiteSpace(options.LegacyPublicKeyId) &&
                !string.IsNullOrWhiteSpace(m_LegacyAttemptedPublicKeyFingerprint))
            {
                // The client keeps this options instance across protocol-session
                // replacements, so the discovered key survives auto reconnects.
                options.LegacyPublicKeyFingerprintOverride =
                    m_LegacyAttemptedPublicKeyFingerprint;
            }

            return res;
        }

        private int ConnectLegacyChallengeWithPublicKeyFallback(
            S7CommPlusClientOptions options,
            LegacyServerSessionRole serverSessionRole)
        {
            if (!string.IsNullOrWhiteSpace(options.LegacyPublicKeyFingerprintOverride) &&
                string.IsNullOrWhiteSpace(options.LegacyPublicKeyId) &&
                options.LegacyPublicKeyResolver == null &&
                m_LegacyPublicKeyFallbackFingerprints == null)
            {
                var cachedFingerprint =
                    options.LegacyPublicKeyFingerprintOverride;
                var cachedResult =
                    ConnectLegacyChallengeCore(options, serverSessionRole);
                if (cachedResult != S7Consts.errS7CommPlusLegacyAuthentication)
                {
                    return cachedResult;
                }

                options.Logger.LogDebug(
                    "Cached legacy S7CommPlus public key {Fingerprint} was rejected by PLC {Address}:{Port}; rediscovering a compatible key.",
                    cachedFingerprint,
                    options.Address,
                    options.Port);
                options.LegacyPublicKeyFingerprintOverride = null;
                m_LegacyPublicKeyFallbackFingerprints = null;
            }

            var candidateIndex = 0;
            while (true)
            {
                var attemptOptions = options;
                if (m_LegacyPublicKeyFallbackFingerprints != null)
                {
                    attemptOptions = options.Clone();
                    attemptOptions.LegacyPublicKeyFingerprintOverride =
                        m_LegacyPublicKeyFallbackFingerprints[candidateIndex];
                }

                var result = ConnectLegacyChallengeCore(attemptOptions, serverSessionRole);
                if (result == 0)
                {
                    return result;
                }
                if (result != S7Consts.errS7CommPlusLegacyAuthentication ||
                    m_LegacyPublicKeyFallbackFingerprints == null ||
                    ++candidateIndex >= m_LegacyPublicKeyFallbackFingerprints.Count)
                {
                    return result;
                }

                options.Logger.LogDebug(
                    "Legacy S7CommPlus public key {Fingerprint} was rejected by PLC {Address}:{Port}; retrying candidate {CandidateNumber} of {CandidateCount} on a fresh connection.",
                    m_LegacyPublicKeyFallbackFingerprints[candidateIndex - 1],
                    options.Address,
                    options.Port,
                    candidateIndex + 1,
                    m_LegacyPublicKeyFallbackFingerprints.Count);
                Thread.Sleep(250);
            }
        }

        private int ConnectLegacyChallengeCore(S7CommPlusClientOptions options, LegacyServerSessionRole serverSessionRole)
        {
            m_LegacyAttemptedPublicKeyFingerprint = null;
            if (options.ConnectTimeoutMilliseconds > 0)
            {
                m_ReadTimeout = Math.Min(
                    options.RequestTimeoutMilliseconds,
                    Math.Max(1000, options.ConnectTimeoutMilliseconds - 1000));
            }

            var elapsed = Environment.TickCount;
            PrepareClient(options.Address, options.RequestTimeoutMilliseconds, options.Port, options.LocalTsap, options.RemoteTsapBytes);
            var res = m_client.Connect();
            if (res != 0)
            {
                return res;
            }
            options.Logger.LogDebug("Legacy S7CommPlus COTP connection established to PLC {Address}:{Port}.", options.Address, options.Port);

            res = SendLegacyCreateObjectRequest(serverSessionRole);
            if (res != 0)
            {
                m_client.Disconnect();
                return res;
            }
            options.Logger.LogDebug("Legacy S7CommPlus CreateObject request sent to PLC {Address}:{Port}.", options.Address, options.Port);

            options.Logger.LogDebug("Legacy S7CommPlus CreateObject response received from PLC {Address}:{Port}.", options.Address, options.Port);

            res = m_client.SendEmptyDtData();
            if (res != 0)
            {
                m_client.Disconnect();
                return res;
            }

            var createObjectPdu = m_ReceivedPDU.ToArray();
            var createObjRes = CreateObjectResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (createObjRes == null || createObjRes.ObjectIds == null || createObjRes.ObjectIds.Count == 0)
            {
                m_client.Disconnect();
                return S7Consts.errIsoInvalidPDU6;
            }

            m_SessionId = createObjRes.ObjectIds[0];
            m_SessionId2 = createObjRes.ObjectIds.Count > 1 ? createObjRes.ObjectIds[1] : 0;

            if (!LegacyChallengeHandshake.TryParse(createObjectPdu, createObjRes, out var challenge))
            {
                m_client.Disconnect();
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }
            options.Logger.LogDebug(
                "Legacy S7CommPlus CSI challenge received from PLC {Address}:{Port}: session {SessionId:X8}, Harpo family {KeyFamily}, OMS family {OmsKeyFamily}, fingerprint {Fingerprint}, auth frame {AuthenticationFrameKind}.",
                options.Address,
                options.Port,
                challenge.SessionId,
                challenge.KeyFamily,
                challenge.OmsKeyFamily,
                challenge.Fingerprint,
                challenge.AuthenticationFrameKind);

            if (!TryCreateLegacyAuthentication(challenge, options, out var keyBlob, out var sessionKey, out var keyFamily, out var publicKey))
            {
                m_client.Disconnect();
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }

            var serverSessionVersion = TryGetServerSession(createObjRes);
            var isAutomaticPublicKeyAttempt =
                options.LegacyPublicKeyResolver == null &&
                string.IsNullOrWhiteSpace(options.LegacyPublicKeyId) &&
                (m_LegacyPublicKeyFallbackFingerprints != null ||
                 !string.IsNullOrWhiteSpace(options.LegacyPublicKeyFingerprintOverride));
            var authenticationReadTimeout = m_ReadTimeout;
            if (isAutomaticPublicKeyAttempt)
            {
                m_ReadTimeout = Math.Min(m_ReadTimeout, 1_000);
                m_client.SetTransportTimeouts(m_ReadTimeout, m_ReadTimeout);
            }
            try
            {
                res = SendLegacyAuthenticationRequest(challenge.SessionId, challenge.AuthenticationFrameKind, keyFamily, keyBlob, sessionKey, publicKey, serverSessionVersion, serverSessionRole);
            }
            finally
            {
                m_ReadTimeout = authenticationReadTimeout;
            }
            if (res != 0)
            {
                m_client.Disconnect();
                return isAutomaticPublicKeyAttempt
                    ? S7Consts.errS7CommPlusLegacyAuthentication
                    : res;
            }
            if (isAutomaticPublicKeyAttempt)
            {
                m_client.SetTransportTimeouts(
                    options.RequestTimeoutMilliseconds,
                    options.RequestTimeoutMilliseconds);
            }
            options.Logger.LogDebug("Legacy S7CommPlus authentication request sent to PLC {Address}:{Port}.", options.Address, options.Port);

            var setMultiVarRes = SetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (setMultiVarRes == null || setMultiVarRes.ReturnValue != 0)
            {
                m_client.Disconnect();
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }
            options.Logger.LogDebug("Legacy S7CommPlus challenge authentication accepted by PLC {Address}:{Port}.", options.Address, options.Port);

            m_LegacySessionKey = sessionKey;
            m_LegacyDigestActive = true;
            m_SequenceNumber = 2;
            m_IntegrityId = uint.MaxValue;

            options.Logger.LogDebug(
                "Legacy S7CommPlus connection to PLC {Address}:{Port} is using conservative default communication limits.",
                options.Address,
                options.Port);
            m_ReadTimeout = options.RequestTimeoutMilliseconds;

            var shouldLegitimate = !string.IsNullOrEmpty(options.Password) || !string.IsNullOrEmpty(options.Username);
            var serverSession = shouldLegitimate ? TryGetServerSession(createObjRes) : null;
            m_ServerSessionVersion = serverSessionVersion;
            if (serverSession != null)
            {
                res = legitimate(serverSession, options.Password, options.Username);
                if (res != 0)
                {
                    m_client.Disconnect();
                    return res;
                }
            }
            else if (!string.IsNullOrEmpty(options.Password))
            {
                m_client.Disconnect();
                return S7Consts.errCliFirmwareNotSupported;
            }

            StartLegacySessionKeyRefresh(options, keyFamily, publicKey);

            options.Logger.LogInformation("Legacy S7CommPlus challenge connection to PLC {Address}:{Port} established in {ElapsedMilliseconds} ms.", options.Address, options.Port, Environment.TickCount - elapsed);
            return 0;
        }

        private void StartLegacySessionKeyRefresh(
            S7CommPlusClientOptions options,
            EPublicKeyFamily keyFamily,
            byte[] publicKey)
        {
            StopLegacySessionKeyRefresh();
            if (!options.LegacySessionKeyRefreshEnabled)
            {
                options.Logger.LogDebug(
                    "Legacy S7CommPlus session-key refresh is disabled for PLC {Address}:{Port}.",
                    options.Address,
                    options.Port);
                return;
            }

            m_LegacySessionKeyFamily = keyFamily;
            m_LegacySessionPublicKey = (byte[])publicKey.Clone();
            m_LegacySessionKeyRefreshIntervalMilliseconds = options.LegacySessionKeyRefreshIntervalMilliseconds;
            m_LegacySessionKeyRefreshLogger = options.Logger;
            m_LegacySessionKeyRefreshEndpoint = $"{options.Address}:{options.Port}";

            var generation = Interlocked.Increment(ref m_LegacySessionKeyRefreshGeneration);
            m_LegacySessionKeyRefreshTimer = new Timer(
                RefreshLegacySessionKey,
                generation,
                m_LegacySessionKeyRefreshIntervalMilliseconds,
                Timeout.Infinite);
            options.Logger.LogDebug(
                "Legacy S7CommPlus session-key refresh scheduled for PLC {Address}:{Port} in {RefreshInterval}.",
                options.Address,
                options.Port,
                options.LegacySessionKeyRefreshInterval);
        }

        private void RefreshLegacySessionKey(object state)
        {
            if (state is not int generation
                || generation != Volatile.Read(ref m_LegacySessionKeyRefreshGeneration))
            {
                return;
            }

            var logger = m_LegacySessionKeyRefreshLogger;
            var endpoint = m_LegacySessionKeyRefreshEndpoint;
            int result;
            try
            {
                lock (m_RequestLock)
                {
                    if (generation != Volatile.Read(ref m_LegacySessionKeyRefreshGeneration)
                        || m_client?.Connected != true
                        || !m_LegacyDigestActive
                        || m_LegacySessionPublicKey == null)
                    {
                        return;
                    }

                    var publicKey = m_LegacySessionPublicKey;
                    var keyFamily = m_LegacySessionKeyFamily;
                    result = RenewLegacySessionKey(keyFamily, publicKey);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Legacy S7CommPlus session-key refresh failed for PLC {Endpoint}.", endpoint);
                result = S7Consts.errS7CommPlusLegacyAuthentication;
            }

            if (result != 0)
            {
                logger?.LogError(
                    "Legacy S7CommPlus session-key refresh failed for PLC {Endpoint} with error {ErrorCode}; closing the stale session.",
                    endpoint,
                    result);
                CloseTransport();
                return;
            }

            logger?.LogInformation("Legacy S7CommPlus session key refreshed for PLC {Endpoint}.", endpoint);
            if (generation == Volatile.Read(ref m_LegacySessionKeyRefreshGeneration))
            {
                m_LegacySessionKeyRefreshTimer?.Change(
                    m_LegacySessionKeyRefreshIntervalMilliseconds,
                    Timeout.Infinite);
            }
        }

        private int RenewLegacySessionKey(EPublicKeyFamily keyFamily, byte[] publicKey)
        {
            var challengeRequest = new GetVarSubstreamedRequest(ProtocolVersion.V2)
            {
                InObjectId = m_SessionId,
                Address = Ids.ServerSessionRequest
            };
            var result = SendS7plusFunctionObjectAndWait(challengeRequest, m_ReadTimeout);
            if (result != 0)
            {
                return result;
            }

            var challengeResponse = GetVarSubstreamedResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (challengeResponse == null
                || challengeResponse.ReturnValue != 0
                || challengeResponse.Value is not ValueUSIntArray challengeValue)
            {
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }

            var challenge = challengeValue.GetValue();
            if (challenge == null || challenge.Length != LegacyOmsConstants.ChallengeLength)
            {
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }

            var newSessionKey = new byte[Constants.SessionKeyLength];
            var blobLength = keyFamily == EPublicKeyFamily.PlcSim
                ? CommonConstants.EncryptedBlobLengthPlcSim
                : CommonConstants.EncryptedBlobLengthRealPlc;
            var keyBlob = new byte[blobLength];
            LegacyAuthenticationScheme.Authenticate(
                keyBlob.AsSpan(),
                newSessionKey.AsSpan(),
                challenge.AsSpan(),
                publicKey.AsSpan(),
                keyFamily);

            Span<byte> publicKeyId = stackalloc byte[Constants.KeyIdLength];
            Span<byte> sessionKeyId = stackalloc byte[Constants.KeyIdLength];
            publicKey.AsSpan().DeriveKeyId(publicKeyId);
            newSessionKey.AsSpan().DeriveKeyId(sessionKeyId);

            var renewalRequest = LegacyChallengeHandshake.CreateSessionKeyRenewalRequest(
                keyFamily,
                m_SessionId,
                publicKeyId,
                sessionKeyId,
                keyBlob);
            if (renewalRequest == null)
            {
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }

            result = SendS7plusFunctionObjectAndWait(renewalRequest, m_ReadTimeout);
            if (result != 0)
            {
                return result;
            }

            var renewalResponse = SetVariableResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (renewalResponse == null || renewalResponse.ReturnValue != 0)
            {
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }

            // The PLC signs the response with the old key. Switch only after that
            // response has been received and verified at the protocol boundary.
            m_LegacySessionKey = newSessionKey;
            return 0;
        }

        private void StopLegacySessionKeyRefresh()
        {
            Interlocked.Increment(ref m_LegacySessionKeyRefreshGeneration);
            var timer = Interlocked.Exchange(ref m_LegacySessionKeyRefreshTimer, null);
            timer?.Dispose();
            m_LegacySessionPublicKey = null;
            m_LegacySessionKeyRefreshLogger = null;
            m_LegacySessionKeyRefreshEndpoint = null;
            m_LegacySessionKeyRefreshIntervalMilliseconds = 0;
        }

        private int SendLegacyCreateObjectRequest(LegacyServerSessionRole serverSessionRole)
        {
            var request = new CreateObjectRequest(ProtocolVersion.V1, 0, false);
            request.SetTiaServerSessionData(serverSessionRole);
            return SendS7plusFunctionObjectAndWait(request, m_ReadTimeout);
        }

        private static ValueStruct TryGetServerSession(CreateObjectResponse createObjRes)
        {
            try
            {
                return createObjRes.ResponseObject.GetAttribute(Ids.ServerSessionVersion) as ValueStruct;
            }
            catch
            {
                return null;
            }
        }

        private bool TryCreateLegacyAuthentication(
            LegacyChallengeHandshake challenge,
            S7CommPlusClientOptions options,
            out byte[] keyBlob,
            out byte[] sessionKey,
            out EPublicKeyFamily keyFamily,
            out byte[] publicKey)
        {
            keyBlob = null;
            sessionKey = null;
            keyFamily = default;
            publicKey = null;

            try
            {
                publicKey = options.LegacyPublicKeyResolver?.Invoke(challenge.Fingerprint);
                if (publicKey == null || publicKey.Length == 0)
                {
                    var publicKeyFingerprint = options.GetLegacyPublicKeyFingerprint(challenge.Fingerprint);
                    if (publicKeyFingerprint != null &&
                        publicKeyFingerprint.Length == 2 &&
                        options.LegacyPublicKeyFallbackEnabled)
                    {
                        var fallbackFingerprints =
                            LegacyPublicKeyCatalog.GetFingerprints(publicKeyFingerprint);
                        if (fallbackFingerprints.Count > 0)
                        {
                            m_LegacyPublicKeyFallbackFingerprints = fallbackFingerprints;
                            publicKeyFingerprint = fallbackFingerprints[0];
                        }
                        else
                        {
                            publicKeyFingerprint = null;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(publicKeyFingerprint) || publicKeyFingerprint.Length == 2)
                    {
                        options.Logger.LogDebug(
                            "Legacy S7CommPlus PLC {Address}:{Port} reported incomplete public-key fingerprint {Fingerprint}, and automatic public-key fallback is {FallbackState}.",
                            options.Address,
                            options.Port,
                            challenge.Fingerprint,
                            options.LegacyPublicKeyFallbackEnabled ? "unavailable" : "disabled");
                        return false;
                    }
                    if (!string.Equals(publicKeyFingerprint, challenge.Fingerprint, StringComparison.Ordinal))
                    {
                        options.Logger.LogDebug(
                            "Legacy S7CommPlus PLC {Address}:{Port} reported family-only public-key fingerprint {Fingerprint}; using public-key fingerprint {PublicKeyFingerprint}.",
                            options.Address,
                            options.Port,
                            challenge.Fingerprint,
                            publicKeyFingerprint);
                    }

                    m_LegacyAttemptedPublicKeyFingerprint = publicKeyFingerprint;
                    var store = new DefaultPublicKeyStore();
                    publicKey = new byte[store.GetPublicKeyLength(publicKeyFingerprint)];
                    store.ReadPublicKey(publicKey.AsSpan(), publicKeyFingerprint);
                }

                keyFamily = challenge.KeyFamily;
                sessionKey = new byte[Constants.SessionKeyLength];
                var blobLength = keyFamily == EPublicKeyFamily.PlcSim
                    ? CommonConstants.EncryptedBlobLengthPlcSim
                    : CommonConstants.EncryptedBlobLengthRealPlc;
                keyBlob = new byte[blobLength];
                LegacyAuthenticationScheme.Authenticate(
                    keyBlob.AsSpan(),
                    sessionKey.AsSpan(),
                    challenge.Challenge.AsSpan(),
                    publicKey.AsSpan(),
                    keyFamily);
                return true;
            }
            catch (UnknownPublicKeyException ex)
            {
                options.Logger.LogDebug(ex, "Legacy S7CommPlus public key {Fingerprint} is not available.", challenge.Fingerprint);
                return false;
            }
            catch (Exception ex)
            {
                options.Logger.LogDebug(ex, "Legacy S7CommPlus authentication blob creation failed for PLC {Address}:{Port}.", options.Address, options.Port);
                return false;
            }
        }

        private int SendLegacyAuthenticationRequest(
            uint sessionId,
            LegacyAuthenticationFrameKind authenticationFrameKind,
            EPublicKeyFamily keyFamily,
            byte[] keyBlob,
            byte[] sessionKey,
            byte[] publicKey,
            ValueStruct serverSessionVersion,
            LegacyServerSessionRole serverSessionRole)
        {
            Span<byte> publicKeyId = stackalloc byte[Constants.KeyIdLength];
            Span<byte> sessionKeyId = stackalloc byte[Constants.KeyIdLength];
            publicKey.AsSpan().DeriveKeyId(publicKeyId);
            sessionKey.AsSpan().DeriveKeyId(sessionKeyId);

            var request = LegacyChallengeHandshake.CreateAuthenticationRequest(
                keyFamily,
                sessionId,
                publicKeyId,
                sessionKeyId,
                keyBlob,
                serverSessionVersion,
                serverSessionRole,
                authenticationFrameKind);
            if (request == null)
            {
                return S7Consts.errS7CommPlusLegacyAuthentication;
            }

            using var stream = new MemoryStream();
            request.Serialize(stream);
            return SendRawS7plusPduAndWait(stream.ToArray(), (int)stream.Length, request.ProtocolVersion, m_ReadTimeout);
        }

        private bool TryWriteLegacyDigest(byte[] destination, int digestOffset, byte[] data, int dataOffset, int dataLength)
        {
            if (!ShouldUseLegacyDigest(ProtocolVersion.V3))
            {
                return false;
            }

            HarpoPacketDigest.CalculateDigest(
                destination.AsSpan(digestOffset + 1, LegacyDigestLength),
                data.AsSpan(dataOffset, dataLength),
                m_LegacySessionKey.AsSpan());
            destination[digestOffset] = LegacyDigestLength;
            return true;
        }

        private bool TryVerifyLegacyDigest(
            byte[] digestBuffer,
            int digestOffset,
            byte[] data,
            int dataOffset,
            int dataLength)
        {
            if (!ShouldUseLegacyDigest(ProtocolVersion.V3))
            {
                return false;
            }

            if (digestBuffer[digestOffset] != LegacyDigestLength)
            {
                return false;
            }

            Span<byte> expected = stackalloc byte[LegacyDigestLength];
            HarpoPacketDigest.CalculateDigest(expected, data.AsSpan(dataOffset, dataLength), m_LegacySessionKey.AsSpan());
            return expected.SequenceEqual(digestBuffer.AsSpan(digestOffset + 1, LegacyDigestLength));
        }

        private void TraceLegacyDigestReceive(
            byte[] pdu,
            int pduLength,
            int digestOffset,
            int bodyOffset,
            int bodyLength,
            int previousBodyLength,
            byte[] accumulatedData,
            int accumulatedDataOffset,
            int accumulatedDataLength,
            bool verified,
            bool matched)
        {
            var traceDirectory = Environment.GetEnvironmentVariable("S7COMMPLUS_LEGACY_DIGEST_TRACE_DIR");
            if (string.IsNullOrWhiteSpace(traceDirectory) || !ShouldUseLegacyDigest(ProtocolVersion.V3))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(traceDirectory);
                Span<byte> currentBodyDigest = stackalloc byte[LegacyDigestLength];
                Span<byte> accumulatedDigest = stackalloc byte[LegacyDigestLength];
                HarpoPacketDigest.CalculateDigest(currentBodyDigest, pdu.AsSpan(bodyOffset, bodyLength), m_LegacySessionKey.AsSpan());
                HarpoPacketDigest.CalculateDigest(accumulatedDigest, accumulatedData.AsSpan(accumulatedDataOffset, accumulatedDataLength), m_LegacySessionKey.AsSpan());

                var receivedDigest = pdu.AsSpan(digestOffset + 1, LegacyDigestLength);
                var includeRaw = string.Equals(
                    Environment.GetEnvironmentVariable("S7COMMPLUS_LEGACY_DIGEST_TRACE_INCLUDE_RAW"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
                var sequence = Interlocked.Increment(ref m_LegacyDigestTraceSequence);
                var path = Path.Combine(traceDirectory, $"legacy-digest-{Process.GetCurrentProcess().Id}.jsonl");
                var json = new StringBuilder(768);
                json.Append('{');
                AppendJson(json, "sequence", sequence);
                AppendJson(json, "pduLength", pduLength);
                AppendJson(json, "bodyLength", bodyLength);
                AppendJson(json, "previousBodyLength", previousBodyLength);
                AppendJson(json, "accumulatedLength", accumulatedDataLength);
                AppendJson(json, "verifiedByDriver", verified);
                AppendJson(json, "matchedByDriver", matched);
                AppendJson(json, "currentBodyMatches", receivedDigest.SequenceEqual(currentBodyDigest));
                AppendJson(json, "accumulatedBodyMatches", receivedDigest.SequenceEqual(accumulatedDigest));
                AppendJson(json, "receivedDigest", RuntimeCompatibility.ToHexString(receivedDigest));
                AppendJson(json, "currentBodyDigest", RuntimeCompatibility.ToHexString(currentBodyDigest));
                AppendJson(json, "accumulatedBodyDigest", RuntimeCompatibility.ToHexString(accumulatedDigest));
                AppendJson(json, "bodySha256", RuntimeCompatibility.ToHexString(RuntimeCompatibility.Sha256(pdu.AsSpan(bodyOffset, bodyLength))));
                AppendJson(json, "accumulatedSha256", RuntimeCompatibility.ToHexString(RuntimeCompatibility.Sha256(accumulatedData.AsSpan(accumulatedDataOffset, accumulatedDataLength))));
                if (string.Equals(
                    Environment.GetEnvironmentVariable("S7COMMPLUS_LEGACY_DIGEST_TRACE_BRUTE_SUFFIX"),
                    "1",
                    StringComparison.OrdinalIgnoreCase))
                {
                    AppendJson(
                        json,
                        "currentSuffixMatchOffset",
                        FindLegacyDigestSuffixMatch(
                            ReadOnlySpan<byte>.Empty,
                            pdu.AsSpan(bodyOffset, bodyLength),
                            receivedDigest));
                    AppendJson(
                        json,
                        "previousPlusSuffixMatchOffset",
                        previousBodyLength == 0
                            ? -1
                            : FindLegacyDigestSuffixMatch(
                                accumulatedData.AsSpan(accumulatedDataOffset, previousBodyLength),
                                pdu.AsSpan(bodyOffset, bodyLength),
                                receivedDigest));
                }
                if (includeRaw)
                {
                    AppendJson(json, "pduHex", RuntimeCompatibility.ToHexString(pdu.AsSpan(0, pduLength)));
                    AppendJson(json, "bodyHex", RuntimeCompatibility.ToHexString(pdu.AsSpan(bodyOffset, bodyLength)));
                    AppendJson(json, "accumulatedHex", RuntimeCompatibility.ToHexString(accumulatedData.AsSpan(accumulatedDataOffset, accumulatedDataLength)));
                }

                if (json[json.Length - 1] == ',')
                {
                    json.Length--;
                }
                json.Append('}');
                json.AppendLine();
                File.AppendAllText(path, json.ToString());
            }
            catch
            {
                // Diagnostics must never affect PLC communication.
            }
        }

        private int FindLegacyDigestSuffixMatch(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> body, ReadOnlySpan<byte> receivedDigest)
        {
            Span<byte> expected = stackalloc byte[LegacyDigestLength];
            if (prefix.Length == 0)
            {
                for (var offset = 0; offset <= body.Length; offset++)
                {
                    HarpoPacketDigest.CalculateDigest(expected, body[offset..], m_LegacySessionKey.AsSpan());
                    if (expected.SequenceEqual(receivedDigest))
                    {
                        return offset;
                    }
                }

                return -1;
            }

            for (var offset = 0; offset <= body.Length; offset++)
            {
                var candidate = new byte[prefix.Length + body.Length - offset];
                prefix.CopyTo(candidate);
                body[offset..].CopyTo(candidate.AsSpan(prefix.Length));
                HarpoPacketDigest.CalculateDigest(expected, candidate, m_LegacySessionKey.AsSpan());
                if (expected.SequenceEqual(receivedDigest))
                {
                    return offset;
                }
            }

            return -1;
        }
#else
        private void StopLegacySessionKeyRefresh()
        {
        }

        private int ConnectLegacyChallenge(S7CommPlusClientOptions options)
        {
            return S7Consts.errCliFunctionNotImplemented;
        }

        private bool TryWriteLegacyDigest(byte[] destination, int digestOffset, byte[] data, int dataOffset, int dataLength)
        {
            return false;
        }

        private bool TryVerifyLegacyDigest(byte[] digestBuffer, int digestOffset, byte[] data, int dataOffset, int dataLength)
        {
            return false;
        }

        private void TraceLegacyDigestReceive(
            byte[] pdu,
            int pduLength,
            int digestOffset,
            int bodyOffset,
            int bodyLength,
            int previousBodyLength,
            byte[] accumulatedData,
            int accumulatedDataOffset,
            int accumulatedDataLength,
            bool verified,
            bool matched)
        {
        }
#endif

        private static void AppendJson(StringBuilder builder, string name, long value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value).Append(',');
        }

        private static void AppendJson(StringBuilder builder, string name, bool value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value ? "true" : "false").Append(',');
        }

        private static void AppendJson(StringBuilder builder, string name, string value)
        {
            builder.Append('"').Append(name).Append("\":\"").Append(value).Append("\",");
        }

        private bool ShouldUseLegacyDigest(byte protocolVersion)
        {
            return m_LegacyDigestActive
                && protocolVersion == ProtocolVersion.V3
                && m_LegacySessionKey != null
                && m_LegacySessionKey.Length >= 24;
        }
    }
}
