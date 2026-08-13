# Subworld Library Community Fork

`SubworldLibraryCommunityFork` is a community-maintained fork of [Subworld Library](https://github.com/jjohnsnaill/SubworldLibrary), an API that lets Terraria mods add dimensions (subworlds) while handling the required game integration, multiplayer routing, world persistence, and loading flow.

John Snail (`jjohnsnaill`) created the original Subworld Library. All credit for the original project and its design belongs to him. This fork exists so the Terraria modding community can maintain compatibility, review contributions, fix long-standing bugs, and continue improving the library when upstream updates are unavailable.

## Project links

- Community fork source and issue tracker: https://github.com/Path-of-Terraria/SubworldLibraryCommunityFork
- Original project: https://github.com/jjohnsnaill/SubworldLibrary
- Original forum thread: https://forums.terraria.org/index.php?threads/86283
- Original wiki and API documentation: https://github.com/jjohnsnaill/SubworldLibrary/wiki

## Compatibility

The Workshop name, assembly name, and internal tModLoader dependency ID are `SubworldLibraryCommunityFork`. Mod authors must opt into the community fork by changing their strong or weak dependency from `SubworldLibrary` to `SubworldLibraryCommunityFork`.

The public C# namespace is `SubworldLibraryCommunityFork`, matching the internal tModLoader package ID as required by tModLoader. Integrations must update their `using SubworldLibrary;` directives to `using SubworldLibraryCommunityFork;` in addition to changing the dependency ID and assembly reference. API type names such as `Subworld` and `SubworldSystem` remain unchanged.

The original `SubworldLibrary` and `SubworldLibraryCommunityFork` must not be enabled together because both install hooks for the same Terraria systems. The fork sorts after the original and stops loading with a clear error that asks the user to disable the original.

## Migrating from Subworld Library

Migrating an existing mod to the community fork requires two changes:

1. Change the tModLoader dependency from `SubworldLibrary` to `SubworldLibraryCommunityFork` in `build.txt`.

   ```ini
   modReferences = SubworldLibraryCommunityFork
   ```

2. Change your C# imports from `using SubworldLibrary;` to `using SubworldLibraryCommunityFork;`.

   ```csharp
   using SubworldLibraryCommunityFork;
   ```

The API type names themselves are unchanged, so existing usages such as `Subworld`, `SubworldSystem`, and `SubserverLink` do not need to be renamed. Remove the original `SubworldLibrary` dependency and do not enable both libraries together.

## Source layout

- `SubworldLibrary.cs` contains the mod entry point, Mod.Call API, and packet dispatch.
- `Hooks/` groups runtime injections by world, save, server, and client responsibility.
- `Networking/` contains the main-server packet relay helpers.
- `SubworldSystem.cs` contains lifecycle state and the public entry/exit API.
- `Systems/` separates player traversal, copied world data, and subworld loading.
- `Utilities/` contains world resizing and cleanup helpers.
- `ModCallExample/` demonstrates weak integration through `Mod.Call`.

## How it works

Terraria was not designed around multiple dimensions, so the library applies targeted runtime hooks to create and load compact worlds, coordinate player travel, preserve world/player state, and route multiplayer traffic through a subserver for each occupied subworld.

Subworlds are highly customizable: they can define their dimensions, world-generation tasks, persistence behavior, update rules, lighting, audio, and loading UI. Subworld Library also removes space, both oceans, and the underworld from subworlds so they can be much smaller than a normal Terraria world.

## Loading and saving

Loading screens can range from plain text to interactive menus. Saved subworlds live under a directory named for the main world's unique ID, and deleting the main world deletes its subworld data as well. A subworld and player changes made inside it can also be configured as temporary.

## Multiplayer

The library opens a server for each occupied subworld and relays the relevant player traffic between the main server and those subservers. Multiplayer behavior is sensitive to join, disconnect, and traversal timing, so changes to these paths should be tested with multiple simultaneous clients in addition to compiling successfully.

## Contributing

Bug reports and focused pull requests are welcome in the community repository. Please include reproduction steps, tModLoader version, single-player or multiplayer context, and relevant logs. For multiplayer changes, describe the host/client topology and the number of players used to verify the fix.

## LICENSE

Subworld Library's purpose is to unify mods and ensure compatibility between them. To fulfill this, Subworld Library and any derivatives must:

- be open source.
- be published, not as part of another mod.
- be allowed to use each other's code (for parity between improvements).
- have this exact license. If Subworld Library updates it to address oversights, it will apply to all derivatives.

**Please consider contributing before forking!**

Using the code for purposes other than making a dimension API is allowed unconditionally!
