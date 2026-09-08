#region License
/******************************************************************************
 * S7CommPlusDriver
 *
 * Based on Snap7 (Sharp7.cs) by Davide Nardella licensed under LGPL
 *
 /****************************************************************************/
#endregion

using OpenSsl;
using S7CommPlusDriver.Internal;
using S7CommPlusDriver.Tls;
using System;
using System.IO;
using System.Threading;

namespace S7CommPlusDriver
{
	// Teilweise basierend auf Snap7 (Sharp7.cs) von Davide Nardella
	// |  Sharp7 is free software: you can redistribute it and/or modify              |
	// |  it under the terms of the Lesser GNU General Public License as published by |
	// |  the Free Software Foundation, either version 3 of the License, or           |
	// |  (at your option) any later version.                                         |
	public class S7Client : IS7TlsConnectorCallback, IDisposable
	{
		public static bool WriteSslKeyToFile;
		public static string WriteSslKeyPath;

        #region [Constants and TypeDefs]

        public int _LastError = 0;

		#endregion

		#region [S7 Telegrams]

		// ISO Connection Request telegram (contains also ISO Header and COTP Header)
		byte[] ISO_CR = {
			// TPKT (RFC1006 Header)
			0x03, // RFC 1006 ID (3)
			0x00, // Reserved, always 0
			0x00, // High part of packet lenght (entire frame, payload and TPDU included)
			0x24, // Low part of packet lenght (entire frame, payload and TPDU included)
			// COTP (ISO 8073 Header)
			0x1f, // PDU Size Length
			0xE0, // CR - Connection Request ID
			0x00, // Dst Reference HI
			0x00, // Dst Reference LO
			0x00, // Src Reference HI
			0x01, // Src Reference LO
			0x00, // Class + Options Flags
			0xC0, // PDU Max Length ID
			0x01, // PDU Max Length HI
			0x0A, // PDU Max Length LO
			0xC1, // Src TSAP Identifier
			0x02, // Src TSAP Length (2 bytes)
			0x01, // Src TSAP HI (will be overwritten)
			0x00, // Src TSAP LO (will be overwritten)
			0xC2, // Dst TSAP Identifier
			0x10, // Dst TSAP Length (16 bytes)
			// Ab hier TSAP ID (String)
			// SIMATIC-ROOT-HMI
		};

		// TPKT + ISO COTP Header (Connection Oriented Transport Protocol)
		byte[] TPKT_ISO = { // 7 bytes
			0x03,0x00,
			0x00,0x1f,      // Telegram Length (Data Size + 31 or 35)
			0x02,0xf0,0x80  // COTP (see above for info)
		};

		#endregion

		#region S7commPlus

		bool m_SslActive = false;
		Thread m_runThread;
		volatile bool m_runThread_DoStop;
		readonly object m_cleanupLock = new object();
		IntPtr m_ptr_ssl_method;
		IntPtr m_ptr_ctx;
		IS7TlsConnector m_sslconn;
		S7CommPlusTlsBackend m_TlsBackend = S7CommPlusTlsBackend.OpenSsl;
		public string LastErrorDetail { get; private set; } = string.Empty;

		DateTime m_DateTimeStarted;
		Native.SSL_CTX_keylog_cb_func m_keylog_cb;

		// OpenSSL m�chte Daten auf den Socket aussenden.
		public void WriteData(byte[] pData, int dataLength)
		{
			// SSL fordert Daten zum Absenden an
			// System.Diagnostics.Trace.WriteLine("S7Client - OpenSSL WriteData: dataLength=" + dataLength);
			int offset = 0;
			int maxPayloadSize = Math.Min(MaxPduSizeToRequest, PDU.Length - IsoHSize);
			if (maxPayloadSize <= 0)
			{
				_LastError = S7Consts.errIsoInvalidPDU;
				return;
			}
			while (offset < dataLength && _LastError == 0)
			{
				int chunkSize = Math.Min(maxPayloadSize, dataLength - offset);
				byte[] sendData = new byte[chunkSize];
				Array.Copy(pData, offset, sendData, 0, chunkSize);
				SendIsoPacket(sendData);
				offset += chunkSize;
			}
		}

