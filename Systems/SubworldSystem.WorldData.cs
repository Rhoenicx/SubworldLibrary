using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Bestiary;
using Terraria.GameContent.Creative;
using Terraria.GameContent.Events;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.Social;
using Terraria.Utilities;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldSystem
	{
		/// <summary>
		/// Can only be called in <see cref="Subworld.CopyMainWorldData"/> or <see cref="Subworld.OnExit"/>!
		/// <br/>Stores data to be transferred between worlds under the specified key, if that key is not already in use.
		/// <br/>Naming the key after the variable pointing to the data is highly recommended to avoid redundant copying. This can be done automatically with nameof().
		/// <br/>Keys starting with '!' cannot be changed once copied, and don't need to be copied back to the main world.
		/// <code>SubworldSystem.CopyWorldData(nameof(DownedSystem.downedBoss), DownedSystem.downedBoss);</code>
		/// </summary>
		public static void CopyWorldData(string key, object data)
		{
			if (data != null && (key[0] != '!' || !copiedData.ContainsKey(key)))
			{
				copiedData[key] = data;
			}
		}

		/// <summary>
		/// Can only be called in <see cref="Subworld.ReadCopiedMainWorldData"/> or <see cref="Subworld.ReadCopiedSubworldData"/>!
		/// <br/>Reads data copied from another world stored under the specified key.
		/// <br/>Keys starting with '!' don't need to be copied back to the main world. (SubworldSystem.Current == null)
		/// <code>DownedSystem.downedBoss = SubworldSystem.ReadCopiedWorldData&lt;bool&gt;(nameof(DownedSystem.downedBoss));</code>
		/// </summary>
		public static T ReadCopiedWorldData<T>(string key) => copiedData.Get<T>(key);

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
			copiedData["!mainId"] = (Main.netMode != NetmodeID.Server || current != null) ? main.UniqueId.ToByteArray() : Main.ActiveWorldFileData.UniqueId.ToByteArray();
			copiedData["!seed"] = Main.ActiveWorldFileData.SeedText;
			copiedData["!gameMode"] = Main.ActiveWorldFileData.GameMode;
			copiedData["!hardMode"] = Main.hardMode;

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
			if (current != null)
			{
				main.UniqueId = new Guid(copiedData.Get<byte[]>("!mainId"));
				Main.ActiveWorldFileData.SetSeed(copiedData.Get<string>("!seed"));
				Main.GameMode = copiedData.Get<int>("!gameMode");
				Main.hardMode = copiedData.Get<bool>("!hardMode");
			}

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

		private static void CacheWorldData()
		{
			SubworldSystem system = ModContent.GetInstance<SubworldSystem>();

			string path = Path.ChangeExtension(main.Path, ".twld");
			bool isCloudSave = main.IsCloudSave;

			if (FileUtilities.Exists(path, isCloudSave))
			{
				FileUtilities.Copy(path, path + ".bak", isCloudSave);
			}

			byte[] file = FileUtilities.ReadAllBytes(path, isCloudSave);
			TagCompound tagCompound = TagIO.FromStream(new MemoryStream(file));

			IList<TagCompound> list = tagCompound.GetList<TagCompound>("modData");

			TagCompound data = new TagCompound
			{
				["mod"] = system.Mod.Name,
				["name"] = system.Name,
				["data"] = new TagCompound
				{
					["mod"] = cache.Mod.Name,
					["name"] = cache.Name,
					["data"] = copiedData
				}
			};

			bool addTag = true;
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i].GetString("mod") == system.Mod.Name && list[i].GetString("name") == system.Name)
				{
					list[i] = data;
					addTag = false;
					break;
				}
			}
			if (addTag)
			{
				list.Add(data);
			}

			using Stream stream = isCloudSave ? new MemoryStream() : new FileStream(path, FileMode.Create);
			TagIO.ToStream(tagCompound, stream);
			if (isCloudSave && SocialAPI.Cloud != null)
			{
				SocialAPI.Cloud.Write(path, ((MemoryStream)stream).ToArray());
			}

			copiedData = null;
		}

		private static void CheckBytes()
		{
			MessageBuffer buffer = NetMessage.buffer[256];
			if (!buffer.checkBytes)
			{
				return;
			}

			lock (buffer)
			{
				int pos = 0;
				int len = buffer.totalData;

				while (len >= 2)
				{
					int packetLen = BitConverter.ToUInt16(buffer.readBuffer, pos);

					// bad packet, skip all remaining data
					if (packetLen < 2)
					{
						len = 0;
						buffer.totalData = 0;
						break;
					}

					if (len < packetLen)
					{
						break;
					}

					BinaryReader reader = buffer.reader;
					if (reader == null)
					{
						buffer.ResetReader();
						reader = buffer.reader;
					}

					long streamPos = reader.BaseStream.Position;
					reader.BaseStream.Position = pos + 3;

					//ModContent.GetInstance<SubworldLibrary>().Logger.Info(Convert.ToHexString(NetMessage.buffer[256].readBuffer, pos, len));

					ModNet.GetMod(ModNet.NetModCount < 256 ? reader.ReadByte() : reader.ReadUInt16()).HandlePacket(reader, 256);

					reader.BaseStream.Position = streamPos + packetLen;
					len -= packetLen;
					pos += packetLen;
				}

				if (len != buffer.totalData)
				{
					for (int i = 0; i < len; i++)
					{
						buffer.readBuffer[i] = buffer.readBuffer[i + pos];
					}
					buffer.totalData = len;
				}

				buffer.checkBytes = false;
			}
		}
	}
}
