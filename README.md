# Fortnite Unity Converter

Fortnite Unity Converter turns Fortnite materials into Unity shaders. Pick a
material, convert it, and drop the finished shader and its textures into your
Unity project. It also converts sky domes, prop Blueprints and Niagara particle
systems into Unity prefabs.

IMPORTANT: this converter can make mistakes. If a shader or prefab doesn't look
right, you may have to edit it by hand. This will be improved in the future.


## Download

Get the latest "Fortnite Unity Converter.exe" from the Releases page on the
right. Run it. No setup is needed.

The app updates itself: when a new version is out, a window shows what changed
and you can update with one click. You can turn update checks off, or check
manually, under App Settings. When an update is found it downloads in the
background, using several connections at once, so installing it is nearly
instant. Set "prefetchUpdates" to false in the settings file to turn that off.


## What it does

- Files: browse your Fortnite files, preview textures, meshes, sounds, materials
  and more, and export them.
- Convert (Shader Converter): turn one or more materials into Unity shaders,
  with their textures.
- My Shaders: see everything you've converted.
- Blueprint Converter: turn a sky dome or a prop Blueprint (lights, sounds and
  particle systems, like the Reboot Van) into a Unity prefab. It saves the
  shaders, textures, meshes, sounds and day cycle in Blueprint Conversions. Copy
  that folder into your Unity project's Assets. If a Unity editor is found, the
  app builds the prefab for you first, in the background. The helper scripts
  ship as one precompiled library in a Shared folder (drag it in with the
  converted folders); the app builds that library once with your Unity editor and
  reuses it. Without a Unity editor the scripts ship as .cs files instead.
- Particle Converter: turn a Niagara system (NS_...) into a Unity prefab. It saves
  each system in its own folder inside Particle Conversions, with its materials,
  shaders, textures and meshes. If a Unity editor is found, the app builds the
  prefab for you first, in a hidden Unity project, and the folder holds no
  scripts: drag it into your Unity project's Assets.
- Fix Shaders: check shader files for common problems, fix the safe ones on copies, and test them in Unity.
  Fixes are made on copies in Documents\Fortnite Unity Converter\Fixed
  Conversions, so your originals are never changed.
- How to Use: a short video and five steps. New users see a welcome pop-up first.
- Sign In: sign in with Discord to show your picture and name in the app.

Your converted shaders and settings are saved in
Documents\Fortnite Unity Converter, so updating or deleting the app never
removes them.

Not everything converts exactly. Some Unreal features have no Unity
equivalent, so the app uses a stand-in and tells you when it does.


## Shader conversion

Convert reads a material's editor graph from the game files and writes a Unity
Built-in shader (Shader "UEPort/<name>") with its textures.

- Parameters keep Unreal's values. A material instance's own values are written
  as the shader's defaults, so the instance converts as it looks in the game.
  Colours are stored so that Unity's colour pickers give back Unreal's linear
  value, and world-space values (positions, camera distance, depth fade) are
  scaled for Unreal's centimetres.
- Blend mode, shading model, two-sided and mask clip set on the master or on the
  instance are read, and masked shaders get a hidden Mask Clip value.
- Textures are exported to the shader's Textures folder next to a
  <name>.textures.json that says how each one must be imported; the Porter
  Texture Setup script applies it. With a Unity editor installed, that script and
  the object bounds script ship precompiled in a Shared folder next to the
  shaders; otherwise they ship as .cs files in Editor.
- Metallic and smoothness get multiplier and offset sliders and ambient occlusion
  and cavity get sliders, all neutral by default, so a conversion looks the same
  until you move them.
- Some numbers differ between the engines by nature, for example emissive
  brightness (Unreal's auto exposure and tonemapper against Unity's Built-in
  pipeline and post effects) and the specular response of lit materials. Those
  are not calibrated automatically yet; the app does not guess them.


## Game files and mapping files

The app reads the game through a built-in helper. Newer game versions store
object properties in a form that needs a mapping file (.usmap) to be read.

- The Latest (Installed) profile gets its keys and mappings from the service
  automatically.
- For another version, make a Custom profile. Fetch Data downloads the keys and,
  when one exists, the mappings for that version. Some versions have no mapping
  file online.
- You can supply the file yourself: pick it in the profile's Mappings File
  setting, or save it as custom.usmap (or custom.<version>.usmap) in
  Documents\Fortnite Unity Converter\Game Files.
- Without a mapping file the helper still starts and says so. A material that
  needs the mapping file fails with a message that names it, and the log shows the
  helper's own reason for any other failure.


## Older game versions

Older builds do not ship a material's editor graph, so there is nothing to
translate. For those materials the converter builds an approximate shader from
what the game files do hold: blend mode, shading model, two-sided and mask clip,
the instance's scalar, vector and texture values, and the textures the material
references.

- Each texture gets a role from its parameter name or, failing that, its asset
  name (Diffuse or _D, Normals or _N, SpecularMasks or _S / SRM / ORM, Emissive,
  Opacity). The first texture for a role is used; extra layers (SpecularMasks_2
  and so on) and textures with no known role are listed and left out.
- A packed mask texture is read as Specular in red, Metallic in green and
  Roughness in blue. With no mask texture, Metallic and Roughness are sliders.