		// OpenSSL meldet fertige Daten (decrypted) zum einlesen
		public void OnDataAvailable()
		{
			// Netzwerk meldet eintreffende Daten
			byte[] buf = new byte[8192];
			int bytesRead = m_sslconn.Receive(ref buf, buf.Length);
			// System.Diagnostics.Trace.WriteLine("S7Client - OpenSSL OnDataAvailable: bytesRead=" + bytesRead);
			byte[] readData = new byte[bytesRead];
			Array.Copy(buf, readData, bytesRead);
			OnDataReceived?.Invoke(readData, bytesRead);
		}

		public void OnSslError(int sslError, string sslState)
		{
			_LastError = S7Consts.errOpenSSL;
			LastErrorDetail = string.IsNullOrWhiteSpace(sslState)
				? $"OpenSSL reported SSL error {sslError}."
				: $"OpenSSL reported SSL error {sslError}: {sslState}";
			NotifyReceiveError(_LastError);
		}

		// OpenSSL Key Callback Funktion. Gibt die ausgehandelden privaten Schl�ssel aus. Kann beispielsweise
		// in eine Wireshark Aufzeichnung eingef�gt werden um dort die TLS Kommunikation zu entschl�sseln.
		public void SSL_CTX_keylog_cb(IntPtr ssl, string line)
		{
			// Never unwind a managed exception through an OpenSSL callback.
			try
			{
				string filename = "key_" + m_DateTimeStarted.ToString("yyyyMMdd_HHmmss") + ".log";
				if (WriteSslKeyPath != null)
					filename = Path.Combine(WriteSslKeyPath, filename);
				using StreamWriter file = new StreamWriter(filename, append: true);
				file.WriteLine(line);
			}
			catch (Exception ex)
			{
				LastErrorDetail = $"TLS key logging failed: {ex.GetType().Name}: {ex.Message}";
			}
		}

		// Startet OpenSSL und aktiviert ab jetzt TLS
		public int SslActivate(S7CommPlusTlsBackend tlsBackend = S7CommPlusTlsBackend.OpenSsl)
		{
			int ret;
			LastErrorDetail = string.Empty;
			try
			{
				m_TlsBackend = tlsBackend;
				if (m_TlsBackend == S7CommPlusTlsBackend.BouncyCastle)
				{
					var bouncyCastleConnector = new BouncyCastleTlsConnector(this);
					m_sslconn = bouncyCastleConnector;
					m_SslActive = true;
					bouncyCastleConnector.StartHandshake();
					return 0;
				}

				ret = Native.OPENSSL_init_ssl(0, IntPtr.Zero); // returns 1 on success or 0 on error
				if (ret != 1)
				{
					LastErrorDetail = "OpenSSL initialization failed.";
					return S7Consts.errOpenSSL;
				}
				m_ptr_ssl_method = Native.ExpectNonNull(Native.TLS_client_method());
				m_ptr_ctx = Native.ExpectNonNull(Native.SSL_CTX_new(m_ptr_ssl_method));
				// TLS 1.3 forcieren, da wegen TLS on IsoOnTCP bekannt sein muss, um wie viele Bytes sich die verschl�sselten
				// Daten verl�ngern um die Pakete auf S7CommPlus-Ebene entsprechend zu fragmentieren.
				// Die Verl�ngerung geschieht z.B. durch Padding und HMAC. Bei TLS 1.3 existiert mit GCM kein Padding und verl�ngert sich immer
				// um 16 Bytes. Da auch TLS_CHACHA20_POLY1305_SHA256 zu den TLS 1.3  CipherSuite z�hlt, explizit die anderen setzen.
				Native.SSL_CTX_ctrl(m_ptr_ctx, Native.SSL_CTRL_SET_MIN_PROTO_VERSION, Native.TLS1_3_VERSION, IntPtr.Zero);
				ret = Native.SSL_CTX_set_ciphersuites(m_ptr_ctx, "TLS_AES_256_GCM_SHA384:TLS_AES_128_GCM_SHA256");
				if (ret != 1)
				{
					LastErrorDetail = "OpenSSL rejected the configured TLS 1.3 cipher suites.";
					return S7Consts.errOpenSSL;
				}
				m_sslconn = new OpenSslTlsConnector(m_ptr_ctx, this);

				// Keylog callback setzen
				if (WriteSslKeyToFile)
				{
					m_keylog_cb = new Native.SSL_CTX_keylog_cb_func(SSL_CTX_keylog_cb);
					Native.SSL_CTX_set_keylog_callback(m_ptr_ctx, m_keylog_cb);
				}

				m_SslActive = true;
			}
			catch (Exception ex)
			{
				LastErrorDetail = CreateOpenSslActivationError(tlsBackend, ex);
				m_SslActive = false;
				m_sslconn?.Dispose();
				m_sslconn = null;
				return S7Consts.errOpenSSL;
			}
			return 0;
		}

