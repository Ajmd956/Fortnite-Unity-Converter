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

- Speed: the game file list is now read in small pieces in the background, so the window no longer freezes ("Not responding") while the Files tab loads it. It starts loading a moment after the app opens, so the Files tab is usually ready when you click it.
- Speed: the app is packed without compression, so Fortnite Unity Converter.exe opens faster. The exe file is bigger as a result.
- Speed: picture and icon files are read without blocking the app, fewer pictures are kept in memory, and spell checking is off.

- Home and News & Updates pictures come from the loading screens in your game files again, not from fortnite-api.com. They are exported at up to 2048 pixels wide (before, a 2048 picture was cut down to 1024). Small framed icons (under 1000 pixels wide) are skipped.
- Clearing the cache is not needed: the new pictures are saved separately from the old ones.

- The app is no longer labelled Beta. This is version v1.0.0, an early build. Everything from the Beta118 entry below is included.

## Beta118 changes

- News & Updates cards are spread evenly across the row, the same way Fortnite Porting spaces them.

## Beta117 changes

- Home and News & Updates cards: the picture area is now 16:9, the shape of a loading screen, so the whole picture shows instead of a cropped strip. On a short window the Home pictures are limited in height and only trimmed slightly.

## Beta116 changes

- News & Home pictures: when no face is found, the picture is positioned so the top of the main subject stays in view, so heads are no longer cut off at the top.

## Beta115 changes

- News & Updates tab: the pictures now change every 10 seconds, like the Home cards.
- Pictures are downloaded at full size and kept in the cache, so they stay sharp.
- Each picture is positioned on its main subject, with faces kept in view, so people are not cut off.

## Beta114 changes

- Status bar: the API status (API: ready and so on) is now the text on the left, and the Log button sits on the right in its place.
- Log opens the Console in App Settings. Status messages, including exports, copies and API changes, are written there with the time, next to the conversion output.

## Beta113 changes

- News & Updates tab rebuilt: a featured banner on top, then a grid of cards, each with a picture, tag, date, short title and subtitle, and a Read More button that opens the details. The pictures are random Fortnite loading screens.
- The Home cards use the same short entries, and the update wording is shorter and says what was fixed.

## Beta112 changes

- Files tab icons: an icon that failed to load (for example right after switching or leaving a folder) is now retried up to four times instead of staying a placeholder until you reopen the folder. A failed asset is only skipped for a minute, not for the whole session.
- Home cards: the fallback pictures taken from the game files are now 1280 pixels wide instead of 640.

## Beta111 changes

- Home cards: pictures are Fortnite loading screens from fortnite-api.com (all of them, cached for a day). If the list can't be fetched, they fall back to the loading screens in your game files.

## Beta110 changes

- Hover card: no shadow and no fade-in, it appears at once.
- Home: only the Website button is left (the GitHub and FortnitePorting links are gone). Put your site's address in its data-url in src\index.html and add it to EXTERNAL_OK in main.js.

## Beta109 changes

- Home: the three cards show random Fortnite loading screens read from the game files. Each card changes every 10 seconds with a fade. If the game files are not loaded yet the cards keep the logo and try again.
- Files: the class line under each file name is gone (it is in the hover card), and names are white and medium weight like Fortnite Porting's.
- Hover card: smaller, with the name and the class below it, and it opens next to the pointer.

## Beta108 changes

- The app no longer uses FortnitePorting's API. Game keys come from fortnite-api.com and are saved in the cache (keys.json), so the app still starts when that site is down. To use your own keys, put a keys.manual.json next to it with the same layout.
- Mappings: a custom.usmap in the cache wins; otherwise the newest Windows mappings from Fortnite Central are downloaded once; otherwise the newest .usmap already in the cache is used. This part could not be checked from where it was written, so see the log if mappings are missing.
- Home: the update cards no longer download pictures from FortnitePorting's news feed.

## Beta105 changes

