using Mono.Cecil.Cil;
using MonoMod.Cil;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;
using static Mono.Cecil.Cil.OpCodes;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldLibrary
	{
		private void RegisterWorldHooks()
		{
			FieldInfo current = typeof(SubworldSystem).GetField("current", BindingFlags.NonPublic | BindingFlags.Static);
			MethodInfo normalUpdates = typeof(Subworld).GetMethod("get_NormalUpdates");

			IL_WorldGen.clearWorld += il =>
			{
				var c = new ILCursor(il);

				c.Emit(Ldsfld, typeof(WorldGen).GetField("lastMaxTilesX", BindingFlags.NonPublic | BindingFlags.Static));
				c.Emit(Ldsfld, typeof(WorldGen).GetField("lastMaxTilesY", BindingFlags.NonPublic | BindingFlags.Static));
				c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("ResizeSubworld", BindingFlags.NonPublic | BindingFlags.Static));
			};

			IL_Main.DoUpdateInWorld += il =>
			{
				ILCursor c, cc;
				if (!(c = new ILCursor(il)).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(SystemLoader), "PreUpdateTime"))
				|| !(cc = c.Clone()).TryGotoNext(i => i.MatchCall(typeof(SystemLoader), "PostUpdateTime")))
				{
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
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
		}

		private static void RegisterWorldDeletionHook()
		{
			IL_Main.EraseWorld += il =>
			{
				var c = new ILCursor(il);

				c.Emit(Ldarg_0);
				c.Emit(OpCodes.Call, typeof(SubworldLibrary).GetMethod("EraseSubworlds", BindingFlags.NonPublic | BindingFlags.Static));
			};
		}
	}
}