		private static string CreateOpenSslActivationError(S7CommPlusTlsBackend tlsBackend, Exception exception)
		{
			return tlsBackend == S7CommPlusTlsBackend.OpenSsl
				? $"OpenSSL backend could not load or initialize its native dependencies for process architecture {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}. {exception.GetType().Name}: {exception.Message}"
				: $"TLS backend {tlsBackend} could not initialize. {exception.GetType().Name}: {exception.Message}";
		}

		// Deaktiviert TLS
		public void SslDeactivate()
		{
			m_SslActive = false;
			try
			{
				Interlocked.Exchange(ref m_sslconn, null)?.Dispose();
			}
			finally
			{
				ReleaseSslContext();
			}
		}

		private void ReleaseSslContext()
		{
			var context = Interlocked.Exchange(ref m_ptr_ctx, IntPtr.Zero);
			if (context != IntPtr.Zero)
				Native.SSL_CTX_free(context);
		}

		/// <summary>
		/// Closes the active transport and TLS resources, then prevents the finalizer from repeating cleanup.
		/// </summary>
		public void Dispose()
		{
			Dispose(DefaultTimeout);
		}

		internal int Dispose(int timeoutMilliseconds)
		{
			try
			{
				return Disconnect(timeoutMilliseconds);
			}
			finally
			{
				GC.SuppressFinalize(this);
			}
		}
		#endregion

		private void StartThread()
		{
			m_runThread_DoStop = false;
			m_runThread = new Thread(RunThread) { IsBackground = true, Name = "S7CommPlus receive" };
			m_runThread.Start();
		}

		// Der Task der kontinuierlich ausgef�hrt wird
		private void RunThread()
		{
			try
			{
				ReceiveLoop();
			}
			catch (Exception ex)
			{
				if (!m_runThread_DoStop)
				{
					LastErrorDetail = $"Receive failed: {ex.GetType().Name}: {ex.Message}";
					_LastError = S7Consts.errTCPDataReceive;
					NotifyReceiveError(_LastError);
				}
			}
			finally
			{
				m_runThread_DoStop = true;
				try
				{
					Interlocked.Exchange(ref Socket, null)?.Close();
				}
				catch (Exception ex)
				{
					LastErrorDetail += $" Transport cleanup failed: {ex.GetType().Name}: {ex.Message}";
				}
				// This also handles disconnect from a callback or a timed-out join:
				// TLS must remain alive until the receive callback has returned.
				lock (m_cleanupLock)
				{
					TryDeactivateSsl();
				}
			}
		}

		private void NotifyReceiveError(int error)
		{
			try
			{
				OnReceiveError?.Invoke(error);
			}
			catch (Exception ex)
			{
				LastErrorDetail += $" Receive error callback failed: {ex.GetType().Name}: {ex.Message}";
			}
		}

		private int TryDeactivateSsl()
		{
			try
			{
				SslDeactivate();
				return 0;
			}
			catch (Exception ex)
			{
				_LastError = S7Consts.errOpenSSL;
				LastErrorDetail = $"TLS cleanup failed: {ex.GetType().Name}: {ex.Message}";
				return _LastError;
			}
		}

		private void ReceiveLoop()
		{
			int Length;
			while (!m_runThread_DoStop)
			{
				// Versuchen zu lesen
				_LastError = 0;
				Length = RecvIsoPacket();
				if (m_runThread_DoStop)
					break;
				if (Length > 0) {
					byte[] Buffer = new byte[Length - TPKT_ISO.Length];
					Array.Copy(PDU, TPKT_ISO.Length, Buffer, 0, Length - TPKT_ISO.Length);
					int Size = Length - TPKT_ISO.Length;
					if (m_SslActive)
					{
						// Durch SSL eingelesene Daten an SSL weiterleiten
						m_sslconn.ReadCompleted(Buffer, Size);
					} else {
						// Wenn etwas gelesen werden konnte, Client benachrichtigen
						OnDataReceived?.Invoke(Buffer, Size);
					}
				}
				else if (_LastError != 0 && _LastError != S7Consts.errTCPReceiveTimeout)
				{
					NotifyReceiveError(_LastError);
					break;
				}
			}
		}

