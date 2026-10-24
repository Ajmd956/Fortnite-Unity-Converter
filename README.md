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
manually, under App Settings.


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
  app builds the prefab for you first, in the background.
- Particle Converter: turn a Niagara system (NS_...) into a Unity prefab. It saves
  each system in its own folder inside Particle Conversions, with its materials,
  meshes and baked particle data. Copy that folder into your Unity project's
  Assets. If a Unity editor is found, the app builds the prefab for you first, in
  a hidden Unity project; otherwise Unity builds it when the folder is copied in.
- Check Shaders: scan shader files for common problems and fix the safe ones.
  Fixes are made on copies in Documents\Fortnite Unity Converter\Fixed
  Conversions, so your originals are never changed.

Your converted shaders and settings are saved in
Documents\Fortnite Unity Converter, so updating or deleting the app never
removes them.

Not everything converts exactly. Some Unreal features have no Unity
equivalent, so the app uses a stand-in and tells you when it does.


## Particle Converter

The Particle Converter has its own engine (ParticleConverter.py) and its own
Unity scripts. Its scripts use their own class names (PorterParticleBuilder,
PorterBakedParticleSystem, PorterParticleReplayBuild, PorterParticleSupport,
PorterParticleDecalProject, and renamed copies of the shader helper scripts), so
Particle Conversions and Blueprint Conversions can sit in one Unity project.

How a system is converted:

- The system is played in the converter for a few seconds and what each emitter
  does is recorded: spawning, lifetimes, colour and size over age, and the values
  each renderer reads.
- Whatever a Unity Particle System can draw is built as a Particle System. The
  particle colour (including HDR values above 1) and up to four Dynamic
  Parameters are sent to the converted shaders through the particle vertex
  streams (_PorterParticleStreams = 1).
- Everything else is recorded tick by tick (the baked replay, files
  <system>_<emitter>_<n>.bytes, format PRB1) and played back in Unity by
  PorterBakedParticleSystem (Baked Particle System in the Add Component menu). The Particle Converter bakes every sprite, ribbon, light,
  decal and mesh renderer it can. PORTER_BAKE=all (the default here), auto
  (only what a Particle System cannot draw) and off (nothing) are supported. The
  Blueprint Converter bakes in auto and leaves mesh renderers to the Particle
  System.

Baked Particle System modules (every one has an on/off box and does nothing
while off; defaults leave the baked look unchanged):

- Main, Emission, Shape, Velocity over Lifetime, Force over Lifetime, Color over
  Lifetime, Size over Lifetime, Opacity over Lifetime, Rotation over Lifetime,
  Noise, Simulation.
- Size by Speed and Color by Speed: a curve or gradient read from each
  particle's baked speed over a speed range.
- Custom Data: a multiply and an add for each Dynamic Parameter slot.
- Texture Sheet Animation (sprites): tiles, and the frame as baked, over
  lifetime, fixed or random, with a start frame.
- Meshes: a scale and a rotation for each mesh of a mesh renderer.
- Renderer: material, facing, sort mode, cast and receive shadows, sorting
  order, light probes, motion vectors. Lights get intensity, range scale,
  shadows and render mode.
- Only controls that act on the baked data are shown.
- Prefabs built before this version use a thin PorterParticlePlayer class that
  inherits all of it, so their saved settings carry over.

What the player does:

- Plays in play mode and in edit mode. In the editor it animates in the Scene
  view while the prefab sits in a scene, and billboards face the Scene view
  camera. In edit mode the meshes, helper components, decal material and lights
  it creates are not saved into your scene or prefab.
- Meshes: the renderer's mesh copied to every particle with its rotation, scale
  and position; X along the velocity or to the camera for those facing modes;
  several meshes picked by each particle's mesh index; the renderer's override
  materials, a submesh each. The mesh's import needs Read/Write on (the builder
  turns it on). Meshes with no override materials draw with the default
  material.
- Sprites: facing the camera or the particle's own direction, length along the
  velocity or the emitter's own vector, pivot, flipbook sub-images, camera
  offset.
- Ribbons: one strip per ribbon ID, points in link order (else age), smoothed
  through its points, facing modes (view, particle facing, side vector), shapes
  (plane, multi-plane, tube), U along the ribbon by the renderer's UV0
  distribution and V across.
