using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.NetModules;
using Terraria.Graphics.Light;
using Terraria.ID;
using Terraria.IO;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.Utilities;
using static Mono.Cecil.Cil.OpCodes;

namespace SubworldLibrary
{
	public class SubworldLibrary : Mod
	{
		private static ILHook tcpSocketHook;
		private static ILHook socialSocketHook;
		private static ILHook handleModPacketHook;
		internal delegate void CacheMessage(string message, Color color);
		internal static CacheMessage cacheMessage;

		public override void Load()
		{
			FieldInfo current = typeof(SubworldSystem).GetField("current", BindingFlags.NonPublic | BindingFlags.Static);
			FieldInfo cache = typeof(SubworldSystem).GetField("cache", BindingFlags.NonPublic | BindingFlags.Static);
			FieldInfo hideUnderworld = typeof(SubworldSystem).GetField("hideUnderworld");
			MethodInfo normalUpdates = typeof(Subworld).GetMethod("get_NormalUpdates");
			MethodInfo shouldSave = typeof(Subworld).GetMethod("get_ShouldSave");
			MethodInfo cacheMessageMethod = typeof(ChatHelper).GetMethod("CacheMessage", BindingFlags.Static | BindingFlags.NonPublic);
			cacheMessage = (CacheMessage)Delegate.CreateDelegate(typeof(CacheMessage), cacheMessageMethod);

			if (Main.dedServ)
			{
				On_ChatHelper.BroadcastChatMessageAs += (orig, messageAuthor, text, color, excludedPlayer) =>
				{
					if (SubworldSystem.sendBroadcasts)
					{
						int index = SubworldSystem.CurrentIndex;
						SubworldSystem.BroadcastBetweenServers(
							messageAuthor,
							text,
							color,
							index == -1 ? ushort.MaxValue : (ushort)index,
							Main.worldName);
					}

					orig(messageAuthor, text, color, excludedPlayer);
				};

				IL_Main.DedServ_PostModLoad += il =>
				{
					ConstructorInfo gameTime = typeof(GameTime).GetConstructor(Type.EmptyTypes);
					MethodInfo update = typeof(Main).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
					FieldInfo saveTime = typeof(Main).GetField("saveTime", BindingFlags.NonPublic | BindingFlags.Static);

					var c = new ILCursor(il);
					ILLabel skip = null;
					if (!c.TryGotoNext(i => i.MatchBr(out skip)))
					{
						Logger.Error("Failed to apply IL patch into: Main.DedServ_PostModLoad");
						return;
					}

					c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("LoadIntoSubworld", BindingFlags.NonPublic | BindingFlags.Static));
					c.Emit(Brfalse, skip);

					c.Emit(Ldarg_0);
					c.Emit(Newobj, gameTime);
					c.Emit(Callvirt, update);

					c.Emit(Newobj, typeof(Stopwatch).GetConstructor(Type.EmptyTypes));
					c.Emit(Stloc_1);
					c.Emit(Ldloc_1);
					c.Emit(Callvirt, typeof(Stopwatch).GetMethod("Start"));

					c.Emit(Ldc_I4_0);
					c.Emit(Stsfld, typeof(Main).GetField("gameMenu"));

					c.Emit(Ldc_R8, 16.666666666666668);
					c.Emit(Stloc_2);
					c.Emit(Ldloc_2);
					c.Emit(Stloc_3);

					var loopStart = c.DefineLabel();
					c.Emit(Br, loopStart);

					var loop = c.DefineLabel();
					c.MarkLabel(loop);

					c.Emit(OpCodes.Call, typeof(Main).Assembly.GetType("Terraria.ModLoader.Engine.ServerHangWatchdog").GetMethod("Checkin", BindingFlags.NonPublic | BindingFlags.Static));

					c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("CheckClients", BindingFlags.NonPublic | BindingFlags.Static));

					c.Emit(Ldsfld, typeof(Netplay).GetField("HasClients"));
					var label = c.DefineLabel();
					c.Emit(Brfalse, label);

					c.Emit(Ldarg_0);
					c.Emit(Newobj, gameTime);
					c.Emit(Callvirt, update);
					var label2 = c.DefineLabel();
					c.Emit(Br, label2);

					c.MarkLabel(label);

					c.Emit(Ldsfld, saveTime);
					c.Emit(Callvirt, typeof(Stopwatch).GetMethod("get_IsRunning"));
					c.Emit(Brfalse, label2);

					c.Emit(Ldsfld, saveTime);
					c.Emit(Callvirt, typeof(Stopwatch).GetMethod("Stop"));
					c.Emit(Br, label2);

					c.MarkLabel(label2);

					c.Emit(Ldloc_1);
					c.Emit(Ldloc_2);
					c.Emit(Ldloca, 3);
					c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("Sleep", BindingFlags.NonPublic | BindingFlags.Static));

