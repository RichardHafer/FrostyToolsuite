# RimeWidgetBlueprint Editor

A plugin for Frosty editor. 
It will let you view/edit "RimeWidgetBlueprint" assets. To use it, go to a RimeWidgetBlueprint and click the 
edit button in the top right, this button will switch views between the normal editor and the ui editor. You can 
also hide a UI element by right clicking and unhide by clicking the unhide button at the top. This plugin doesn't 
support UIs with "Lists" or "Rows" so if you see a RimeWidgetBlueprint that seems invisible that is probably why.

**This plugin will only work with PvZ BFN!** You can test it with other games but I'm pretty sure it won't work, 
if you want you can edit the source to make it work for other games.

## Building
To build this plugin, open the .sln file in Visual Studio. Make sure at the top it's set to 'Release' and 'x64'.
Then, Right click the project to add the references:
- FrostyCore.dll
- FrostySdk.dll
- FrostyHash.dll
- FrostyControls.dll
- LocalizedStringPlugin.dll
- TexturePlugin.dll

You can get these from your Frosty Editor folder.
Then just build the project by right clicking the project and clicking build, or you can press Ctrl + B. You can put the
RimeWidgetBlueprintEditor.dll in the Plugins folder in Frosty Editor.

## Update log
v1.1.0.0:

- Arrow key/WASD movement for precise movements
- A zooming/panning feature!
- Visible hitboxes when hovering over UI elements
- Textures are no longer written to your temp file and are written to memory, so textures should automatically update if you change them. You might need to re-open the UI editor or use the refresh button to see them though.
- Support for text rotation
- Support for Font Effects, text should now look a lot more accurate (they can be laggy though so you can disable them in settings)
- Improved the loading times
- Some extra options, you can find them at Tools > Options > UI Editor Options
- Some bug fixes
