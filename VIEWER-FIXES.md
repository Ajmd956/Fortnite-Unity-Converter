# Viewer fixes (measured from Fortnite Porting screenshots + source)

Screenshots were taken at editor zoom 0.68 (Sound Cue) and 0.945 (Material); the grid and text widths were used to get true zoom-1 sizes.

Node graph (Sound Cue + Material)
- Node box now includes the 2px border like Nodify: header 25px, rows 18px, single-row node 49px tall
- Texture Sample node ~335x173 (preview 128x128, 8px margin, 28px gaps), Output node 191x157
- Pins 14px (were 16), 12.2px from the node edge; port labels start 26.5px in, weight 400 (were 600)
- Wires: LightGray #D3D3D3 at 50% opacity, 3 world units thick and scaling with zoom (were bright, 1.5px fixed)
- Stage background #212024; comment header 29px / 14px text
- Initial view is zoom 1 centred on the average node position, like Fortnite Porting (Home does the same)

Chrome
- Tab strip starts 17px from the window edge (was 22px), in all five viewers
- Search box 32px tall (was 38px); list items 12px text, 8/16px padding, 4px pitch, secondary text #A8A4C0

Model viewer: card title uses #E2DFF0, cards spaced 12px.
Preview-type dialog (index.html): subtitle #A8A4C0, buttons 12px padding and weight 400.

Checked and left alone: Properties colours (identical to Json.xshd), Texture controls, Sound viewer, 3D camera framing.

Public build: the Particle Converter and Blueprint Converter buttons are grayed out and open a popup instead of switching, and the Files tab no longer sends Blueprints or Niagara systems to them. The two convert actions in main.js refuse to run as well. The switch is app/build-flavor.json: {"public": true} locks them, {"public": false} turns both converters back on for a private build. If the file is missing the app stays locked.

Experimental switches: with Enable Experimental Settings on, App Settings > Experimental has a Particle Converter and a Blueprint Converter toggle (settings expParticle and expBlueprint, both off by default), each with a warning under it. A converter is unlocked in the public build only while the master switch and its own toggle are both on, in the interface and in main.js. Turning the master switch off locks both again.
