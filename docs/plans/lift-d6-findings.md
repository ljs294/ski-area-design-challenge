# Sessellift D6: adversarial review findings

**Audience:** the project owner and coding agents. **Status:** findings for the owner's review; nothing fixed yet.
**Date:** 2026-10-21. **Task:** lift models 2, project 1 (the Sessellift D6 detachable six-seat lift: two terminals, the
six-seat chair and its detachable grip, the wide tower heads). Built with the
[lift asset pipeline](../../tools/assets/lifts/README.md) under the [model contract](p3-07-model-contract.md).

**How it was reviewed.** Seven hostile reviewers, each told to find fault and to fix nothing:
- Round 2 checked the rebuilt models against the owner's 1:50 layouts: the return, the drive, and the grip and chair.
- Round 3 judged realism against photos of the built lift and the in-game look: the terminals, the tower heads, the
  whole lift as one machine, and the chair and grip.
  - They read the game's shader, importer and LOD distances.
  - They built every model in Blender and swept a chair through about 15,000 poses in the stations and through the
    heads, checking every part the chair touches.

Their reports and evidence crops stay in the local reference folder outside the repo, because they show the real lift.
Each finding below cites its reviewer ids, so its evidence can be traced there: P1T terminals, P2T towers, P3C chair,
P5 whole lift, V1 return, V2 drive, V3 grip.

**Severity:**
- **S1:** a player notices it at normal game distances, or it departs clearly from the real lift.
- **S2:** noticeable up close or in the Lift Lab.
- **S3:** detail or polish.

## Verdict

**Not ready for Gate 2.** There are 74 findings (16 S1, 38 S2, 20 S3), plus 5 review-tooling items and 16 decisions
for you. These are right and should stay:
- The massing: the 21.9 m glazed capsule, the visor rings, the white underbody, the masts.
- The glazing layout, the chair's height and seat count, and the grip's measured layout.
- The rope and rail geometry, and the order in which a chair detaches and re-attaches.

Two kinds of problem stand in the way:
- **Parts run through each other.** Every tower's crossarm runs through the rope and every grip. In both stations the
  chairs pass through the rail's hangers, the horns, the floor and the drive's bend-sheave brackets.
- **It reads as a blockout, not the glossy station in the photos.** Flat-shaded, tiled, matte glass, a black slab of a
  roof. The level of detail players see most (LOD2) keeps the least of the design.
- **The chairs don't read in the game.**
  - Under the default Snow layer the seats turn white, so the line nearly disappears against the snow. Every chair
    family does this today, not only the D6.
  - The real chair's galvanised back is black on ours.
  - The level most chairs on a line use (LOD2) has no backrest.
  - Chairs are culled past 300–900 m while the towers stay, so the far half of a line has none.

Most of the look is free in triangles (shading, materials, textures, snow values). The budget has room for the rest:
- terminals about 25k / 6.5k / 2.2k spare at LOD0 / LOD1 / LOD2;
- heads about 10k / 3k / 700;
- the chair has almost none; its budgets need raising first (decision 13).

## The ten that matter most

1. **H1: the crossarm runs through the rope and every grip** on the s4, s6, b8 and c8 heads, so chairs would pass
   through every tower. On the real head the crossarm stops short of the rope.
2. **C1: the seats turn white in the game's default view.** The Snow layer is on by default, and the seat pads take
   full snow, so the line nearly vanishes against the snow. Every photo of the real lift is a snowy day with black
   seats. Every chair family does this (decision 16).
3. **L1: the glass reads as flat blue-grey panels.** The real glass is mirror-black, with the far side's ribs crossing
   behind it. The game's glass shader scales its own reflection down.
4. **L2: the roof is a dead black slab.** The real roof is a silver ribbed band with lifeline posts. It is the largest
   surface the game's camera sees.
5. **S1: the rail's hangers stand in the grips' path:** 14 posts per terminal that every chair passes through.
6. **H2 and H3: the head's frame and walkways are wrong.** The lifting frame has posts that don't exist, braces raked
   about 40° (the real ones are near vertical), and a beam past the ropes. The real head reads as an "H" of walkways
   along the sheave trains with railed cages on top; ours is a thin T.
7. **L3 and L4: flat shading and a 1 m seam grid** make the hood look faceted and hammered. Zero triangles to fix.
8. **What players see most keeps the least.**
   - The terminals' LOD2 merges the window ribs into one band, and LOD3 has no glass (L8).
   - The chair's LOD2, the level most of a line uses, has no backrest (C3).
   - Chairs are culled past 300–900 m while towers stay to 1.6 km and more (C4).
   - The chair's back is black where the real one shows galvanised steel (C2).
9. **S5, S6, S7: station clashes.** Solid horns swallow the hanger's guide roller, the drive's bend-sheave brackets
   run through the rope, and the floor cuts the hangers in the loop.
10. **G1 and G2: the game would draw it wrongly** as wired today: red livery, chairs riding round inside the white
    tub, no footings. This goes to the integration task.

Three drawing departures are also S1: the drive's portal layout (S2), the return's "carriage" that is really a footing
outline (S3), and the drive's belly back (S4).

## A. Wide tower heads

Current triangles: 7.1k–13.4k / 2.2k–3.9k / 416–664 / 72–96 against the 24k / 7k / 1.4k / 280 budget.