		public _OnDataReceived OnDataReceived;
		public delegate void _OnDataReceived(byte[] PDU, int len);
		public Action<int> OnReceiveError;

		#region [Internals]

		// Defaults
		private static int ISOTCP = 102; // ISOTCP Port
		private static int MinPduSizeToRequest = 240;
		private static int MaxPduSizeToRequest = 960;
		private static int DefaultTimeout = 2000;
		private static int IsoHSize = 7; // TPKT+COTP Header Size

		// Properties
		private int _PDULength = 0;
		private int _PduSizeRequested = 480;
		private int _PLCPort = ISOTCP;
		private int _RecvTimeout = DefaultTimeout;
		private int _SendTimeout = DefaultTimeout;
		private int _ConnTimeout = DefaultTimeout;

		// Privates
		private string IPAddress;
		private byte LocalTSAP_HI;
		private byte LocalTSAP_LO;
		private byte[] RemoteTSAP_S;
		private byte LastPDUType;
		private byte[] PDU = new byte[2048];
		private IS7Transport Socket = null;
		private readonly Func<IS7Transport> _transportFactory;
		private int Time_ms = 0;

		private void CreateSocket()
		{
			try
			{
				Socket = _transportFactory();
			}
			catch
			{
			}
		}

		private int TCPConnect()
		{
			if (_LastError == 0)
				try
				{
					_LastError = Socket.Connect(IPAddress, _PLCPort, _ConnTimeout, _RecvTimeout, _SendTimeout);
				}
				catch
				{
					_LastError = S7Consts.errTCPConnectionFailed;
				}
			return _LastError;
		}

		/// <summary>
		/// Receives one transport fragment through a stable reference so concurrent disconnect can detach the client
		/// transport without causing a null-reference race in the receive thread.
		/// </summary>
		/// <param name="Buffer">Destination buffer for the received bytes.</param>
		/// <param name="Start">Zero-based destination offset.</param>
		/// <param name="Size">Number of bytes expected from the transport.</param>
		private void RecvPacket(byte[] Buffer, int Start, int Size)
		{
			var socket = Socket;
			if (socket != null && socket.Connected)
				_LastError = socket.Receive(Buffer, Start, Size);
			else
				_LastError = S7Consts.errTCPNotConnected;
		}

		/// <summary>
		/// Sends one transport fragment through a stable reference so concurrent disconnect reports a connection error
		/// instead of dereferencing a transport that has just been detached.
		/// </summary>
		/// <param name="Buffer">Buffer containing the bytes to transmit.</param>
		/// <param name="Len">Number of bytes to transmit.</param>
		private void SendPacket(byte[] Buffer, int Len)
		{
			var socket = Socket;
			_LastError = socket != null
				? socket.Send(Buffer, Len)
				: S7Consts.errTCPNotConnected;
		}

		private void SendPacket(byte[] Buffer)
		{
			if (Connected)
				SendPacket(Buffer, Buffer.Length);
			else
				_LastError = S7Consts.errTCPNotConnected;
		}

		public void Send(byte[] Buffer)
		{
			_LastError = 0;
			if (m_SslActive)
			{
				m_sslconn.Write(Buffer, Buffer.Length);
			}
			else
			{
				SendIsoPacket(Buffer);
			}
		}

		internal int SendEmptyDtData()
		{
			byte[] emptyDtData = { 0x03, 0x00, 0x00, 0x07, 0x02, 0xF0, 0x00 };
			SendPacket(emptyDtData);
			return _LastError;
		}

		private int SendIsoPacket(byte[] Buffer)
		{
			// Packt die zu sendenden Daten in den Iso-Header ein.
			int Size = Buffer.Length;
			_LastError = 0;

			Array.Copy(TPKT_ISO, 0, PDU, 0, TPKT_ISO.Length);
			SetWordAt(PDU, 2, (ushort)(Size + TPKT_ISO.Length));
			try
			{
				Array.Copy(Buffer, 0, PDU, TPKT_ISO.Length, Size);
			}
			catch
			{
				return S7Consts.errIsoInvalidPDU;
			}
			SendPacket(PDU, TPKT_ISO.Length + Size);

			return _LastError;
		}