- Model Viewer: when a mesh has no render data, the "[diag]" line now also shows the real parse error (CUE4Parse normally swallows it), so the cause can be read off the screen.

## Beta99 changes

- Home: the update cards now show pictures, the same ones Fortnite Porting's Home page shows for its news. The app downloads them from Fortnite Porting's news feed when it starts and keeps a copy in Cache\\News, so they also show offline after the first time. If the download fails, the cards keep the logo.

## Beta98 changes

- Files tab: fixed textures (and mesh icons) sometimes never loading after you leave a folder and come back. Leaving a folder did not stop its thumbnail requests, so coming back asked for the same texture a second time while the first was still running. The second request deleted the first one's output folder and replaced its waiter, so the first never finished (using up one of the three thumbnail slots for up to five minutes) and the texture was then marked as failed for the rest of the session. Requests for the same file now share one run, and a file is only given up on after it fails twice.

## Beta97 changes

- Model Viewer and mesh icons: if the mesh object a file opens has no render data, the helper looks for another mesh in the same package that does. When a mesh still fails, the error text now ends with a "[diag]" line (class, whether render data is present, the package's exports) so the cause can be read off the screen.

## Beta96 changes

- Model Viewer and mesh icons: Fortnite Porting's own viewer builds meshes with mesh quality "All" and Nanite off, while this app used "Highest". A mesh that fails (for example "Mesh has no LOD data") is now retried that way, replacing the earlier Nanite retry. Needs the helper rebuilt, which Build.bat does.

## Beta95 changes

- Window buttons (main window and all viewers): hovering minimize, maximize or close no longer shows a name pop-up, the maximize icon no longer changes to a restore icon, and the main window's button group sits flush with the right edge like Fortnite Porting.
- Model Viewer: the Nanite retry for "Mesh has no LOD data" now also runs when the first export throws instead of reporting a failure. If the mesh really has no geometry in the game files, it still can't be shown.

## Beta94 changes

- Files tab: static and skeletal meshes now show a rendered picture of the mesh as their icon, drawn the way Fortnite Porting draws it (128 x 128, grey, lit from the camera side, same framing). A mesh that cannot be drawn keeps its type icon.
- Model Viewer: a mesh that failed with "Mesh has no LOD data" (a Nanite-only mesh) is now tried again with its Nanite geometry. This needs the .NET helper to be rebuilt, which Build.bat does.

## Beta93 changes

- The Model Viewer now loads the textures of the model's materials the way Fortnite Porting does: a diffuse, a normal and a specular-mask texture for each of up to four layers, found by Fortnite Porting's own parameter names. Materials that use layers pick the layer per vertex, and "Use 2/3/4 Layers" or "Use 2/3/4 Materials" switches set how many layers count. Texture parameters the material instance leaves empty are taken from its parent materials. A slot with no texture uses Fortnite Porting's default (grey diffuse, flat normal, no specular).
- The shader is Fortnite Porting's own: tangent-space normals, the same lighting and the same tone mapping. Back faces are no longer flipped, as in Fortnite Porting.
- The primary mesh card now uses Fortnite Porting's card gradient, and the disabled Select Animation button uses its 50% opacity.

## Beta92 changes

- New Model Viewer window for meshes, built from Fortnite Porting's own viewer code: same layout (3D view on the left, a card for the primary mesh and a Select Animation button on the right), same free-fly camera (field of view 70, speed 5, hold left or right mouse to look, W A S D move, Q / E down and up, the mouse wheel changes the speed between 3 and 150), same grid (1 m and 4 m lines, red X axis, blue Z axis, fades out at 250 m), same background colour and same lighting. Meshes with no texture are drawn in the same default grey.
- Opening several meshes at once puts them in one scene side by side along the X axis, like Fortnite Porting. Opening meshes again replaces the scene.
- Select Animation is shown but disabled: animation playback is not built yet.

## Beta90 changes

- Files tab: every file icon is now 80 x 80 with 8 px rounded corners and fills its square (type icons were 64 px and previews 88 px). Folder icons are unchanged.

## Beta89 changes

- Fixed the empty band at the top of the Files, My Shaders, Check Shaders and Settings pages (a stray closing tag in the Updates page closed the content area early). Pages fill the window again.

## Beta88 changes

- New Properties Viewer window with tabs, one per asset. It has line numbers, coloured JSON, indent guides and fold boxes (a folded object or list shows as {...}). Game paths in the text are coloured and clickable: clicking one opens that asset's properties in a new tab, and a path ending in a number scrolls to that object. Opening an asset that already has a tab selects that tab. A path that is not under /Game or /Engine is also tried under the game feature plugin folders, and the tab shows an error if no asset is found.
- The Volume slider in App Settings is reworked to match: 22 px thumb with a 10 px centre dot, grey track after the thumb, 21 tick marks lined up with the thumb positions, and the icon level with the track. The sound viewer slider uses the same style.
- Model previews are unchanged for now (the model viewer is next).

## Beta87 changes

- New Texture Viewer window with tabs: textures you open share one window, one tab each (opening a texture that is already open selects its tab). It has a Mipmap box, a Layer box and red, green, blue and alpha channel switches. Mipmaps are made from the exported picture, so mipmap 0 is the full texture; the Layer box stays at 0 because one layer is exported.
- New Sound Viewer window: a small player with play and pause, the current time, a seek slider and the total time. The sound plays as soon as it opens, and opening another sound plays it in the same window.
- Both windows follow the app's theme colours. Model and JSON previews are unchanged for now.

## Beta86 changes

- Colour parameters show the HDR colour picker (with the eyedropper) again, and keep Unreal's exact numbers. The slot stays a Vector, so Unity does not convert it from gamma to linear (the cause of the wrong colours in Beta81 to Beta83); a small drawer script, Editor/PorterColorDrawer.cs, draws it as a colour picker and writes the raw values back. The converter writes that script next to the shaders; keep it in your Unity project. Convert shaders again, and make new materials from them, or reset old ones.

## Beta85 changes

- Fixes the Files tab build error in Beta84 (CS0136: the variable name `first` was declared twice in one method in ApiHost.cs). No other changes.

## Beta84 changes

- Colour parameters are written as plain Vector slots with Unreal's exact numbers again (four number boxes, no colour picker). Unity converts Color properties from gamma to linear in Linear projects, which Beta81 to Beta83 did not account for, so colours came out wrong there. Vector slots are not converted, so they match in any project. Convert shaders again and make new materials from them, because old materials keep their old values.

## Beta83 changes

- Textures compressed in formats that need Detex (for example T_SpritesVar_Candy_Sugar_Mask, which Reaper uses) failed with "Detex decompression failed: not initialized" because the decoder was never started. The helper now starts it. Textures that failed before, in the converter and in the Files tab, should export; convert the shader again to get them.
- Transform nodes from Tangent space to Local or View space in the pixel stage are converted (through world space) instead of being passed through unchanged. Reaper's matcaps and the sprite shaders' Tangent to Local transform used these. Convert shaders again.

## Beta82 changes

- Files tab: texture files show a thumbnail of the texture, loaded as you scroll.
- Files tab: previewing and exporting a texture now goes through the same texture reader the converter uses for shader textures (the old exporter is only a fallback). HDR textures export as .hdr and cannot be previewed as a picture.

## Beta81 changes

- Colour parameters now keep Unreal's exact values. Unity does not convert colours marked [HDR] when it passes them to the shader, but the converter encoded every colour as if it did, so bright colours came out too dim (a glow of 30 arrived as 4.7) and dim ones too bright. Colours are written as plain Unreal values now. Shaders converted with an older version need to be converted again, and materials made from them should be recreated (or reset), because they still hold the old encoded numbers.
- When a texture can't be exported, the warning now says why (the error FortnitePorting's API returned), and the request is tried a second time first.
- Time-of-day parameters named TOD_ are hidden with the other in-game-only parameters.