| # | sev | finding | fix | conf | source |
|---|---|---|---|---|---|
| H1 | S1 | **The crossarm runs through the rope and every grip.** It reaches 450 mm past each rope, but the rows hang only 320 mm under it. So on s4, s6 and b8 the rope's top is 82–147 mm inside the beam, the inner sheaves 26–60 mm, and every grip passes wholly through it (210 mm, at every tilt). On the c8 the upper (hold-down) row sits 133 mm inside it. The grip's springs clear it by 5 mm on a level rope and cut in 7 / 33 / 57 mm at 10 / 20 / 30°. Only the d8 is clear. On the real head the crossarm ends at the train, inboard of the rope; the trains bolt to its end and the grips pass outboard. Ours is about 6.5 m wide against a real 5.8–5.9 m. | End the crossarm on the rows' lug line, about 150–400 mm inboard of the rope, and bolt each row to its end face. Keep the lifting beam reaching over the ropes. Add a build check that sweeps the grip's envelope along both ropes through every head at ±30° and fails under 50 mm (R4). | high | P2T-01, P5-01, P5-02 |
| H2 | S1 | **The lifting frame has the wrong parts and proportions**, at the top of the silhouette. (1) Two heavy posts the real head doesn't have. (2) Braces raked about 40°; the real struts are near vertical (about 12°), tapered plates 0.20 → 0.33 m wide. (3) Our beam spans ±3.25 m; the real one ends over the ropes (±2.87 m), with a double clevis lifting lug at each end. (4) Beam depth 0.30 m against about 0.22. The frame's height (1.5 m) and its feet (±1.9 m) are right. | Delete the posts. Make the struts tapered plates from ±1.95 m at the crossarm to ±2.25 m under the beam. Beam half-length = gauge/2 + 0.07 m, depth 0.22 m. Add clevis lugs under each end and a tie-off rail under the beam. | high (posts, rake, extent); medium (sizes) | P2T-03 |
| H3 | S1 | **The access layout isn't the photographed one.** Ours is one 0.9 m deck on the downhill face, hung at the crossarm's underside (so the crossarm stands as a 0.46 m wall beside it), and a ladder that ends in grab rails. Every real head has three things: a grated walkway beside each sheave train, inboard of the rope, as long as the train and sloped with it, with railings on both sides; two small railed cages on top of the crossarm; and the ladder continuing up the centre to the lifting beam. Side-on, the commonest game view, the real head reads as an "H", ours as a thin T. | Per rope, a walkway about 0.7 m wide, 0.4–1.1 m inboard of the rope and 0.4–0.6 m below it. It runs the train's chord plus 0.3 m along the chord, with rails. Two cages, about 1.2 × 0.8 m, on the crossarm's top at v ±0.5…1.7 m, with 1.1 m hoop-topped rails. The ladder runs on to the beam at v 0. All of it stays outside the grip's envelope (H1's check). Cost +1.5–2.5k LOD0, +0.5k LOD1, two slabs at LOD2. | high (layout); medium (sizes) | P2T-02, P5-20 |
| H4 | S2 | **Hold-down rows (d8 and the c8's upper row): the grip's lever stands 105–110 mm up into the sheaves.** The jaw's top is 68–73 mm in and the guard cones 31–37 mm. The tyre plate also runs 2–15 mm into the outer yoke legs. A real hold-down sheave is lifted only a few tens of millimetres by a passing grip. | Check the lever's line against the owner's end-on grip photo. It should stay within about 50 mm of the rope's axis until it is outboard of the sheave's 67.5 mm half-width (for example, pivot near v +40, w +45). Outer yoke legs ≤ 40 mm past on hold-down rows. Re-run the sweep. | medium | P5-03 |
| H5 | S2 | **The number plate is on the uphill face, and black.** That is opposite the deck and ladder, against the code's own comment and the quad's head. The real plate is on the downhill face at the crossarm's centre: light, with dark numerals. | Flip it to the downhill face (socket too). White plate style, dark digits when the numbers come. Keep our larger size. | high (side); medium (colour) | P2T-04 |
| H6 | S2 | **The crossarm is a constant 0.42 × 0.52 m box.** The real one is haunched: about 0.3 m deep at the ends and 0.55 m at the mast, with a sloped underside rising from a flared hexagonal junction on a bolted flange ring. Riders look up at exactly this. | Loft the crossarm. Add the flare plate, and a flange ring with bolt heads at LOD0 in place of the square cap. +60–150 tris. | medium | P2T-05 |
| H7 | S2 | **No rope catchers or switch boxes.** Every real rocker carries a curved catcher plate on its outboard side; the row of them gives the trains their serrated silhouette at 20–100 m. | One curved plate per rocker, outboard (about 12 mm thick, 0.25 m long), below the grip's wheel line and swept against the grip envelope. A small switch box per train. About +40 tris a rocker at LOD0, a quad at LOD1. | medium | P2T-06, P5-18 |
| H8 | S2 | **The equalisers are 163 mm square bars**, so the trains read as "sheaves on sticks". The real trains hang from chunky box equalisers 0.3–0.4 m deep with large pin bosses. | For the D6: equaliser about 300 deep × 160 wide, with a boss at the main pin. +50 tris. | medium | P2T-07 |
| H9 | S2 | **LOD3 drops the lifting frame.** From about 300 m to 1.6 km the head becomes a bar with two blocks, and the frame pops in at the switch. | Keep the beam and both struts at LOD3 as three boxes: +36 tris, 72 → 108 of 280. | high | P2T-11 |
| H10 | S2 | **The towers next to both terminals carry 12-sheave trains on a breakover curve.** The planned Gate 2 set (s10, s12, d12, c12) has no curved 12, so the first and last towers would be drawn under-sheaved. | Add b12 (breakover, 12 a rope) to the Gate 2 set: decision 7. About 22k LOD0 with walkways, close to the 24k cap. | medium-high | P2T-10 |
| H11 | S3 | **Ladder:** rung pitch 250 against about 305 mm (12 in), width 400 against 480, and no fall-arrest cable up its centre. The missing cage is right. | A D6 ladder option: rungs 305, half-width 240, a 10 mm cable at LOD0. | medium | P2T-13 |
| H12 | S3 | **The mast is plain 1 m sections.** Real masts show seams every 1.3–1.5 m, bolted flange joints, a darker cast foot and, on tall towers, a larger lower section with a conical step. | A flange-ring section (one every 5 m or so), seams in the AO, a foot section. Low priority. | low-medium | P2T-14 |
| H13 | S3 | **Top-centre kit missing:** a junction box with cable loops, a floodlight on combination heads, a sensor at the beam's centre, rating plates near the beam's ends. | +200–300 tris LOD0, mostly gone by LOD1. | medium | P2T-15 |
| H14 | S3 | **Bar grating and square-cornered rails.** The real ones are perforated plank grating (cleated on slopes) and hoop-topped cage rails. | A perforated-plank tile; bent rail corners. | medium | P2T-16 |
| H15 | S3 | **The rope passes low against the crossarm.** The real rope passes about level with the top of the crossarm's end. H1 and H6 (a 0.3 m-deep end) mostly settle it. | Recheck after H1 and H6. | low | P2T-17 |
| H16 | S3 | **The rope guards and jaw ends bite 26–30 mm into the support sheaves.** In reality the sheave rides up the guard cone and the rope lifts for a moment: the "thump" of a chair over a tower. Nothing lifts in the game. | Accept it (invisible beyond about 10 m), or animate it (stunning idea 10). Decision 1 changes the guards' shape. | medium | P5-14 |

## B. Terminals: clashes and structure

Both terminals unless marked.

| # | sev | finding | fix | conf | source |
|---|---|---|---|---|---|
| S1 | S1 | **The rail's hangers stand in the grips' path.** Each is an 80 × 80 post on the rail's centreline, running from the rail up to the portal beam. Every chair's running wheels, tyre plate and springs pass right through 7 posts per straight: 14 per terminal. The rail's back loop has no support at all. The drawings carry the rail from below (a member under the rail down to the floor) and draw nothing above it in the grips' band. | Delete the hangers. Carry the rail from below: posts or a web at v about 3010–3035 from the floor to the rail's underside, or brackets from the portal's inner leg (S2), round the back loop too. Keep v 2700–3560, w 3480–4050 clear all the way round. | high (clash); medium (support form) | V3 V2-01, V1 N1, V2 N02, P5-05 |
| S2 | S1 | **Drive: the portals are in the wrong layout.** The drawing (end view and isometric) has a separate inverted-U frame over each grip path: inner leg v ±2414…2582 from w about 3175, outer leg 3603–3768, and a chamfered top only between them. Ours has its inner legs near the centre (v ±917…1079) and one beam across the whole width. | Per-side portals, as drawn. This also clears the bullwheel's spokes (S9) and gives S1's rail an inner leg to hang from. The return's round 1 reviewer measured inner legs at ±998 on the return's sheet, so recheck the return's end view before changing the return. | high (drive v); medium (heights, the return) | V2 N01 |
| S3 | S1 | **Return: the "tensioning carriage" is the rear footing.** The plan's dashed rectangle it was built from matches the side view's rear footing to 4–13 mm, and no elevation draws a carriage. Built as a carriage, its rear end hangs 191 mm below the hood's bottom edge for about 1.1 m behind the belly. | Delete it: nothing below the hood's edge outside the belly. The return's tensioning gear isn't drawn on this sheet, so it stays out. | high (protrusion); medium (reading) | V1 N6 |
| S4 | S1 | **Drive: the belly's back is a sloped wedge.** The drawing has the tray's underside flat at w 2690 behind u −4245, and the keel's back face vertical (w 1944–2690). Ours lofts the two levels into a 2.27 m wedge, 222 mm low at the keel's back. | Flat tray underside; vertical keel back. | high | V2 N06 |
| S5 | S2 | **The horns are solid wedges where the hanger's guide roller runs.** The roller is buried up to 108 mm deep over about 2 m, and the arm's brackets touch the horns. The drawings (end view, plan, isometric) show a guide channel open inboard: upper and lower guides that capture the roller and flare toward the tip to catch a swinging chair. The real horns are galvanised formed blades with a folded lower flange; ours are dark paint. | A channel with its web outboard (v ≥ 3560–3590). At the root, a lower guide topped at about 3455 and an upper guide with its underside at about 3655, flaring to the drawn outline. The drive's lower edge follows the drawn curve (V2 N08's points). Galvanised. About +100 tris. | high | V3 V2-04, V1 N2, V2 N03, N08, P5-06, P1T-09 |
| S6 | S2 | **Drive: the bend-sheave brackets run through the rope and every grip.** Each is a 120 × 240 box from the sheave's axle straight up to beam height at the rope's v. The plain Ø550 drums also clip the jaw (2.6 mm) and the body (19 mm), and the u 6976 sheave sits 5 mm into the rope the game draws. | Mount each sheave from inboard, as `parts.sheave_bracket` does, on a cross member from the main beams: no steel above w 3570 within v 2680–2960. Grooved drums like the line sheaves. Place them on the rope the game draws (the socket line, or a curved bend through all three, as drawn). | high | V3 V2-03, V2 N04, P5-07, P5-13 |
| S7 | S2 | **The station floor is a rectangle, so every hanger cuts through it round the back of the loop.** At floor height the hanger is about 490 mm outboard of the grip. The floor's edge channels also hang 16 mm below the soffit. | Give the floor the soffit's slot round the loop (rail + 242…542), or end the outer floor where the corner starts. Channels ≤ 100 mm deep. | high | V3 V2-02, V1 N4, V2 N05, N11, P5-08 |
| S8 | S2 | **The station frame touches neither mast.** The beams pass about 870 mm outboard of the rear saddle and end about 970 mm above the front column's head; all four posts float 106–132 mm off the beams. The drawing has a rear cross-head across the beams on the leg, and a front bracket (u 8466–8847, w 4346–4520) with a drop frame and column bracket plates (v ±175…575, up to w 3146). | Add them, and run the beams to the posts. Mostly hidden, but seen through the glass and from below. | medium | V1 N7, V2 N15 |
| S9 | S2 | **Static parts sit in the bullwheel's swept volume:** spokes through the portal's inner legs (return u 657, drive u 978), the soffit's plane across the belly's top through the rim, the hub housing 50 mm into the hub, and the floor's edge channels. | Drive: S2's portals. Return: inner legs on a cross member above w 3140. No soffit over the belly's top; housing from ≥ 250; floor out of the sweep. | high | V1 N5, N10, N11; V2 N10 |
| S10 | S2 | **Return: saddle and belly back.** The saddle is a box where the drawing has a curved bracket (up to 168 mm proud in one corner). The belly's top band ends at u −4468, not −4559. | Cut the box's corner along the drawn curve; put the top level's back at −4468. | medium | V1 N8, N9 |
| S11 | S2 | **Drive: the tray's prow runs 1,046 mm too long.** Its edge meets the hood at u 6870; ours runs on to 7916. | `drive.belly.wedge` [0, 6870, 7916, 7916]. | high (line); low (its 3D form) | V2 N07 |
| S12 | S3 | **The running wheels sit 20.5 mm into the rail all round the loop**: wheel bottom at 3501.5, rail top at 3522. The rail is built as a four-sided tube (a diamond, corner up), not the 40 × 50 bar. The drawing itself disagrees with itself by 20 mm here. | A flat-topped 40 × 50 bar with its top at 3501.5 (centre 3476.5), keeping the grips and the rope at their drawn height. Decision 4. | high | P5-11, V3 V2-05, V1 N3, V2 N09 |
| S13 | S3 | **Return: nothing carries the rope's 5° bend.** The drive has three sheaves a rope; the bend needs about 8.7 % of the rope's tension a rope. The return's drawing draws none. Hidden above the soffit. | Mirror the drive's three a rope, mounted as in S6. Decision 5. | high (need); medium (count) | P5-10 |
| S14 | S3 | **No detach machinery.** There are no tyre conveyors (the tyre plate bears on nothing), no opening and closing rails for the lever's roller, and no lead-in ramps at the rail ends. With the jaws always closed, the descending rope runs through the closed jaw for about 0.6 m inboard of each bend. The photos show a red row of machinery low along both sides, inside the glass (by its position, the conveyors' guards); the drawings show none. | A simplified tyre bank, opening rails and ramps, plus an "open" grip pose: stunning idea 4, decision 6. | high | P5-12, P1T-17 |
| S15 | S3 | **Small drawing residuals**, all under about 170 mm and mostly hidden. Return: the flip-bay transoms are whole facets (168 / 138 mm tall against 83 / 48); pane bottoms 19 mm low; band bottom 19 mm high; no belly panel joints; the upper overhead pair (low confidence); the column's bearing pads and Ø1260 flange; the ladder's foot bracket; rear bolts 37–46 mm forward; the rear leg's side cover; the box under the line end; posts above the overhead members to the roof; a self-intersecting outer-leg polygon (47 mm); a 14 mm belly side-panel sliver; a ladder landing on nothing. Drive: ladder handrails 310 mm too long; the line-end opening's top perhaps 4270, not 4100 (low confidence); the rear leg's manhole; fittings on the band. | Fix with the S1–S11 rebuild; values in the reports. | mixed | V1 §1–3, V2 §1–2 |

