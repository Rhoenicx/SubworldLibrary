using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using Terraria.ModLoader;
using Terraria;
using Terraria.ID;
using System.Diagnostics;

namespace SubworldLibrary
{
	internal class SubserverLink
	{
		private Stopwatch _watchdog;
		private readonly int _id;
		private readonly NamedPipeServerStream _pipeIn;
		private readonly NamedPipeServerStream _pipeOut;
		private List<byte[]> _queue;
		private Process _process;
		private bool _connectedIn;
		private bool _connectedOut;
		private int _joinTime;
		private int _keepOpenTime;
		private bool _playersHasBeenConnected;
		public bool Connecting => !_connectedIn && !_connectedOut;
		public bool Disconnecting { get; private set; }

		public SubserverLink(string name, int id, int joinTime = 0, int keepOpenTime = 0)
		{
			_pipeIn = new NamedPipeServerStream(name + "_IN", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
			_pipeOut = new NamedPipeServerStream(name + "_OUT", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
			_queue = new List<byte[]>(16);
			_id = id;
			_joinTime = joinTime;
			_keepOpenTime = keepOpenTime;

			_pipeIn.BeginWaitForConnection(new AsyncCallback(ConnectedPipeInCallBack), this);
			_pipeOut.BeginWaitForConnection(new AsyncCallback(ConnectedPipeOutCallBack), this);

			_process = new Process();
			_process.StartInfo.FileName = Process.GetCurrentProcess().MainModule!.FileName;
			_process.StartInfo.Arguments = "tModLoader.dll -server -showserverconsole -world \"" + Main.worldPathName + "\" -subworld \"" + name + "\"" + CopyArguments();
			_process.StartInfo.UseShellExecute = true;
			_process.Start();

			_watchdog = new Stopwatch();
			_watchdog.Start();
		}

		private static string CopyArguments()
		{
			string arguments = "";
			if (Program.LaunchParameters.ContainsKey("-modpath"))
			{
				arguments += " -modpath \"" + Program.LaunchParameters["-modpath"] + "\"";
			}

			if (Program.LaunchParameters.ContainsKey("-modpack"))
			{
				arguments += " -modpack \"" + Program.LaunchParameters["-modpack"] + "\"";
			}

			arguments += " -players " + Main.maxNetPlayers;

			if (Program.LaunchParameters.ContainsKey("-lang"))
			{
				arguments += " -lang " + Program.LaunchParameters["-lang"];
			}

			if (Program.LaunchParameters.ContainsKey("-detailednetlog") || ModNet.DetailedLogging)
			{
				arguments += " -detailednetlog";
			}

			return arguments;
		}

		private void ConnectedPipeInCallBack(IAsyncResult iar)
		{
			try
			{
				_pipeIn.EndWaitForConnection(iar);

				_connectedIn = true;

				if (_connectedOut)
				{
					_watchdog.Restart();
				}

				ProcessQueue();
			}
			catch (Exception e)
			{
				Close();
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while waiting for pipeIn to connect " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
			}
		}

		private void ConnectedPipeOutCallBack(IAsyncResult iar)
		{
			try
			{
				_pipeOut.EndWaitForConnection(iar);

				_connectedOut = true;

				if (_connectedIn)
				{
					_watchdog.Restart();
				}

				new Thread(MainServerCallBack)
				{
					Name = "Subserver Packets",
					IsBackground = true
				}.Start();
			}
			catch (Exception e)
			{
				Close();
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while waiting for pipeOut to connect " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
			}
		}

		public void Send(byte[] data)
		{
			try
			{
				if (!Netplay.Disconnect && !Disconnecting)
				{
					lock (this)
					{
						if (QueueData(data))
						{
							return;
						}
					}

					_pipeIn.Write(data);
				}
			}
			catch (Exception e)
			{
				Close();
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while writing data pipeIn to " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
			}
		}

		private bool QueueData(byte[] data)
		{
			if (_queue == null)
			{
				return false;
			}

			_queue.Add(data);
			return true;
		}

		private void ProcessQueue()
		{
			try
			{
				lock (this)
				{
					for (int i = 0; i < _queue.Count; i++)
					{
						_pipeIn.Write(_queue[i]);
					}

					_queue = null;
				}
			}
			catch (Exception e)
			{
				Close();
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while writing queue pipeIn to " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
			}
		}

		public void Update()
		{
			// Subserver booting up
			if (Connecting)
			{
				try
				{	
					bool exited = _process.HasExited;
					bool timedOut = SubworldLibrary.serverConfig.EnableSubserverStartupTime && _watchdog?.ElapsedMilliseconds > SubworldLibrary.serverConfig.SubserverStartupTimeMax * 60000;
					
					if (timedOut || exited)
					{
						// The subserver process has been closed abruptly. This was due to some external factor.
						if (!timedOut && exited)
						{
							ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Process of subworld " + SubworldSystem.subworlds[_id].FullName + " got closed abruptly!");
						}

						// The startup timer has been expired, terminate the subserver process.
						if (!exited)
						{ 
							ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Subworld " + SubworldSystem.subworlds[_id].FullName + " took too long to start up! Try increasing timeout in server config...");
							_process.Kill();
						}

						Close();
					}
				}
				catch (Exception e)
				{
					Close();
					ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred handling process " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
				}

				return;
			}

			// Execute Close after 1 second of Disconnection
			if (Disconnecting)
			{
				if (_watchdog?.ElapsedMilliseconds > 1000)
				{
					Close();
				}

				return;
			}

			// => Server active; not connecting or disconnecting.

			// Check if there are players present on the corresponding subserver
			bool playersPresent = SubworldSystem.playerLocations.ContainsValue(_id);

			// Check if a player is present in the subserver
			if (playersPresent)
			{
				_watchdog.Restart();
				_playersHasBeenConnected = true;
			}

			// No player has been connected
			if (!_playersHasBeenConnected)
			{
				// JoinTime and KeepOpen is not infinite, and joinTime has expired
				if (_joinTime != -1 && _keepOpenTime != -1 && _watchdog?.ElapsedMilliseconds > _joinTime * 1000)
				{
					if (_id >= 0 && _id < SubworldSystem.subworlds.Count)
					{
						// Give the subworld a chance to react on the server close request.
						// Here is where StartServer() should be called again to refresh timers.
						SubworldSystem.subworlds[_id].OnJoinTimeExpired();
					}

					// Check the timer again; if not reset during OnJoinTimeExpired => close server
					if (_watchdog?.ElapsedMilliseconds > _joinTime * 1000)
					{
						Disconnect();
					}
				}

				return;
			}

			// No players are present, the keepOpen time is not infinite and has been expired
			if (!playersPresent && _keepOpenTime != -1 && _watchdog?.ElapsedMilliseconds > _keepOpenTime * 1000)
			{
				if (_id >= 0 && _id < SubworldSystem.subworlds.Count)
				{
					// Give the subworld a chance to react on the server close request.
					// Here is where StartServer() should be called again to refresh timers.
					SubworldSystem.subworlds[_id].OnKeepOpenTimeExpired();
				}

				// Check the timer again; if not reset during OnKeepOpenTimeExpired => close server
				if (_watchdog?.ElapsedMilliseconds > _keepOpenTime * 1000)
				{
					Disconnect();
				}
			}
		}

		public void Refresh(int joinTime = int.MinValue, int keepOpenTime = int.MinValue)
		{
			if (Disconnecting)
			{
				return;
			}

			// Apply the new given joinTime
			if (joinTime != int.MinValue)
			{
				_joinTime = joinTime;
			}

			// Apply the new given keepOpenTime
			if (keepOpenTime != int.MinValue)
			{
				_keepOpenTime = keepOpenTime;
			}

			_watchdog?.Restart();
		}

		public void Disconnect(bool closing = false)
		{
			// Only when not already disconnecting
			if (Disconnecting)
			{
				return;
			}

			// When closing set Disconnection early, this prevents
			// packets being sent on a possibly broken/closed pipe
			if (closing)
			{
				Disconnecting = true;
			}

			// Move all players that are still 'connected' to the closed subserver back to the main server
			for (int i = 0; i < Netplay.Clients.Length; i++)
			{
				if (Netplay.Clients[i].State != 0
					&& Netplay.Clients[i].Socket != null
					&& SubworldSystem.playerLocations.TryGetValue(Netplay.Clients[i].Socket, out int id) && _id == id)
				{
					SubworldSystem.MovePlayerToSubserver(i, ushort.MaxValue);
				}
			}

			// Send disconnect request packet
			Send(SubworldSystem.GetStopSubserverPacket(_id));

			// Set Disconnect and restart timer
			Disconnecting = true;
			_watchdog?.Restart();
		}

		private void Close()
		{
			// Set disconnection status
			Disconnect(true);

			// Close pipes and clear objects
			_pipeIn?.Close();
			_pipeOut?.Close();
			_queue = null;
			_process = null;
			_watchdog?.Stop();
			_watchdog = null;

			// Clear link from Dictionary
			SubworldSystem.links.Remove(_id);
		}

		private void MainServerCallBack()
		{
			try
			{
				while (!Netplay.Disconnect && SubworldSystem.links.ContainsKey(_id) && !Disconnecting)
				{
					byte[] packetInfo = new byte[3];
					if (_pipeOut.Read(packetInfo) < 3)
					{
						break;
					}

					byte low = packetInfo[1];
					byte high = packetInfo[2];
					int length = (high << 8) | low;

					byte[] data = new byte[length];
					_pipeOut.Read(data, 2, length - 2);
					data[0] = low;
					data[1] = high;

					// Determine if the packet belongs to Subworld Library
					bool subLibPacket = data[2] == MessageID.ModPacket && (ModNet.NetModCount < 256 ? data[3] : BitConverter.ToUInt16(data, 3)) == ModContent.GetInstance<SubworldLibrary>().NetID;
					SubLibMessageType messageType = subLibPacket ? (SubLibMessageType)data[ModNet.NetModCount < 256 ? 4 : 5] : SubLibMessageType.None;

					switch (messageType)
					{
						case SubLibMessageType.None:
						case SubLibMessageType.Broadcast:
							{
								RemoteClient client = Netplay.Clients[packetInfo[0]];
								if (client.IsConnected() && SubworldSystem.playerLocations[client.Socket] == _id && client.State == 10)
								{
									try 
									{ 
										client.Socket.AsyncSend(data, 0, length, (state) => { }); 
									}
									catch (Exception e)
									{
										ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred client AsyncSend " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
									}
								}
							}
							break;

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

									Buffer.BlockCopy(data, 0, buffer.readBuffer, buffer.totalData, length);
									buffer.totalData += length;
									buffer.checkBytes = true;
								}
							}
							break;
					}
				}
			}
			catch (Exception e)
			{
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Exception occurred while reading data pipeOut from " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
			}
			finally
			{
				Close();
			}
		}
	}
}
