#region License
/******************************************************************************
 * S7CommPlusDriver
 *
 * Based on Snap7 (Sharp7.cs) by Davide Nardella licensed under LGPL
 *
 /****************************************************************************/
#endregion

using System;
using System.Net.Sockets;

namespace S7CommPlusDriver
{
    //
    class MsgSocket
	{
		private Socket TCPSocket;
		private int _ReadTimeout = 2000;
		private int _WriteTimeout = 2000;
		private int _ConnectTimeout = 1000;
		public int LastError = 0;

		public MsgSocket()
		{
		}

		// Socket owns a safe handle and performs its own finalization. This wrapper
		// must not dispose managed objects from the GC finalizer thread.

		public void Close()
		{
			if (TCPSocket != null)
			{
				TCPSocket.Dispose();
				TCPSocket = null;
			}
		}

		private void CreateSocket()
		{
			TCPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			TCPSocket.NoDelay = true;
			TCPSocket.ReceiveTimeout = _ReadTimeout;
			TCPSocket.SendTimeout = _WriteTimeout;
		}

		private void TCPPing(string Host, int Port)
		{
			// To Ping the PLC an Asynchronous socket is used rather then an ICMP packet.
			// This allows the use also across Internet and Firewalls (obviously the port must be opened)
			LastError = 0;
			Socket PingSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			try
			{
				IAsyncResult result = PingSocket.BeginConnect(Host, Port, null, null);
				bool success = result.AsyncWaitHandle.WaitOne(_ConnectTimeout, true);

				if (!success)
				{
					LastError = S7Consts.errTCPConnectionFailed;
				}
			}
			catch
			{
				LastError = S7Consts.errTCPConnectionFailed;
			};
			PingSocket.Close();
		}

		public int Connect(string Host, int Port)
		{
			LastError = 0;
			if (!Connected)
			{
				// TWI: TCPPing rausgenommen, st�rt bei Wireshark Analyse
				//TCPPing(Host, Port);
				if (LastError == 0)
					try
					{
						CreateSocket();
						TCPSocket.Connect(Host, Port);
					}
					catch
					{
						LastError = S7Consts.errTCPConnectionFailed;
					}
			}
			return LastError;
		}

		public int Receive(byte[] Buffer, int Start, int Size)
		{
			int bytesReadTotal = 0;
			LastError = 0;
			if (!Connected)
			{
				LastError = S7Consts.errTCPNotConnected;
				return LastError;
			}
			while (LastError == 0 && bytesReadTotal < Size)
			{
				try
				{
					var bytesRead = TCPSocket.Receive(Buffer, Start + bytesReadTotal, Size - bytesReadTotal, SocketFlags.None);
					if (bytesRead == 0)
					{
						LastError = S7Consts.errTCPConnectionReset;
						Close();
					}
					else
					{
						bytesReadTotal += bytesRead;
					}
				}
				catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
				{
					LastError = S7Consts.errTCPReceiveTimeout;
				}
				catch
				{
					LastError = S7Consts.errTCPDataReceive;
				}
				if (LastError != 0 && LastError != S7Consts.errTCPReceiveTimeout)
				{
					Close();
				}
			}
			return LastError;
		}

		public int Send(byte[] Buffer, int Size)
		{
			LastError = 0;
			if (!Connected)
			{
				LastError = S7Consts.errTCPNotConnected;
				return LastError;
			}
			try
			{
				int BytesSent = TCPSocket.Send(Buffer, Size, SocketFlags.None);
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
			{
				LastError = S7Consts.errTCPSendTimeout;
				Close();
			}
			catch
			{
				LastError = S7Consts.errTCPDataSend;
				Close();
			}
			return LastError;
		}

		public bool Connected
		{
			get
			{
				return (TCPSocket != null) && (TCPSocket.Connected);
			}
		}

		public int ReadTimeout
		{
			get
			{
				return _ReadTimeout;
			}
			set
			{
				_ReadTimeout = value;
				if (TCPSocket != null)
				{
					TCPSocket.ReceiveTimeout = value;
				}
			}
		}

		public int WriteTimeout
		{
			get
			{
				return _WriteTimeout;
			}
			set
			{
				_WriteTimeout = value;
				if (TCPSocket != null)
				{
					TCPSocket.SendTimeout = value;
				}
			}

		}
		public int ConnectTimeout
		{
			get
			{
				return _ConnectTimeout;
			}
			set
			{
				_ConnectTimeout = value;
			}
		}
	}
}
