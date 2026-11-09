ENGLISH

# FORK NOTE

This is a Fork of Hargabt's Fork of Frosty Toolsuite and that one is based on [selphea's fork of 1.0.7](https://github.com/selphea/FrostyToolsuite). Mine is specifically aimed at improving the Dead Space Remake modding experience. Report issues if you notice any.

## New in this fork

From here on out I will refer to Dead Space Remake as "DSR".
- MeshVariation Database recognition fixed for DSR so for quite a lot of meshes, texture previewing works again in Mesh Viewer. Still working on extending support for this.
- Biggest one: Automatic mod installation. It goes like this: User must set a backup location or else mods wont compile at all. Once done, on first ever backup creation it creates an appropiate, efficient backup. Mod gets compiled as usual, .symlink steps and cmd admin entirely removed. It checks if a mod is already installed, if yes, replace files with backup and remove any additional .cas files. Files automatically gets imported properly from ModData, and that's it. Works beautifully. Also applies to the Mod Manager.
- "Restore Vanilla" button added next to Compile button which will turn game back to vanilla using CAS-deletion and backup.
- Localization rework, across whole software: Add German (de-DE) and English (en-US) string resources for FrostyEditor, FrostyModManager, FrostyPlugin. All the individual plugins are not covered yet, more translations can be added as wanted
- Fixed sound importing to a major extent, included support for .opus and .snr files (EALayer3)
- Attempted fix for mod compilation inconsistencies when re-opening or re-compiling projects.
FrostySdk: EbxWriter and AssetManager fixes. Any instabilities with certain file types in Frosty Editor such as Widgets on re-opening project files or re-compiling and more should be fixed. (Still testing this)
- Local read-only MCP server in Frosty Editor, so an external client can browse the loaded EBX tree and read individual assets. See [MCP](#mcp).

## What's coming next?

- Bug fixes and more features

# FrostyToolsuite
The most advanced modding platform for games running on DICE's Frostbite game engine.

## Setup

1. Download the source code.
2. Open the solution (found under FrostyEditor) with Visual Studio 2022, and make sure the project is set to ``Release - Final`` and ``x64``. Close out of retarget window if prompted.
3. For applications, build only Frosty Editor or FrostyModManager, rest is the others. Only build the projects themselves, never the solution.

## MCP

Frosty Editor hosts an MCP server on loopback while a game profile is loaded. It starts after the profile (and project, if one was opened) is ready, and it stops when the editor window closes. The log line looks like `MCP server listening at http://127.0.0.1:17341/mcp`.

The preferred port is `McpPort` in the editor config. The default is `17341`. Only `127.0.0.1` is bound. If that port cannot be opened, the editor tries 24816, 27182, 4040, 8765, 9090, 54321, and 50080. The log line is the address to put in the client config.

Cursor client config:

```json
{
  "mcpServers": {
    "frosty": {
      "url": "http://127.0.0.1:17341/mcp"
    }
  }
}
```

The server speaks JSON-RPC over HTTP (`initialize`, `tools/list`, `tools/call`, `ping`). Reading tools never modify assets. Field and array tools edit one object and mark the project dirty. Values use the same JSON shape as `get_object`. `expectedValue` rejects a write when the current value differs. `save_project` writes the open project file, and `export_mod` writes a mod with the project's existing mod settings. `export_mod` leaves an existing file alone unless `overwrite` is true.

| Tool | What it returns |
| --- | --- |
| `get_status` | Loaded profile, project name, unsaved changes, EBX count. If no profile is loaded, every tool reports that. |
| `list_folder` | One level of the EBX tree, same paths as the Data Explorer. Empty `path` is the root. Optional `modifiedOnly` and `limit` (default 500). |
| `search_assets` | Assets whose name contains `query`. Optional `type` (that class and its subclasses), `modifiedOnly`, and `limit` (default 50). |
| `get_asset` | One EBX asset by full name. Objects are flat. Pointers stay references (`internal` guid or `external` asset name) and are not expanded. Large arrays and the payload are cut; the result sets `truncated`. |
| `get_object` | One object inside an asset, by the guid from `get_asset`. Use this when `get_asset` was truncated. |
| `clone_layer_object` | Copies one entity from a LayerData asset into another. Fields and external references are kept, the copy gets a new GUID, and it is appended to the target Objects array. |
| `remove_layer_object` | Removes the one Objects entry with the given GUID from a LayerData asset. |
| `save_project` | Saves the open project through the editor save API. A project that has never been saved has to be saved in the editor first. |
| `export_mod` | Writes a `.fbmod` through the editor export API. `outputPath` is required. An existing file is replaced only when `overwrite` is true. |
| `get_asset_metadata` | Type, GUID, object count, modified and dirty flags, and whether the asset exists. |
| `get_type_info` | Fields of an EBX type, their value kinds, defaults, and enum names. `includeInheritedFields` defaults to true. |
| `get_array_slice` | One page of an array. `offset` is the first index and `limit` is at most 256. The result includes the full array length, each item's absolute index, and complete reference data. `nextOffset` is present when more items remain. Nothing is written. |
| `set_object_field` | Sets one field. `path` is names and array indexes, such as `["Transform", "trans", "x"]`. Optional `expectedValue` must match the current value. |
| `set_object_fields` | Sets several fields on one object. If any expected value disagrees, none of the changes are kept. |
| `insert_array_item` | Inserts a value into an array. An index equal to the length appends it. |
| `remove_array_item` | Removes one array item by index. Optional `expectedItem` is checked first. |
| `move_array_item` | Moves one array item from `fromIndex` to `toIndex`. |
| `clone_object` | Copies one object, keeps its references, assigns a new GUID, and appends it to `targetArrayPath`. The source object is left unchanged. |
| `create_object` | Creates an EBX object, checks `fields` against the type, assigns a GUID, and appends it to an array on `targetGuid`. |
| `remove_object` | Removes the one array entry with that GUID. Optional `expectedType` is checked first. |
| `validate_project` | Checks duplicate GUIDs, broken references, and values that do not match their fields. Omitting `assets` checks the current modified assets. Nothing is written. |
| `undo_last_change` | Reverses the latest MCP edit. Optional `asset` and `guid` have to match that edit. An edit that cannot be undone returns an error. |

## License
The Content, Name, Code, and all assets are licensed under a Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License.
