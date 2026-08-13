using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.Net.Sockets;
using static Mono.Cecil.Cil.OpCodes;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldLibrary
	{
		private static ILHook tcpSocketHook;
		private static ILHook socialSocketHook;

		private void RegisterServerHooks()
		{
			FieldInfo current = typeof(SubworldSystem).GetField("current", BindingFlags.NonPublic | BindingFlags.Static);
			MethodInfo shouldSave = typeof(Subworld).GetMethod("get_ShouldSave");
			bool subserver = Program.LaunchParameters.ContainsKey("-subworld");

			IL_Netplay.UpdateServerInMainThread += il =>
			{
				var c = new ILCursor(il);
				if (!c.TryGotoNext(MoveType.AfterLabel, i => i.MatchRet()))
				{
					Logger.Error("FAILED:");
					return;
				}
				c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("CheckBytes", BindingFlags.NonPublic | BindingFlags.Static));
				// no-ops on subservers; see UpdateRejoiningPlayers
				c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("UpdateRejoiningPlayers", BindingFlags.NonPublic | BindingFlags.Static));
			};

			// these are effectively not called on subservers, no need to patch them
			if (!subserver)
			{
				socialSocketHook = new ILHook(typeof(SocialSocket).GetMethod("Terraria.Net.Sockets.ISocket.AsyncSend", BindingFlags.NonPublic | BindingFlags.Instance), AsyncSend);
				tcpSocketHook = new ILHook(typeof(TcpSocket).GetMethod("Terraria.Net.Sockets.ISocket.AsyncSend", BindingFlags.NonPublic | BindingFlags.Instance), AsyncSend);

				void AsyncSend(ILContext il)
				{
					var c = new ILCursor(il);
					if (!c.TryGotoNext(MoveType.After, i => i.MatchRet()))
					{
						Logger.Error("FAILED:");
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

				IL_NetMessage.CheckBytes += il =>
				{
					ILCursor c, cc;
					if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(BitConverter), "ToUInt16"))
					|| !c.Instrs[c.Index].MatchStloc(out int index)
					|| !c.TryGotoNext(MoveType.After, i => i.MatchCallvirt(typeof(Stream), "get_Position"), i => i.MatchStloc(out _))
					|| !(cc = c.Clone()).TryGotoNext(i => i.MatchLdsfld(typeof(NetMessage), "buffer"), i => i.MatchLdarg(0), i => i.MatchLdelemRef(), i => i.MatchLdfld(typeof(MessageBuffer), "reader")))
					{
						Logger.Error("FAILED:");
						return;
					}

					c.Emit(Ldsfld, typeof(NetMessage).GetField("buffer"));
					c.Emit(Ldarg_0);
					c.Emit(Ldelem_Ref);
					c.Emit(Ldloc, index);
					c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("InterceptFreeze", BindingFlags.NonPublic | BindingFlags.Static));
					var skip = c.DefineLabel();
					c.Emit(Brtrue, skip);
					cc.MarkLabel(skip);

					c.Emit(Ldsfld, typeof(NetMessage).GetField("buffer"));
					c.Emit(Ldarg_0);
					c.Emit(Ldelem_Ref);
					c.Emit(Ldloc_2);
					c.Emit(Ldloc, index);
					c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("DenyRead", BindingFlags.NonPublic | BindingFlags.Static));

					var label = c.DefineLabel();
					c.Emit(Brtrue, label);

					cc.MoveAfterLabels();

					cc.MarkLabel(label);

					cc.Index = c.Instrs.Count - 1;
					cc.Emit(Ldarg_0);
					cc.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("AllowAutoShutdown", BindingFlags.NonPublic | BindingFlags.Static));
				};

				IL_Netplay.UpdateConnectedClients += il =>
				{
					ILCursor c, cc;
					if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchCallvirt(typeof(RemoteClient), "Reset"))
					|| !(cc = c.Clone()).TryGotoPrev(i => i.MatchStfld(typeof(RemoteClient), "State")))
					{
						Logger.Error("FAILED:");
						return;
					}
					c.MoveAfterLabels();

					c.Emit(Ldloc_1);
					c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("SyncDisconnect", BindingFlags.NonPublic | BindingFlags.Static));

					cc.MoveAfterLabels();

					cc.Emit(Ldloc_1);
					cc.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("AllowAutoShutdown", BindingFlags.NonPublic | BindingFlags.Static));
				};

				return;
			}

			IL_Main.DedServ_PostModLoad += il =>
			{
				var c = new ILCursor(il);
				if (!c.TryGotoNext(i => i.MatchBr(out _)))
				{
					Logger.Fatal("FAILED - subserver cannot run without this injection!");
					Main.instance.Exit();
					return;
				}

				ConstructorInfo gameTime = typeof(GameTime).GetConstructor(Type.EmptyTypes);
				MethodInfo update = typeof(Main).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
				FieldInfo saveTime = typeof(Main).GetField("saveTime", BindingFlags.NonPublic | BindingFlags.Static);

				// DedServ must not run, so this abomination replicates the update loop (lots of private methods and reflection is slow)

				c.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("LoadIntoSubworld", BindingFlags.NonPublic | BindingFlags.Static));

				c.Emit(Ldarg_0);
				c.Emit(Newobj, gameTime);
				c.Emit(Callvirt, update);

				c.Emit(Newobj, typeof(Stopwatch).GetConstructor(Type.EmptyTypes));
				c.Emit(Stloc_1);
				c.Emit(Ldloc_1);
				c.Emit(Callvirt, typeof(Stopwatch).GetMethod("Start"));

				c.Emit(Ldc_I4_0);
				c.Emit(Stsfld, typeof(Main).GetField("gameMenu"));

				// vanilla magic number, not sure why it's 1 higher than 60 / 1000
				c.Emit(Ldc_R8, 16.666666666666668);
				c.Emit(Stloc_2);
				c.Emit(Ldloc_2);
				c.Emit(Stloc_3);

				var loopStart = c.DefineLabel();
				c.Emit(Br, loopStart);

				// {

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

				// }

				c.Emit(Ldsfld, current);
				c.Emit(Callvirt, shouldSave);
				label = c.DefineLabel();
				c.Emit(Brfalse, label);
				c.Emit(OpCodes.Call, typeof(WorldFile).GetMethod("SaveWorld", Type.EmptyTypes));
				c.MarkLabel(label);

				c.Emit(OpCodes.Call, typeof(SystemLoader).GetMethod("OnWorldUnload"));

				c.Emit(Ret);
			};
		}
	}
}
