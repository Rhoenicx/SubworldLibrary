using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Reflection;
using Terraria;
using Terraria.Graphics.Light;
using Terraria.Localization;
using static Mono.Cecil.Cil.OpCodes;

namespace SubworldLibraryCommunityFork
{
	/// <summary>
	/// Provides the community fork's tModLoader entry point and runtime integration hooks.
	/// </summary>
	public partial class SubworldLibrary
	{
		private void RegisterClientHooks()
		{
			FieldInfo current = typeof(SubworldSystem).GetField("current", BindingFlags.NonPublic | BindingFlags.Static);
			FieldInfo cache = typeof(SubworldSystem).GetField("cache", BindingFlags.NonPublic | BindingFlags.Static);
			FieldInfo hideUnderworld = typeof(SubworldSystem).GetField("hideUnderworld");
			FieldInfo noReturn = typeof(SubworldSystem).GetField("noReturn");

			IL_Main.DoDraw += il =>
			{
				var c = new ILCursor(il);
				if (!c.TryGotoNext(MoveType.After, i => i.MatchStsfld(typeof(Main), "HoverItem")))
				{
					Logger.Error("FAILED:");
					return;
				}

				c.Emit(Ldsfld, typeof(Main).GetField("gameMenu"));
				var skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, current);
				var label = c.DefineLabel();
				c.Emit(Brfalse, label);

				c.Emit(Ldsfld, current);
				c.Emit(Ldarg_1);
				c.Emit(Callvirt, typeof(Subworld).GetMethod("DrawSetup"));
				c.Emit(Ret);

				c.MarkLabel(label);

				c.Emit(Ldsfld, cache);
				c.Emit(Brfalse, skip);

				c.Emit(Ldsfld, cache);
				c.Emit(Ldarg_1);
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
					Logger.Error("FAILED:");
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
				|| !(cc = c.Clone()).TryGotoNext(i => i.MatchStloc(out _), i => i.MatchLdcI4(0)))
				{
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
					return;
				}

				ccc.Index -= 4;

				ccc.Emit(Ldsfld, current);
				var skip = ccc.DefineLabel();
				ccc.Emit(Brfalse, skip);

				ccc.Emit(OpCodes.Call, typeof(SubworldSystem).GetMethod("Exit"));
				var label = ccc.DefineLabel();
				ccc.Emit(Br, label);

				ccc.MarkLabel(skip);

				ccc.Index += 6;
				ccc.MarkLabel(label);

				cccc.Emit(Ldsfld, noReturn);
				cccc.Emit(Brtrue, label);

				c.Emit(Ldsfld, current);
				skip = c.DefineLabel();
				c.Emit(Brfalse, skip);

				c.Emit(Ldstr, "Mods.SubworldLibraryCommunityFork.Return");
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
					Logger.Error("FAILED:");
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
					Logger.Error("FAILED:");
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
	}
}
