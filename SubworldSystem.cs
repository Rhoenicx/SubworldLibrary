using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.Net;
using Terraria.Net.Sockets;

namespace SubworldLibraryCommunityFork
{
	internal class SubserverSocket : ISocket
	{
		private int id;

		internal static RemoteAddress address;

		public SubserverSocket(int id)
		{
			this.id = id;
		}

		void ISocket.AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state) { }

		void ISocket.AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state)
		{
			lock (SubworldSystem.queue)
			{
				SubworldSystem.queue[SubworldSystem.totalData] = (byte)id;
				Buffer.BlockCopy(data, offset, SubworldSystem.queue, SubworldSystem.totalData + 1, size);
				SubworldSystem.totalData += size + 1;
			}
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

	/// <summary>
	/// Coordinates subworld registration, transitions, persistence, and multiplayer routing.
	/// </summary>
	public partial class SubworldSystem : ModSystem
	{
		internal static List<Subworld> subworlds;

		internal static Subworld current;
		internal static Subworld cache;
		private static WorldFileData main;
		private static int suppressAutoShutdown;

		internal static TagCompound copiedData;
		internal static int[] playerLocations;
		internal static int[] pendingMoves;
		internal static HashSet<ISocket> deniedSockets;

		// players who have been dropped back to State 1 by FinishMove and are replaying the vanilla
		// join handshake against the main server. See UpdateRejoiningPlayers.
		internal static bool[] rejoining;
		// set while a player is rejoining if any other player was mid-transition at the same time,
		// which is the window where join syncs get dropped in both directions.
		internal static bool[] rejoinNeedsResync;

		internal static NamedPipeClientStream pipeIn;
		internal static NamedPipeClientStream pipeOut;
		internal static byte[] queue;
		internal static int totalData;

		/// <inheritdoc />
		public override void OnModLoad()
		{
			subworlds = new List<Subworld>();

			playerLocations = new int[256];
			Array.Fill(playerLocations, -1);

			pendingMoves = new int[256];
			Array.Fill(pendingMoves, -1);

			rejoining = new bool[256];
			rejoinNeedsResync = new bool[256];

			deniedSockets = new HashSet<ISocket>();

			WorldFile.OnWorldLoad += ReadCachedData;
			Player.Hooks.OnEnterWorld += OnEnterWorld;
			Netplay.OnDisconnect += OnDisconnect;

			suppressAutoShutdown = -1;
		}

		/// <inheritdoc />
		public override void Unload()
		{
			WorldFile.OnWorldLoad -= ReadCachedData;
			Player.Hooks.OnEnterWorld -= OnEnterWorld;
			Netplay.OnDisconnect -= OnDisconnect;
		}

		private static void ReadCachedData()
		{
			if (copiedData == null || current != null || cache != null)
			{
				return;
			}

			ReadCopiedMainWorldData();
		}

		private static void OnEnterWorld(Player player)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
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

		/// <inheritdoc />
		public override void SaveWorldData(TagCompound tag)
		{
			// cached world data is saved in ExitWorldCallBack
		}

		/// <inheritdoc />
		public override void LoadWorldData(TagCompound tag)
		{
			if (!tag.TryGet("mod", out string mod) || !tag.TryGet("name", out string name) || !tag.TryGet("data", out TagCompound data))
			{
				return;
			}

			if (!ModContent.TryFind(mod, name, out Subworld subworld))
			{
				return;
			}

			copiedData = data;
		}

		private static bool _noReturn;

		/// <summary>
		/// Hides the Return button.
		/// <br/>Its value is reset before <see cref="Subworld.OnEnter"/> is called, and after <see cref="Subworld.OnExit"/> is called.
		/// </summary>
		public static bool noReturn
		{
			get => _noReturn;
			set => _noReturn = value;
		}
		private static bool _hideUnderworld;

		/// <summary>
		/// Hides the Underworld background.
		/// <br/>Its value is reset before <see cref="Subworld.OnEnter"/> is called, and after <see cref="Subworld.OnExit"/> is called.
		/// </summary>
		public static bool hideUnderworld
		{
			get => _hideUnderworld;
			set => _hideUnderworld = value;
		}

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
		public static string CurrentPath => Path.Combine(main.IsCloudSave ? Main.CloudWorldPath : Main.WorldPath, main.UniqueId.ToString(), current.FileName + ".wld");

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
			if (Main.netMode == NetmodeID.Server)
			{
				return;
			}

			if (index == int.MinValue)
			{
				current = null;
				Main.menuMode = 10;
				Main.gameMenu = true;

				Task.Factory.StartNew(ExitWorldCallBack, null);
				return;
			}

			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				if (current == null && index >= 0)
				{
					main = Main.ActiveWorldFileData;
				}

				current = index < 0 ? null : subworlds[index];
				Main.menuMode = 10;
				Main.gameMenu = true;

				Task.Factory.StartNew(ExitWorldCallBack, index);
				return;
			}

			ModPacket packet = ModContent.GetInstance<SubworldLibrary>().GetPacket();
			packet.Write(index < 0 ? ushort.MaxValue : (ushort)index);
			packet.Send();
		}

		/// <summary>
		/// Tries to send the specified player to the subworld with the specified ID.
		/// </summary>
		public static void MovePlayerToSubworld(string id, int player)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || (Main.netMode == NetmodeID.Server && current != null))
			{
				return;
			}

			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].FullName == id)
				{
					if (Main.netMode == NetmodeID.SinglePlayer)
					{
						BeginEntering(i);
						return;
					}

					MovePlayerToSubserver(player, (ushort)i);
					return;
				}
			}
		}

		/// <summary>
		/// Sends the specified player to the specified subworld.
		/// </summary>
		public static void MovePlayerToSubworld<T>(int player) where T : Subworld
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || (Main.netMode == NetmodeID.Server && current != null))
			{
				return;
			}

			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].GetType() == typeof(T))
				{
					if (Main.netMode == NetmodeID.SinglePlayer)
					{
						BeginEntering(i);
						return;
					}

					MovePlayerToSubserver(player, (ushort)i);
					return;
				}
			}
		}

		/// <summary>
		/// Sends the specified player to the main world.
		/// </summary>
		public static void MovePlayerToMainWorld(int player)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || (Main.netMode == NetmodeID.Server && current != null))
			{
				return;
			}

			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				BeginEntering(-1);
				return;
			}

			MovePlayerToSubserver(player, ushort.MaxValue);
		}

	}
}