## Beta80 changes

- Saturate nodes are now a plain saturate(). The previous version widened the ramp using fwidth(), which differed from Unreal at sharp edges (for example the column edges in the CheatMaster code rain) and changed with resolution and distance.
- Object-space X is now mirrored consistently: LocalPosition already was, but object normals, world to local and local to world transforms, and view to local conversions were not, so anything combining a local position with a local direction could come out mirrored. Shaders converted with an older version need to be converted again.

## Beta79 changes

- PorterObjectBounds.cs now flips the X bounds the same way the converted shader flips local X, so bounding-box based patterns (such as the CheatMaster code rain) line up on models that are not symmetric left to right. The script is rewritten the next time you convert a shader that uses bounds; then use Porter > Add Object Bounds To Selection again.

## Beta78 changes

- Custom code nodes that read the object bounds (GetPrimitiveData LocalObjectBoundsMin / Max) now use the shader's per-object bounds values instead of breaking, and Mod() is converted. This fixed the same "Unexpected token '}'" error on MI_Sprite_EightBitBlaster_CheatMaster.
- Shaders converted with an older version keep the error until they are converted again or run through Check shaders, which now repairs these nodes.

## Beta77 changes

- Custom code nodes (Unreal "Custom" material nodes) no longer break the shader. A node written as a bare expression gets its missing return, the camera and view matrices it reads (WorldToClip, ClipToView and similar) are mapped to Unity, and code with no Unity equivalent is replaced by 0 and reported as NOT CONVERTED, so the shader still compiles. This fixed "Unexpected token '}'. Expected one of: ',' ';'" on MI_Sprite_Vampire_LootHacker.
- Check shaders finds and repairs these broken Custom functions in shaders that were already converted.