		private UInt16 GetWordAt(byte[] Buffer, int Pos)
		{
			return (UInt16)((Buffer[Pos] << 8) | Buffer[Pos + 1]);
		}

		private void SetWordAt(byte[] Buffer, int Pos, UInt16 Value)
		{
			Buffer[Pos] = (byte)(Value >> 8);
			Buffer[Pos + 1] = (byte)(Value & 0x00FF);
		}

		private int RecvIsoPacket()
		{
			Boolean Done = false;
			int Size = 0;
			int emptyDataPacketCount = 0;
			while ((_LastError == 0) && !Done)
			{
				// Get TPKT (4 bytes)
				RecvPacket(PDU, 0, 4);
				if (_LastError == 0)
				{
					Size = GetWordAt(PDU, 2);
					if (Size < IsoHSize || Size > PDU.Length)
					{
						_LastError = S7Consts.errIsoInvalidPDU;
						break;
					}
					// Check 0 bytes Data Packet (only TPKT+COTP = 7 bytes)
					if (Size == IsoHSize)
					{
						RecvPacket(PDU, 4, 3); // Skip remaining 3 bytes and Done is still false
						emptyDataPacketCount++;
						if (emptyDataPacketCount > 16)
						{
							_LastError = S7Consts.errIsoInvalidPDU;
						}
					}
					else
						Done = true;
				}
			}
			if (_LastError == 0)
			{
				RecvPacket(PDU, 4, 3); // Skip remaining 3 COTP bytes
				LastPDUType = PDU[5];   // Stores PDU Type, we need it
										// Receives the S7 Payload
				RecvPacket(PDU, 7, Size - IsoHSize);
			}
			if (_LastError == 0)
				return Size;
			else
				return 0;
		}

		private int ISOConnect()
		{
			int Size;
			byte[] isocon = new byte[ISO_CR.Length + RemoteTSAP_S.Length];
			ISO_CR[16] = LocalTSAP_HI;
			ISO_CR[17] = LocalTSAP_LO;

			ISO_CR[3] = (byte)(20 + RemoteTSAP_S.Length);
			ISO_CR[4] = (byte)(15 + RemoteTSAP_S.Length);
			ISO_CR[19] = (byte)RemoteTSAP_S.Length;

			Array.Copy(ISO_CR, isocon, 20);
			Array.Copy(RemoteTSAP_S, 0, isocon, 20, RemoteTSAP_S.Length);

			// Sends the connection request telegram
			SendPacket(isocon);
			if (_LastError == 0)
			{
				// Gets the reply (if any)
				Size = RecvIsoPacket();
				if (_LastError == 0)
				{
					if (Size < IsoHSize || LastPDUType != (byte)0xD0) // 0xD0 = CC Connection confirm
						_LastError = S7Consts.errIsoConnect;
					else
						_PDULength = GetNegotiatedTpduSize(Size);
				}
			}
			return _LastError;
		}

		private int GetNegotiatedTpduSize(int packetSize)
		{
			const int cotpParametersOffset = 11;
			const byte tpduSizeParameter = 0xC0;
			int cotpEnd = Math.Min(packetSize, 5 + PDU[4]);
			int position = cotpParametersOffset;

			while (position + 2 <= cotpEnd)
			{
				byte parameterCode = PDU[position++];
				int parameterLength = PDU[position++];
				if (position + parameterLength > cotpEnd)
					break;

				if (parameterCode == tpduSizeParameter && parameterLength == 1)
				{
					int exponent = PDU[position];
					if (exponent >= 7 && exponent < 31)
						return Math.Min(1 << exponent, S7CommPlusProtocolConstants.DefaultIsoTpduSize);
				}

				position += parameterLength;
			}

			return S7CommPlusProtocolConstants.DefaultIsoTpduSize;
		}

		public byte[] getOMSExporterSecret()
		{
			if (m_sslconn == null) return null;
			return m_sslconn.GetOmsExporterSecret();
		}

		#endregion

		#region [Class Control]

		public S7Client()
			: this(() => new SocketS7Transport())
		{
		}

		internal S7Client(Func<IS7Transport> transportFactory)
		{
			_transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
			m_DateTimeStarted = DateTime.Now;
			CreateSocket();
		}

