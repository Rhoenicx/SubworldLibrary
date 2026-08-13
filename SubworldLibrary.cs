using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI.Chat;
using Terraria.WorldBuilding;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldLibrary : Mod
	{
		/// <inheritdoc />
		public override void Load()
		{
			if (ModLoader.TryGetMod("SubworldLibrary", out Mod originalSubworldLibrary) && originalSubworldLibrary != this)
			{
				throw new InvalidOperationException(
					"SubworldLibraryCommunityFork cannot be enabled alongside the original SubworldLibrary mod. " +
					"Disable SubworldLibrary, keep only SubworldLibraryCommunityFork enabled, and reload your mods.");
			}

			RegisterWorldHooks();
			RegisterSaveHooks();

			if (Main.dedServ)
			{
				RegisterServerHooks();

				// The original main-server path returns here after installing its relay hooks.
				if (!Program.LaunchParameters.ContainsKey("-subworld"))
				{
					return;
				}
			}
			else
			{
				RegisterClientHooks();
			}

			RegisterWorldDeletionHook();
		}

		/// <inheritdoc />
		public override object Call(params object[] args)
		{
			try
			{
				string message = args[0] as string;
				switch (message)
				{
					case "Register":
						Mod mod = args[1] as Mod;

						int i = 6;
						CrossModSubworld subworld = new CrossModSubworld(
							args[2] as string,
							Convert.ToInt32(args[3]),
							Convert.ToInt32(args[4]),
							args[5] as List<GenPass>,
							args.Length > i ? args[i] as WorldGenConfiguration : null,
							args.Length > ++i ? Convert.ToInt32(args[i]) : -1,
							args.Length > ++i ? Convert.ToBoolean(args[i]) : false,
							args.Length > ++i ? Convert.ToBoolean(args[i]) : false,
							args.Length > ++i ? Convert.ToBoolean(args[i]) : false,
							args.Length > ++i ? Convert.ToBoolean(args[i]) : false,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action : null,
							args.Length > ++i ? args[i] as Action<GameTime> : null,
							args.Length > ++i ? args[i] as Func<bool> : null,
							args.Length > ++i ? args[i] as Func<Entity, float> : null);

						mod.AddContent(subworld);

						return subworld.Name;
					case "Enter":
						return SubworldSystem.Enter(args[1] as string);
					case "Exit":
						SubworldSystem.Exit();
						return true;
					case "Current":
						return SubworldSystem.Current.FullName;
					case "IsActive":
						return SubworldSystem.IsActive(args[1] as string);
					case "AnyActive":
						return SubworldSystem.AnyActive(args[1] as Mod);
				}
			}
			catch (Exception e)
			{
				Logger.Error("Call error: " + e.StackTrace + e.Message);
			}
			return false;
		}

		// HOW SUBWORLD LIBRARY HANDLES PACKETS
		// when a client is in a subworld, all packets sent to and from them are relayed to the subserver belonging to that subworld (see DenyRead)
		// sublib packets are never relayed to subservers automatically, but may be sent to them by sublib directly
		// subservers send packets to the main server via SubserverSocket
		/// <inheritdoc />
		public override void HandlePacket(BinaryReader reader, int whoAmI)
		{
			if (Main.netMode == NetmodeID.Server)
			{
				// packet came from a sub/server
				if (whoAmI == 256)
				{
					switch (reader.ReadByte())
					{
						case 0: // mirror NetBestiaryModule
							Main.BestiaryTracker.Kills.SetKillCountDirectly(ContentSamples.NpcsByNetId[reader.ReadInt16()].GetBestiaryCreditId(), reader.ReadUInt16());
							return;

						case 1: // mirror NetBestiaryModule
							Main.BestiaryTracker.Sights.SetWasSeenDirectly(ContentSamples.NpcsByNetId[reader.ReadInt16()].GetBestiaryCreditId());
							return;

						case 2: // mirror NetBestiaryModule
							Main.BestiaryTracker.Chats.SetWasChatWithDirectly(ContentSamples.NpcsByNetId[reader.ReadInt16()].GetBestiaryCreditId());
							return;

						case 3: // mirror NetTextModule
							int sender = reader.ReadByte(); // this may be set to 255 by the main server, since the actual sender may be unknown on the subserver
							ChatManager.Commands.ProcessIncomingMessage(ChatMessage.Deserialize(reader), sender);
							return;

						default:
							return;
					}
				}

				if (SubworldSystem.current != null)
				{
					int mod = ModNet.NetModCount < 256 ? reader.ReadByte() : reader.ReadUInt16();
					if (mod != NetID)
					{
						ModNet.GetMod(mod).HandlePacket(reader, 256);
						return;
					}

					Netplay.Clients[whoAmI].PendingTerminationApproved = true;
					return;
				}

				// always read an id in case a request is sent multiple times
				ushort id = reader.ReadUInt16();
				if (SubworldSystem.pendingMoves[whoAmI] >= 0)
				{
					SubworldSystem.FinishMove(whoAmI);
				}
				else if (!SubworldSystem.noReturn && Netplay.Clients[whoAmI].State == 10)
				{
					// A client that has not finished joining cannot legitimately ask to move - a real
					// request is always sent from a fully joined client. Without the state check, a
					// duplicate or late ack arriving after FinishMove starts a whole new move for a
					// client that is still replaying the join handshake, deactivating it again partway
					// through and leaving it desynced for everyone.
					SubworldSystem.MovePlayerToSubserver(whoAmI, id);
				}
			}
			else
			{
				ushort id = reader.ReadUInt16();

				// might be better to set this at the end of the update cycle?
				SubworldSystem.current = id < ushort.MaxValue ? SubworldSystem.subworlds[id] : null;

				Main.menuMode = 10;
				Main.gameMenu = true;

				ModPacket packet = GetPacket();
				packet.Write(id);
				packet.Send();

				Task.Factory.StartNew(SubworldSystem.ExitWorldCallBack, id < ushort.MaxValue ? id : -1);
			}
		}
	}
}
