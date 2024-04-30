using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.Audio;
using Terraria.Chat;
using Terraria.GameContent;
using Terraria.GameContent.Bestiary;
using Terraria.GameContent.Creative;
using Terraria.GameContent.Events;
using Terraria.Graphics.Capture;
using Terraria.ID;
using Terraria.IO;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.Social;
using Terraria.Utilities;
using Terraria.WorldBuilding;

namespace SubworldLibrary
{
	internal class SubserverSocket : ISocket
	{
		private int id;

		internal static NamedPipeClientStream pipe;
		internal static RemoteAddress address;

		public SubserverSocket(int id)
		{
			this.id = id;
		}

		void ISocket.AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state) { }

		void ISocket.AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state)
		{
			byte[] packet = new byte[size + 1];
			packet[0] = (byte)id;
			Buffer.BlockCopy(data, offset, packet, 1, size);
			pipe.Write(packet);
		}

		void ISocket.Close() { }

		void ISocket.Connect(RemoteAddress address) { }

		RemoteAddress ISocket.GetRemoteAddress() => address;

		bool ISocket.IsConnected() => Netplay.Clients[id].IsActive;

		bool ISocket.IsDataAvailable() => false;

		void ISocket.SendQueuedPackets() { }

		bool ISocket.StartListening(SocketConnectionAccepted callback) => false;

		void ISocket.StopListening() { }
	}

	internal class SubserverLink
	{
		private NamedPipeClientStream pipe;
		private List<byte[]> queue;

		public SubserverLink(string name)
		{
			pipe = new NamedPipeClientStream(".", name + "_IN", PipeDirection.Out);
			queue = new List<byte[]>(16);
		}

		public bool Connecting => queue != null;

		public bool QueueData(byte[] data)
		{
			if (queue == null)
			{
				return false;
			}

			queue.Add(data);
			return true;
		}

		public void Send(byte[] data)
		{
			lock (this)
			{
				if (QueueData(data))
				{
					return;
				}
			}
			pipe.Write(data);
		}

		public void ConnectAndProcessQueue()
		{
			pipe.Connect();
			pipe.Write(queue[0]);
			lock (this)
			{
				int size = 0;
				for (int i = 1; i < queue.Count; i++)
				{
					size += queue[i].Length;
				}

				byte[] bytes = new byte[size];
				size = 0;
				for (int i = 1; i < queue.Count; i++)
				{
					Buffer.BlockCopy(queue[i], 0, bytes, size, queue[i].Length);
					size += queue[i].Length;
				}
				pipe.Write(bytes);

				queue = null;
			}
		}

		public void Close()
		{
			pipe.Close();
		}
	}

	internal class ServerMessageBuffer
	{
		public const int MaxBufferLength = 131070;
		public byte[] buffer = new byte[MaxBufferLength];

		public int dataAmount;
		public bool dataReady;

		public MemoryStream stream;
		public BinaryReader reader;

		public ServerMessageBuffer()
		{
			Reset();
		}

		public void Reset()
		{
			Array.Clear(buffer, 0, buffer.Length);
			dataAmount = 0;
			dataReady = false;
			ResetReader();
		}

		public void ResetReader() 
		{
			stream?.Close();
			stream = new MemoryStream(buffer);
			reader = new BinaryReader(stream);
		}
	}

	public class SubworldSystem : ModSystem
	{
		internal static List<Subworld> subworlds;

		internal static Subworld current;
		internal static Subworld cache;
		internal static WorldFileData main;

		internal static TagCompound copiedData;

		internal static Dictionary<ISocket, int> playerLocations;
		internal static Dictionary<int, SubserverLink> links;

		internal static ServerMessageBuffer serverMessageBuffer;

		public override void OnModLoad()
		{
			if (Main.dedServ)
			{
				serverMessageBuffer = new ServerMessageBuffer();
			}

			subworlds = new List<Subworld>();
			Player.Hooks.OnEnterWorld += OnEnterWorld;
			Netplay.OnDisconnect += OnDisconnect;
		}

		public override void Unload()
		{
			serverMessageBuffer = null;
			Player.Hooks.OnEnterWorld -= OnEnterWorld;
			Netplay.OnDisconnect -= OnDisconnect;
		}

		/// <summary>
		/// Hides the Return button.
		/// <br/>Its value is reset before <see cref="Subworld.OnEnter"/> is called, and after <see cref="Subworld.OnExit"/> is called.
		/// </summary>
		public static bool noReturn;
		/// <summary>
		/// Hides the Underworld background.
		/// <br/>Its value is reset before <see cref="Subworld.OnEnter"/> is called, and after <see cref="Subworld.OnExit"/> is called.
		/// </summary>
		public static bool hideUnderworld;

		/// <summary>
		/// Whether the current (sub)world should forward chat to other server. 
		/// True by default: send chat to other servers
		/// </summary>
		public static bool sendChatToOtherServers = true;

		/// <summary>
		/// The current subworld.
		/// </summary>
		public static Subworld Current => current;
		/// <summary>
		/// Returns true if the current subworld's ID matches the specified ID.
		/// <code>SubworldSystem.IsActive("MyMod/MySubworld")</code>
		/// </summary>
		public static bool IsActive(string id) => current?.FullName == id;
		/// <summary>
		/// Returns true if the specified subworld is active.
		/// </summary>
		public static bool IsActive<T>() where T : Subworld => current?.GetType() == typeof(T);
		/// <summary>
		/// Returns true if not in the main world.
		/// </summary>
		public static bool AnyActive() => current != null;
		/// <summary>
		/// Returns true if the current subworld is from the specified mod.
		/// </summary>
		public static bool AnyActive(Mod mod) => current?.Mod == mod;
		/// <summary>
		/// Returns true if the current subworld is from the specified mod.
		/// </summary>
		public static bool AnyActive<T>() where T : Mod => current?.Mod == ModContent.GetInstance<T>();
		/// <summary>
		/// The current subworld's file path.
		/// </summary>
		public static string CurrentPath => Path.Combine(Main.WorldPath, Path.GetFileNameWithoutExtension(main.Path), current.FileName + ".wld");

		/// <summary>
		/// Tries to enter the subworld with the specified ID.
		/// <code>SubworldSystem.Enter("MyMod/MySubworld")</code>
		/// </summary>
		public static bool Enter(string id)
		{
			if (current != cache)
			{
				return false;
			}

			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].FullName == id)
				{
					BeginEntering(i);
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Enters the specified subworld.
		/// </summary>
		public static bool Enter<T>() where T : Subworld
		{
			if (current != cache)
			{
				return false;
			}

			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].GetType() == typeof(T))
				{
					BeginEntering(i);
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Exits the current subworld.
		/// </summary>
		public static void Exit()
		{
			if (current != null && current == cache)
			{
				BeginEntering(current.ReturnDestination);
			}
		}

		private static void BeginEntering(int index)
		{
			if (Main.netMode == 2)
			{
				return;
			}

			if (index == int.MinValue)
			{
				Task.Factory.StartNew(ExitWorldCallBack, null);
				return;
			}

			if (Main.netMode == 0)
			{
				if (current == null && index >= 0)
				{
					main = Main.ActiveWorldFileData;
				}
				Task.Factory.StartNew(ExitWorldCallBack, index);
				return;
			}

			ModPacket packet = ModContent.GetInstance<SubworldLibrary>().GetPacket();
			packet.Write((byte)SubLibMessageType.BeginEntering);
			packet.Write(index < 0 ? ushort.MaxValue : (ushort)index);
			packet.Send();
		}

		/// <summary>
		/// Tries to send the specified player to the subworld with the specified ID.
		/// </summary>
		public static void MovePlayerToSubworld(string id, int player)
		{
			if (Main.netMode == 1 || (Main.netMode == 2 && current != null))
			{
				return;
			}

			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].FullName == id)
				{
					if (Main.netMode == 0)
					{
						BeginEntering(i);
						break;
					}

					MovePlayerToSubserver(player, (ushort)i);
					break;
				}
			}
		}

		/// <summary>
		/// Sends the specified player to the specified subworld.
		/// </summary>
		public static void MovePlayerToSubworld<T>(int player) where T : Subworld
		{
			if (Main.netMode == 1 || (Main.netMode == 2 && current != null))
			{
				return;
			}

			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].GetType() == typeof(T))
				{
					if (Main.netMode == 0)
					{
						BeginEntering(i);
						break;
					}

					MovePlayerToSubserver(player, (ushort)i);
					break;
				}
			}
		}

		internal static void MovePlayerToSubserver(int player, ushort id)
		{
			Mod subLib = ModContent.GetInstance<SubworldLibrary>();
			RemoteClient client = Netplay.Clients[player];

			bool inSubworld = playerLocations.TryGetValue(client.Socket, out int location);
			if (inSubworld)
			{
				if (id == location)
				{
					return;
				}

				byte[] data;
				if (ModNet.NetModCount < 256)
				{
					data = new byte[6] { (byte)player, 5, 0, 250, (byte)subLib.NetID, (byte)SubLibMessageType.MovePlayerOnServer };
				}
				else
				{
					data = new byte[7] { (byte)player, 7, 0, 250, (byte)subLib.NetID, (byte)(subLib.NetID >> 8), (byte)SubLibMessageType.MovePlayerOnServer };
				}
				links[location].Send(data);

				if (id == ushort.MaxValue)
				{
					playerLocations.Remove(client.Socket);
					client.State = 0;
					client.ResetSections();

					ModPacket leavePacket = subLib.GetPacket();
					leavePacket.Write((byte)SubLibMessageType.MovePlayerOnClient);
					leavePacket.Write(id);
					leavePacket.Send(player);

					return;
				}
			}
			if (id == ushort.MaxValue)
			{
				return;
			}

			ModPacket enterPacket = subLib.GetPacket();
			enterPacket.Write((byte)SubLibMessageType.MovePlayerOnClient);
			enterPacket.Write(id);
			enterPacket.Send(player);

			// this respects the vanilla call order

			if (!inSubworld)
			{
				Main.player[player].active = false;
				NetMessage.SendData(14, -1, player, null, player, 0);
			}

			ChatHelper.BroadcastChatMessage(NetworkText.FromKey("Mods.SubworldLibrary.Move", client.Name, subworlds[id].DisplayName), new Color(255, 240, 20), player);

			if (!inSubworld)
			{
				Player.Hooks.PlayerDisconnect(player);
			}

			playerLocations[client.Socket] = id;

			StartSubserver(id);
		}

		private static void SyncDisconnect(int player)
		{
			int netId = ModContent.GetInstance<SubworldLibrary>().NetID;
			byte[] data; // client, (ushort) size, packet id, (byte/ushort) sublib net id twice (read a second time by sublib to sync a leaving client)
			if (ModNet.NetModCount < 256)
			{
				data = new byte[6] { (byte)player, 5, 0, 250, (byte)netId, (byte)SubLibMessageType.MovePlayerOnServer};
			}
			else
			{
				data = new byte[7] { (byte)player, 7, 0, 250, (byte)netId, (byte)(netId >> 8), (byte)SubLibMessageType.MovePlayerOnServer };
			}

			foreach (SubserverLink link in links.Values)
			{
				link.Send(data);
			}
		}

		/// <summary>
		/// Starts a subserver for the subworld with the specified ID, if one is not running already.
		/// </summary>
		public static void StartSubserver(int id)
		{
			if (links.ContainsKey(id))
			{
				return;
			}

			string name = subworlds[id].FileName;

			Process p = new Process();
			p.StartInfo.FileName = Process.GetCurrentProcess().MainModule!.FileName;
			p.StartInfo.Arguments = "tModLoader.dll -server -showserverconsole -world \"" + Main.worldPathName + "\" -subworld \"" + name + "\"";
			p.StartInfo.UseShellExecute = true;
			p.Start();

			new Thread(MainServerCallBack)
			{
				Name = "Subserver Packets",
				IsBackground = true
			}.Start(id);

			links[id] = new SubserverLink(name);

			copiedData = new TagCompound();
			CopyMainWorldData();

			using (MemoryStream stream = new MemoryStream())
			{
				TagIO.ToStream(copiedData, stream);
				links[id].QueueData(stream.ToArray());
			}

			copiedData = null;

			Task.Run(links[id].ConnectAndProcessQueue);
		}

		/// <summary>
		/// Tries to get the index of the subworld with the specified ID.
		/// <br/> Typically used for <see cref="Subworld.ReturnDestination"/>.
		/// <br/> Returns <see cref="int.MinValue"/> if the subworld couldn't be found.
		/// <code>public override int ReturnDestination => SubworldSystem.GetIndex("MyMod/MySubworld");</code>
		/// </summary>
		public static int GetIndex(string id)
		{
			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].FullName == id)
				{
					return i;
				}
			}
			return int.MinValue;
		}

		/// <summary>
		/// Gets the index of the specified subworld.
		/// <br/> Typically used for <see cref="Subworld.ReturnDestination"/>.
		/// </summary>
		public static int GetIndex<T>() where T : Subworld
		{
			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].GetType() == typeof(T))
				{
					return i;
				}
			}
			return int.MinValue;
		}

		private static byte[] GetPacketHeader(int size, int mod, SubLibMessageType type)
		{
			byte[] packet = new byte[size];

			int i = 0;
			packet[i++] = 0;

			packet[i++] = (byte)(size - 1);
			packet[i++] = (byte)((size - 1) >> 8);

			packet[i++] = 250;

			short subLib = ModContent.GetInstance<SubworldLibrary>().NetID;

			if (ModNet.NetModCount < 256)
			{
				packet[i++] = (byte)subLib;
				packet[i++] = (byte)type;
				packet[i++] = (byte)mod;
			}
			else
			{
				packet[i++] = (byte)subLib;
				packet[i++] = (byte)(subLib >> 8);
				packet[i++] = (byte)type;
				packet[i++] = (byte)mod;
				packet[i++] = (byte)(mod >> 8);
			}

			return packet;
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to a subserver.
		/// <br/> Use <see cref="GetIndex"/> to get the subserver's ID.
		/// </summary>
		public static void SendToSubserver(int subserver, Mod mod, byte[] data)
		{
			if (Main.netMode == 2 && current == null && links.ContainsKey(subserver))
			{
				int header = ModNet.NetModCount < 256 ? 7 : 9;
				byte[] packet = GetPacketHeader(data.Length + header, mod.NetID, SubLibMessageType.SendToSubserver);
				Buffer.BlockCopy(data, 0, packet, header, data.Length);
				links[subserver].Send(packet);
			}
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to all subservers.
		/// </summary>
		public static void SendToAllSubservers(Mod mod, byte[] data)
		{
			if (Main.netMode == 2 && current == null)
			{
				int header = ModNet.NetModCount < 256 ? 7 : 9;
				byte[] packet = GetPacketHeader(data.Length + header, mod.NetID, SubLibMessageType.SendToSubserver);
				Buffer.BlockCopy(data, 0, packet, header, data.Length);

				foreach (SubserverLink link in links.Values)
				{
					link.Send(packet);
				}
			}
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to all subservers added by that mod.
		/// </summary>
		public static void SendToAllSubserversFromMod(Mod mod, byte[] data)
		{
			if (Main.netMode == 2 && current == null)
			{
				int header = ModNet.NetModCount < 256 ? 7 : 9;
				byte[] packet = GetPacketHeader(data.Length + header, mod.NetID, SubLibMessageType.SendToSubserver);
				Buffer.BlockCopy(data, 0, packet, header, data.Length);

				foreach (KeyValuePair<int, SubserverLink> pair in links)
				{
					if (subworlds[pair.Key].Mod == mod)
					{
						pair.Value.Send(packet);
					}
				}
			}
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to the main server.
		/// </summary>
		public static void SendToMainServer(Mod mod, byte[] data)
		{
			if (Main.netMode == 2 && current != null)
			{
				int header = ModNet.NetModCount < 256 ? 7 : 9;
				byte[] packet = GetPacketHeader(data.Length + header, mod.NetID, SubLibMessageType.SendToMainServer);
				Buffer.BlockCopy(data, 0, packet, header, data.Length);
				SubserverSocket.pipe.Write(packet);
			}
		}

		/// <summary>
		/// Can only be called in <see cref="Subworld.CopyMainWorldData"/> or <see cref="Subworld.OnExit"/>!
		/// <br/>Stores data to be transferred between worlds under the specified key, if that key is not already in use.
		/// <br/>Naming the key after the variable pointing to the data is highly recommended to avoid redundant copying. This can be done automatically with nameof().
		/// <code>SubworldSystem.CopyWorldData(nameof(DownedSystem.downedBoss), DownedSystem.downedBoss);</code>
		/// </summary>
		public static void CopyWorldData(string key, object data)
		{
			if (data != null && !copiedData.ContainsKey(key))
			{
				copiedData[key] = data;
			}
		}

		/// <summary>
		/// Can only be called in <see cref="Subworld.ReadCopiedMainWorldData"/> or <see cref="Subworld.ReadCopiedSubworldData"/>!
		/// <br/>Reads data copied from another world stored under the specified key.
		/// <code>DownedSystem.downedBoss = SubworldSystem.ReadCopiedWorldData&lt;bool&gt;(nameof(DownedSystem.downedBoss));</code>
		/// </summary>
		public static T ReadCopiedWorldData<T>(string key) => copiedData.Get<T>(key);

		private static void EraseSubworlds(int index)
		{
			WorldFileData world = Main.WorldList[index];
			string path = Path.Combine(Main.WorldPath, Path.GetFileNameWithoutExtension(world.Path));
			if (FileUtilities.Exists(path, world.IsCloudSave))
			{
				FileUtilities.Delete(path, world.IsCloudSave);
			}
		}

		private static bool ChangeAudio()
		{
			if (current != null)
			{
				return current.ChangeAudio();
			}
			if (cache != null)
			{
				return cache.ChangeAudio();
			}
			return false;
		}

		private static bool ManualAudioUpdates()
		{
			if (current != null)
			{
				return current.ManualAudioUpdates;
			}
			if (cache != null)
			{
				return cache.ManualAudioUpdates;
			}
			return false;
		}

		private static void CopyMainWorldData()
		{
			copiedData["seed"] = Main.ActiveWorldFileData.SeedText;
			copiedData["gameMode"] = Main.ActiveWorldFileData.GameMode;
			copiedData["hardMode"] = Main.hardMode;

			// it's called reflection because the code is ugly like you
			using (MemoryStream stream = new MemoryStream())
			{
				using BinaryWriter writer = new BinaryWriter(stream);

				FieldInfo field = typeof(NPCKillsTracker).GetField("_killCountsByNpcId", BindingFlags.NonPublic | BindingFlags.Instance);
				Dictionary<string, int> kills = (Dictionary<string, int>)field.GetValue(Main.BestiaryTracker.Kills);

				writer.Write(kills.Count);
				foreach (KeyValuePair<string, int> item in kills)
				{
					writer.Write(item.Key);
					writer.Write(item.Value);
				}

				field = typeof(NPCWasNearPlayerTracker).GetField("_wasNearPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
				HashSet<string> sights = (HashSet<string>)field.GetValue(Main.BestiaryTracker.Sights);

				writer.Write(sights.Count);
				foreach (string item in sights)
				{
					writer.Write(item);
				}

				field = typeof(NPCWasChatWithTracker).GetField("_chattedWithPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
				HashSet<string> chats = (HashSet<string>)field.GetValue(Main.BestiaryTracker.Chats);

				writer.Write(chats.Count);
				foreach (string item in chats)
				{
					writer.Write(item);
				}

				copiedData["bestiary"] = stream.GetBuffer();
			}
			using (MemoryStream stream = new MemoryStream())
			{
				using BinaryWriter writer = new BinaryWriter(stream);

				FieldInfo field = typeof(CreativePowerManager).GetField("_powersById", BindingFlags.NonPublic | BindingFlags.Instance);
				foreach (KeyValuePair<ushort, ICreativePower> item in (Dictionary<ushort, ICreativePower>)field.GetValue(CreativePowerManager.Instance))
				{
					if (item.Value is IPersistentPerWorldContent power)
					{
						writer.Write((ushort)(item.Key + 1));
						power.Save(writer);
					}
				}
				writer.Write((ushort)0);

				copiedData["powers"] = stream.GetBuffer();
			}

			copiedData[nameof(Main.drunkWorld)] = Main.drunkWorld;
			copiedData[nameof(Main.getGoodWorld)] = Main.getGoodWorld;
			copiedData[nameof(Main.tenthAnniversaryWorld)] = Main.tenthAnniversaryWorld;
			copiedData[nameof(Main.dontStarveWorld)] = Main.dontStarveWorld;
			copiedData[nameof(Main.notTheBeesWorld)] = Main.notTheBeesWorld;
			copiedData[nameof(Main.remixWorld)] = Main.remixWorld;
			copiedData[nameof(Main.noTrapsWorld)] = Main.noTrapsWorld;
			copiedData[nameof(Main.zenithWorld)] = Main.zenithWorld;

			CopyDowned();

			foreach (ICopyWorldData data in ModContent.GetContent<ICopyWorldData>())
			{
				data.CopyMainWorldData();
			}
		}

		private static void ReadCopiedMainWorldData()
		{
			Main.ActiveWorldFileData.SetSeed(copiedData.Get<string>("seed"));
			Main.GameMode = copiedData.Get<int>("gameMode");
			Main.hardMode = copiedData.Get<bool>("hardMode");

			// i'm sorry that was mean
			using (MemoryStream stream = new MemoryStream(copiedData.Get<byte[]>("bestiary")))
			{
				using BinaryReader reader = new BinaryReader(stream);

				FieldInfo field = typeof(NPCKillsTracker).GetField("_killCountsByNpcId", BindingFlags.NonPublic | BindingFlags.Instance);
				Dictionary<string, int> kills = (Dictionary<string, int>)field.GetValue(Main.BestiaryTracker.Kills);

				int count = reader.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					kills[reader.ReadString()] = reader.ReadInt32();
				}

				field = typeof(NPCWasNearPlayerTracker).GetField("_wasNearPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
				HashSet<string> sights = (HashSet<string>)field.GetValue(Main.BestiaryTracker.Sights);

				count = reader.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					sights.Add(reader.ReadString());
				}

				field = typeof(NPCWasChatWithTracker).GetField("_chattedWithPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
				HashSet<string> chats = (HashSet<string>)field.GetValue(Main.BestiaryTracker.Chats);

				count = reader.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					chats.Add(reader.ReadString());
				}
			}
			using (MemoryStream stream = new MemoryStream(copiedData.Get<byte[]>("powers")))
			{
				using BinaryReader reader = new BinaryReader(stream);

				FieldInfo field = typeof(CreativePowerManager).GetField("_powersById", BindingFlags.NonPublic | BindingFlags.Instance);
				Dictionary<ushort, ICreativePower> powers = (Dictionary<ushort, ICreativePower>)field.GetValue(CreativePowerManager.Instance);

				ushort id;
				while ((id = reader.ReadUInt16()) > 0)
				{
					((IPersistentPerWorldContent)powers[(ushort)(id - 1)]).Load(reader, 0);
				}
			}

			Main.drunkWorld = copiedData.Get<bool>(nameof(Main.drunkWorld));
			Main.getGoodWorld = copiedData.Get<bool>(nameof(Main.getGoodWorld));
			Main.tenthAnniversaryWorld = copiedData.Get<bool>(nameof(Main.tenthAnniversaryWorld));
			Main.dontStarveWorld = copiedData.Get<bool>(nameof(Main.dontStarveWorld));
			Main.notTheBeesWorld = copiedData.Get<bool>(nameof(Main.notTheBeesWorld));
			Main.remixWorld = copiedData.Get<bool>(nameof(Main.remixWorld));
			Main.noTrapsWorld = copiedData.Get<bool>(nameof(Main.noTrapsWorld));
			Main.zenithWorld = copiedData.Get<bool>(nameof(Main.zenithWorld));

			ReadCopiedDowned();

			foreach (ICopyWorldData data in ModContent.GetContent<ICopyWorldData>())
			{
				data.ReadCopiedMainWorldData();
			}
		}

		private static void CopyDowned()
		{
			copiedData[nameof(NPC.downedSlimeKing)] = NPC.downedSlimeKing;

			copiedData[nameof(NPC.downedBoss1)] = NPC.downedBoss1;
			copiedData[nameof(NPC.downedBoss2)] = NPC.downedBoss2;
			copiedData[nameof(NPC.downedBoss3)] = NPC.downedBoss3;

			copiedData[nameof(NPC.downedQueenBee)] = NPC.downedQueenBee;
			copiedData[nameof(NPC.downedDeerclops)] = NPC.downedDeerclops;

			copiedData[nameof(NPC.downedQueenSlime)] = NPC.downedQueenSlime;

			copiedData[nameof(NPC.downedMechBoss1)] = NPC.downedMechBoss1;
			copiedData[nameof(NPC.downedMechBoss2)] = NPC.downedMechBoss2;
			copiedData[nameof(NPC.downedMechBoss3)] = NPC.downedMechBoss3;
			copiedData[nameof(NPC.downedMechBossAny)] = NPC.downedMechBossAny;

			copiedData[nameof(NPC.downedPlantBoss)] = NPC.downedPlantBoss;
			copiedData[nameof(NPC.downedGolemBoss)] = NPC.downedGolemBoss;

			copiedData[nameof(NPC.downedFishron)] = NPC.downedFishron;
			copiedData[nameof(NPC.downedEmpressOfLight)] = NPC.downedEmpressOfLight;

			copiedData[nameof(NPC.downedAncientCultist)] = NPC.downedAncientCultist;

			copiedData[nameof(NPC.downedTowerSolar)] = NPC.downedTowerSolar;
			copiedData[nameof(NPC.downedTowerVortex)] = NPC.downedTowerVortex;
			copiedData[nameof(NPC.downedTowerNebula)] = NPC.downedTowerNebula;
			copiedData[nameof(NPC.downedTowerStardust)] = NPC.downedTowerStardust;

			copiedData[nameof(NPC.downedMoonlord)] = NPC.downedMoonlord;

			copiedData[nameof(NPC.downedGoblins)] = NPC.downedGoblins;
			copiedData[nameof(NPC.downedClown)] = NPC.downedClown;
			copiedData[nameof(NPC.downedFrost)] = NPC.downedFrost;
			copiedData[nameof(NPC.downedPirates)] = NPC.downedPirates;
			copiedData[nameof(NPC.downedMartians)] = NPC.downedMartians;

			copiedData[nameof(NPC.downedHalloweenTree)] = NPC.downedHalloweenTree;
			copiedData[nameof(NPC.downedHalloweenKing)] = NPC.downedHalloweenKing;

			copiedData[nameof(NPC.downedChristmasTree)] = NPC.downedChristmasTree;
			copiedData[nameof(NPC.downedChristmasSantank)] = NPC.downedChristmasSantank;
			copiedData[nameof(NPC.downedChristmasIceQueen)] = NPC.downedChristmasIceQueen;

			copiedData[nameof(DD2Event.DownedInvasionT1)] = DD2Event.DownedInvasionT1;
			copiedData[nameof(DD2Event.DownedInvasionT2)] = DD2Event.DownedInvasionT2;
			copiedData[nameof(DD2Event.DownedInvasionT3)] = DD2Event.DownedInvasionT3;
		}

		private static void ReadCopiedDowned()
		{
			NPC.downedSlimeKing = copiedData.Get<bool>(nameof(NPC.downedSlimeKing));

			NPC.downedBoss1 = copiedData.Get<bool>(nameof(NPC.downedBoss1));
			NPC.downedBoss2 = copiedData.Get<bool>(nameof(NPC.downedBoss2));
			NPC.downedBoss3 = copiedData.Get<bool>(nameof(NPC.downedBoss3));

			NPC.downedQueenBee = copiedData.Get<bool>(nameof(NPC.downedQueenBee));
			NPC.downedDeerclops = copiedData.Get<bool>(nameof(NPC.downedDeerclops));

			NPC.downedQueenSlime = copiedData.Get<bool>(nameof(NPC.downedQueenSlime));

			NPC.downedMechBoss1 = copiedData.Get<bool>(nameof(NPC.downedMechBoss1));
			NPC.downedMechBoss2 = copiedData.Get<bool>(nameof(NPC.downedMechBoss2));
			NPC.downedMechBoss3 = copiedData.Get<bool>(nameof(NPC.downedMechBoss3));
			NPC.downedMechBossAny = copiedData.Get<bool>(nameof(NPC.downedMechBossAny));

			NPC.downedPlantBoss = copiedData.Get<bool>(nameof(NPC.downedPlantBoss));
			NPC.downedGolemBoss = copiedData.Get<bool>(nameof(NPC.downedGolemBoss));

			NPC.downedFishron = copiedData.Get<bool>(nameof(NPC.downedFishron));
			NPC.downedEmpressOfLight = copiedData.Get<bool>(nameof(NPC.downedEmpressOfLight));

			NPC.downedAncientCultist = copiedData.Get<bool>(nameof(NPC.downedAncientCultist));

			NPC.downedTowerSolar = copiedData.Get<bool>(nameof(NPC.downedTowerSolar));
			NPC.downedTowerVortex = copiedData.Get<bool>(nameof(NPC.downedTowerVortex));
			NPC.downedTowerNebula = copiedData.Get<bool>(nameof(NPC.downedTowerNebula));
			NPC.downedTowerStardust = copiedData.Get<bool>(nameof(NPC.downedTowerStardust));

			NPC.downedMoonlord = copiedData.Get<bool>(nameof(NPC.downedMoonlord));

			NPC.downedGoblins = copiedData.Get<bool>(nameof(NPC.downedGoblins));
			NPC.downedClown = copiedData.Get<bool>(nameof(NPC.downedClown));
			NPC.downedFrost = copiedData.Get<bool>(nameof(NPC.downedFrost));
			NPC.downedPirates = copiedData.Get<bool>(nameof(NPC.downedPirates));
			NPC.downedMartians = copiedData.Get<bool>(nameof(NPC.downedMartians));

			NPC.downedHalloweenTree = copiedData.Get<bool>(nameof(NPC.downedHalloweenTree));
			NPC.downedHalloweenKing = copiedData.Get<bool>(nameof(NPC.downedHalloweenKing));

			NPC.downedChristmasTree = copiedData.Get<bool>(nameof(NPC.downedChristmasTree));
			NPC.downedChristmasSantank = copiedData.Get<bool>(nameof(NPC.downedChristmasSantank));
			NPC.downedChristmasIceQueen = copiedData.Get<bool>(nameof(NPC.downedChristmasIceQueen));

			DD2Event.DownedInvasionT1 = copiedData.Get<bool>(nameof(DD2Event.DownedInvasionT1));
			DD2Event.DownedInvasionT2 = copiedData.Get<bool>(nameof(DD2Event.DownedInvasionT2));
			DD2Event.DownedInvasionT3 = copiedData.Get<bool>(nameof(DD2Event.DownedInvasionT3));
		}

		private static void MainServerCallBack(object id)
		{
			NamedPipeServerStream pipe = new NamedPipeServerStream(subworlds[(int)id].FileName + "_OUT", PipeDirection.In);
			try
			{
				pipe.WaitForConnection();

				while (pipe.IsConnected)
				{
					byte[] packetInfo = new byte[3];
					if (pipe.Read(packetInfo) < 3)
					{
						break;
					}

					byte low = packetInfo[1];
					byte high = packetInfo[2];
					int length = (high << 8) | low;

					byte[] data = new byte[length];
					pipe.Read(data, 2, length - 2);
					data[0] = low;
					data[1] = high;

					bool subLibPacket = data[2] == 250 && (ModNet.NetModCount < 256 ? data[3] : BitConverter.ToUInt16(data, 3)) == ModContent.GetInstance<SubworldLibrary>().NetID;
					SubLibMessageType messageType = subLibPacket ? (SubLibMessageType)data[ModNet.NetModCount < 256 ? 4 : 5] : SubLibMessageType.None;

					if (subLibPacket && (messageType == SubLibMessageType.SendToMainServer
						|| messageType == SubLibMessageType.SendToSubserver
						|| messageType == SubLibMessageType.SynchronizeChatMessage))
					{
						lock (serverMessageBuffer)
						{
							while (serverMessageBuffer.dataAmount + length > serverMessageBuffer.buffer.Length)
							{
								Monitor.Exit(serverMessageBuffer);
								Thread.Yield();
								Monitor.Enter(serverMessageBuffer);
							}

							Buffer.BlockCopy(data, 0, serverMessageBuffer.buffer, serverMessageBuffer.dataAmount, length);
							serverMessageBuffer.dataAmount += length;
							serverMessageBuffer.dataReady = true;
						}
					}
					else if (subLibPacket && messageType != SubLibMessageType.ChatMessage)
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
					else
					{
						Netplay.Clients[packetInfo[0]].Socket.AsyncSend(data, 0, length, (state) => { });
					}
				}
			}
			finally
			{
				pipe.Close();
				links[(int)id].Close();
				links.Remove((int)id);
			}
		}

		private static bool LoadIntoSubworld()
		{
			if (Program.LaunchParameters.TryGetValue("-subworld", out string id))
			{
				for (int i = 0; i < subworlds.Count; i++)
				{
					if (subworlds[i].FileName != id)
					{
						continue;
					}

					Main.myPlayer = 255;
					main = Main.ActiveWorldFileData;
					current = subworlds[i];

					NamedPipeServerStream pipe = new NamedPipeServerStream(current.FileName + "_IN", PipeDirection.In);
					pipe.WaitForConnection();

					copiedData = TagIO.FromStream(pipe);
					LoadWorld();
					copiedData = null;

					// replicates Netplay.InitializeServer, no need to set ReadBuffer because it's not used
					for (int j = 0; j < 256; j++)
					{
						Netplay.Clients[j].Id = j;
						Netplay.Clients[j].Reset();
					}
					SubserverSocket.address = new TcpAddress(IPAddress.Any, 0);

					new Thread(SubserverCallBack)
					{
						IsBackground = true
					}.Start(pipe);

					SubserverSocket.pipe = new NamedPipeClientStream(".", current.FileName + "_OUT", PipeDirection.Out);
					SubserverSocket.pipe.Connect();

					return true;
				}

				Netplay.Disconnect = true;
				Main.instance.Exit();
				return true;
			}

			playerLocations = new Dictionary<ISocket, int>();
			links = new Dictionary<int, SubserverLink>();

			return false;
		}

		private static void SubserverCallBack(object pipeObject)
		{
			NamedPipeServerStream pipe = (NamedPipeServerStream)pipeObject;
			try
			{
				while (pipe.IsConnected && !Netplay.Disconnect)
				{
					byte[] packetInfo = new byte[3];
					if (pipe.Read(packetInfo) < 3)
					{
						break;
					}

					byte low = packetInfo[1];
					byte high = packetInfo[2];
					int length = (high << 8) | low;

					byte[] data = new byte[length];
					pipe.Read(data, 2, length - 2);
					data[0] = low;
					data[1] = high;

					if (data[2] == 250 && (ModNet.NetModCount < 256 ? data[3] : BitConverter.ToUInt16(data, 3)) == ModContent.GetInstance<SubworldLibrary>().NetID
						&& ((SubLibMessageType)data[ModNet.NetModCount < 256 ? 4 : 5] == SubLibMessageType.SendToMainServer
						|| (SubLibMessageType)data[ModNet.NetModCount < 256 ? 4 : 5] == SubLibMessageType.SendToSubserver
						|| (SubLibMessageType)data[ModNet.NetModCount < 256 ? 4 : 5] == SubLibMessageType.SynchronizeChatMessage))
					{
						lock (serverMessageBuffer)
						{
							while (serverMessageBuffer.dataAmount + length > serverMessageBuffer.buffer.Length)
							{
								Monitor.Exit(serverMessageBuffer);
								Thread.Yield();
								Monitor.Enter(serverMessageBuffer);
							}

							Buffer.BlockCopy(data, 0, serverMessageBuffer.buffer, serverMessageBuffer.dataAmount, length);
							serverMessageBuffer.dataAmount += length;
							serverMessageBuffer.dataReady = true;
						}
					}
					else
					{
						MessageBuffer buffer = NetMessage.buffer[packetInfo[0]];

						// Intentional packet leak: block everything besides Hello when not
						// successfully logged in (yet). Prevents server from booting the client.
						if (Netplay.Clients[buffer.whoAmI].State == 0 && data[2] != 1)
						{
							continue;
						}

						lock (buffer)
						{
							while (buffer.totalData + length > buffer.readBuffer.Length)
							{
								Monitor.Exit(buffer);
								Thread.Yield();
								Monitor.Enter(buffer);
							}

							//TODO: is there a race condition here? will removing this statement make it more frequent?
							if (!Netplay.Clients[buffer.whoAmI].IsActive)
							{
								if (Netplay.Clients[buffer.whoAmI].Socket == null)
								{
									Netplay.Clients[buffer.whoAmI].Socket = new SubserverSocket(buffer.whoAmI);
								}
								Netplay.Clients[buffer.whoAmI].IsActive = true;
							}

							Buffer.BlockCopy(data, 0, buffer.readBuffer, buffer.totalData, length);
							buffer.totalData += length;
							buffer.checkBytes = true;
						}
					}
				}
			}
			finally
			{
				Netplay.Disconnect = true;
				pipe.Close();
				SubserverSocket.pipe.Close();
			}
		}

		internal static void ExitWorldCallBack(object index)
		{
			// presumably avoids a race condition?
			int netMode = Main.netMode;

			if (index != null)
			{
				if (netMode == 0)
				{
					WorldFile.CacheSaveTime();

					if (copiedData == null)
					{
						copiedData = new TagCompound();
					}
					if (cache != null)
					{
						cache.CopySubworldData();
						cache.OnExit();
					}
					if ((int)index >= 0)
					{
						CopyMainWorldData();
					}
				}
				else
				{
					Netplay.Connection.State = 1;
					cache?.OnExit();
				}

				current = (int)index < 0 ? null : subworlds[(int)index];
			}
			else
			{
				current = null;
			}

			Main.invasionProgress = -1;
			Main.invasionProgressDisplayLeft = 0;
			Main.invasionProgressAlpha = 0;
			Main.invasionProgressIcon = 0;

			noReturn = false;

			if (current != null)
			{
				hideUnderworld = true;
				current.OnEnter();
			}
			else
			{
				hideUnderworld = false;
			}

			Main.gameMenu = true;

			SoundEngine.StopTrackedSounds();
			CaptureInterface.ResetFocus();

			Main.ActivePlayerFileData.StopPlayTimer();
			Player.SavePlayer(Main.ActivePlayerFileData);
			Player.ClearPlayerTempInfo();

			Rain.ClearRain();

			if (netMode != 1)
			{
				WorldFile.SaveWorld();
			}
			else if (index == null)
			{
				Netplay.Disconnect = true;
				Main.netMode = 0;
			}
			SystemLoader.OnWorldUnload();

			Main.fastForwardTimeToDawn = false;
			Main.fastForwardTimeToDusk = false;
			Main.UpdateTimeRate();

			if (index == null)
			{
				cache = null;
				Main.menuMode = 0;
				return;
			}

			WorldGen.noMapUpdate = true;
			if (cache != null && cache.NoPlayerSaving)
			{
				PlayerFileData playerData = Player.GetFileData(Main.ActivePlayerFileData.Path, Main.ActivePlayerFileData.IsCloudSave);
				if (playerData != null)
				{
					playerData.Player.whoAmI = Main.myPlayer;
					playerData.SetAsActive();
				}
			}

			if (netMode != 1)
			{
				LoadWorld();
			}
			else
			{
				NetMessage.SendData(1);
				Main.autoPass = true;
			}
		}

		private static void ProcessServerMessageBuffer()
		{
			if (!serverMessageBuffer.dataReady)
			{
				return;
			}

			bool lockTaken = false;
			try
			{
				Monitor.Enter(serverMessageBuffer, ref lockTaken);

				int start = 0;
				int amount = serverMessageBuffer.dataAmount;

				try
				{
					while (amount >= 2)
					{
						int length = BitConverter.ToUInt16(serverMessageBuffer.buffer, start);

						if (amount < length)
						{
							break;
						}

						if (serverMessageBuffer.buffer[start + 2] == 250 && (ModNet.NetModCount < 256 ? serverMessageBuffer.buffer[start + 3] : BitConverter.ToUInt16(serverMessageBuffer.buffer, 3)) == ModContent.GetInstance<SubworldLibrary>().NetID)
						{
							int header = ModNet.NetModCount < 256 ? 4 : 5;
							serverMessageBuffer.reader.BaseStream.Position = start + header;
							ModContent.GetInstance<SubworldLibrary>().HandlePacket(serverMessageBuffer.reader, 255);
						}

						amount -= length;
						start += length;
					}
				}
				catch
				{ 
				}

				if (amount != serverMessageBuffer.dataAmount)
				{
					for (int index = 0; index < amount; ++index)
					{
						serverMessageBuffer.buffer[index] = serverMessageBuffer.buffer[index + start];
					}
					serverMessageBuffer.dataAmount = amount;
				}

				serverMessageBuffer.dataReady = false;
			}
			finally
			{
				if (lockTaken)
				{
					Monitor.Exit(serverMessageBuffer);
				}
			}
		}

		private static void LoadWorld()
		{
			bool isSubworld = current != null;
			bool cloud = main.IsCloudSave;
			string path = isSubworld ? CurrentPath : main.Path;

			Main.rand = new UnifiedRandom((int)DateTime.Now.Ticks);

			cache?.OnUnload();

			Main.ToggleGameplayUpdates(false);

			WorldGen.gen = true;
			WorldGen.loadFailed = false;
			WorldGen.loadSuccess = false;

			if (!isSubworld || current.ShouldSave)
			{
				if (!isSubworld)
				{
					Main.ActiveWorldFileData = main;
				}

				TryLoadWorldFile(path, cloud, 0);
			}

			if (isSubworld)
			{
				if (WorldGen.loadFailed)
				{
					ModContent.GetInstance<SubworldLibrary>().Logger.Warn("Failed to load \"" + Main.worldName + (WorldGen.worldBackup ? "\" from file" : "\" from file, no backup"));
				}

				if (!WorldGen.loadSuccess)
				{
					LoadSubworld(path, cloud);
				}

				current.OnLoad();
			}
			else if (!WorldGen.loadSuccess)
			{
				ModContent.GetInstance<SubworldLibrary>().Logger.Error("Failed to load \"" + main.Name + (WorldGen.worldBackup ? "\" from file" : "\" from file, no backup"));
				Main.menuMode = 0;
				if (Main.netMode == 2)
				{
					Netplay.Disconnect = true;
				}
				return;
			}

			WorldGen.gen = false;

			if (Main.netMode != 2)
			{
				if (Main.mapEnabled)
				{
					Main.Map.Load();
				}
				Main.sectionManager.SetAllSectionsLoaded();
				while (Main.mapEnabled && Main.loadMapLock)
				{
					Main.statusText = Lang.gen[68].Value + " " + (int)((float)Main.loadMapLastX / Main.maxTilesX * 100 + 1) + "%";
					Thread.Sleep(0);
				}

				if (Main.anglerWhoFinishedToday.Contains(Main.LocalPlayer.name))
				{
					Main.anglerQuestFinished = true;
				}

				Main.QueueMainThreadAction(SpawnPlayer);
			}
		}

		private static void SpawnPlayer()
		{
			Main.LocalPlayer.Spawn(PlayerSpawnContext.SpawningIntoWorld);
			WorldFile.SetOngoingToTemps();
			Main.resetClouds = true;
			Main.gameMenu = false;
		}

		private static void OnEnterWorld(Player player)
		{
			if (Main.netMode == 1)
			{
				cache?.OnUnload();
				current?.OnLoad();
			}
			cache = current;
		}

		private static void OnDisconnect()
		{
			if (current != null || cache != null)
			{
				Main.menuMode = 14;
			}
			current = null;
			cache = null;
		}

		private static void LoadSubworld(string path, bool cloud)
		{
			Main.worldName = current.DisplayName.Value;
			if (Main.netMode == 2)
			{
				Console.Title = Main.worldName;
			}
			WorldFileData data = new WorldFileData(path, cloud)
			{
				Name = Main.worldName,
				GameMode = Main.GameMode,
				CreationTime = DateTime.Now,
				Metadata = FileMetadata.FromCurrentSettings(FileType.World),
				WorldGeneratorVersion = Main.WorldGeneratorVersion,
				UniqueId = Guid.NewGuid()
			};
			data.SetSeed(main.SeedText);
			Main.ActiveWorldFileData = data;

			Main.maxTilesX = current.Width;
			Main.maxTilesY = current.Height;
			Main.spawnTileX = Main.maxTilesX / 2;
			Main.spawnTileY = Main.maxTilesY / 2;
			WorldGen.setWorldSize();
			WorldGen.clearWorld();
			Main.worldSurface = Main.maxTilesY * 0.3;
			Main.rockLayer = Main.maxTilesY * 0.5;
			GenVars.waterLine = Main.maxTilesY;
			Main.weatherCounter = 18000;
			Cloud.resetClouds();

			ReadCopiedMainWorldData();

			double weight = 0;
			for (int i = 0; i < current.Tasks.Count; i++)
			{
				weight += current.Tasks[i].Weight;
			}
			WorldGenerator.CurrentGenerationProgress = new GenerationProgress();
			WorldGenerator.CurrentGenerationProgress.TotalWeight = weight;

			WorldGenConfiguration config = current.Config;

			for (int i = 0; i < current.Tasks.Count; i++)
			{
				WorldGen._genRand = new UnifiedRandom(data.Seed);
				Main.rand = new UnifiedRandom(data.Seed);

				GenPass task = current.Tasks[i];

				WorldGenerator.CurrentGenerationProgress.Start(task.Weight);
				task.Apply(WorldGenerator.CurrentGenerationProgress, config?.GetPassConfiguration(task.Name));
				WorldGenerator.CurrentGenerationProgress.End();
			}
			WorldGenerator.CurrentGenerationProgress = null;

			Main.WorldFileMetadata = FileMetadata.FromCurrentSettings(FileType.World);

			if (current.ShouldSave)
			{
				WorldFile.SaveWorld(cloud);
			}

			SystemLoader.OnWorldLoad();
		}

		private static void TryLoadWorldFile(string path, bool cloud, int tries)
		{
			LoadWorldFile(path, cloud);
			if (WorldGen.loadFailed)
			{
				if (tries == 1)
				{
					if (FileUtilities.Exists(path + ".bak", cloud))
					{
						WorldGen.worldBackup = true;

						FileUtilities.Copy(path, path + ".bad", cloud);
						FileUtilities.Copy(path + ".bak", path, cloud);
						FileUtilities.Delete(path + ".bak", cloud);

						string tMLPath = Path.ChangeExtension(path, ".twld");
						if (FileUtilities.Exists(tMLPath, cloud))
						{
							FileUtilities.Copy(tMLPath, tMLPath + ".bad", cloud);
						}
						if (FileUtilities.Exists(tMLPath + ".bak", cloud))
						{
							FileUtilities.Copy(tMLPath + ".bak", tMLPath, cloud);
							FileUtilities.Delete(tMLPath + ".bak", cloud);
						}
					}
					else
					{
						WorldGen.worldBackup = false;
						return;
					}
				}
				else if (tries == 3)
				{
					FileUtilities.Copy(path, path + ".bak", cloud);
					FileUtilities.Copy(path + ".bad", path, cloud);
					FileUtilities.Delete(path + ".bad", cloud);

					string tMLPath = Path.ChangeExtension(path, ".twld");
					if (FileUtilities.Exists(tMLPath, cloud))
					{
						FileUtilities.Copy(tMLPath, tMLPath + ".bak", cloud);
					}
					if (FileUtilities.Exists(tMLPath + ".bad", cloud))
					{
						FileUtilities.Copy(tMLPath + ".bad", tMLPath, cloud);
						FileUtilities.Delete(tMLPath + ".bad", cloud);
					}

					return;
				}
				TryLoadWorldFile(path, cloud, tries++);
			}
		}

		private static void LoadWorldFile(string path, bool cloud)
		{
			bool flag = cloud && SocialAPI.Cloud != null;
			if (!FileUtilities.Exists(path, flag))
			{
				return;
			}

			if (current != null)
			{
				Main.ActiveWorldFileData = new WorldFileData(path, cloud);
			}

			try
			{
				int status;
				using (BinaryReader reader = new BinaryReader(new MemoryStream(FileUtilities.ReadAllBytes(path, flag))))
				{
					status = current != null ? current.ReadFile(reader) : WorldFile.LoadWorld_Version2(reader);
				}
				if (Main.netMode == 2)
				{
					Console.Title = Main.worldName;
				}
				SystemLoader.OnWorldLoad();
				typeof(ModLoader).Assembly.GetType("Terraria.ModLoader.IO.WorldIO").GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { path, flag });
				if (status != 0)
				{
					WorldGen.loadFailed = true;
					WorldGen.loadSuccess = false;
					return;
				}
				WorldGen.loadSuccess = true;
				WorldGen.loadFailed = false;

				if (current != null)
				{
					current.PostReadFile();
					cache?.ReadCopiedSubworldData();
					ReadCopiedMainWorldData();
				}
				else
				{
					PostLoadWorldFile();
					cache.ReadCopiedSubworldData();
					copiedData = null;
				}
			}
			catch
			{
				WorldGen.loadFailed = true;
				WorldGen.loadSuccess = false;
			}
		}

		internal static void PostLoadWorldFile()
		{
			GenVars.waterLine = Main.maxTilesY;
			Liquid.QuickWater(2);
			WorldGen.WaterCheck();
			Liquid.quickSettle = true;
			int updates = 0;
			int amount = Liquid.numLiquid + LiquidBuffer.numLiquidBuffer;
			float num = 0;
			while (Liquid.numLiquid > 0 && updates < 100000)
			{
				updates++;
				float progress = (amount - Liquid.numLiquid + LiquidBuffer.numLiquidBuffer) / (float)amount;
				if (Liquid.numLiquid + LiquidBuffer.numLiquidBuffer > amount)
				{
					amount = Liquid.numLiquid + LiquidBuffer.numLiquidBuffer;
				}
				if (progress > num)
				{
					num = progress;
				}
				else
				{
					progress = num;
				}
				Main.statusText = Lang.gen[27].Value + " " + (int)(progress * 100 / 2 + 50) + "%";
				Liquid.UpdateLiquid();
			}
			Liquid.quickSettle = false;
			Main.weatherCounter = WorldGen.genRand.Next(3600, 18000);
			Cloud.resetClouds();
			WorldGen.WaterCheck();
			NPC.setFireFlyChance();
			if (Main.slimeRainTime > 0)
			{
				Main.StartSlimeRain(false);
			}
			NPC.SetWorldSpecificMonstersByWorldID();
		}

		internal static void SynchoronizeChatMessages(byte messageAuthor, NetworkText text, Color color, int excludedPlayer, bool forward = false, ushort subworldID = ushort.MaxValue)
		{
			if (sendChatToOtherServers || forward)
			{
				int netId = ModContent.GetInstance<SubworldLibrary>().NetID;

				// Init stream and writer
				MemoryStream stream = new();
				BinaryWriter writer = new(stream);

				// Write the packet
				writer.Write((byte)0);
				writer.Write((ushort)0);
				writer.Write(MessageID.ModPacket);
				writer.Write((byte)netId);
				if (ModNet.NetModCount >= 256)
				{
					writer.Write((byte)(netId >> 8));
				}
				writer.Write((byte)SubLibMessageType.SynchronizeChatMessage);
				writer.Write(forward ? subworldID : (current != null && subworlds.Contains(current) ? (ushort)subworlds.IndexOf(current) : ushort.MaxValue));
				writer.Write(messageAuthor);
				writer.Write(Netplay.Clients[messageAuthor].Name);
				text.Serialize(writer);
				writer.WriteRGB(color);
				writer.Write(excludedPlayer == -1 ? ushort.MaxValue : (ushort)excludedPlayer);

				// Convert packet
				byte[] packet = new byte[stream.Length];
				Buffer.BlockCopy(stream.GetBuffer(), 0, packet, 0, (int)stream.Length);
				packet[1] = (byte)(stream.Length - 1);
				packet[2] = (byte)((stream.Length - 1) >> 8);

				// Send packet
				if (current == null)
				{
					foreach (int i in links.Keys)
					{
						if (i != subworldID)
						{
							links[i].Send(packet);
						}
					}
				}
				else
				{
					SubserverSocket.pipe.Write(packet);
				}
			}
		}
	}
}