## C. Terminals: realism and look

Current triangles: 11.1k–11.5k / 7.2k–7.5k / 1.29k / 551–557 against the 36k / 14k / 3.5k / 600 budget.

| # | sev | finding | fix | conf | source |
|---|---|---|---|---|---|
| L1 | S1 | **The glass reads as flat blue-grey panels, lighter than the hood.** The real glass is mirror-black with bright sky and snow reflections, and the far side's ribs and bracing cross the near ones in a diamond lattice. In the game, the glass shader blends straight alpha at 0.6, so the glass's own reflection is cut to 0.6 and it becomes a dull tint. Our ribs are 115 mm; the real ones are about 75. | Premultiplied glass that keeps its reflection (only the diffuse is scaled by alpha), with Fresnel alpha from about 0.45 face-on to 0.95 grazing. Near-black glass swatch. Ribs 75 mm (heavy 150). Match LOD2's opaque glass to LOD1's look. The shader is shared with every lift: decision 11. | high (shader); medium (in Unity: not yet seen there) | P1T-02, P1T-23, P1T-24 |
| L2 | S1 | **The roof is a dead black slab, the largest surface from the game's camera.** The real crown is a silver ribbed roof band, with lifeline posts (A-frame tripods) at both ends and a cable along it. At the line end there is a low silver box with an A-frame mast; there are small roof units (also in the drawing's end view, up to about 0.5 m tall). | Give the roof faces their own class (galvanised, or a new roof aluminium, snow 1) with ribs about 0.5 m apart (texture, or geometry at LOD0–1). Add two posts and a 6 mm cable, the line-end box and mast, and 2–3 units. +400–700 LOD0, +200 LOD1, posts as boxes at LOD2. | medium-high (colour, posts); medium (the band's extent) | P1T-01 |
| L3 | S2 | **Every curved surface is flat-shaded.** Faces never share vertices, so smooth shading can't work. The arch (16 facets), the rings, the cap steps and the tub's round back show as stepped highlights on gloss black and as stripes on a light livery, and draw 12 creases across every pane. | Weld coincident vertices per smoothing group, with hard edges at the band, the roof kink, the ring faces and the window edges (or write split normals). LOD2 arch 6 → 10 facets, tub 4 → 8. Zero triangles. | high | P1T-06, V1 R1-11 |
| L4 | S2 | **The panel texture puts a seam every metre both ways, plus 8 mm noise, on every livery and white face.** It is mapped in world metres, so seams cross the windows, beat against the 800 mm bays and turn the soffit into a ceiling grid. Up close the hood looks hammered. The real hood is smooth gloss panels with few joints. | A seam-free gloss texture (noise below 0.05) for the hood and the white. Joints as geometry or UVs only where real: the bay lines, the end walls' window edges, one horizontal joint, the skirt every 1.6 m. The tub nearly jointless. | high | P1T-07 |
| L5 | S2 | **The hood's finish is too matte.** Livery smoothness is 0.50; the real hood is mirror-gloss. The skirt (the 582 mm below the band, facing out and down) reads silver in every sunlit photo, by reflecting the snow or as bright metal, but would read flat black in the game. | A gloss livery class of about 0.8, so other lifts keep theirs. The skirt as its own class: decision 10. Check that the Lift Lab's reflection probe sees the snow. | medium | P1T-08 |
| L6 | S2 | **The white tub is a faceted tray:** a 268 mm wall, a shallow slope, a keel, flat facets with the 1 m seam grid. The real one (seen from the back ends) is a smooth white drum. It has a rolled fillet where it meets the soffit, a crisp lower lip with a shadow line, and a nearly flat underside. A boxy chin on the rear support carries a plate, a spot or camera and a small red light. The section matches the layout drawing. | Keep the plan and depth. Add a rolled top fillet (3 segments, r about 200), a lower lip and the chin, with smooth normals and corner segments 16 / 12 / 8 / 4 by LOD. +1.2k LOD0, +500 LOD1, +100 LOD2. Decision 9. | high (character); low (wall height) | P1T-05 |
| L7 | S2 | **The end windows lack their guard bars:** two horizontal galvanised bars across the middle panes (at about 45 % and 65 % of the height), and a header rail above. They are the brightest accents on the dark end at 20–150 m. | Two Ø50 tubes and a flat bar per end window, in front of the glass, at LOD0–1; one box at LOD2. About 150 tris. | high | P1T-10 |
| L8 | S2 | **LOD2 and LOD3 lose the station's identity.** LOD2 is shown from 140 to 440 m on High, the level players see most. Its 25 ribbed bays become one band, the end window loses its frame, the arch has 6 facets and the tub 4. LOD3 has no glass at all. Yet it spends its 600 triangles on hidden beams, the merged bullwheel and the horns. The bullwheel costs 2.5k / 1.05k / 204 at LOD0–2, though it is hidden in the tub. | LOD2: rib quads proud of the band (about 200 tris), arch 10, tub 8, the roof band, the guard bars as a box, galvanised horns. That is +700–900, to about 2.2k of 3.5k. LOD3: a glazing band each side (about 16 tris); drop the beams, the wheel and the horns. Bullwheel: about 800 at LOD0, a disc at LOD1, none below. | high | P1T-11, P1T-12, P1T-21 |
| L9 | S2 | **In Unity the station is hollow.** The lift shader culls back faces, and the soffit, tub, end walls and caps are single-sided; only the body has a lining. Through the LOD0–1 glass the player would see the snow under the terminal and the sky through the end walls. Blender's renders show back faces, so no review photo shows this. | Interior twins: an upward face over the soffit and the tub's top, and the lining continued over the end walls and caps. About 150 tris at LOD0–1. Review renders cull back faces (R2). | medium-high | P1T-13, P1T-26 |
| L10 | S2 | **Snow in the wrong places.** The ribs between the panes are hood faces with snow 1.0, so in winter the hood turns zebra-striped. Inside the closed hood, the decks, the beam and portal tops, the rail and the column head take snow 0.5–0.8 and would turn white (the game's snow follows the surface normal only). The real ribs stay dark, with snow patches on the panes and roof and icicles along the ring tops. | Ribs and the window section's bands snow 0–0.2; roof and cap tops 1.0. Snow 0 on everything under the roof (interior styles). | high | P1T-14, P1T-15 |
| L11 | S2 | **What shows through the glass is wrong.** The photos show the hood's inner arch ribs and X bracing, which make the diamond lattice with the near ribs, and the red row of machinery low along both sides. Ours shows beams, portals, a deck and a dark lining a hand's width behind the panes. | Inner ribs (a flat bar per bay, LOD0–1), with X bracing in the upper bays. Lining only under the roof and behind the skirt. The red row with S14. About +1.5–2.5k LOD0, +600 LOD1. | medium | P1T-17 |
| L12 | S2 | **The ring's lip is a flat 232 mm face with one crease.** The real one is a thick rounded bead (about 150–200 mm) with a continuous highlight, ending at its foot in a bright vertical cap. | A rounded bead (5–6 segments, smooth normals) at LOD0–1, 3 at LOD2; a bright cap at the foot. +800 LOD0, +300 LOD1. | medium | P1T-18 |
| L13 | S2 | **The rear support's finish:** the photos show it galvanised (ours is grey paint), and perhaps wider (1.0–1.2 m against 700 mm). The drawings measure 700, fixed in round 1. | Keep the drawn width (decision 8); finish galvanised. | medium (finish) | P1T-16 |
| L14 | S3 | **The ladder's rungs are safety yellow at LOD0 and galvanised from LOD1:** wrong for this lift, which is all galvanised, and a colour pop at 50 m. | Galvanised rungs for the D6. | high | P1T-19 |
| L15 | S3 | **Small fittings missing:** red lamps at the hood's bottom corners and inside the line-end ring; a white lamp or sensor at the ring's foot; the return's back-end access door (chamfered corner, hinges) with a galvanised hoist beam out of the wall; the chin's plate, spot and red light. Drip spouts under the glazing are out of scope (as gutter spouts were) unless you want them. | Boxes and cylinders at LOD0–1, about 300 tris. A fictional brand plate where the real lift carries its maker's. | high | P1T-20 |
| L16 | S3 | **AO is unreviewed.** The photos were rendered with AO off; the soffit has 20 m strips, the end walls a few big polygons, and the terminal is baked without the ground (no contact shadow at the feet). | Subdivide the soffit (about 1 m) and the end walls near the ring (about 0.5 m): about 400 tris at LOD0–1. Bake the feet against the ground. One AO-on photo set before Gate 2. | medium | P1T-22 |

## D. Chair and grip

Current triangles: 2,106 / 306 / 72 against the 2,400 / 320 / 80 budget, which leaves almost no headroom. Most fixes
below need the budgets raised first (C13, decision 13).

How the game shows the chair:
- LOD0 to 10 m, LOD1 to 35 m and LOD2 to 300 m, each multiplied by the quality level's LOD bias (1 to 3); past that,
  culled.
- At the detach-six's 100 ft spacing, the chair ahead of a rider is always at LOD1.
- Most of a line's chairs are at LOD2.

| # | sev | finding | fix | conf | source |
|---|---|---|---|---|---|
| C1 | S1 | **The seats turn white in the game's default view.** The seat pads have snow capacity 1.0, and the game sets the lifts' snow load to 1 whenever the Snow layer is on, which is the default. The bench top goes fully white, the backrest front about 30 %, the LOD2 bench box all white. The black seat is the chair's main read, so against snow the chair nearly disappears. The grip's black and aluminium parts whiten too. Every photo of the real lift is a snowy day with black seats. The review photos hid this: they render with snow off. | Seat snow 0 (at most 0.15); chair steel 0.1; grip parts 0, since grips running through the stations' tyres don't hold snow. A "parked overnight" look, if ever wanted, gets its own lower chair snow load in the game. The Sessellift quad and Chairworks chairs have the same 1.0, so decision 16 covers every chair family. | high | P3C-01 |
| C2 | S1 | **The backrest is black from behind.** On the real chair the back is a bright galvanised shell: pressed, with one diagonal rib per bay, riveted edges and a black top edge. Half the chairs on a line are seen from behind, so a real line alternates black fronts and silver backs. Ours is black every way. | The backrest as a black pad on the front and a galvanised shell 3–5 mm thick behind, at LOD0–2 (+2 tris at LOD0–1). The ribs as a pressed-panel texture, or about 10 thin quads at LOD0. | high | P3C-02 |
| C3 | S1 | **LOD2 has no backrest.** LOD2, drawn from 35–105 m out to 300–900 m, covers most chairs on a line. It boxes only the bench (412 × 146 mm). So the dark seat shrinks from about 550 mm tall to 146: about 1.4 px at 100 m, against the real chair's 4–5 px black block. Nothing silver shows from behind. | Add the backrest as one two-tone box (+12 tris, 84 of a raised 120). | high | P3C-03 |
| C4 | S1 | **Chairs are culled past 300 m × LOD bias (300 / 450 / 600 / 900 m).** The heads stay to 800 × bias and the terminals to 1,200 × bias, so from the base or an overview the far half of a line shows towers and rope but no chairs. The real line reads as a dotted line of dark specks all the way up. At 600 m a chair is still about 5 px wide. | An LOD3 "speck" out to about 1,200 m: bench and backrest block, top bar and hanger as 3–4 boxes, at most 30 tris, instanced. About 1.8k tris a lift. Decision 13. | medium-high | P3C-04 |
| C5 | S2 | **The chair is too wide.** The frames stand 87 mm per side wider than drawn (frame centre 1,614 against 1,527 at the bottom; +164 overall). The bench is 2,916 against 2,826 (6 × 471). | The widen rule stays (LM2, LM8); only its numbers change: widen about 399–420 for the frames, and seat pitch 471 for the bench (or one widen of about 420). | high | V3 V2-10, V2-11 |
| C6 | S2 | **The bench is a plank, not padded.** A 40 mm seat with sharp edges and a 25 mm backrest slab. The real bench has a padded backrest (about 80–100 mm, rounded top edge, black end caps) and a large round front bolster (about Ø140) in 6 segments split by 5 light aluminium bands. Up close ours reads as a park bench; side-on at 20–70 m the seat almost vanishes. | An 8-sided Ø140 bolster along the bench's front in 6 seat segments, with 5 light bands 30 mm wide. A 90 mm backrest pad with a bevelled top and black end caps. At LOD1, a 4-sided bolster with 5 thin bands. About +200 LOD0, +60 LOD1. | high (shape); medium (sizes) | P3C-05 |
| C7 | S2 | **The seat's underside is black.** The real seat pan is galvanised with a zig-zag flat-bar lattice under it. Skiers under the line, the ground-level photo camera and the Lift Lab all see chairs from below. | The bench's bottom face in chair steel (0 tris); the lattice as about 8 thin quads at LOD0, or a texture. | high | P3C-06 |
| C8 | S2 | **The raised safety bar is too sparse.** The real bar has a comb of one stay per seat, a black handle per seat, three black flat footrests (at about 0 and ±950 mm) and six black triangular rubber stops on the top bar, one per seat. Ours has two arms, the end legs and a middle leg, two grey handles and no stops. The comb and the black stops are the regular pattern that reads in every photo out to about 40 m. | Add 4 stays (about +48 tris) and 6 stops (about +48). Handles and footrests in black rubber, the footrests at 0 and ±938. Keep the raised pose (owner rule). | high (presence); medium (layout) | P3C-07, V3 V2-17 |
| C9 | S2 | **The hanger's tail and clamp.** The level run into the clamp sits about 73 mm low (drawn at w −1575), and the lower bend is three straight runs where the real hanger has one smooth large-radius bend. The clamp is small (±126 × u ±49 × w −1609…−1869) and darker than the hanger. The drawing and the photos show a big square galvanised plate (±179 × u ±65 × w −1525…−1894) with a small black plate on its face, and the hanger sweeps into its top edge. Smaller: the hanger's outer bulge is 97 mm high, the top bar 11 mm low and 10 mm thin, the web plate's lower edge off the drawn line. | V2-12's tail points, filleted (about 6 steps, +50 tris at LOD0; 6–8 points at LOD1). The clamp as drawn, in chair steel, with 4 bolt heads and a 2-tri black plate per face. Add the bulge point (578, −1160); top bar at w −1734, Ø70; the web plate on the drawn line. | high | V3 V2-12…V2-16, P3C-12, P3C-13 |
| C10 | S2 | **Grip parts that break the look up close.** (1) The guide roller floats 38 mm off the arm's end. (2) The crossbar passes through the top roller. (3) The springs are square four-point helices of three-sided wire; they read as blue blades, where the photos show round, tight coils. (4) The springs' lower seats are 14 mm thick against 50. (5) The tyre plate lacks its ribbed band, its main feature in both grip photos. (6) No wheel shoulder or flag plate, and the wheel carrier is a barrel, not a box. | (1) Taper the arm to v 687. (2) Split the crossbar into two seat blocks on the spring axes, under the roller. (3) Springs with at least 8 points a turn and 5–6 sides at LOD0: about +1.1–2.2k tris for both (decision 13). (4) Seats 50 long, 12-sided. (5) Ribs as geometry or a trim texture. (6) Shoulder ring, flag plate, carrier box. | high / medium | V3 V2-06, V2-07, V2-09, V2-18, V2-19, V2-21 |
| C11 | S2 | **At LOD1 the grip loses all its cream, and LOD2's parts float apart.** LOD1 is what a rider sees on the chair ahead, the framing in which the real grip shows blue springs and three cream rollers. At LOD1 the grip becomes an aluminium box, a black bar and one blue slab, and the hanger drops a bend (a straight run, then a hard corner). The switch is a hard pop (no cross-fade) at 10 × bias m. At LOD2 the grip box floats 231 mm inboard of and 65 mm above the hanger post, and the post stops 67 mm above the top bar. | LOD1: the top and guide rollers as 6-sided cream discs (about 48 tris), the wheels as 2 cream boxes (24), two thin spring bars, the bend's points kept. Needs LOD1 at about 450–600 (decision 13). LOD2: post at v 362–460, from w −25 to −1715. | high | P3C-08, V3 V2-20 |
| C12 | S2 | **The rope guards' shape.** The model's coaxial sleeves (Ø138 → Ø52 round the rope) reach 44 mm below the rope's underside, so they cut every support sheave the grip passes (H16). The owner's end-on grip photo shows slender solid black rods (base about Ø70, rounded tip about Ø20) with no bore. | Decision 1: rods lying over the rope, lengths as now. | low-medium (where the rod sits); high (that the sleeve reaches below) | V3 V2-08 |
| C13 | S2 | **The budgets block the fixes.** LOD0 is at 2,106 of 2,400, with no room for round springs (+1.1–2.2k) or the padded bench (about +300). LOD1 is at 306 of 320 and LOD2 at 72 of 80. LOD0 is drawn only within 10 × bias m (one chair a rope at most), and LOD1 covers about 4 chairs. | Raise chair_detach to 4,000 / 600 / 120 and add an LOD3 of 30 tris to 1,200 m. A lift at High then draws about 2 × 4,000 + 4 × 600 + 35 × 120 + 60 × 30 ≈ 16k chair triangles, under one terminal's LOD0. Decision 13. | high | P3C-15 |
| C14 | S3 | **Every LOD1 tube is a flat-shaded square.** The side frames and hanger have 4 sides at LOD1, and tubes only smooth from 5 sides up. In low sun they show a lit face and a dark one, and round LOD0 tubes pop to square at 10–20 m. | Smooth the chair's LOD1 tubes whatever their side count (0 tris), or give the hanger 5 sides (about +20). | high | P3C-09 |
| C15 | S3 | **The seat floats, and the armrests are missing.** The bench, backrest and bottom member end at ±1,458, short of the frames (the edge rails' inner face is at ±1,490, the frames at ±1,575), and nothing ties them together. The real chair bolts its seat pan to the frames through galvanised end brackets. The real bench ends also carry black curved armrests. | One galvanised 10 mm end bracket per side (12 tris each), and the bottom member run to the frame. Two black rubber armrests of 4–5 points with 5 sides (about +80 tris, LOD0 only). | high | P3C-10, P3C-11 |
| C16 | S3 | **A line runs down the middle of the cream roller faces.** The 14-sided caps have vertices on the vertical diameter, and the baked AO interpolates across their triangulation; Unity will repeat it. | Disc caps with a centre vertex (a fan), or AO per cap face on small discs. | medium | P3C-14 |
| C17 | S3 | **Three rope sizes:** the grip spec's 48 mm, the D6's 50.8 and the game placeholder's 60. The 50.8 rope pokes 0.4 mm through the jaw's bottom flat. | 50.8 for the jaw's profile, the sheave groove and the game (G3). | high | P5-17 |

## E. Kit-wide: these touch other families' models (owner's call)

| # | sev | finding | fix | conf | source |
|---|---|---|---|---|---|
| K1 | S2 | **The rope rests on nothing.** Every kit sheave puts the rope's axis on the flange tips (r 216 + 25.4 mm). But the rubber groove sits at r 186–194, between flanges wider than the rope. So the rope floats about 30 mm above the liner on every sheave of every head and terminal row, the quad's included. Real line sheaves seat the rope in a rubber liner that stands proud of the steel rim. | Recommended: keep the game's 0.216 m contact radius and move the profile to it. The liner's groove bottom goes to r 216, shaped to the rope; liner shoulders about Ø452–460; steel flanges below them (about Ø410–420). No rope socket or game change, but every head model rebuilds. The alternative moves every rope socket 30 mm and needs the rope tests re-run. Decision 3. | high | P2T-09, P5-04 |
| K2 | S2 | **Red end sheaves.** The kit paints each train's first and last sheaves red (the game's grounding-sheave rule). Every train in every photo of this lift is galvanised with dark liners. | Decision 2. | high (photos) | P2T-08 |
| K3 | S2 | **Towers stop casting shadows at the LOD2 switch.** The wide heads, masts and bases cast from LOD0–1 only, so a whole tower loses its shadow at about 90 m, inside High's 150 m and Ultra's 250 m shadow distance. Long tower shadows across the piste are one of the strongest cues in the photos; a line where the nearest towers cast and the next ones don't looks pasted on. | Shadows to LOD2 for heads, masts and bases (every family's masts), measured with the benchmark. If too costly, a shadow-only stand-in. Decision 12. | medium | P2T-12 |
| K4 | S1 | **Every chair family's seats turn white under the default Snow layer**, not only the D6's (C1). The Sessellift quad and the Chairworks chair also give their seat pads snow 1.0. No ruling chose it. | Seat snow 0 (at most 0.15) and grip parts 0 for every chair. The quad's and Chairworks chairs rebuild and re-import. Decision 16. | high | P3C-01 |
| K5 | S3 | **The sheaves are a size small for the D6:** Ø432 over the flanges, tread Ø372 for a 50.8 mm rope (7.3 ×). A 2 in rope usually runs on Ø420–500 treads, and ours look small beside the six-seat grip. | Keep Ø432 (the game's contact radius is one constant): decision 14. | low-medium | P5-16 |

## F. Game side (for the integration task after P3-09; not this thread's files)

| # | sev | finding | fix | source |
|---|---|---|---|---|
| G1 | S1 | **Every built lift is red.** The built-lift view sets one livery colour for all lifts, and the importer fills the livery renderers only for kind `terminal`. The D6 would never show black. | A per-type default livery (the D6 black), chosen by the player later; include `terminal_detach`. Decision 15. | P1T-03 |
| G2 | S1 | **The D6 would draw wrongly as wired today.** (1) Chairs ride round the bullwheel, inside the white tub with their hangers through its walls, because nothing reads the terminal's `paths.chairs`. (2) The station rope runs straight from the wheel's socket to the line end, missing the climb and the bend (270 mm low there). (3) Footings read `pier_top`, but the D6 has `pier_top_1` and `pier_top_2`, so no footings appear. (4) `LiftAssetTests` has no budgets for the three new kinds, and its facing test skips the D6. (5) The lift rig doesn't know `tower_head_wide`. | Station chairs follow `paths.chairs` (slow, detached); the rope goes through the bend sockets; two footings; budgets and tests per kind. | P1T-04, P2T-19 |
| G3 | S2 | **The detach-six placeholder** draws a 60 mm rope on the quad's 4.12 m gauge, with the quad's 2.66 m chair drop. | 50.8 mm, 5.6 m and 3.27 m (3.22 to the bench's underside), which also feed the ground-clearance checks. | P5-09 |
| G4 | S3 | **Chair spacing:** 100 ft at 1,000 fpm is a 6 s interval, 3,600 riders an hour, the top of the six-seat range; most run 2,400–3,200. | Default about 120 ft (3,000 an hour), with 100 ft as the maximum. In the stations, chairs at least 3.5 m apart in the return's loop and 2.5 m in the drive's. | P5-19 |
| G5 | S3 | **Swing clearance for task 09's line checks.** The chair hangs 751 mm from the mast at rest and 230 mm when swung 11.5° inboard; it touches at about 16.4°. Opposing chairs pass 2.1 m apart, and 1.07 m when both swing 11.5° toward each other. | Keep the swing case in the clearance analysis. | P5-15 |

## G. Review tooling (before the next round)

| # | finding | fix | source |
|---|---|---|---|
| R1 | The underside shot puts the eye under the snow plane, so nobody has seen the tub from below. The LOD line-ups frame the 15 m piers and crop the hoods. Two comparison sheets are mislabelled. | Raise the underside eye. LOD line-ups cut at grade, from the game's angle (about 35°). Relabel the sheets. | P1T-25 |
| R2 | The review materials render back faces, which makes the glass look opaque and hides the hollow interior (L9). | Cull back faces in the review materials. | P1T-26 |
| R3 | The review data has no tower heads, and the head line-ups space 6.5 m heads 4.5 m apart. | Include the heads; space the line-ups at least 8 m apart. | P2T-18 |
| R4 | The build's clash sweep only checks an asset's own moving parts, never chair against terminal or chair against head. | A build check: the grip's envelope along both ropes through every head at ±30°, and along `paths.chairs` and out past the horns in each terminal. Fail under 50 mm. The reviewers' sweep scripts can seed it. | P2T-01, P5 |
| R5 | Nobody has seen the models in the game's light. | AO on; photos from the game's angles at the LOD switch distances; a short line with chairs passing each head type. | P1T, P2T verdicts |

## Good as is (don't fix these away)

**Terminals:**
- The massing and silhouette match the drawings and the photos: the 21.9 m capsule, the flat crown with its kink at the shoulder, the skirt tucked under the band, the overhanging visor rings.
- The glazing's extent and rhythm: 25 bays over the shoulder, black ribs, two flip-up bays with transoms.
- The end walls, set back 520 mm inside the rings, with the four-pane window in place.
- The line end: low openings each side, the end window, the horns' outline and position.
- The underbody's plan and depth, with the folded-plate wedge at the line end.
- The round galvanised front column with its inclined ladder; the rear leg's lean and taper; the concrete piers.
- The hood, nose, windows, bullwheel, rope, rail plan, masts, ladder and piers sit within about 25 mm of the drawings at both ends.
- Clearances round the hanger loop: soffit slot 63–80 mm, belly 122, line-end opening 146, portals 55, bullwheel 242, masts and ladder over 300.
- The rider sockets. Loading is 17 mm off the drawn gates' centre; unloading is at the crest of the exit slope. Seat height is 377 mm over the load level (drawn about 370).
- The order of events. The wheels land on the rail while the grip is still on the rope, there are 4.2–4.5 m to open and close, the acceleration and braking lengths are plausible, and the rope stays in the tilted wheel's plane.
- The hidden tilted bullwheel (the real one is hidden too).
- Snow on the roof and cap tops and none on vertical faces; LOD3 within 600; shadow LODs matched to the shadow distance; LOD cross-fade.

**Tower heads:**
- The mast's Ø610 matches the real mast at the head. No ladder cage, and the ladder on the downhill face, as photographed.
- The trapezoid yoke idea, the beam's height (1.5 m) and the struts' feet (±1.9 m).
- Single-sided trains: frames inboard, sheaves on the rope, combination frames open outboard for the grips.
- The sheaves' pitch and spacing; galvanised throughout with dark liners; rows on lugs and pins, posable; the gauge-parametric build.
- The d8 clears every grip by at least 273 mm at ±30°. The lugs, railings and lifting beam clear every grip at every tilt.

**Chair and grip:**
- The palette, sampled against the photos: spring blue, roller cream, the black seat and the galvanised steel. Don't
  retune them.
- The chair takes no livery: galvanised and black whatever the hood's colour, as on the real lift.
- The grip is built to the measured numbers almost everywhere (26 values exact). Its layout and colours read right
  up close: silver jaw and body, blue springs, cream wheels and rollers, black guards and plate.
- The chair hangs at the drawn height (bench within 5–7 mm), with six seats, the drawn −100 mm axis offset, and the
  hanger's upper run within 7 mm.
- The front silhouette: the frames' rounded top corners, the top bar with its lens-shaped web plate, the bench hung
  low.
- The raised bar suits the game's empty chairs. The posing is right: the grip lies along the rope and the hanger hangs
  plumb.
- Chairs cast shadows at every LOD, so a line's shadow dots the snow under it.
- Opposing chairs pass 2.1 m apart.

## To make it stunning (ranked, beyond accuracy)

| # | idea | cost | source |
|---|---|---|---|
| 1 | **Terminal shading and materials pass:** welded normals, gloss livery, seam-free panels, Fresnel glass, a gloss skirt. This alone moves it from blockout to product shot. | 0 tris; 1–2 days | P1T |
| 2 | **The photographed "H" heads:** walkways along the trains, cages on top, the central ladder; perforated grating with see-through holes at LOD0–1, so low sun and snow glint through. | +1.5–2.5k LOD0, +0.5k LOD1 | P2T, P5 |
| 3 | **The glazing as photographed:** inner arch ribs and X bracing, so the near and far ribs cross into a moving diamond lattice as the camera passes; the red conveyor row inside; strip lights for dusk if the shader gains emission. | +2–2.5k LOD0, +600 LOD1 | P1T |
| 4 | **A working station:** a tyre bank along each straight (about 20 black Ø400 tyres a side, spinning at LOD0), short opening and closing rails, lead-in ramps, and the grip's "open" pose. Chairs visibly slow, open, swing round and close. Needs game work. | +2–3k LOD0, +600 LOD1 per terminal; a spin pivot per bank, a hinge per grip | P5 |
| 5 | **The silver ribbed roof** with lifeline posts and the line-end box: what the player looks at from above. | +500–700 LOD0 | P1T |
| 6 | **Serrated trains:** rope catchers and chunky box equalisers, the dark rhythmic strips that make a line read as a real lift from 20–150 m. | +0.4–0.8k LOD0 | P2T |
| 7 | **Identity kept at range:** LOD2 rib quads and roof band on the terminals; the frame at LOD3 and shadows to LOD2 on the towers. | +900 / +36 tris; shadow cost | P1T, P2T |
| 8 | **The ends as sculpture:** the rounded ring bead with its bright foot, the tub's rolled rim and chin, the guard bars, the return's door and hoist beam, red corner lamps. | +2.4k LOD0, +900 LOD1 | P1T |
| 9 | **A haunched crossarm** with the flared, bolted mast joint that riders look up at on every tower. | +0.1–0.2k | P2T |
| 10 | **The tower "thump":** lift the touched rocker by the guard's height as each grip passes, then drop it back. The rockers are already posable. | 0 tris; a few lines in the game | P5 |
| 11 | **Winter:** dark ribs, patchy snow on the panes, icicles along the ring tops, rime on trains and railings. | +200 LOD0–1; texture work | P1T, P2T |
| 12 | **Lights at dusk:** a floodlight on each combination head and a small light at each junction box, so a string of lights runs up the line. | +50 tris; shader emission | P2T |
| 13 | **Galvanising variation:** two or three tints (bright new, dull, a darker cast foot) and a fine spangle texture. | 0 tris | P2T |
| 14 | **A fictional maker's plate** on the end wall and the chin, the real lift's strongest graphic accent. | 4 tris, one texture tile | P1T |
| 15 | **Mast rhythm:** flange rings every 5 m or so, seams, a yellow warning sign at 2 m. | about +100 tris a variant | P2T |
| 16 | **A chair line that reads like the real one:** black seats under snow (C1), a two-tone backrest at every LOD (C2, C3) so a line alternates black fronts coming and silver backs going, the banded bolster so six seats read at 40–70 m (C6), and the LOD3 specks so the dotted line runs from base to top (C4). | about +14 tris at LOD2; +200 / +60 at LOD0 / LOD1; 30 tris a far chair | P3C |
| 17 | **A gentle swing:** about ±1.5° fore-and-aft on the existing hanger hinge, kicked as a chair leaves a station and passes each tower, then damped, with a per-chair phase from the chair's index (deterministic). Real chairs visibly rock there. | 0 tris; one sine per chair per frame in the game, no allocation | P3C |
| 18 | **No cloned chairs:** a ±3 % brightness per chair on the galvanised parts, and chair numbers on the clamp's black plate from a small atlas. | 0–2 tris; a per-instance value | P3C |

## Budgets after the fixes (estimates)

| model | now (LOD0 / 1 / 2 / 3) | after (estimate) | budget |
|---|---|---|---|
| Terminal | 11.1–11.5k / 7.2–7.5k / 1.29k / 557 | about 19–22k / 10k / 2.2k / 560 (rebalanced) | 36k / 14k / 3.5k / 600 |
| Head b8 | 13.4k / 3.9k / 664 / 72 | about 16k / 4.5k / 700 / 110 | 24k / 7k / 1.4k / 280 |
| Head b12 or c12 | (Gate 2) | about 22k / 6k / 900 / 110: tight | as above |
| Chair | 2,106 / 306 / 72 / none | about 3.6–4.0k / 500 / 90 / 30 | 2,400 / 320 / 80 / none; proposed 4,000 / 600 / 120 / 30 (decision 13) |

## Owner decisions (recommendation first)

1. **Rope guards:** rods lying over the rope, as in your end-on photo, instead of sleeves round it. *Recommended: rods.*
2. **End sheaves:** galvanised for the D6, as photographed, while the quad keeps its red. *Recommended: galvanised.*
3. **Rope seat, kit-wide:** move the sheave profile so the rope sits in its liner at the same contact radius. Every
   head model rebuilds; nothing changes in the game. *Recommended: yes.*
4. **Wheels on the rail:** lower the rail 20.5 mm, keeping the grip and rope at their drawn heights, instead of
   raising the grip. *Recommended: lower the rail.*
5. **The return's bend sheaves:** physically needed but not drawn. Add three a rope, hidden above the soffit.
   *Recommended: add them.*
6. **Detach machinery:** in the photos (the red row) but not in the drawings. A simplified tyre bank and opening rails
   at LOD0–1; the game's animation later. *Recommended: yes.*
7. **The Gate 2 head set:** s10, s12, d12 and c12, plus a curved b12 for the towers next to the terminals.
   *Recommended: yes.*
8. **The rear support:** keep the drawn 700 mm width; change only its finish to galvanised. *Recommended: yes.*
9. **The white tub:** keep the layout's depth and add the rim, lip and chin. The photos' taller wall is only an
   estimate. *Recommended: yes.*
10. **The skirt:** gloss black 0.9, which reads silver in the sun by reflection, rather than aluminium.
    *Recommended: gloss black.*
11. **The glass shader**, shared with every lift: premultiplied Fresnel glass, verified in the Lift Lab on every family
    at Gate 3. *Recommended: yes.*
12. **Tower shadows to LOD2,** kit-wide; costs frame time, measured with the benchmark. *Recommended: yes, if the
    benchmark holds the budget.*
13. **The chair's budgets:** 2,400 / 320 / 80 → 4,000 / 600 / 120, plus a new LOD3 of 30 tris out to 1,200 m. That
    makes room for round springs, the padded bench, the LOD1 grip's rollers and the LOD2 backrest, and keeps far
    chairs visible. LOD0 is only shown within 10–30 m. A lift then draws about 16k chair triangles at High, less than
    one terminal's LOD0. *Recommended: yes.*
14. **Sheave size:** keep Ø432, the game's single contact radius. *Recommended: keep.*
15. **The D6's default livery in the game:** black, set by the integration task. *Recommended: yes.*
16. **Seat snow, every chair family:** seats snow 0 (at most 0.15) and grips 0 on the quad and Chairworks chairs too, so
    lines stay visible on snow; those chairs rebuild and re-import. *Recommended: yes.*

**Already settled, so not reopened:**
- the mirror image of the reference;
- the drive's tops of concrete from the drawn geometry;
- the drive's R2735 rail corners;
- the chair body widened by rule.

**Out of scope:** the operator house, the generator, walkways at grade (where the drive's ladder lands), the drive's
hidden box, gutter spouts, the garage switch, the roof's comm fitting.

## Proposed fix order (after your go)

1. **Clashes and structure:** H1, H4, S1–S13. The crossarm, the rail's support, the horns, the bend sheaves, the
   floor, the portals, the carriage, the belly, the frame's connections, the wheel's sweep. Then the clash sweep
   becomes a build check (R4).
2. **The heads' look:** H2, H3, H5–H16.
3. **The terminals' free look pass:** L3–L5, L9, L10. Then the glass (L1, decision 11).
4. **The terminals' detail:** L2, L6–L8, L11–L16, and S14 (decision 6).
5. **The chair and grip:** the budgets first (C13, decision 13). Then the snow and the two-tone backrest (C1–C3), the
   LOD3 specks (C4), then C5–C17.
6. **Kit-wide items** once decided: K1–K4.
7. **Review tooling** (R1–R3, R5). Then re-render with AO on and back faces culled, from the game's angles and at the
   LOD switch distances, and run a fresh adversarial pass before Gate 2.
8. **The game list** (G1–G5) goes to the Master Planner for the integration task.
