# Brand brief: Alpine Labs and Ski Area Design Challenge

Task P2-03.1, 2026-10-10. The owner asked for "a completely new design concept for both Alpine Labs and Ski Area
Design Challenge … distinct brands that are both simplistic but are amazing", chosen by adversarial review.
Everything on the earlier board (`logo-options.html`: trail signs, peaks, twin peaks, peak-shaped letters) is
superseded and must not come back.

## 1. Two brands, not a family

| | Alpine Labs (the studio) | Ski Area Design Challenge (the game) |
| --- | --- | --- |
| Feel | **Precise, technical, quiet.** It signs the work, the way an engineer's stamp or a maker's mark does. | **Modern winter-sports energy.** Fast, bold, confident: a contemporary sports brand, not a cosy tycoon game. |
| Sources | The logic of measurement, not its symbols: a stated ratio, one exact cut, a module made visible (engineering drawing, cartography, instruments). | The carved edge and its angle, a fresh groomed line on snow, race gates, bib numbers, sports kit, the fall line as a direction. The mountain may appear (owner's ruling), but only as something speed acts on, never as a plain peak silhouette. |
| Not allowed | Mountains, peaks, slopes, a peak-shaped A, beakers, flasks, atoms. | Twin peaks; a lone or generic peak or triangle; any letter shaped as a peak; trail or road sign plates; anything on the superseded board. |
| Type | Its own typeface, not Overpass. No monospace, no geometric grotesk at default spacing (that is Vercel and Leica territory). | Its own display typeface, not Overpass. |
| Colour | Ink and paper (#1f1c19 on #fbf8f2, or reversed). At most one accent, used outside the game, at least 15 ΔE2000 from every Trailhead token. | Only #1f1c19, #fbf8f2, #1a6b3c (green) and #22558f (blue). A colour version uses exactly one of green or blue, never both. Energy comes from form and type, not hue. |
| Lockups | Mark plus "Alpine Labs" in one line; the mark alone as an avatar. | The name is 25 characters: a stacked lockup (fits 308 wide with a cap height of at least 14), a one-line lockup and a wordless icon. The icon is the lockup's mark, same silhouette. Initials monograms (SADC, SA) only as a declared alternative. |

The two brands share **no motif, angle language, typeface or silhouette type**. Someone seeing both should assume
two different makers.

**Wordmarks:** whichever typeface is chosen, the wordmark carries one drawn modification (a cut, joined letters or a
changed terminal) that follows the mark's module. Up to two of a designer's concepts may be wordmark-only, with the
avatar cut from the wordmark; the letters count as one part.

**Precise, not cold (studio):** one deliberate, measured break in symmetry or one hand-cut detail. It must not read
as a SaaS app or a bank.

**Sports energy (game):** a dominant diagonal or shear of 12–25°, heavy weight, tight spacing and type that leans
forward. It does not mean a pictogram of a sport.

**Simple** means: one idea, at most three parts, describable in one sentence that includes its numbers (for example
"a square with one 30° notch a third of its width"; never submit the example itself).

**Amazing** means: it passes every test in §6.

## 2. Where the marks appear

Sizes are in stage units (a 1280×720 stage, drawn ×1.5 at 1080p; the player can scale the UI from 50% to 150%).

**The game:**
- **The loading box:** at most 320 wide, over the title picture (`docs/plans/brand/assets/title-16x9.jpg`) on a
  dark panel (#1c1916 at 95%) in the dark theme and a light panel (#faf8f4) in the light theme.
- **The title name plate:** 308 wide of content on sign white (#fbf8f2), inside a 2 px #1f1c19 border, on a wooden
  signpost. It shows the logo and the game name, with the studio name below.
- **Credits.**
- **The Windows app icon:** 16, 24 and 32 px on a dark (#202020) and a light (#f3f3f3) taskbar, plus 48 and 256.
- **Later:** a Steam capsule, which must still read at 120×45.

**The studio:**
- The bottom line of the loading box: at most 160×20.
- Credits.
- An avatar or favicon at 16, 24 and 32 px.

## 3. Colour rules
- The game mark works in **one colour first**: ink on sign white, and white on graphite. Green and blue are accents
  only, never needed to read the mark. They fall below 3:1 contrast on the dark panel.
- **No red, orange or amber hue** in either brand (HSL 345–50° with real saturation). Surveyor orange means "planned
  work" in the game's UI and is used for nothing else.
- Green, blue and black also mean the trail ratings (beginner, intermediate, expert). Don't build a mark on that
  trio.
- Every colour in a mark has at least 3:1 contrast on the ground it sits on.

## 4. Construction rules (learned the hard way in earlier rounds)
- Holes are real holes in the shape, never shapes painted in the background colour.
- The 16 and 24 px icons are separate hand-fitted drawings on whole pixels, with no feature thinner than 1 px.
  Shrinking the master is not allowed.
- **Curves:** build them from straight legs joined by true arcs. A spline with tight bends pinches on a thick stroke
  and reads as a worm.
- **Shapes that misread:**
  - No discs on the ends of lines: they read as a bone, a matchstick or a pin.
  - No mirrored pairs inside a shape: two of anything side by side inside an outline reads as eyes. Earlier marks
    became a cat, a dog and a robot.
  - Nothing that looks like a sign on posts or a band over columns: it reads as a bank or museum icon.
- **Letters:** check every pair of lines for letters and numerals. Earlier marks spelled "/S", "/3", "2", "S1" and
  "ʃ".
- **Thin lines:** thin knockouts turn into grey scratches at 32 px and below. Either they're at least 1.5 units on a
  24-unit grid, or they're absent from the small sizes.

## 5. Bans
- **Styling:** gradients, glass, pills, drop shadows, glows, bevels, tiny letter-spaced capitals, thin outline icons,
  stencil lettering.
- **Motifs:** swooshes, trailing parallel streaks, motion blur (speed comes from slant, shear, asymmetry, or one cut that implies a direction), hexagons, circuit traces, globes, compass roses, contour-line patterns,
  crosshair-in-circle, plus-in-square.
- **Ski clichés:** the green-circle/blue-square/black-diamond trio, skier pictograms, snowflakes, crossed skis,
  summit flags, mountain-in-a-circle badges.
- **Copying:** real brands (decision A2: no real equipment brands) and copied art. Use patterns only.
- **Look-alikes to rule out:**
  - **Studio:** Alpine Labs (the camera-gear company), Garmin, Vercel, Leica, Trimble, Esri, the Ordnance Survey
    bench-mark arrow, printer's registration marks, the Swiss and Red Cross flags.
  - **Game:** the Material and Font Awesome mountain and ski glyphs, Patagonia, The North Face, Salomon, Atomic,
    Black Diamond, Adidas Performance, Strava, Paramount, Epic/Ikon, Jackson Hole Mountain Resort, Snowtopia, Alto's
    Adventure, SSX.

## 6. The tests for "amazing"

| Test | Pass when |
| --- | --- |
| T1 Memory | A viewer sees the mark once at 128 px and describes it in at most 10 words; a blind agent redraws it from the words; a judge picks the original out of four. The three are separate agents given no brief and no designer text. |
| T2 Squint | Blurred by 4% of its width and thresholded at 50%, it keeps an overlap (IoU) of at least 0.80 with the original, with the same parts and holes. (`tools/brand/checks.mjs`) |
| T3 One colour | It works as one fill: no strokes, no opacity, no gradients. (`checks.mjs`) |
| T4 Small sizes | The hand-fitted 16 and 24 px icons and the master at 32 px have no stroke or gap under 1 px, and read on both taskbars. (`checks.mjs` plus your eye) |
| T5 Literal reads | Three viewers who haven't read this brief say what it looks like at 16, 48 and 256 px. It fails if two of them name a face, a stray letter or numeral, a body part, a weapon, a religious or political symbol, or a known logo. |
| T6 Any other company | Fails if two of three independent viewers say it could belong to a bank, a SaaS app or an outdoor shop. |
| T7 Top tier | A construction sheet shows the module and every angle and radius derived from it, with optical overshoots. A judge shown it beside three named top-tier marks in its field does not pick it as the weakest. |
| T8 Distinct | A viewer shown the studio and game marks unlabelled says "different makers". |
| T9 In place | It reads in every context in §2, and on the board rendered 390 px wide without zooming. |
| T10 Character | Game: 2 of 3 viewers answer "moving" to "still or moving?". Studio: 2 of 3 pick "precise / engineered" from five adjectives. |
| T11 Regression | Shown beside the superseded board, a judge cannot call it a peak, a sign or a variant of any earlier mark. |

## 7. Fonts
- Boards load free fonts (OFL or Apache) from Google Fonts by CSS only; nothing is downloaded.
- Each concept declares its families in `concept.json`.
- The two brands never share a family, and neither uses Overpass.
- The in-game UI type stays Overpass.

## 8. How a concept is submitted
- Concepts are drawn as real SVG, not only described.
- **Each concept has:**
  - a master mark on a 24-unit grid (`viewBox="0 0 24 24"`, fills only);
  - a lockup with the wordmark in the declared typeface;
  - a hand-fitted 16 px icon (`viewBox="0 0 16 16"`);
  - its construction sentence;
  - the output of `node tools/brand/checks.mjs <svg> --brand studio|game` (and `--icon` for the 16 px file);
  - a one-sentence motion idea for the loading box: the mark builds from its construction in at most 1.2 s, or a
    loop of at most 2 s (no spinning, no pulsing opacity).
- Work in the folder you are given under `test-results/brand/`. Render sheets with
  `node tools/brand/render.mjs <page.html> <prefix>`, or headless Edge directly.
