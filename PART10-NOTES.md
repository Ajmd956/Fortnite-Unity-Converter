# part10

Changes on top of part9. None of it has run in Unity or the Electron app; Python was compiled and the new baker fields were run against stub data, the C# files were bracket-checked only.

- Light inverse-squared falloff is now passed to Unity (`inverseSquared`), and such lights skip the exponent-to-range adjustment.
- Sprites: per-particle Pivot Offset binding is baked (`pivotOff`) and added to the renderer's pivot.
- Renderer Cast Shadows (`bCastShadows`) is passed on; the player's Cast Shadows starts On when it was set.
- Sprites and meshes: camera distance culling (`bEnableDistanceCulling`, `DistanceCullRange`) is baked as `cullMin`/`cullMax` metres, applied hard, with a "Camera Distance Cull" control in the Renderer section.

Not done: material parameter bindings, user-parameter materials, UV scale, sub-UV blending, ribbon twist/tension. The Niagara property names for the culling range are the ones I expect from Unreal's renderer properties; a dump that uses them would confirm.
