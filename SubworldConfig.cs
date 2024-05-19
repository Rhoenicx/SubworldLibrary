using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace SubworldLibrary
{
	internal class SubworldServerConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ServerSide;

		// Setting for maximum time a subserver may spend starting up.
		// During this time the server loads mods.
		[DefaultValue(120)]
		[Slider]
		[Range(30, 600)]
		public int SubserverStartupTimeMax;

		public override void OnLoaded()
		{
			SubworldLibrary.serverConfig = this;
		}
	}

	internal class SubworldClientConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		// Hides the world name from all chat messages and/or broadcasts
		[DefaultValue(false)]
		public bool HideWorldName;

		public override void OnLoaded()
		{
			SubworldLibrary.clientConfig = this;
		}
	}
}