## Beta76 changes

- Curves are fetched from the game files automatically and baked into the shader (see Curves below). Nothing needs exporting by hand any more.
- Files tab, Export to Unity: material files (parent materials, instances, functions) go into Convert and convert automatically. Anything else (textures, sounds, meshes, raw assets) shows "not available for a conversion to Unity yet" instead. Switch Export to Assets Folder or Custom Folder to export those.

## Beta74 changes

- Files tab: Export swaps to a Cancel button as soon as it is clicked (before the folder picker opens). Cancel stops the running export, a failed export no longer leaves Export stuck, the Export to bar matches the other buttons at rest, and the Files tab bottom bar, status bar and window buttons are aligned to Fortnite Porting's pixel positions.

## Beta73 changes

- Files tab: the file list builds by itself the first time you open Files. Refreshing it no longer restarts the API, sends you to Home or locks the other tabs.
- Custom Folder: exports ask for the folder every time and write the files straight into it, with no extra subfolder (the Fortnite Porting behaviour). The destination menus in settings show their icons.
- Setup window: matches the app (SF Pro font, app colours, own title bar), shows the app name and icon on the taskbar, and runs its steps without opening a console window.
- The build downloads a fixed version of CUE4Parse (commit 2e29798), so upstream changes can't break it.


## Performance

Textures download and convert several at a time, and the app reads files in the background instead of on its main thread, so it opens faster and doesn't freeze or show Not Responding while the Files list, shader list or previews load.


## Updates

When a newer version is published, the app shows an "Update" window with Cancel and Download. Download replaces the app and reopens it. It checks a few seconds after opening and every few hours. The version it compares with is the one the app was built as.

To publish versions (one time): put these files in a GitHub repository (public, so the app can read it), then write that repository as "owner/name" in app/update.json. To release, create a tag like v1.0.1 (or run the Release workflow by hand and type 1.0.1). GitHub builds the exe and attaches it to a release; every copy of the app that has this update code then offers it. Turn the check off with "checkUpdates": false in settings.json.

