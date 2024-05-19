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
				SubworldSystem.StopSubserver(_id);
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred while waiting for pipeIN to connect " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
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
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred while waiting for pipeOUT to connect " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
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
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred while writing data to pipe " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
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
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred while writing queue " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
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
					bool timedOut = _watchdog?.ElapsedMilliseconds > SubworldLibrary.serverConfig.SubserverStartupTimeMax * 1000;
					
					// The subserver takes too long to start up or the process has been terminated by an external factor.
					if (timedOut || exited)
					{
						if (!timedOut && exited)
						{
							ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Process of subworld " + SubworldSystem.subworlds[_id].FullName + " got closed abruptly!");
						}

						if (!exited)
						{ 
							ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Subworld " + SubworldSystem.subworlds[_id].FullName + " took too long to start up! Try increasing timeout in server config");
							_process.Kill();
						}

						Close();
					}
				}
				catch (Exception e)
				{
					Close();
					ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred checking process " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
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

			// No player has been connected and the timeout time has been expired
			if (!_playersHasBeenConnected)
			{
				if (_joinTime != -1 && _keepOpenTime != -1 && _watchdog?.ElapsedMilliseconds > _joinTime * 1000)
				{
					if (_id >= 0 && _id < SubworldSystem.subworlds.Count)
					{
						SubworldSystem.subworlds[_id].OnJoinTimeExpired();
					}

					if (_watchdog?.ElapsedMilliseconds > _joinTime * 1000)
					{
						Disconnect();
					}
				}

				return;
			}

			// Close the server after the keepOpenTime expired
			if (!playersPresent && _keepOpenTime != -1 && _watchdog?.ElapsedMilliseconds > _keepOpenTime * 1000)
			{
				if (_id >= 0 && _id < SubworldSystem.subworlds.Count)
				{
					SubworldSystem.subworlds[_id].OnKeepOpenTimeExpired();
				}

				if (_watchdog?.ElapsedMilliseconds > _keepOpenTime * 1000)
				{
					Disconnect();
				}
			}
		}

		public void Refresh(int joinTime = int.MinValue, int keepOpenTime = int.MinValue)
		{
			if (!Disconnecting)
			{
				if (joinTime != int.MinValue)
				{ 
					_joinTime = joinTime;
				}

				if (keepOpenTime != int.MinValue)
				{ 
					_keepOpenTime = keepOpenTime;
				}

				_watchdog?.Restart();
			}
		}

		public void Disconnect()
		{
			// only when not already disconnecting
			if (Disconnecting)
			{
				return;
			}

			// Set Disconnect and restart timer
			Disconnecting = true;
			_watchdog?.Restart();

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
		}

		private void Close()
		{
			// Set disconnection status
			Disconnect();

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
								if (client.IsConnected())
								{
									try 
									{ 
										client.Socket.AsyncSend(data, 0, length, (state) => { }); 
									}
									catch (Exception e) 
									{
										ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred client AsyncSend " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
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
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn(Main.worldName + " - Exception occurred while reading data from pipe " + SubworldSystem.subworlds[_id].FullName + ": " + e.Message);
			}
			finally
			{
				Close();
			}
		}
	}
}
