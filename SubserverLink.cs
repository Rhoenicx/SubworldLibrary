using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Terraria;
using Terraria.ModLoader.IO;

namespace SubworldLibrary
{
	internal class SubserverLink
	{
		private NamedPipeServerStream pipeOut;
		private NamedPipeServerStream pipeIn;

		private bool _connected;
		private byte[] queue;
		private int totalData;

		public SubserverLink(string name, TagCompound data)
		{
			using MemoryStream stream = new MemoryStream(131070);

			pipeOut = new NamedPipeServerStream(name + ".OUT", PipeDirection.In);
			pipeIn = new NamedPipeServerStream(name + ".IN", PipeDirection.Out);

			TagIO.ToStream(data, stream);
			queue = stream.GetBuffer();
			totalData = (int)stream.Length;
		}

		public bool Connected => _connected;

		public void Close()
		{
			_connected = false;

			pipeOut.Close();
			pipeIn.Close();
		}

		public void Send(byte[] data)
		{
			if (!_connected)
			{
				return;
			}
			lock (queue)
			{
				while (totalData + data.Length > queue.Length)
				{
					Monitor.Exit(queue);
					Thread.Yield();
					Monitor.Enter(queue);
				}
				Buffer.BlockCopy(data, 0, queue, totalData, data.Length);
				totalData += data.Length;
			}
		}

		public void Send(byte[] data, int offset, int length, byte client)
		{
			if (!_connected)
			{
				return;
			}
			lock (queue)
			{
				while (totalData + length >= queue.Length)
				{
					Monitor.Exit(queue);
					Thread.Yield();
					Monitor.Enter(queue);
				}
				queue[totalData] = client;
				Buffer.BlockCopy(data, offset, queue, totalData + 1, length);
				totalData += length + 1;
			}
		}

		public void ConnectAndSend(object id)
		{
			try
			{
				SendLoop((int)id);
			}
			finally
			{
				SubworldSystem.StopSubserver((int)id);
			}
		}

		public void ConnectAndRead(object id)
		{
			try
			{
				ReadLoop((int)id);
			}
			finally
			{
				SubworldSystem.StopSubserver((int)id);
			}
		}

		private void ReadLoop(int id)
		{
			pipeOut.WaitForConnection();

			// world data has been read, packets can now be sent
			_connected = true;

			// prompt clients to connect to the subserver
			for (int i = 0; i < 256; i++)
			{
				if (Netplay.Clients[i].IsConnected() && SubworldSystem.playerLocations[i] == id)
				{
					Netplay.Clients[i].Socket.AsyncSend(new byte[] { 5, 0, 3, (byte)i, 0 }, 0, 5, (state) => { });
				}
			}

			while (pipeOut.IsConnected && !Netplay.Disconnect)
			{
				byte[] packetInfo = new byte[3];
				if (pipeOut.Read(packetInfo) < 3)
				{
					break;
				}

				byte low = packetInfo[1];
				byte high = packetInfo[2];
				int length = (high << 8) | low;

				byte[] data = new byte[length];
				pipeOut.Read(data, 2, length - 2);
				data[0] = low;
				data[1] = high;

				if (packetInfo[0] == 255 && data[2] == 255)
				{
					// this packet actually came from a subserver, put it in message buffer 256 for reading on the main thread
					MessageBuffer buffer = NetMessage.buffer[256];
					lock (buffer)
					{
						while (buffer.totalData + length > buffer.readBuffer.Length)
						{
							Monitor.Exit(buffer);
							Thread.Yield();
							Monitor.Enter(buffer);
						}
						Buffer.BlockCopy(data, 0, buffer.readBuffer, buffer.totalData, length);
						buffer.totalData += length;
						buffer.checkBytes = true;
					}
					continue;
				}

				// prevents a race condition where a subserver tries to send packets to a client who just left
				if (SubworldSystem.playerLocations[packetInfo[0]] == id)
				{
					Netplay.Clients[packetInfo[0]].Socket.AsyncSend(data, 0, length, (state) => { });
				}
			}
		}

		private void SendLoop(int id)
		{
			pipeIn.WaitForConnection();

			int sleep = 0;
			while (pipeIn.IsConnected && !Netplay.Disconnect)
			{
				if (totalData <= 0)
				{
					// vanilla's server loop does this, not sure what the nuance here is
					if (++sleep == 10)
					{
						Thread.Sleep(1);
						sleep = 0;
						continue;
					}
					Thread.Sleep(0);
					continue;
				}

				byte[] data;
				lock (queue)
				{
					data = new byte[totalData];
					Buffer.BlockCopy(queue, 0, data, 0, totalData);
					totalData = 0;
				}
				pipeIn.Write(data, 0, data.Length);
			}
		}
	}
}