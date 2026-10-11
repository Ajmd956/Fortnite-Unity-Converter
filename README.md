# Fortnite Unity Converter (v1.0.0 early build)

Fortnite Unity Converter turns Fortnite materials into Unity shaders.
You give it a material path, it pulls the material's node graph and textures
out of FortnitePorting, and it writes a Unity .shader file (plus textures)
that you can drop straight into a Unity project.

IMPORTANT: this converter can make mistakes. If a shader doesn't look right,
you may have to edit it by hand. This will be improved in the future.


## How to install

1. Extract the zip. You get three things: Build.bat, README.md and an "app" folder.
2. Double-click Build.bat. A setup window with a progress bar opens and does
   everything for you. Don't close it until it says "All done!".
3. When it finishes, the app opens by itself. The folder now holds "Fortnite Unity Converter.exe"; run it any time after that.

The build needs Python and Node.js installed. The built-in API and the Files tab also need the .NET 10 SDK (dotnet.microsoft.com); if it is missing, the build downloads it for you; if that fails, the build still works but leaves the Files tab out. When it is done, the build deletes
the "app" folder and Build.bat, so only the exe and this readme are left. Run
"Build.bat keep" instead if you want to keep the build files; the exe is then
put in a "release" folder. If a build fails, nothing is deleted and the log is
at %TEMP%\FortniteUnityConverter_build.log.


## How to use it

- Convert: paste one or more material paths (one per line) and press CONVERT.
  The app starts its own API in the background (the sidebar shows API: ready
  when it's up), so FortnitePorting no longer has to be open. The first start
  reads the game files and takes a minute. The API needs the .NET 10 SDK at
  build time, like the Files tab.
- My shaders: lists everything you've converted.
- Check shaders: scans .shader files for common problems, fixes the safe ones,
  and can compile them in your Unity editor to catch what is left. A .bak copy
  of any changed shader is kept next to it.

Converted shaders are saved in "Fortnite Conversions", one folder each, with
everything they pulled in filed under "Cache". Both folders, and settings.json, live in
Documents\Fortnite Unity Converter, so updating or deleting the app never removes them.


## How it's made

- The window (tabs, settings, log) is an Electron app written in HTML, CSS and
  JavaScript.
- The conversion engine is a Python program, packaged into an EXE with
  PyInstaller. Electron runs it and shows its output live.
- The engine asks FortnitePorting's local bridge (default
  http://localhost:24320) for the material graph and textures, translates the
  Unreal material nodes into Unity shader code, and exports the textures.
- The shader checker has static checks and fixes, plus an optional loop that
  compiles the shader in Unity in batch mode, reads Unity's error messages,
  applies fixes and recompiles.
- Build.bat opens a progress window (app\build.ps1) that builds the engine and the Electron app and cleans up afterwards.

Not everything converts exactly. Unreal features with no Unity equivalent get
a stand-in, and the log warns you when that happens.


## Credits

- @psdkyo - https://x.com/psdkyo
  Made the Blender add-on for the edited version of FortnitePorting that this
  project was based on. It made this converter possible. Thank you!

- FortnitePorting by h4lfheart (Half) and contributors
  https://github.com/h4lfheart/FortnitePorting
  This app grew out of FortnitePorting and still builds on its work (its
  CUE4Parse fork, parts of its viewer and its audio decoders). It no longer
  uses FortnitePorting's servers. Thank you to everyone who works on it.
  The audio decoders radadec.exe and binkadec.exe (engine/tools) are taken from
  its repository.

This project is not affiliated with or endorsed by Epic Games, FortnitePorting
or Unity. Fortnite is a trademark of Epic Games.


## Curves

Materials that read colour or direction values from Unreal curve assets (curve atlas rows, curve floats) get them automatically: the app's API reads the curve out of the game files, saves it as JSON in the shader's Cache folder and bakes it into the shader. If the game files are not available, you can still export the curve as JSON from FModel (right-click the asset, Save Properties) and put the .json in the Curves folder inside Cache (named after the curve, for example CQ_FloatingPiecesVectorLUT.json). A curve that cannot be found either way is listed as APPROXIMATED with its name.


## v1.0.0 early build changes

- The app is no longer labelled Beta. This is version v1.0.0, an early build.
- Speed: the game file list is read in small pieces in the background, textures download and convert several at a time, and files are read without blocking the window, so the app opens faster and no longer freezes.
- Speed: the app is packed without compression, so the exe opens faster. The exe file is bigger as a result.
- Updates: the update window is styled like Fortnite Porting's, shows your version, the size and what changed, and downloads inside the app. It warns you first if a conversion is running. App Settings has a Check for Updates switch and a Check now button, and a failed check is retried a few minutes later.
- Loading screens: the Home and News & Updates pictures are real Fortnite loading screens from your game files, picked at random, never repeated on screen at the same time, and changed every 10 seconds. HUD tips and plain map art are no longer used. If no pictures load, the Console in App Settings says why.
- News & Updates: the cards fill two full rows of three.


## Performance

Textures download and convert several at a time, and the app reads files in the background instead of on its main thread, so it opens faster and doesn't freeze or show Not Responding while the Files list, shader list or previews load.


## Updates

When a newer version is published, the app shows an "Update" window with Cancel and Download, plus the new version's notes. Download replaces the app and reopens it. It checks a few seconds after opening and every few hours. The version it compares with is the one the app was built as.

To publish versions (one time): put these files in a GitHub repository (public, so the app can read it), then write that repository as "owner/name" in app/update.json. Every push to the main branch that changes the app folder builds the exe and publishes a release named v1.0.N (N is the run number); you can also run the Release workflow by hand and type a version such as 1.0.1. Every copy of the app that has this update code then offers it. Turn the check off with "checkUpdates": false in settings.json.