		~S7Client()
		{
			// Managed objects (especially Thread) may already have been finalized.
			// Do not join threads, dispose transports/TLS connectors, or invoke callbacks here.
			try
			{
				ReleaseSslContext();
			}
			catch (Exception)
			{
				// A finalizer must never terminate the host process.
			}
		}

		public int Connect()
		{
			// A timed-out disconnect must finish before this instance can be reused.
			if (m_runThread != null && m_runThread.IsAlive)
				return Connected && !m_runThread_DoStop ? 0 : S7Consts.errCliDestroying;
			_LastError = 0;
			_PDULength = 0;
			Time_ms = 0;
			int Elapsed = Environment.TickCount;
			if (!Connected)
			{
				Socket?.Close();
				CreateSocket();
				TCPConnect(); // First stage : TCP Connection
				if (_LastError == 0)
				{
					ISOConnect(); // Second stage : ISOTCP (ISO 8073) Connection
					if (_LastError == 0)
					{
						//	_LastError = S7P_InitSSLRequest(); // Third stage : Init SSL Request
						StartThread();
					}
				}
			}
			if (_LastError != 0)
			{
				var connectError = _LastError;
				Disconnect();
				_LastError = connectError;
			}
			else
				Time_ms = Environment.TickCount - Elapsed;

			return _LastError;
		}

		public int SetConnectionParams(string Address, ushort LocalTSAP, byte[] RemoteTSAP)
		{
			int LocTSAP = LocalTSAP & 0x0000FFFF;
			IPAddress = Address;
			LocalTSAP_HI = (byte)(LocTSAP >> 8);
			LocalTSAP_LO = (byte)(LocTSAP & 0x00FF);

			RemoteTSAP_S = new byte[RemoteTSAP.Length];
			Array.Copy(RemoteTSAP, RemoteTSAP_S, RemoteTSAP.Length);

			return 0;
		}

		/// <summary>
		/// Closes the active transport and waits up to the default shutdown timeout for the receive thread to finish.
		/// </summary>
		/// <returns>Zero when cleanup completed, or an S7 client error when the receive thread did not stop in time.</returns>
		public int Disconnect()
		{
			return Disconnect(DefaultTimeout);
		}

		/// <summary>
		/// Atomically detaches and closes the active transport before waiting for the receive thread, making repeated or
		/// concurrent cleanup safe while still allowing a later <see cref="Connect"/> call to create a fresh transport.
		/// </summary>
		/// <param name="timeoutMilliseconds">Maximum time to wait for the receive thread to stop.</param>
		/// <returns>Zero when cleanup completed, or an S7 client error when the receive thread did not stop in time.</returns>
		public int Disconnect(int timeoutMilliseconds)
		{
			m_runThread_DoStop = true;
			int result = 0;
			var socket = Interlocked.Exchange(ref Socket, null);
			try
			{
				result = socket?.Close() ?? 0;
			}
			catch (Exception ex)
			{
				result = S7Consts.errTCPDataReceive;
				LastErrorDetail = $"Transport cleanup failed: {ex.GetType().Name}: {ex.Message}";
			}
			var receiveThread = m_runThread;
			if (receiveThread == Thread.CurrentThread)
				return _LastError = result;
			if (receiveThread != null && receiveThread.IsAlive)
			{
				if (!receiveThread.Join(Math.Max(1, timeoutMilliseconds)))
				{
					_LastError = S7Consts.errCliDestroying;
					return _LastError;
				}
			}
			lock (m_cleanupLock)
			{
				var tlsResult = TryDeactivateSsl();
				if (result == 0)
					result = tlsResult;
			}

			return _LastError = result;
		}

		public int GetParam(Int32 ParamNumber, ref int Value)
		{
			int Result = 0;
			switch (ParamNumber)
			{
				case S7Consts.p_u16_RemotePort:
					{
						Value = PLCPort;
						break;
					}
				case S7Consts.p_i32_PingTimeout:
					{
						Value = ConnTimeout;
						break;
					}
				case S7Consts.p_i32_SendTimeout:
					{
						Value = SendTimeout;
						break;
					}
				case S7Consts.p_i32_RecvTimeout:
					{
						Value = RecvTimeout;
						break;
					}
				case S7Consts.p_i32_PDURequest:
					{
						Value = PduSizeRequested;
						break;
					}
				default:
					{
						Result = S7Consts.errCliInvalidParamNumber;
						break;
					}
			}
			return Result;
		}

