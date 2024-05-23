using System;
using System.IO.Pipes;
using Terraria.ModLoader;
using Terraria;
using System.Threading;
using Terraria.ID;
using System.Diagnostics;

namespace SubworldLibrary
{
	internal class MainserverLink
	{
		private static Stopwatch _watchdog;
		internal static NamedPipeClientStream PipeIn;
		internal static NamedPipeClientStream PipeOut;
		public static bool Disconnecting { get; private set; }

		public static void Connect(string name)
		{ 
			// Initialize the subserver pipes.
			PipeIn = new NamedPipeClientStream(".", name + "_IN", PipeDirection.In);
			PipeOut = new NamedPipeClientStream(".", name + "_OUT", PipeDirection.Out);

			try
			{
				// Try to connect both pipes to the waiting main server.
				// If this does not complete within a second => error and close.
				PipeIn.Connect(1000);
				PipeOut.Connect(1000);
			}
			catch (Exception e) 
			{
				Close();
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while connecting pipes: " + e.Message);
			}
		}

		public static void LaunchCallback()
		{
			new Thread(SubserverCallBack)
			{
				Name = "Mainserver Packets",
				IsBackground = true
			}.Start();
		}

		public static void Send(byte[] data)
		{
			try
			{
				if (!Netplay.Disconnect && PipeOut.IsConnected && !Disconnecting)
				{
					PipeOut.Write(data);
				}
			}
			catch (Exception e)
			{
				Close();
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while writing data pipeOut: " + e.Message);
			}
		}

		public static void Update()
		{
			if (Disconnecting && _watchdog.ElapsedMilliseconds > 500)
			{
				Close();
			}
		}

		public static void Disconnect()
		{
			_watchdog ??= new();
			_watchdog.Start();
			Disconnecting = true;
		}

		public static void Close()
		{
			// Set disconnection status
			Disconnect();

			// Close pipes and clear objects
			PipeIn?.Close();
			PipeOut?.Close();
			_watchdog?.Stop();
			_watchdog = null;

			// Send shutdown request
			Netplay.Disconnect = true;
		}

		private static void SubserverCallBack()
		{
			try
			{
				while (!Netplay.Disconnect && PipeIn.IsConnected && !Disconnecting)
				{
					byte[] packetInfo = new byte[3];
					if (PipeIn.Read(packetInfo) < 3)
					{
						break;
					}

					byte low = packetInfo[1];
					byte high = packetInfo[2];
					int length = (high << 8) | low;

					byte[] data = new byte[length];
					PipeIn.Read(data, 2, length - 2);
					data[0] = low;
					data[1] = high;

					bool subLibPacket = data[2] == MessageID.ModPacket && (ModNet.NetModCount < 256 ? data[3] : BitConverter.ToUInt16(data, 3)) == ModContent.GetInstance<SubworldLibrary>().NetID;
					SubLibMessageType messageType = subLibPacket ? (SubLibMessageType)data[ModNet.NetModCount < 256 ? 4 : 5] : SubLibMessageType.None;

					switch (messageType)
					{
						case SubLibMessageType.SendToMainServer:
						case SubLibMessageType.SendToSubserver:
						case SubLibMessageType.BroadcastBetweenServers:
						case SubLibMessageType.StopSubserver:
							{ 
								lock (SubworldSystem.serverMessageBuffer)
								{
									while (SubworldSystem.serverMessageBuffer.dataAmount + length > SubworldSystem.serverMessageBuffer.buffer.Length)
									{
										Monitor.Exit(SubworldSystem.serverMessageBuffer);
										Thread.Yield();
										Monitor.Enter(SubworldSystem.serverMessageBuffer);
									}

									Buffer.BlockCopy(data, 0, SubworldSystem.serverMessageBuffer.buffer, SubworldSystem.serverMessageBuffer.dataAmount, length);
									SubworldSystem.serverMessageBuffer.dataAmount += length;
									SubworldSystem.serverMessageBuffer.dataReady = true;
								}
							}
							break;

						default:
							{
								MessageBuffer buffer = NetMessage.buffer[packetInfo[0]];
								lock (buffer)
								{
									while (buffer.totalData + length > buffer.readBuffer.Length)
									{
										Monitor.Exit(buffer);
										Thread.Yield();
										Monitor.Enter(buffer);
									}

									if (!Netplay.Clients[buffer.whoAmI].IsActive && data[2] == MessageID.Hello)
									{
										if (!Netplay.Clients[buffer.whoAmI].IsConnected())
										{
											Netplay.Clients[buffer.whoAmI].Socket = new SubserverSocket(buffer.whoAmI);
										}
										Netplay.Clients[buffer.whoAmI].IsActive = true;
									}

									if (Netplay.Clients[buffer.whoAmI].IsConnected())
									{
										Buffer.BlockCopy(data, 0, buffer.readBuffer, buffer.totalData, length);
										buffer.totalData += length;
										buffer.checkBytes = true;
									}
								}
							}
							break;
					}
				}
			}
			catch (Exception e)
			{
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while reading data pipeIn: " + e.Message);
			}
			finally
			{
				Close();
			}
		}

	}
}
