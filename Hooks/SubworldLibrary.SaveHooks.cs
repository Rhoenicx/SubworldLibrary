using MonoMod.Cil;
using System.Reflection;
using Terraria;
using Terraria.IO;
using Terraria.Utilities;
using static Mono.Cecil.Cil.OpCodes;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldLibrary
	{
		private void RegisterSaveHooks()
		{
			FieldInfo cache = typeof(SubworldSystem).GetField("cache", BindingFlags.NonPublic | BindingFlags.Static);
			MethodInfo shouldSave = typeof(Subworld).GetMethod("get_ShouldSave");

			IL_Player.SavePlayer += il =>
			{
				ILCursor c, cc, ccc;
				if (!(c = new ILCursor(il)).TryGotoNext(i => i.MatchCall(typeof(Player), "InternalSaveMap"))
				|| !(cc = c.Clone()).TryGotoNext(MoveType.AfterLabel, i => i.MatchLdsfld(typeof(Main), "ServerSideCharacter"))
				|| !(ccc = cc.Clone()).TryGotoNext(MoveType.After, i => i.MatchCall(typeof(FileUtilities), "ProtectedInvoke")))
				{
					Logger.Error("FAILED:");
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
	}
}