		// Set Properties for compatibility with Snap7.net.cs
		public int SetParam(Int32 ParamNumber, ref int Value)
		{
			int Result = 0;
			switch (ParamNumber)
			{
				case S7Consts.p_u16_RemotePort:
					{
						PLCPort = Value;
						break;
					}
				case S7Consts.p_i32_PingTimeout:
					{
						ConnTimeout = Value;
						break;
					}
				case S7Consts.p_i32_SendTimeout:
					{
						SendTimeout = Value;
						break;
					}
				case S7Consts.p_i32_RecvTimeout:
					{
						RecvTimeout = Value;
						break;
					}
				case S7Consts.p_i32_PDURequest:
					{
						PduSizeRequested = Value;
						break;
					}
				default:
					{
						Result = S7Consts.errCliInvalidParamNumber;
						break;
					}
			}
			return Result;
		}

		#endregion

		#region [Info Functions / Properties]

		public static string ErrorText(int Error)
		{
			switch (Error)
			{
				case 0: return "OK";
				case S7Consts.errTCPSocketCreation: return "SYS : Error creating the Socket";
				case S7Consts.errTCPConnectionTimeout: return "TCP : Connection Timeout";
				case S7Consts.errTCPConnectionFailed: return "TCP : Connection Error";
				case S7Consts.errTCPReceiveTimeout: return "TCP : Data receive Timeout";
				case S7Consts.errTCPDataReceive: return "TCP : Error receiving Data";
				case S7Consts.errTCPSendTimeout: return "TCP : Data send Timeout";
				case S7Consts.errTCPDataSend: return "TCP : Error sending Data";
				case S7Consts.errTCPConnectionReset: return "TCP : Connection reset by the Peer";
				case S7Consts.errTCPNotConnected: return "CLI : Client not connected";
				case S7Consts.errTCPUnreachableHost: return "TCP : Unreachable host";
				case S7Consts.errIsoConnect: return "ISO : Connection Error";
				case S7Consts.errIsoInvalidPDU: return "ISO : Invalid PDU received";
				case S7Consts.errIsoInvalidDataSize: return "ISO : Invalid Buffer passed to Send/Receive";
				case S7Consts.errCliNegotiatingPDU: return "CLI : Error in PDU negotiation";
				case S7Consts.errCliInvalidParams: return "CLI : invalid param(s) supplied";
				case S7Consts.errCliJobPending: return "CLI : Job pending";
				case S7Consts.errCliTooManyItems: return "CLI : too may items (>20) in multi read/write";
				case S7Consts.errCliInvalidWordLen: return "CLI : invalid WordLength";
				case S7Consts.errCliPartialDataWritten: return "CLI : Partial data written";
				case S7Consts.errCliSizeOverPDU: return "CPU : total data exceeds the PDU size";
				case S7Consts.errCliInvalidPlcAnswer: return "CLI : invalid CPU answer";
				case S7Consts.errCliAddressOutOfRange: return "CPU : Address out of range";
				case S7Consts.errCliInvalidTransportSize: return "CPU : Invalid Transport size";
				case S7Consts.errCliWriteDataSizeMismatch: return "CPU : Data size mismatch";
				case S7Consts.errCliItemNotAvailable: return "CPU : Item not available";
				case S7Consts.errCliInvalidValue: return "CPU : Invalid value supplied";
				case S7Consts.errCliCannotStartPLC: return "CPU : Cannot start PLC";
				case S7Consts.errCliAlreadyRun: return "CPU : PLC already RUN";
				case S7Consts.errCliCannotStopPLC: return "CPU : Cannot stop PLC";
				case S7Consts.errCliCannotCopyRamToRom: return "CPU : Cannot copy RAM to ROM";
				case S7Consts.errCliCannotCompress: return "CPU : Cannot compress";
				case S7Consts.errCliAlreadyStop: return "CPU : PLC already STOP";
				case S7Consts.errCliFunNotAvailable: return "CPU : Function not available";
				case S7Consts.errCliUploadSequenceFailed: return "CPU : Upload sequence failed";
				case S7Consts.errCliInvalidDataSizeRecvd: return "CLI : Invalid data size received";
				case S7Consts.errCliInvalidBlockType: return "CLI : Invalid block type";
				case S7Consts.errCliInvalidBlockNumber: return "CLI : Invalid block number";
				case S7Consts.errCliInvalidBlockSize: return "CLI : Invalid block size";
				case S7Consts.errCliNeedPassword: return "CPU : Function not authorized for current protection level";
				case S7Consts.errCliInvalidPassword: return "CPU : Invalid password";
				case S7Consts.errCliAccessDenied: return "CPU : Access denied";
				case S7Consts.errCliNoPasswordToSetOrClear: return "CPU : No password to set or clear";
				case S7Consts.errCliJobTimeout: return "CLI : Job Timeout";
				case S7Consts.errCliFunctionRefused: return "CLI : function refused by CPU (Unknown error)";
				case S7Consts.errCliPartialDataRead: return "CLI : Partial data read";
				case S7Consts.errCliBufferTooSmall: return "CLI : The buffer supplied is too small to accomplish the operation";
				case S7Consts.errCliDestroying: return "CLI : Cannot perform (destroying)";
				case S7Consts.errCliInvalidParamNumber: return "CLI : Invalid Param Number";
				case S7Consts.errCliCannotChangeParam: return "CLI : Cannot change this param now";
				case S7Consts.errCliFunctionNotImplemented: return "CLI : Function not implemented";
				case S7Consts.errCliFirmwareNotSupported: return "CLI : Firmware not supported";
				case S7Consts.errCliDeviceNotSupported: return "CLI : Device type not supported";
				case S7Consts.errOpenSSL: return "OPENSSL : OpenSSL error";
				case S7Consts.errInitSslResponse: return "S7COMMP : Init SSL response error";
				case S7Consts.errS7CommPlusLegacyAuthentication: return "S7COMMP : Legacy challenge authentication failed";
				case S7Consts.errS7CommPlusDigestMismatch: return "S7COMMP : Legacy packet digest mismatch";
				case S7Consts.errS7CommPlusLegacyRequestTooLarge: return "S7COMMP : Legacy request exceeds single-frame limit";
				default: return "CLI : Unknown error (0x" + Convert.ToString(Error, 16) + ")";
			};
		}

