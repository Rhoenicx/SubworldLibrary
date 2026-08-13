using Microsoft.Xna.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.NetModules;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.UI.Chat;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldLibrary
	{
		private static void SendBestiary(byte[] buffer, int start)
		{
			byte type = buffer[start + 5];
			short id = BitConverter.ToInt16(buffer, start + 6);

			MemoryStream stream = new MemoryStream(ModNet.NetModCount < 256 ? (type == 0 ? 10 : 8) : (type == 0 ? 11 : 9));
			using BinaryWriter writer = new BinaryWriter(stream);

			writer.Write((byte)255);
			if (ModNet.NetModCount < 256)
			{
				writer.Write(type == 0 ? (ushort)9 : (ushort)7);
				writer.Write((byte)255);
				writer.Write((byte)ModContent.GetInstance<SubworldLibrary>().NetID);
			}
			else
			{
				writer.Write(type == 0 ? (ushort)10 : (ushort)8);
				writer.Write((byte)255);
				writer.Write((ushort)ModContent.GetInstance<SubworldLibrary>().NetID);
			}
			writer.Write(type);
			writer.Write(id);
			if (type == 0)
			{
				writer.Write(BitConverter.ToUInt16(buffer, start + 8));
			}

			byte[] data = stream.GetBuffer();

			MessageBuffer serverBuffer = NetMessage.buffer[256];
			lock (serverBuffer)
			{
				while (serverBuffer.totalData + data.Length > serverBuffer.readBuffer.Length)
				{
					Monitor.Exit(serverBuffer);
					Thread.Yield();
					Monitor.Enter(serverBuffer);
				}
				Buffer.BlockCopy(data, 1, serverBuffer.readBuffer, serverBuffer.totalData, data.Length - 1);
				serverBuffer.totalData += data.Length - 1;
				serverBuffer.checkBytes = true;
			}

			for (int i = 0; i < SubworldSystem.subworlds.Count; i++)
			{
				SubworldSystem.subworlds[i].link?.Send(data);
			}
		}

		private static void SendText(MessageBuffer buffer, int start, int length)
		{
			// reader position is reset by vanilla
			buffer.reader.BaseStream.Position = start + 5;

			string command;
			string worldName;

			ChatMessage message = ChatMessage.Deserialize(buffer.reader);
			buffer.reader.BaseStream.Position = start + 5;

			int sentFrom = SubworldSystem.playerLocations[buffer.whoAmI];
			if (sentFrom >= 0)
			{
				worldName = SubworldSystem.subworlds[sentFrom].DisplayName.Value;

				command = buffer.reader.ReadString();

				// only read commands where they were sent from
				if (command != "Say")
				{
					SubworldSystem.subworlds[sentFrom].link?.Send(buffer.readBuffer, start, length, (byte)buffer.whoAmI);
					return;
				}
			}
			else
			{
				worldName = Main.worldName;

				command = buffer.reader.ReadString();

				// only read commands where they were sent from
				if (command != "Say")
				{
					ChatManager.Commands.ProcessIncomingMessage(message, buffer.whoAmI);
					return;
				}
			}

			string prepend =
				"[" +
				worldName +
				"] <" +
				Main.player[buffer.whoAmI].name +
				"> " +
				buffer.reader.ReadString();

			int len = Encoding.UTF8.GetByteCount(command) + Encoding.UTF8.GetByteCount(prepend);

			MemoryStream stream = new MemoryStream(len + (ModNet.NetModCount < 256 ? 9 : 10));
			using BinaryWriter writer = new BinaryWriter(stream);

			writer.Write((byte)255);
			if (ModNet.NetModCount < 256)
			{
				writer.Write((ushort)(len + 8));
				writer.Write((byte)255);
				writer.Write((byte)ModContent.GetInstance<SubworldLibrary>().NetID);
			}
			else
			{
				writer.Write((ushort)(len + 9));
				writer.Write((byte)255);
				writer.Write((ushort)ModContent.GetInstance<SubworldLibrary>().NetID);
			}
			writer.Write((byte)3);
			writer.Write((byte)255);
			writer.Write(command);
			writer.Write(prepend);

			// the stream's length is exact, so GetBuffer can be used instead of ToArray
			byte[] data = stream.GetBuffer();

			if (sentFrom < 0)
			{
				ChatManager.Commands.ProcessIncomingMessage(message, buffer.whoAmI);

				for (int i = 0; i < SubworldSystem.subworlds.Count; i++)
				{
					SubworldSystem.subworlds[i].link?.Send(data);
				}
				return;
			}

			// other clients may not know the name of this client, so pretend this is a server message
			message.Text = prepend;
			ChatManager.Commands.ProcessIncomingMessage(message, 255);

			for (int i = 0; i < SubworldSystem.subworlds.Count; i++)
			{
				if (i != sentFrom)
				{
					SubworldSystem.subworlds[i].link?.Send(data);
					continue;
				}

				SubworldSystem.subworlds[i].link?.Send(buffer.readBuffer, start, length, (byte)buffer.whoAmI);
			}
		}

		private static bool DenyRead(MessageBuffer buffer, int start, int length)
		{
			byte[] buf = buffer.readBuffer;

			// always read sublib packets on the main server, MovePlayerToSubserver and SyncDisconnect will send them to subservers directly
			if (buf[start + 2] == 250 && (ModNet.NetModCount < 256 ? buf[start + 3] : BitConverter.ToUInt16(buf, start + 3)) == ModContent.GetInstance<SubworldLibrary>().NetID)
			{
				return false;
			}

			if (buf[start + 2] == 82)
			{
				ushort packetId = BitConverter.ToUInt16(buf, start + 3);

				// propagate chat messages
				if (packetId == NetManager.Instance.GetId<NetTextModule>())
				{
					Netplay.Clients[buffer.whoAmI].TimeOutTimer = 0;

					SendText(buffer, start, length);

					// the packet was read, don't read it again
					return true;
				}
			}

			int id = SubworldSystem.playerLocations[buffer.whoAmI];
			if (id < 0)
			{
				return false;
			}

			Netplay.Clients[buffer.whoAmI].TimeOutTimer = 0;

			SubworldSystem.subworlds[id].link?.Send(buf, start, length, (byte)buffer.whoAmI);

			return true;
		}

		private static bool DenySend(ISocket socket, byte[] data, int start, int length, ref object state)
		{
			// always send sublib packets
			if (data[start + 2] == 250 && (ModNet.NetModCount < 256 ? data[start + 3] : BitConverter.ToUInt16(data, start + 3)) == ModContent.GetInstance<SubworldLibrary>().NetID)
			{
				return false;
			}

			if (Thread.CurrentThread.Name == "Subserver Packets")
			{
				if (data[start + 2] == 82)
				{
					ushort packetId = BitConverter.ToUInt16(data, start + 3);

					// propagate bestiary updates
					if (packetId == NetManager.Instance.GetId<NetBestiaryModule>())
					{
						SendBestiary(data, start);

						// the main server should always send this packet
						return false;
					}
				}
				return false;
			}

			return SubworldSystem.deniedSockets.Contains(socket);
		}

		private static void Sleep(Stopwatch stopwatch, double delta, ref double target)
		{
			double now = stopwatch.ElapsedMilliseconds;
			double remaining = target - now;
			target += delta;
			if (target < now)
			{
				target = now + delta;
			}
			if (remaining <= 0)
			{
				Thread.Sleep(0);
				return;
			}
			Thread.Sleep((int)remaining);
		}

		private static void CheckClients()
		{
			bool active = false;
			for (int i = 0; i < 256; i++)
			{
				RemoteClient client = Netplay.Clients[i];

				// the other checks vanilla does aren't needed
				if (client.PendingTerminationApproved)
				{
					client.Reset();

					NetMessage.SendData(MessageID.PlayerActive, -1, i, null, i, 0);
					ChatHelper.BroadcastChatMessage(NetworkText.FromKey(Lang.mp[20].Key, client.Name), new Color(255, 240, 20), i);
					Player.Hooks.PlayerDisconnect(i);

					continue;
				}

				if (client.State > 0)
				{
					active = true;
				}
			}

			if (active)
			{
				Netplay.HasClients = true;
				return;
			}

			if (Netplay.HasClients)
			{
				Netplay.HasClients = false;
				Netplay.Disconnect = true;
			}
		}
	}
}
