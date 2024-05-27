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
				if (!Netplay.Disconnect && PipeOut != null && PipeOut.IsConnected && !Disconnecting)
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

						case SubLibMessageType.MovePlayerOnServer:
							{
								// Replicate vanilla call order for disconnect here:
								int whoAmI = NetMessage.buffer[packetInfo[0]].whoAmI;

								// Verify if the player is still connected on the subserver
								if (!Netplay.Clients[whoAmI].IsConnected() && !Netplay.Clients[whoAmI].IsActive)
								{
									break;
								}

								// Log message
								ModNet.Log(Netplay.Clients[whoAmI].Id, "Terminating: Connection lost");

								// Reset the client when the packet comes in
								Netplay.Clients[whoAmI].Reset();

								// Run vanilla SyncDisconnect, this takes care of synchronizing
								// the disconnect to other players and the chat/log messages.
								NetMessage.SyncDisconnectedPlayer(whoAmI);

								// Run CheckClients to update Netplay.HasClients
								SubworldLibrary.CheckClients();
							}
							break;

						default:
							{
								MessageBuffer buffer = NetMessage.buffer[packetInfo[0]];
								lock (buffer)
								{
									// Wait for space in the readBuffer
									while (buffer.totalData + length > buffer.readBuffer.Length)
									{
										Monitor.Exit(buffer);
										Thread.Yield();
										Monitor.Enter(buffer);
									}

									// Packet is Hello, player is logging in to the server.
									if (!Netplay.Clients[buffer.whoAmI].IsConnected())
									{
										// Overwrite the Socket of this client
										Netplay.Clients[buffer.whoAmI].Socket = new SubserverSocket(buffer.whoAmI);

										// Put the client Active
										Netplay.Clients[buffer.whoAmI].IsActive = true;

										// Set state to 1, we expect to receive SyncPlayer somewhere during
										// the first time this client joins this subserver
										Netplay.Clients[buffer.whoAmI].State = 1;

										// Run CheckClients to update Netplay.HasClients
										SubworldLibrary.CheckClients();
									}

									// Put the data inside the buffer of this client.
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