					c.MarkLabel(loopStart);

					c.Emit(Ldsfld, typeof(Netplay).GetField("Disconnect"));
					c.Emit(Brfalse, loop);

					c.Emit(Ldsfld, current);
					c.Emit(Callvirt, shouldSave);
					label = c.DefineLabel();
					c.Emit(Brfalse, label);
					c.Emit(OpCodes.Call, typeof(WorldFile).GetMethod("SaveWorld", Type.EmptyTypes));
					c.MarkLabel(label);

					c.Emit(OpCodes.Call, typeof(SystemLoader).GetMethod("OnWorldUnload"));

					c.Emit(Ret);
				};

				IL_Netplay.UpdateServerInMainThread += il =>
				{
					ILCursor c = new(il) { Index = il.Body.Instructions.Count - 1 };
					c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("ProcessServerMessageBuffer", BindingFlags.NonPublic | BindingFlags.Static));
				};

				IL_NetMessage.CheckBytes += il =>
				{
					ILCursor c;
					if (!(c = new(il)).TryGotoNext(
						x => x.MatchLdloc(3),
						x => x.MatchLdcI4(0),
						x => x.MatchBle(out _),
						x => x.MatchLdsfld(typeof(ModNet), "DetailedLogging"),
						x => x.MatchBrfalse(out _)))
					{
						Logger.Error("Failed to apply IL patch into: NetMessage.CheckBytes - PrepareMessageBuffer");
						return;
					}

					c.Emit(Ldsfld, typeof(NetMessage).GetField("buffer"));
					c.Emit(Ldarg_0);
					c.Emit(Ldelem_Ref);
					c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("PrepareMessageBuffer", BindingFlags.NonPublic | BindingFlags.Static));
				};

				handleModPacketHook = new ILHook(typeof(ModNet).GetMethod("HandleModPacket", BindingFlags.Static | BindingFlags.NonPublic), HandleModPacket);

				void HandleModPacket(ILContext il)
				{
					ILCursor c = new(il);
					ILLabel label = c.DefineLabel();

					if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdsfld(typeof(ModNet), "ReadUnderflowBypass"), i => i.MatchBrtrue(out label)))
					{
						Logger.Error("Failed to apply IL patch into: ModNet.HandleModPacket");
						return;
					}

					c.Emit(Ldsfld, typeof(Netplay).GetField("Clients", BindingFlags.Static | BindingFlags.Public));
					c.Emit(Ldarg, 1);
					c.Emit(Ldelem_Ref);
					c.Emit(Ldfld, typeof(RemoteClient).GetField("State", BindingFlags.Instance | BindingFlags.Public));
					c.Emit(Ldc_I4, 0);
					c.Emit(Ble, label);
				}

				if (!Program.LaunchParameters.ContainsKey("-subworld"))
				{
					IL_NetMessage.DoesPlayerSlotCountAsAHost += il =>
					{
						ILCursor c = new(il);

						if (!c.TryGotoNext(
							i => i.MatchLdsfld<Netplay>("Clients"),
							i => i.MatchLdarg0(),
							i => i.MatchLdelemRef(),
							i => i.MatchLdfld<RemoteClient>("State"),
							i => i.MatchLdcI4(10),
							i => i.MatchBneUn(out ILLabel _)))
						{
							Logger.Error("Failed to apply IL patch into: NetMessage.DoesPlayerSlotCountAsAHost");
							return;
						}

						ILLabel label = il.DefineLabel();

						c.Index += 6;
						c.MarkLabel(label);
						c.Index -= 6;

						c.Emit(Ldarg_0);
						c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("CheckLocalHost", BindingFlags.NonPublic | BindingFlags.Static));
						c.Emit(Brtrue, label);
					};

					IL_NetMessage.CheckBytes += il =>
					{
						ILCursor c, cc;
						if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(BitConverter), "ToUInt16"))
						|| !c.Instrs[c.Index].MatchStloc(out int index)
						|| !c.TryGotoNext(MoveType.After, i => i.MatchCallvirt(typeof(Stream), "get_Position"), i => i.MatchStloc(out _))
						|| !(cc = c.Clone()).TryGotoNext(i => i.MatchLdsfld(typeof(NetMessage), "buffer"), i => i.MatchLdarg(0), i => i.MatchLdelemRef(), i => i.MatchLdfld(typeof(MessageBuffer), "reader")))
						{
							Logger.Error("Failed to apply IL patch into: NetMessage.CheckBytes - DenyRead");
							return;
						}

						c.Emit(Ldsfld, typeof(NetMessage).GetField("buffer"));
						c.Emit(Ldarg_0);
						c.Emit(Ldelem_Ref);
						c.Emit(Ldloc_2);
						c.Emit(Ldloc, index);
						c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("DenyRead", BindingFlags.NonPublic | BindingFlags.Static));

						var label = c.DefineLabel();
						c.Emit(Brtrue, label);

						cc.MarkLabel(label);
					};

					socialSocketHook = new ILHook(typeof(SocialSocket).GetMethod("Terraria.Net.Sockets.ISocket.AsyncSend", BindingFlags.NonPublic | BindingFlags.Instance), AsyncSend);
					tcpSocketHook = new ILHook(typeof(TcpSocket).GetMethod("Terraria.Net.Sockets.ISocket.AsyncSend", BindingFlags.NonPublic | BindingFlags.Instance), AsyncSend);

					void AsyncSend(ILContext il)
					{
						var c = new ILCursor(il);
						if (!c.TryGotoNext(MoveType.After, i => i.MatchRet()))
						{
							Logger.Error("Failed to apply IL patch into: SocialSocket/TcpSocket.AsyncSend");
							return;
						}
						c.MoveAfterLabels();

						c.Emit(Ldarg_0);
						c.Emit(Ldarg_1);
						c.Emit(Ldarg_2);
						c.Emit(Ldarg_3);
						c.Emit(Ldarga, 5);
						c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("DenySend", BindingFlags.NonPublic | BindingFlags.Static));
						var label = c.DefineLabel();
						c.Emit(Brfalse, label);
						c.Emit(Ret);
						c.MarkLabel(label);
					}

					IL_Netplay.UpdateConnectedClients += il =>
					{
						var c = new ILCursor(il);
						if (!c.TryGotoNext(MoveType.After, i => i.MatchCallvirt(typeof(RemoteClient), "Reset"))
						|| !c.Instrs[c.Index].MatchLdloc(out int index))
						{
							Logger.Error("Failed to apply IL patch into: Netplay.UpdateConnectedClients");
							return;
						}

						c.Emit(Ldloc, index);
						c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("SyncDisconnect", BindingFlags.NonPublic | BindingFlags.Static));
					};
				}
				else
				{
					IL_NetMessage.CheckBytes += il =>
					{
						ILCursor c, cc;
						if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(BitConverter), "ToUInt16"))
						|| !c.Instrs[c.Index].MatchStloc(out int index)
						|| !c.TryGotoNext(MoveType.After, i => i.MatchCallvirt(typeof(Stream), "get_Position"), i => i.MatchStloc(out _))
						|| !(cc = c.Clone()).TryGotoNext(i => i.MatchLdsfld(typeof(NetMessage), "buffer"), i => i.MatchLdarg(0), i => i.MatchLdelemRef(), i => i.MatchLdfld(typeof(MessageBuffer), "reader")))
						{
							Logger.Error("Failed to apply IL patch into: NetMessage.CheckBytes - DenyReadSubServer");
							return;
						}

						c.Emit(Ldsfld, typeof(NetMessage).GetField("buffer"));
						c.Emit(Ldarg_0);
						c.Emit(Ldelem_Ref);
						c.Emit(Ldloc_2);
						c.Emit(Ldloc, index);
						c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("DenyReadSubServer", BindingFlags.NonPublic | BindingFlags.Static));

						var label = c.DefineLabel();
						c.Emit(Brtrue, label);

						cc.MarkLabel(label);
					};
				}
			}
			else
			{
				IL_Main.DoDraw += il =>
				{
					var c = new ILCursor(il);
					if (!c.TryGotoNext(MoveType.After, i => i.MatchStsfld(typeof(Main), "HoverItem")))
					{
						Logger.Error("Failed to apply IL patch into: Main.DoDraw");
						return;
					}

					c.Emit(Ldsfld, typeof(Main).GetField("gameMenu"));
					var skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(Ldsfld, current);
					var label = c.DefineLabel();
					c.Emit(Brfalse, label);

					c.Emit(Ldc_R4, 1f);
					c.Emit(Dup);
					c.Emit(Stsfld, typeof(Main).GetField("_uiScaleUsed", BindingFlags.NonPublic | BindingFlags.Static));
					c.Emit(OpCodes.Call, typeof(Matrix).GetMethod("CreateScale", new Type[] { typeof(float) }));
					c.Emit(Stsfld, typeof(Main).GetField("_uiScaleMatrix", BindingFlags.NonPublic | BindingFlags.Static));

					c.Emit(Ldsfld, current);
					c.Emit(Ldarg_0);
					c.Emit(Callvirt, typeof(Subworld).GetMethod("DrawSetup"));
					c.Emit(Ret);

					c.MarkLabel(label);

					c.Emit(Ldsfld, cache);
					c.Emit(Brfalse, skip);

					c.Emit(Ldc_R4, 1f);
					c.Emit(Dup);
					c.Emit(Stsfld, typeof(Main).GetField("_uiScaleUsed", BindingFlags.NonPublic | BindingFlags.Static));
					c.Emit(OpCodes.Call, typeof(Matrix).GetMethod("CreateScale", new Type[] { typeof(float) }));
					c.Emit(Stsfld, typeof(Main).GetField("_uiScaleMatrix", BindingFlags.NonPublic | BindingFlags.Static));

					c.Emit(Ldsfld, cache);
					c.Emit(Ldarg_0);
					c.Emit(Callvirt, typeof(Subworld).GetMethod("DrawSetup"));
					c.Emit(Ret);

					c.MarkLabel(skip);
				};

				IL_Main.DrawBackground += il =>
				{
					ILCursor c, cc;
					if (!(c = new ILCursor(il)).TryGotoNext(i => i.MatchLdcI4(330))
					|| !(cc = c.Clone()).TryGotoNext(i => i.MatchStloc(out _), i => i.MatchLdcR4(255)))
					{
						Logger.Error("Failed to apply IL patch into: Main.DrawBackGround");
						return;
					}

					c.Emit(Ldsfld, hideUnderworld);
					var skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(Conv_R8);
					var label = c.DefineLabel();
					c.Emit(Br, label);

					c.MarkLabel(skip);

					cc.MarkLabel(label);
				};

				IL_Main.OldDrawBackground += il =>
				{
					ILCursor c, cc;
					if (!(c = new ILCursor(il)).TryGotoNext(i => i.MatchLdcI4(230))
					|| !(cc = c.Clone()).TryGotoNext(i => i.MatchStloc(18), i => i.MatchLdcI4(0)))
					{
						Logger.Error("Failed to apply IL patch into: Main.OldDrawBackground");
						return;
					}

					c.Emit(Ldsfld, hideUnderworld);
					var skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(Conv_R8);
					var label = c.DefineLabel();
					c.Emit(Br, label);

					c.MarkLabel(skip);

					cc.MarkLabel(label);
				};

				IL_Main.UpdateAudio += il =>
				{
					ILCursor c, cc;
					if (!(c = new ILCursor(il)).TryGotoNext(MoveType.AfterLabel, i => i.MatchLdsfld(typeof(Main), "swapMusic"))
					|| !(cc = c.Clone()).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(Main), "UpdateAudio_DecideOnNewMusic"))
					|| !cc.Instrs[cc.Index].MatchBr(out ILLabel label))
					{
						Logger.Error("Failed to apply IL patch into: Main.UpdateAudio");
						return;
					}

					c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("ChangeAudio", BindingFlags.NonPublic | BindingFlags.Static));
					var skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("ManualAudioUpdates", BindingFlags.NonPublic | BindingFlags.Static));
					c.Emit(Brfalse, label);

					var ret = c.DefineLabel();
					ret.Target = c.Instrs[c.Instrs.Count - 1];
					c.Emit(Leave, ret);

					c.MarkLabel(skip);
				};

				IL_IngameOptions.Draw += il =>
				{
					ILCursor c, cc, ccc, cccc;
					if (!(c = new ILCursor(il)).TryGotoNext(i => i.MatchLdsfld(typeof(Lang), "inter"), i => i.MatchLdcI4(35))
					|| !(cc = c.Clone()).TryGotoNext(MoveType.After, i => i.MatchCallvirt(typeof(LocalizedText), "get_Value"))
					|| !(ccc = cc.Clone()).TryGotoNext(i => i.MatchLdnull(), i => i.MatchCall(typeof(WorldGen), "SaveAndQuit"))
					|| !(cccc = ccc.Clone()).TryGotoPrev(MoveType.AfterLabel, i => i.MatchLdloc(out _), i => i.MatchLdcI4(1), i => i.MatchAdd(), i => i.MatchStloc(out _)))
					{
						Logger.Error("Failed to apply IL patch into: IngameOptions.Draw");
						return;
					}

					ccc.Emit(Ldsfld, current);
					var skip = ccc.DefineLabel();
					ccc.Emit(Brfalse, skip);

					ccc.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("Exit"));
					var label = ccc.DefineLabel();
					ccc.Emit(Br, label);

					ccc.MarkLabel(skip);

					ccc.Index += 2;
					ccc.MarkLabel(label);

					cccc.Emit(Ldsfld, typeof(SubworldSystem).GetField("noReturn"));
					cccc.Emit(Brtrue, label);

					c.Emit(Ldsfld, current);
					skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(Ldstr, "Mods.SubworldLibrary.Return");
					c.Emit(OpCodes.Call, typeof(Language).GetMethod("GetTextValue", new Type[] { typeof(string) }));
					label = c.DefineLabel();
					c.Emit(Br, label);

					c.MarkLabel(skip);

					cc.MarkLabel(label);
				};

				IL_TileLightScanner.GetTileLight += il =>
				{
					ILCursor c, cc, ccc;
					if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchStloc(1))
					|| !(cc = c.Clone()).TryGotoNext(MoveType.AfterLabel, i => i.MatchLdarg(2), i => i.MatchCall(typeof(Main), "get_UnderworldLayer"))
					|| !(ccc = cc.Clone()).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(TileLightScanner), "ApplyHellLight")))
					{
						Logger.Error("Failed to apply IL patch into: TileLightScanner.GetTileLight");
						return;
					}

					c.Emit(Ldsfld, current);
					var skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(Ldsfld, current);
					c.Emit(Ldloc_0);
					c.Emit(Ldarg_1);
					c.Emit(Ldarg_2);
					c.Emit(Ldloca, 1);
					c.Emit(Ldarg_3);
					c.Emit(Callvirt, typeof(Subworld).GetMethod("GetLight"));

					c.Emit(Brfalse, skip);
					c.Emit(Ret);

					c.MarkLabel(skip);

					cc.Emit(Ldsfld, hideUnderworld);
					skip = cc.DefineLabel();
					cc.Emit(Brtrue, skip);

					ccc.MarkLabel(skip);
				};

				IL_Player.UpdateBiomes += il =>
				{
					ILCursor c, cc;
					if (!(c = new ILCursor(il)).TryGotoNext(MoveType.AfterLabel, i => i.MatchLdloc(out _), i => i.MatchLdfld(typeof(Point), "Y"), i => i.MatchLdsfld(typeof(Main), "maxTilesY"))
					|| !(cc = c.Clone()).TryGotoNext(i => i.MatchStloc(out _)))
					{
						Logger.Error("Failed to apply IL patch into: Player.UpdateBiomes");
						return;
					}

					c.Emit(Ldsfld, hideUnderworld);
					var skip = c.DefineLabel();
					c.Emit(Brfalse, skip);

					c.Emit(Ldc_I4_0);
					var label = c.DefineLabel();
					c.Emit(Br, label);

					c.MarkLabel(skip);

					cc.MarkLabel(label);
				};

				IL_Main.DrawUnderworldBackground += il =>
				{
					var c = new ILCursor(il);

					c.Emit(Ldsfld, hideUnderworld);
					var skip = c.DefineLabel();
					c.Emit(Brtrue, skip);
					c.Index = c.Instrs.Count - 1;
					c.MarkLabel(skip);
				};

				IL_Netplay.AddCurrentServerToRecentList += il =>
				{
					var c = new ILCursor(il);

					c.Emit(Ldsfld, current);
					var skip = c.DefineLabel();
					c.Emit(Brtrue, skip);
					c.Index = c.Instrs.Count - 1;
					c.MarkLabel(skip);
				};
			}

			IL_Main.EraseWorld += il =>
			{
				var c = new ILCursor(il);

				c.Emit(Ldarg_0);
				c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("EraseSubworlds", BindingFlags.NonPublic | BindingFlags.Static));
			};

			IL_Main.DoUpdateInWorld += il =>
			{
				ILCursor c, cc;
				if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(SystemLoader), "PreUpdateTime"))
				|| !(cc = c.Clone()).TryGotoNext(i => i.MatchCall(typeof(SystemLoader), "PostUpdateTime")))
				{
					Logger.Error("Failed to apply IL patch into: Main.DoUpdateInWorld");
					return;
				}

				c.Emit(Ldsfld, current);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, normalUpdates);
				var label = c.DefineLabel();
				c.Emit(Brfalse, label);

				c.MarkLabel(skip);

				cc.MarkLabel(label);
			};

			IL_WorldGen.UpdateWorld += il =>
			{
				var c = new ILCursor(il);
				if (!c.TryGotoNext(i => i.MatchCall(typeof(WorldGen), "UpdateWorld_Inner")))
				{
					Logger.Error("Failed to apply IL patch into: WorldGen.UpdateWorld");
					return;
				}

				c.Emit(Ldsfld, current);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, typeof(Subworld).GetMethod("Update"));

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, normalUpdates);
				var label = c.DefineLabel();
				c.Emit(Brfalse, label);

				c.MarkLabel(skip);

				c.Index++;
				c.MarkLabel(label);
			};

			IL_Player.Update += il =>
			{
				ILCursor c, cc;
				if (!(c = new ILCursor(il)).TryGotoNext(MoveType.AfterLabel, i => i.MatchLdsfld(out _), i => i.MatchConvR4(), i => i.MatchLdcR4(4200))
				|| !(cc = c.Clone()).TryGotoNext(MoveType.After, i => i.MatchLdarg(0), i => i.MatchLdarg(0), i => i.MatchLdfld(typeof(Player), "gravity"))
				|| !cc.Instrs[cc.Index].MatchLdloc(out int index))
				{
					Logger.Error("Failed to apply IL patch into: Player.Update");
					return;
				}

				c.Emit(Ldsfld, current);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, normalUpdates);
				var label = c.DefineLabel();
				c.Emit(Brtrue, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Ldarg_0);
				c.Emit(Callvirt, typeof(Subworld).GetMethod("GetGravity"));
				c.Emit(Stloc, index);
				c.Emit(Br, label);

				c.MarkLabel(skip);

				cc.Index -= 3;
				cc.MarkLabel(label);
			};

			IL_NPC.UpdateNPC_UpdateGravity += il =>
			{
				ILCursor c, cc;
				if (!(c = new ILCursor(il)).TryGotoNext(i => i.MatchLdsfld(out _), i => i.MatchConvR4(), i => i.MatchLdcR4(4200))
				|| !(cc = c.Clone()).TryGotoNext(MoveType.After, i => i.MatchLdarg(0), i => i.MatchLdarg(0), i => i.MatchCall(typeof(NPC), "get_gravity"))
				|| !cc.Instrs[cc.Index].MatchLdloc(out int index))
				{
					Logger.Error("Failed to apply IL patch into: NPC.UpdateNPC_UpdateGravity");
					return;
				}

				c.Emit(Ldsfld, current);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, normalUpdates);
				var label = c.DefineLabel();
				c.Emit(Brtrue, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Ldarg_0);
				c.Emit(Callvirt, typeof(Subworld).GetMethod("GetGravity"));
				c.Emit(Stloc, index);
				c.Emit(Br, label);

				c.MarkLabel(skip);

				cc.Index -= 3;
				cc.MarkLabel(label);
			};

			IL_Liquid.Update += il =>
			{
				var c = new ILCursor(il);
				if (!c.TryGotoNext(i => i.MatchLdarg(0), i => i.MatchLdfld(typeof(Liquid), "y"), i => i.MatchCall(typeof(Main), "get_UnderworldLayer")))
				{
					Logger.Error("Failed to apply IL patch into: Liquid.Update");
					return;
				}

				c.Emit(Ldsfld, current);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, normalUpdates);
				c.Emit(Brfalse, (ILLabel)c.Instrs[c.Index + 3].Operand);

				c.MarkLabel(skip);
			};

			IL_Player.SavePlayer += il =>
			{
				ILCursor c, cc, ccc;
				if (!(c = new ILCursor(il)).TryGotoNext(i => i.MatchCall(typeof(Player), "InternalSaveMap"))
				|| !(cc = c.Clone()).TryGotoNext(MoveType.AfterLabel, i => i.MatchLdsfld(typeof(Main), "ServerSideCharacter"))
				|| !(ccc = cc.Clone()).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(FileUtilities), "ProtectedInvoke")))
				{
					Logger.Error("Failed to apply IL patch into: Player.SavePlayer");
					return;
				}

				c.Index -= 3;

				c.Emit(Ldsfld, cache);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, cache);
				c.Emit(Callvirt, shouldSave);
				var label = c.DefineLabel();
				c.Emit(Brfalse, label);

				c.MarkLabel(skip);

				cc.MarkLabel(label);

				cc.Emit(Ldsfld, cache);
				skip = cc.DefineLabel();
				cc.Emit(Brfalse, skip);

				cc.Emit(Ldsfld, cache);
				cc.Emit(Callvirt, typeof(Subworld).GetMethod("get_NoPlayerSaving"));
				label = cc.DefineLabel();
				cc.Emit(Brtrue, label);

				cc.MarkLabel(skip);

				ccc.MarkLabel(label);
			};

			IL_WorldFile.SaveWorld_bool_bool += il =>
			{
				var c = new ILCursor(il);

				c.Emit(Ldsfld, cache);
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);
				c.Emit(Ldsfld, cache);
				c.Emit(Callvirt, shouldSave);
				c.Emit(Brtrue, skip);
				c.Emit(Ret);

				c.MarkLabel(skip);
			};
		}

		private static void PrepareMessageBuffer(MessageBuffer buffer)
		{
			int start = 0;
			int totalData = buffer.totalData;
			int writePosition = 0;
			List<byte> bytes = new();

			while (totalData >= 2)
			{
				int length = (int)BitConverter.ToInt16(buffer.readBuffer, start);

				if (totalData < length)
				{
					continue;
				}
				
				if (buffer.readBuffer[start + 2] != 250
						|| (ModNet.NetModCount < 256 && buffer.readBuffer[start + 3] != (byte)ModContent.GetInstance<SubworldLibrary>().NetID)
						|| (ModNet.NetModCount >= 256 && BitConverter.ToUInt16(buffer.readBuffer, start + 3) != ModContent.GetInstance<SubworldLibrary>().NetID))
				{
					totalData -= length;
					start += length;
					continue;
				}

				for (int i = start; i < start + length; i++)
				{
					bytes.Add(buffer.readBuffer[i]);
				}

				totalData -= length;
				Buffer.BlockCopy(buffer.readBuffer, start + length, buffer.readBuffer, start, totalData);
				writePosition = start + totalData;
			}

			for (int i = 0; i < bytes.Count; i++)
			{
				buffer.readBuffer[writePosition + i] = bytes[i];
			}
		}

		private static bool DenyRead(MessageBuffer buffer, int start, int length)
		{
			RemoteClient client = Netplay.Clients[buffer.whoAmI];
			byte MessageType = buffer.readBuffer[start + 2];

			// Only accept 'Hello' packets if the client is not connected
			if (client.State == 0 && MessageType != MessageID.Hello)
			{
				return true;
			}

			// Determine the connection state of this client
			SubworldSystem.playerLocations.TryAdd(client.Socket, -1);

			// Received packet contains the netID of Subworld Library. Let this packet through on the main server.
			if (MessageType == MessageID.ModPacket 
				&& (ModNet.NetModCount < 256 ? buffer.readBuffer[start + 3] : BitConverter.ToUInt16(buffer.readBuffer, start + 3)) == ModContent.GetInstance<SubworldLibrary>().NetID)
			{
				return false;
			}

			// If the client's location is the main server, let this packet through by default.
			if (SubworldSystem.playerLocations.TryGetValue(Netplay.Clients[buffer.whoAmI].Socket, out int id) && id <= -1)
			{
				return false;
			}

			// Verify if the client's location subworld is still active
			if (!SubworldSystem.links.TryGetValue(id, out SubserverLink link))
			{
				// Somehow this client is/was connected to a closed subserver...
				if (!Netplay.Disconnect)
				{
					// Try to force the client back to the main server
					SubworldSystem.MovePlayerToSubserver(buffer.whoAmI, ushort.MaxValue);
				}
				else
				{
					// Kick the client	
					NetMessage.BootPlayer(buffer.whoAmI, Lang.mp[2].ToNetworkText());
				}

				// Block the packet.
				return true;
			}

			// The subserver link is still starting up and/or processing its queue,
			// while this client is trying to connect to the subserver.
			// Only accept Hello messages into the queue; block everything else.
			if (link.Connecting && MessageType != MessageID.Hello)
			{
				return true;
			}

			// Reset the client's timeout on the main server when this client is connected to a sub server.
			// This keeps the main server from disconnecting this client.
			client.TimeOutTimer = 0;

			// Copy the data from the readBuffer and send it to the sub server
			// where the corresponding client is connected.
			byte[] packet = new byte[length + 1];
			packet[0] = (byte)buffer.whoAmI;
			Buffer.BlockCopy(buffer.readBuffer, start, packet, 1, length);
			link.Send(packet);

			if (packet[3] == MessageID.NetModules)
			{
				ushort packetId = BitConverter.ToUInt16(packet, 4);

				if (packetId == NetManager.Instance.GetId<NetBestiaryModule>())
				{
					return false;
				}
			}
			return true;
		}

		private static bool DenyReadSubServer(MessageBuffer buffer, int start, int length)
		{
			// Intentional packet leak: block everything besides Hello when not
			// successfully logged in (yet). Prevents server from booting the client.
			RemoteClient client = Netplay.Clients[buffer.whoAmI];
			if (client.State == 0 && buffer.readBuffer[start + 2] != MessageID.Hello)
			{
				return true;
			}

			return false;
		}

		private static bool DenySend(ISocket socket, byte[] data, int start, int length, ref object state)
		{
			return Thread.CurrentThread.Name != "Subserver Packets" && SubworldSystem.playerLocations.TryGetValue(socket, out int id) && id > -1;
		}

		private static bool CheckLocalHost(int player)
		{
			return Netplay.Clients[player].Socket != null && SubworldSystem.playerLocations.ContainsKey(Netplay.Clients[player].Socket);
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
			bool connection = false;
			for (int i = 0; i < 255; i++)
			{
				RemoteClient client = Netplay.Clients[i];
				if (client.PendingTermination)
				{
					if (client.PendingTerminationApproved)
					{
						client.Reset();

						NetMessage.SendData(14, -1, i, null, i, 0);
						ChatHelper.BroadcastChatMessage(NetworkText.FromKey(Lang.mp[20].Key, client.Name), new Color(255, 240, 20), i);
						Player.Hooks.PlayerDisconnect(i);
					}
					continue;
				}
				if (client.State > 0)
				{
					connection = true;
					break;
				}
			}

			if (!Netplay.HasClients)
			{
				Netplay.HasClients = connection;
				return;
			}

			if (!connection)
			{
				Netplay.Disconnect = true;
				Netplay.HasClients = false;
			}
		}

		public override object Call(params object[] args)
		{
			try
			{
				string message = args[0] as string;
				switch (message)
				{
					case "Enter":
						return SubworldSystem.Enter(args[1] as string);
					case "Exit":
						SubworldSystem.Exit();
						return true;
					case "Current":
						return SubworldSystem.Current;
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

		public override void HandlePacket(BinaryReader reader, int whoAmI)
		{
			SubLibMessageType messageType = (SubLibMessageType)reader.ReadByte();

			switch (messageType)
			{
				// Always sent from client => main server
				case SubLibMessageType.BeginEntering:
					{
						ushort id = reader.ReadUInt16();

						if (Main.netMode == NetmodeID.Server && !SubworldSystem.noReturn)
						{
							SubworldSystem.MovePlayerToSubserver(whoAmI, id);
						}
					}
					break;


				// Sent from main server => sub server
				case SubLibMessageType.MovePlayerOnServer:
					{
						if (Main.netMode == NetmodeID.Server && SubworldSystem.current != null)
						{
							Netplay.Clients[whoAmI].Reset();

							NetMessage.SendData(14, -1, whoAmI, null, whoAmI, 0);
							ChatHelper.BroadcastChatMessage(NetworkText.FromKey(Lang.mp[20].Key, Netplay.Clients[whoAmI].Name), new Color(255, 240, 20), whoAmI);
							Player.Hooks.PlayerDisconnect(whoAmI);

							CheckClients();
						}
					}
					break;


				// Sent from main server => client
				case SubLibMessageType.MovePlayerOnClient: 
					{
						ushort id = reader.ReadUInt16();

						if (Main.netMode == NetmodeID.MultiplayerClient)
						{ 
							Task.Factory.StartNew(SubworldSystem.ExitWorldCallBack, id < ushort.MaxValue ? id : -1);
						}
					}
					break;


				// SendToMainServer: Sent from any sub server => main server
				// SendToSubserver: Sent from main server => this sub server
				case SubLibMessageType.SendToMainServer:				
				case SubLibMessageType.SendToSubserver:
					{
						ModNet.GetMod(ModNet.NetModCount < 256 ? reader.ReadByte() : reader.ReadInt16()).HandlePacket(reader, whoAmI);
					}
					break;

				// BroadcastBetweenServers: Sent from any server to this server
				case SubLibMessageType.BroadcastBetweenServers:
					{
						ushort worldID = reader.ReadUInt16();
						string worldName = reader.ReadString();
						byte messageAuthor = reader.ReadByte();
						string messageAuthorName = "";
						if (messageAuthor < byte.MaxValue)
						{
							messageAuthorName = reader.ReadString();
						}
						NetworkText text = NetworkText.Deserialize(reader);
						Color color = reader.ReadRGB();

						if (Main.netMode == NetmodeID.Server)
						{
							// Main server forwards packets to other server
							if (SubworldSystem.current == null)
							{
								SubworldSystem.BroadcastBetweenServers(
									messageAuthor,
									text,
									color,
									worldID,
									worldName);
							}

							// Send the broadcast to connected clients
							if (SubworldSystem.receiveBroadcasts && !SubworldSystem.broadcastDenyList.Contains(worldID))
							{
								ModPacket packet = GetPacket();
								packet.Write((byte)SubLibMessageType.Broadcast);
								packet.Write(worldID);
								packet.Write(worldName);
								packet.Write(messageAuthor);
								if (messageAuthor < byte.MaxValue)
								{
									packet.Write(messageAuthorName);
								}
								text.Serialize(packet);
								packet.WriteRGB(color);
								packet.Send(-1, whoAmI);
							}
						}
					}
					break;

				case SubLibMessageType.Broadcast:
					{
						ushort worldID = reader.ReadUInt16();
						string worldName = reader.ReadString();
						byte messageAuthor = reader.ReadByte();
						string messageAuthorName = "";
						if (messageAuthor < byte.MaxValue)
						{
							messageAuthorName = reader.ReadString();
						}
						NetworkText text = NetworkText.Deserialize(reader);
						Color color = reader.ReadRGB();

						if (Main.netMode != NetmodeID.MultiplayerClient)
						{
							break;
						}

						if (messageAuthor < byte.MaxValue && !Main.player[messageAuthor].active)
						{
							Main.player[messageAuthor].name = messageAuthorName;
						}

						SubworldSystem.DisplayMessage(
							messageAuthor,
							text,
							color,
							worldName);
					}
					break;
			}
		}
	}

	internal enum SubLibMessageType : byte
	{
		None = 0, // Fail-safe: we should not use 0
		BeginEntering,
		MovePlayerOnServer,
		MovePlayerOnClient,
		SendToMainServer,
		SendToSubserver,
		BroadcastBetweenServers,
		Broadcast
	}
}