- Transparency comes from the opacity texture, or from the base colour texture's
  alpha for masked and translucent materials. Unlit materials draw their
  emissive (or base colour) texture as emission.
- The result is marked APPROXIMATE in the output panel and in the shader's
  .textures.json. Panners, masks, blends and custom effects from the original
  graph are not reproduced, so standard props and characters should come out
  close and effect materials will not.


## Particle Converter

The Particle Converter has its own engine (ParticleConverter.py). A converted
system is made only of Unity's own components, with no scripts, so it works
where scripts are not allowed, such as VRChat avatars.

How a system is converted:

- The system is played in the converter for a few seconds and what each emitter
  does is recorded: spawning, lifetimes, colour and size over age, speed,
  rotation and the values each renderer reads.
- Every renderer becomes a Unity Particle System:
  - Sprites become Billboard (or Stretched Billboard when velocity aligned)
    particles, with Texture Sheet Animation for sub-images.
  - Meshes become Mesh particles, one submesh per override material.
  - Ribbons become the Trails module in Ribbon mode, with the width over the
    trail read from the replay.
  - Lights become the Lights module, with a light prefab of its own saved next to
    the system (<emitter>_Light.prefab). A light exists for each live particle,
    up to the module's Max Lights, and its intensity follows the colour scale.
  - Decals become Horizontal Billboard particles lying flat. A Particle System
    cannot project onto surfaces, so a decal does not wrap over uneven ground.
- The particle colour (including HDR values above 1) and up to four Dynamic
  Parameters are sent to the converted shaders through the particle vertex
  streams (_PorterParticleStreams = 1).
- Scripted emitters are built from a replay of their scripts: spawn counts,
  lifetime, colour and size curves, and starting position, velocity and rotation
  as ranges.

Where the files go:

- Each system is saved in its own folder inside Particle Conversions, with its
  prefab, materials, shaders, textures, meshes and their .meta files. A Shared
  folder holds the fallback shader the materials may use.
- If a Unity editor is found, the app builds the prefab for you first, in a
  hidden Unity project that holds all the helper scripts. The finished folders
  contain no scripts: drag each converted folder and the Shared folder into your
  Unity project's Assets.
- If no Unity editor is found, or a prefab could not be built, the folders also
  hold the helper scripts and Unity builds the prefab when they are copied in.

What the converter tells you instead of guessing (notes in the output panel and
the .propreport.txt next to each system):

- Emitters whose scripted particles have no Age or Lifetime cannot have colour or
  Dynamic Parameter curves over age read, and say so. Stateless emitters are read
  from their own settings instead.
- Colour and Dynamic Parameter curves need particles that lived out their whole
  life. When fewer than 8 did, the system is played again for longer (the longest
  lifetime plus 2 seconds, at most 30) only to read those curves. Values that
  still never finish a particle's life are not carried, and say so.
- Particles that never change get a constant curve.
- Renderers that read more than 8 Dynamic Parameter values keep the first 8 (two
  vertex streams); the rest are listed as NOT APPLIED.
- Emitters it cannot convert (for example GPU emitters, or emitters that need
  a vector field that is not in the export) are listed as not converted. GPU
  emitters are built from their CPU scripts.
- Meshes or materials that are missing from the export are named.

Every Particle System renderer gets Max Particle Size 555 (Unity's default of 0.5 made particles shrink when the camera came close).

Material instance overrides: blend mode, shading model, two-sided and mask clip set on the material instance are read, so an instance that overrides its parent (for example translucent to opaque) converts as in Unreal. Masked shaders get a hidden Mask Clip value.

Known limits: material parameter bindings, user-parameter materials, UV scale,
sub-UV blending, ribbon twist and tension, ribbon IDs, camera distance culling
and a decal material's own graph are not read; light brightness is not
calibrated against Unreal; a Mesh renderer whose mesh the game sets through a
user parameter is built as a Particle System; shaders that use dynamic
parameters must be converted again after the converter is updated.


## Blueprint Converter

- Sky domes: the sky follows the camera, the camera's far clip is raised to reach
  the clouds, and a Time of Day controller drives the day cycle.
- Props: meshes and materials, headlights and other lights, every sound cue
  (volume, pitch, looping, distance falloff, exported as WAV) and Niagara
  particle systems. Sky-light and post-process components are left out because
  Unity has no equivalent.
- Meshes arrive in Unity mirrored in X, so the converter mirrors where needed.


## Public build and experimental switches

In the public build the Particle Converter and Blueprint Converter buttons are
grayed out and open a popup instead of switching, the Files tab does not send
Blueprints or Niagara systems to them, and the two convert actions refuse to
run. The switch is app/build-flavor.json: {"public": true} locks them,
{"public": false} turns both back on for a private build. If the file is
missing the app stays locked.

With Enable Experimental Settings on, App Settings > Experimental has a Particle
Converter and a Blueprint Converter toggle (settings expParticle and
expBlueprint, both off by default), each with a warning under it. A converter is
unlocked in the public build only while the master switch and its own toggle are
both on. Turning the master switch off locks both again.


## Viewers

The Files tab previews textures, meshes, sounds, materials and node graphs
(Sound Cue and Material) in a layout matched to Fortnite Porting.


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
