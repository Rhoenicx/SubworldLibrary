using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Threading;
using Terraria;
using Terraria.Audio;
using Terraria.Graphics.Capture;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.Net;
using Terraria.Social;
using Terraria.Utilities;
using Terraria.WorldBuilding;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldSystem
	{
		private static void LoadIntoSubworld()
		{
			playerLocations = null;

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

					queue = new byte[131070];

					pipeIn = new NamedPipeClientStream(".", current.FileName + ".IN", PipeDirection.In);
					pipeIn.Connect();

					copiedData = TagIO.FromStream(pipeIn);
					LoadWorld();
					copiedData = null;

					// replicates Netplay.InitializeServer, no need to set ReadBuffer because it's not used
					for (int j = 0; j < 256; j++)
					{
						RemoteClient client = Netplay.Clients[j];
						client.Id = j;
						client.TileSections = new bool[Main.maxTilesX / 200 + 1, Main.maxTilesY / 150 + 1];
						client.Reset();
					}
					SubserverSocket.address = new TcpAddress(IPAddress.Any, 0);

					new Thread(SubserverReadLoop)
					{
						IsBackground = true
					}.Start();

					new Thread(CheckTimeout)
					{
						IsBackground = true
					}.Start();

					pipeOut = new NamedPipeClientStream(".", current.FileName + ".OUT", PipeDirection.Out);
					pipeOut.Connect();

					new Thread(SubserverSendLoop)
					{
						IsBackground = true
					}.Start();

					return;
				}
			}

			Netplay.Disconnect = true;
			Main.instance.Exit();
		}

		private static void SubserverSendLoop()
		{
			try
			{
				int sleep = 0;
				while (pipeOut.IsConnected && !Netplay.Disconnect)
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
					pipeOut.Write(data, 0, data.Length);
				}
			}
			finally
			{
				Netplay.Disconnect = true;
				pipeIn?.Close();
				pipeOut?.Close();
			}
		}

		private static void SubserverReadLoop()
		{
			try
			{
				while (pipeIn.IsConnected && !Netplay.Disconnect)
				{
					// client (1 byte)
					// length (2 bytes)
					// packet type (1 byte)
					byte[] packetInfo = new byte[4];
					if (pipeIn.Read(packetInfo) < 4)
					{
						break;
					}

					suppressAutoShutdown = 0;

					MessageBuffer buffer;
					if (packetInfo[0] == 255 && packetInfo[3] == 255)
					{
						// this packet actually came from the main server, put it in message buffer 256 for reading on the main thread
						buffer = NetMessage.buffer[256];
					}
					else
					{
						buffer = NetMessage.buffer[packetInfo[0]];
					}

					byte low = packetInfo[1];
					byte high = packetInfo[2];
					int length = (high << 8) | low;

					lock (buffer)
					{
						while (buffer.totalData + length > buffer.readBuffer.Length)
						{
							Monitor.Exit(buffer);
							Thread.Yield();
							Monitor.Enter(buffer);
						}

						buffer.readBuffer[buffer.totalData] = low;
						buffer.readBuffer[buffer.totalData + 1] = high;
						buffer.readBuffer[buffer.totalData + 2] = packetInfo[3];
						pipeIn.Read(buffer.readBuffer, buffer.totalData + 3, length - 3);

						if (buffer.whoAmI < 256 && !Netplay.Clients[buffer.whoAmI].IsActive)
						{
							RemoteClient client = Netplay.Clients[buffer.whoAmI];
							if (client.Socket == null)
							{
								client.Socket = new SubserverSocket(buffer.whoAmI);
							}
							client.State = 1;
							client.IsActive = true;
						}

						buffer.totalData += length;
						buffer.checkBytes = true;
					}
				}
			}
			finally
			{
				Netplay.Disconnect = true;
				pipeIn?.Close();
				pipeOut?.Close();
			}
		}

		private static void CheckTimeout()
		{
			Stopwatch timer = new Stopwatch();
			timer.Start();

			while (true)
			{
				Thread.Sleep(1000);
				if (suppressAutoShutdown == 0)
				{
					suppressAutoShutdown = -1;
					timer.Restart();
				}
				else if (timer.ElapsedMilliseconds > 30000)
				{
					ModContent.GetInstance<SubworldLibrary>().Logger.Info("No packets received in 30 seconds, closing");
					Netplay.Disconnect = true;
					Main.instance.Exit();
					return;
				}
			}
		}

		internal static void ExitWorldCallBack(object index)
		{
			// presumably avoids a race condition?
			int netMode = Main.netMode;

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

				CopyMainWorldData();
			}
			else
			{
				if (index != null)
				{
					Netplay.Connection.State = 3;
				}
				cache?.OnExit();
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
				Main.netMode = NetmodeID.SinglePlayer;
			}
			SystemLoader.OnWorldUnload();

			Main.fastForwardTimeToDawn = false;
			Main.fastForwardTimeToDusk = false;
			Main.UpdateTimeRate();

			if (index == null)
			{
				if (netMode != 1)
				{
					CacheWorldData();
				}
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

			for (int i = 0; i < 255; i++)
			{
				if (i != Main.myPlayer)
				{
					Main.player[i].active = false;
				}
			}

			if (netMode != 1)
			{
				LoadWorld();
			}
			// the subserver prompts packets from the client first now, so this is no longer needed
			/*else
			{
				NetMessage.SendData(1);
				Main.autoPass = true;
			}*/
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
				if (Main.netMode == NetmodeID.Server)
				{
					Netplay.Disconnect = true;
				}
				return;
			}

			WorldGen.gen = false;

			if (Main.netMode != NetmodeID.Server)
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

		private static void LoadSubworld(string path, bool cloud)
		{
			Main.worldName = current.DisplayName.Value;
			if (Main.netMode == NetmodeID.Server)
			{
				Console.Title = Main.worldName;
			}
			WorldFileData data = new WorldFileData(path, cloud)
			{
				Name = Main.worldName,
				CreationTime = DateTime.Now,
				Metadata = FileMetadata.FromCurrentSettings(FileType.World),
				WorldGeneratorVersion = Main.WorldGeneratorVersion,
				UniqueId = Guid.NewGuid()
			};
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
				if (Main.netMode == NetmodeID.Server)
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
					ReadCopiedMainWorldData();
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
	}
}