- Lights: a pool of point lights, one per live light particle.
- Decals: projected onto the scene by Porter/DecalProject (Scripts), using the
  decal material's texture and tint (not its graph). Needs the camera depth
  texture; without the shader, or with projectDecals off, decals are flat quads.
- Baked sprites and ribbons carry the particle's age and random number in the
  first free vertex stream slots (_PorterAgeSlot, _PorterRandSlot).
- A warning is written to the Unity console when a player has no baked data
  assigned.

What the converter tells you instead of guessing (notes in the output panel and
the .propreport.txt next to each system):

- Emitters whose scripted particles have no Age or Lifetime cannot have colour or
  Dynamic Parameter curves over age read, and say so. Stateless emitters are read
  from their own settings instead.
- Colour and Dynamic Parameter curves need particles that lived out their whole
  life. When fewer than 8 did, the system is played again for longer (the longest
  lifetime plus 2 seconds, at most 30) only to read those curves; the baked replay
  keeps its length. Values that still never finish a particle's life are not
  carried, and say so.
- Particles that never change get a constant curve.
- Renderers that read more than 8 Dynamic Parameter values keep the first 8 (two
  vertex streams); the rest are listed as NOT APPLIED.
- Emitters it cannot convert (for example GPU emitters, or emitters that need
  a vector field that is not in the export) are listed as not converted. GPU
  emitters are left as still pieces, as the Material Porter does.
- Meshes or materials that are missing from the export are named.

Scripted emitters stay Particle Systems by default. Set PORTER_BAKE_SCRIPTED=1 to play their sprites and meshes from the baked replay instead (PORTER_BAKE_MESHES=0 keeps meshes as Particle Systems, PORTER_BAKE=off bakes nothing). Baked meshes are made readable automatically. The baked mesh path has not been run in Unity.

Converter Options (the gear at the right of the Converter tab, or Converter Options in the sidebar) has a Bake Scripts switch (on by default), next to Fix Problems and Compile in Unity. On sets PORTER_BAKE=all and PORTER_BAKE_SCRIPTED=1 for that conversion; off sets PORTER_BAKE=auto and PORTER_BAKE_SCRIPTED=0, so only what a Particle System cannot draw is baked. Baked replays show a Particle System style inspector (playback bar, Main, Color, Size, Renderer, Data).

Every Particle System renderer gets Max Particle Size 555 (Unity's default of 0.5 made particles shrink when the camera came close).

Body anchor: every particle system has a PorterParticleBodyAnchor. Drop the character's body or a bone into Body (or type its name in Body Name) and the whole system is parented to it at zero, so it sits where the Unreal system sat on the rig.

Material instance overrides: blend mode, shading model, two-sided and mask clip set on the material instance are read, so an instance that overrides its parent (for example translucent to opaque) converts as in Unreal. Masked shaders get a hidden Mask Clip value.

The ParticleMeshes folder in Unity only holds meshes Unity generates; exported meshes are in Meshes.

Known limits: bound material parameters and a decal material's own graph are not
read; light brightness is one number (lightScale on the prefab) and is not
calibrated against Unreal; a Mesh renderer whose mesh the game sets through a
user parameter is built as a Particle System; shaders that use dynamic
parameters must be converted again after the converter is updated.

Testing: the Python side (the replay, the bake, every baked file's layout and the
generated spec) was run through the real conversion path on the NS_SDVB_Body and
NS_SDVB_Head dumps; only IdleCubes changed from the last version, and its
baked files are identical. The C# scripts have only been
syntax-checked; they have not been compiled or run in Unity here, so check a
converted prefab in Unity before relying on it.


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

The Files tab viewers were matched to Fortnite Porting by measuring screenshots
and its source.

- Node graph (Sound Cue and Material): node box with the 2px border, header
  25px, rows 18px, Texture Sample node about 335x173, Output node 191x157, pins
  14px, port labels weight 400, wires LightGray at 50% opacity and 3 world units
  thick scaling with zoom, stage background #212024, initial view at zoom 1
  centred on the average node position.
- Chrome: tab strip 17px from the window edge, search box 32px tall, list items
  12px text with 8/16px padding and a 4px pitch, secondary text #A8A4C0.
- Model viewer: card title #E2DFF0, cards spaced 12px.
- Preview-type dialog: subtitle #A8A4C0, buttons 12px padding and weight 400.


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
