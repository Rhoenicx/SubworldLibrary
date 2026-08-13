using Microsoft.Xna.Framework.Graphics;
using System;
using System.IO;
using System.Reflection;
using Terraria;
using Terraria.IO;
using Terraria.Map;
using Terraria.Utilities;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldLibrary
	{
		private static void ResizeSubworld(int lastWidth, int lastHeight)
		{
			if ((Main.maxTilesX <= 8400 || Main.maxTilesX <= lastWidth) && (Main.maxTilesY <= 2400 || Main.maxTilesY <= lastHeight))
			{
				return;
			}

			ushort newWidth = (ushort)Math.Clamp(Main.maxTilesX + 1, 8401, 65535);
			ushort newHeight = (ushort)Math.Clamp(Main.maxTilesY + 1, 2401, 65535);
			if (newWidth == ushort.MaxValue)
			{
				Main.maxTilesX = 65534;
			}
			if (newHeight == ushort.MaxValue)
			{
				Main.maxTilesY = 65534;
			}

			Main.tile = (Tilemap)Activator.CreateInstance(typeof(Tilemap), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { newWidth, newHeight }, null);
			Main.Map = new WorldMap(newWidth, newHeight);

			// try to match vanilla dimensions
			Main.mapTargetX = (newWidth + 1678) / 1680;
			Main.mapTargetY = (newHeight + 1198) / 1200;
			Main.instance.mapTarget = new RenderTarget2D[Main.mapTargetX, Main.mapTargetY];

			Main.initMap = new bool[Main.mapTargetX, Main.mapTargetY];
			Main.mapWasContentLost = new bool[Main.mapTargetX, Main.mapTargetY];
		}

		private static void EraseSubworlds(int index)
		{
			WorldFileData world = Main.WorldList[index];
			string path = Path.Combine(world.IsCloudSave ? Main.CloudWorldPath : Main.WorldPath, world.UniqueId.ToString());
			if (FileUtilities.Exists(path, world.IsCloudSave))
			{
				FileUtilities.Delete(path, world.IsCloudSave);
			}
		}

		private static bool InterceptFreeze(MessageBuffer buffer, int length)
		{
			if (length < 2)
			{
				buffer.totalData = 0;
				buffer.checkBytes = false;
				return true;
			}
			return false;
		}
	}
}
