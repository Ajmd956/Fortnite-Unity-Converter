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
  that folder into your Unity project's Assets and Unity builds the prefab.
- Check Shaders: scan shader files for common problems and fix the safe ones.
  A .bak copy of any changed shader is kept next to it.

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


Sky fix v7: the model exporter now writes one material slot per mesh section, so a sky dome and its sun/moon quad import as separate submeshes. engine/tools/fbx_add_material_slots.py adds the slots to an FBX that was exported earlier (it keeps a .bak copy).

Sky fix v8: the sky shader now has its own atmosphere (single scattering with Unreal's default Sky Atmosphere values) instead of reading Unity's skybox probe, so day, dusk and night come from the sun and moon directions. The Time of Day controller sets those directions from the day data (sunrise and sunset are the hours the Moon Color curve switches off and on; Sun Height is an estimate because the real sun track is not in the export). The night and cloud exposure multipliers are gone. The sun's highlight roll-off is built in, and the sky rig's far clip now reaches the dome and the sun. Reconvert the sky Blueprint to get the new shaders and scripts.

Sky fix v23: the filmic tone curve in the sky shaders was missing the "- 1" in its toe calculation and the toe/shoulder blend flip that Unreal's FilmToneMap uses, so mid grey 0.18 came out at 0.64 and everything under 0.03 became pure black (black night, hard black patches, oversaturated galaxy). Fixed: 0.18 now maps to 0.18. Sun Brightness and Moon Brightness go back to 12 and 12 (they had been lowered to 2.8 and 1.3 only to hide the broken curve). 12 is the Sun light's intensity in Unreal; with the noon values and the fixed curve, the shader's noon sky lands close to the Unreal shot. Reconvert the sky Blueprint to get the new shaders. Not changed yet: the atmosphere itself (planet radius 6371, multi scattering factor 2, transmittance minimum light elevation 9.5) and night exposure, which still need the time of day of the Unreal reference shots.

Sky fix v25: reconverting a Blueprint skipped every material whose .shader already existed, so the sky, sun/moon and cloud shaders kept the old broken tone curve (blocky galaxy, black holes at night) even after the v23 fix. Sky, sun/moon and cloud shaders are now always regenerated. If the night sky still looks like v22, delete the old Shaders folder of the sky Blueprint in Unity once, then reconvert.

Sky fix v26: Porter Sky Lighting has a new Ambient Saturation slider (default 0.4). The ambient light that lights characters and props came straight from the sky colour, which at night is strongly purple and blue and tinted skin. Lowering the slider keeps the brightness and removes the tint. Set it to 1 for the old behaviour. VRChat note: the Time of Day controller, sky rig, sky lighting, height fog and object bounds are scripts, which VRChat strips from avatars and worlds, so in VRChat only the converted shaders and particle systems work, frozen at their default values.

Sky fix v27: Porter Height Fog now looks for the Time of Day controller and sky rig again once a second whenever either reference is missing (after a reconvert they pointed at the old, deleted objects, so the fog lost the day data and fell back to Max Opacity and Brightness, which can be 0 on older components). If Max Opacity or Brightness show 0, use the component's menu, Reset, to restore 0.012 / 0.05 / 0.3 / 1.

Sky fix v28: Porter Sky Lighting hands the Environment Lighting Source back to Unity when Drive Ambient is turned off or the component is disabled (it sets it to Skybox). While Drive Ambient is on, the script rewrites the ambient light several times a second, which is why the Source box in the Lighting window shows blank and cannot be changed.

Sky fix v29: the sky shader's ozone layer now uses the day data's Other Tent Distribution (Tip Value 2, Width 20 in the Fortnite sky) instead of a fixed 1 and 15. Ozone is what turns the upper sky deep violet and blue at dusk, so the zenith should be darker and more saturated, closer to Unreal. Reconvert the sky Blueprint to get the new shaders (the sky shaders are now regenerated every time).

Sky fix v30: the sky now uses the day data's Multi Scattering Factor (2 in the Fortnite sky) for its second-order scattering, which brightens the dusk and night sky towards Unreal. The sun light's colour now clamps the elevation used for its transmittance to Transmittance Min Light Elevation Angle (about 9 degrees), so the sun light does not go as red and dim at the horizon, as in Unreal. Reconvert the sky Blueprint to get the new shaders.

Sky fix v31: the baked cloud shaders now use the same filmic tone curve as the sky dome (including its gamma correction for projects set to Gamma colour space) instead of a separate highlight limiter. The clouds were not gamma corrected, so in a Gamma project they came out much darker than the sky. Reconvert the sky Blueprint to get the new cloud shaders.

Sky fix v32: Cloud Exposure now defaults to 4 (it was 1). With the shared tone curve, 4 matches Unreal's noon clouds, and the dim dusk cloud colours in the day data need the same lift. Existing cloud materials keep the value they already have.

Sky fix v33: the baked cloud shaders roll very bright, saturated colours back toward white and peach (Unreal's tonemapper does this, the per-channel curve did not), which removes the neon orange-red rims at dusk. The Unreal cloud material already has its own Color Boost of 4, so Cloud Exposure stacks on top of it; lower Cloud Exposure if the clouds look too hot at any hour. Reconvert the sky Blueprint to get the new cloud shaders.

Sky fix v34: Cloud Exposure defaults back to 1. With the material's own Color Boost of 4, an exposure of 4 made a 16x total that pushed every hour into the tone curve's white shoulder, so clouds stayed white as the time of day changed. Materials already in Unity keep their saved value: set Cloud Exposure to 1 on the cloud materials, or delete the Shaders and Materials and reconvert.

App v35: Shader, Particle (Niagara) and Blueprint converters now live under one Converter tab, switched with three buttons at the top. The Blueprint converter now shows the "finished" popup, and the popup for every converter shows the output folder path. App Settings has a new Conversion Cache row with a Clear cache button (it empties the Cache folder only, not your converted shaders, particles or Blueprints). Includes the v34 Cloud Exposure default of 1.

App v35 also: the "already running" window uses the new look and brings the open window to the front, and News & Updates has new entries for this release.

Sky fix: the clouds' Direct Sun Light Color (any game-wide property named like _C_..DirectSunLight..Color) is not animated in the day data, because Unreal sets it while the game runs. The Time of Day controller now sets it from the sun light after the atmosphere (white at midday, warmer toward the horizon), so cloud colours follow the time of day on every converted sky dome. Cloud Sun Color Strength on the controller tunes it (0 = white). Reconvert the sky Blueprint to get the new script.

App v36: the Blueprint Converter now converts prop Blueprints, not only sky domes. A prop's meshes and materials, spot and point lights, sound cues (volume, pitch, looping, attenuation) and Niagara systems become one Unity prefab, built automatically when the folder is in your project. Sounds are exported as WAV files into the Blueprint's Audio folder, and a .propreport.txt next to the prefab lists everything removed, defaulted or not applied. Niagara drag (applied as exp(-drag*age)), light intensity (passed through unscaled; PorterPropBuilder.cs has a LightIntensityScale at the top) and mesh particle orientation are not verified against Unreal.
