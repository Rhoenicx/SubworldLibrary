using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria.ModLoader;
using Terraria.Net.Sockets;
using Terraria.Net;
using Terraria;

namespace SubworldLibrary
{
	internal class SubserverSocket : ISocket
	{
		private readonly int _id;
		private bool _connected;
		internal static RemoteAddress address;

		public SubserverSocket(int id)
		{
			_id = id;
			_connected = true;
		}

		void ISocket.AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state) { }

		void ISocket.AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state)
		{
			byte[] packet = new byte[size + 1];
			packet[0] = (byte)_id;
			Buffer.BlockCopy(data, offset, packet, 1, size);
			MainserverLink.Send(packet);
		}

		void ISocket.Close() 
		{
			_connected = false;
		}

		void ISocket.Connect(RemoteAddress address) { }

		RemoteAddress ISocket.GetRemoteAddress() => address;

		bool ISocket.IsConnected() => _connected;

		bool ISocket.IsDataAvailable() => false;

		void ISocket.SendQueuedPackets() { }

		bool ISocket.StartListening(SocketConnectionAccepted callback) => false;

		void ISocket.StopListening() { }
	}

}
