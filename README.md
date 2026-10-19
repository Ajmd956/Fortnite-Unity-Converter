# Fortnite Unity Converter

Fortnite Unity Converter turns Fortnite materials into Unity shaders. Pick a
material, convert it, and drop the finished shader and its textures into your
Unity project.

IMPORTANT: this converter can make mistakes. If a shader doesn't look right,
you may have to edit it by hand. This will be improved in the future.


## Download

Get the latest "Fortnite Unity Converter.exe" from the Releases page on the
right. Run it. No setup is needed.

The app updates itself: when a new version is out, a window shows what changed
and you can update with one click. You can turn update checks off, or check
manually, under App Settings.


## What it does

- Files: browse your Fortnite files, preview textures, meshes and more, and
  export them.
- Convert: turn one or more materials into Unity shaders.
- My Shaders: see everything you've converted.
- Blueprint Converter: turn a sky dome or a prop Blueprint (lights, sounds and
  particle systems, like the Reboot Van) into a Unity prefab. It saves the
  shaders, textures, meshes, sounds and day cycle in Blueprint Conversions. Copy
  that folder into your Unity project's Assets. If a Unity editor is found, the app
  builds the prefab for you first, in the background.
- Check Shaders: scan shader files for common problems and fix the safe ones.
  Fixes are made on copies in Documents\Fortnite Unity Converter\Fixed
  Conversions, so your originals are never changed.

Your converted shaders and settings are saved in
Documents\Fortnite Unity Converter, so updating or deleting the app never
removes them.

Not everything converts exactly. Some Unreal features have no Unity
equivalent, so the app uses a stand-in and tells you when it does.


## License

Fortnite Unity Converter is distributed under the [GNU General Public License
v3.0](LICENSE), the same license as FortnitePorting, which this project builds
on.

You are free to use, share and modify it, as long as any version you distribute
stays under the same license and keeps its source available.


## Credits

- @psdkyo - https://x.com/psdkyo
  Made the Blender add-on for the edited version of FortnitePorting that this
  project was based on. It made this converter possible. Thank you!

- FortnitePorting by h4lfheart (Half) and contributors
  https://github.com/h4lfheart/FortnitePorting
  This app grew out of FortnitePorting and still builds on its work. Thank you
  to everyone who works on it.

This project is not affiliated with or endorsed by Epic Games, FortnitePorting
or Unity. Fortnite is a trademark of Epic Games.


## What's new in this update

- Check Shaders now works on copies. Fixes are saved in Documents\Fortnite
  Unity Converter\Fixed Conversions (with each shader's textures), your
  original shaders are never changed, and no .bak files are written.
- With "Compile in Unity" on, Check Shaders compiles, fixes and compiles again
  until Unity reports no errors or no fix applies, then lists what it could not
  fix.
- New automatic fixes: too many interpolators in unlit shaders (the unused
  lightmap, ambient and vertex-light data is switched off first, then the
  target is raised if needed), properties Unity does not know, duplicate
  declarations, and loops Unity cannot unroll.
- The Blueprint Converter builds the prefab itself. After converting, it runs
  Unity hidden in a throwaway project and saves the prefab, its materials and
  their .meta files in Blueprint Conversions, so links to textures and shaders
  survive when you copy the folder into your project. The finished popup waits
  for Unity. Without a Unity editor, the prefab is built when you copy the
  folder into your project, as before.
- New Customizability tab in App Settings: the theme and every colour, corner
  roundness, UI scale, text size, spacing, font, and a Reset button.
- News & Updates shows three rows of cards.
