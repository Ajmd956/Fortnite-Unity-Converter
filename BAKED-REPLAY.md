# Baked particle replay (ParticleFixes-3)

What it does: ribbons, per-particle lights, decals and sprites that read more than 4 Dynamic Parameter
values are recorded tick by tick from the Niagara replay (`ParticleBake.py`, file `<system>_<emitter>_<n>.bytes`,
format PRB1) and played back in Unity by `PorterParticleReplay.cs`. Everything else still goes through the Particle System.

PORTER_BAKE=auto (default) bakes only those; =all bakes every sprite too; =off bakes nothing.

Files: app/engine/ParticleBake.py, unity/PorterParticleReplay.cs (copied to <out>/Scripts), unity/PorterReplayBuild.cs
(copied to <out>/Editor). Copy the whole Blueprint Conversions folder into Assets as before.

Tested: the Python bake and spec on the 11 NS_SDVB dumps (tests/). NOT tested: the C# (not compiled, not run in Unity),
the shader change (second custom stream, PorterDP1 with 4 arguments), brightness of baked lights.

Age and random: baked sprites and ribbons carry the particle age and random number in the first free slots (_PorterAgeSlot, _PorterRandSlot).

Known limits: 8 Dynamic Parameter values per baked renderer (two vertex streams), the rest are logged NOT APPLIED;
ribbons use a camera-facing strip, UV tiling/facing/link order are not read; decals are flat quads;
mesh renderers are not baked (they keep the Particle System path).


## ParticleFixes-5: the Particle Converter plays the Blender add-on's replay

The Particle Converter (`NiagaraPorter.convert`, output in Particle Conversions) now bakes every sprite, ribbon, light, decal and
mesh renderer (PORTER_BAKE defaults to `all` there; `auto` and `off` still work) and builds a `PorterParticlePlayer` for each
(`Scripts/PorterParticlePlayer.cs`, the same code as PorterParticleReplay.cs under its own class name, so both converters'
folders can sit in one project). The Blueprint Converter still bakes in `auto` and leaves mesh renderers to the Particle System.

What the player now does, as the Blender add-on does:
- Meshes: the renderer's mesh copied to every particle with its rotation, scale and position; X along the velocity or to the
  camera for those facing modes; several meshes picked by each particle's mesh index; the renderer's override materials, a
  submesh each. The mesh's import needs Read/Write on (the builder turns it on). Meshes with no override materials draw with the
  default material (their own materials are not converted).
- Sprites: facing the camera, to the particle's own direction (custom facing vector), length along the velocity or the emitter's
  own vector; pivot; flipbook sub-images (SubImageSize, SubImageIndex).
- Ribbons: one strip per ribbon ID, points in link order (RibbonLinkOrder, else age), U along the ribbon by the renderer's UV0
  distribution (scaled, uniform, tiled, tiled from start), V across. NOTE: U now runs along the ribbon (Unreal's and Blender's
  way); files baked before this version ran V along it.

Tested: the Python bake and every baked file's layout on the SDVB dumps. NOT tested: any of the C# in Unity (not compiled or run).
Still not done: decals are flat quads (no projection in Built-in), ribbon facing modes and tube/multi-plane shapes, camera offset,
GPU emitters, and a Mesh renderer whose mesh the game sets through a user parameter (it is built as a Particle System).


## ParticleFixes-6: more of the Material Porter's replay behaviour

- Ribbons: Catmull-Rom smoothing (4 samples between points, colour/values blended), facing modes (0 view, 1 particle facing, 2 side vector),
  shapes (plane, multi-plane, tube via ribbonShape/ribbonSides), UV1 in the mesh's UV0.zw.
- Camera offset (sprites, meshes): drawn that far towards the camera.
- Bake fixes: visibility tag check, decal orientation (no zero quaternion), light colour (alpha scaling, ColorAdd).
- Bake of renderers enabled by system/emitter/user bindings is respected.

NOT tested: none of the C# was compiled or run in Unity (only syntax-checked); the bake was not run on real Niagara dumps here.
Still not done: decal projection, bound material parameters, light brightness calibration, GPU emitters.