		public int LastError()
		{
			return _LastError;
		}

		public int RequestedPduLength()
		{
			return _PduSizeRequested;
		}

		public int NegotiatedPduLength()
		{
			return _PDULength;
		}

		public int ExecTime()
		{
			return Time_ms;
		}

		public int ExecutionTime
		{
			get
			{
				return Time_ms;
			}
		}

		public int PduSizeNegotiated
		{
			get
			{
				return _PDULength;
			}
		}

		public int PduSizeRequested
		{
			get
			{
				return _PduSizeRequested;
			}
			set
			{
				if (value < MinPduSizeToRequest)
					value = MinPduSizeToRequest;
				if (value > MaxPduSizeToRequest)
					value = MaxPduSizeToRequest;
				_PduSizeRequested = value;
			}
		}

		public int PLCPort
		{
			get
			{
				return _PLCPort;
			}
			set
			{
				_PLCPort = value;
			}
		}

		public int ConnTimeout
		{
			get
			{
				return _ConnTimeout;
			}
			set
			{
				_ConnTimeout = value;
			}
		}

		public int RecvTimeout
		{
			get
			{
				return _RecvTimeout;
			}
			set
			{
				_RecvTimeout = value;
			}
		}

		public int SendTimeout
		{
			get
			{
				return _SendTimeout;
			}
			set
			{
				_SendTimeout = value;
			}
		}

		/// <summary>
		/// Replaces the handshake deadlines in both the client configuration and the already-created transport.
		/// </summary>
		/// <param name="receiveTimeoutMilliseconds">Maximum wait for request response data.</param>
		/// <param name="sendTimeoutMilliseconds">Maximum wait for request transmission.</param>
		internal void SetTransportTimeouts(int receiveTimeoutMilliseconds, int sendTimeoutMilliseconds)
		{
			_RecvTimeout = receiveTimeoutMilliseconds;
			_SendTimeout = sendTimeoutMilliseconds;
			Socket?.SetTimeouts(receiveTimeoutMilliseconds, sendTimeoutMilliseconds);
		}

		public bool Connected
		{
			get
			{
				return (Socket != null) && (Socket.Connected);
			}
		}
		#endregion
	}
}
