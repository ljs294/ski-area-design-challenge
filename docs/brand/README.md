# Brand: Twin Peaks

The owner chose this brand for Alpine Labs and Ski Area Design Challenge on 2026-10-11 (task P2-03.1). The history is
in `docs/plans/brand/`:
- `logo-options.html`: the board where Twin Peaks was drawn, reviewed in five adversarial rounds, and refined at the
  owner's notes.
- `brief.md`: a later fresh-concept round that the owner judged too bland, kept for its lessons.

## The marks

**The mark.** Two crisp peaks in the proportions of the demo mountain's real twin summit:
- The lower peak stands in front, cut free by one clean gap.
- Every outer flank is exactly 2:1.
- The mark sits on a 24-unit grid with its ground line at 21, so the base lands on whole pixels at 16, 24, 32, 48, 64 and 256.

| Mark | What it is | Where |
| --- | --- | --- |
| **Game mark** | The pair, with a trail winding down the tall peak and a straight lift bar up the front one. The trail is straight legs joined by true arcs, 1.08 wide; the lift and the gap are 1.0. | The app icon at 48 and above; the game's lockup. |
| **Game mark, small cut** | The same, with every line 1.25. | 32 to 64 px. |
| **Plain pair** | The two peaks with a 1.5 gap. | The studio's mark; the game's 16 and 24 px icons, where the lines would turn to grey scratches. |
| **Game lockup** | The game mark, then "Ski Area" over "Design Challenge" in Overpass Black. The A of Area is a peak at the mark's angle, its point level with the back summit; the second line sits on the ground line. | The loading box, the title name plate, Credits. |
| **Alpine Labs lockup** | The plain pair. Alpine's A is a third peak rising from behind them: it stands on their ground line with its summit level with the front one, and the front peak's flank stands in for its left leg. "lpine Labs" follows in Overpass ExtraBold on the ground line. | The loading box's studio line; a plate of its own in the title's bottom-right corner, beside Credits, Settings and Quit. |
| **Alpine Labs, small** | The plain pair with an ordinary A. | Below 32 px tall, where the tucked A merges into the peaks. |
| **Studio name** | "Alpine Labs" alone, its A a cap-height peak. | A master only: the game no longer shows it. |

## Files
- **Masters:** `docs/brand/masters/*.svg`, in ink (#1f1c19). These are for print and the store, and for the app icon
  at the first release (task 10, with the company name).
- **In the game:** `Assets/MountainPlanner/Art/UI/Brand/Resources/Brand/*.svg`, white single-fill layers.
  - They import as vector images that keep their viewBox (`Editor/HudIconImport.cs`).
  - `Flow.uss` sizes each one to its viewBox and tints it: ink on the sign-white plates (the name plate and Alpine
    Labs' corner plate), `var(--strong)` in the loading box and Credits.
  - `Tests/EditMode/BrandTests.cs` checks all of this.
- **Regenerate:** run `python tools/brand/export.py`. It needs fontTools and shapely.
  - The geometry is in `tools/brand/twinpeaks.py`.
  - The wordmarks are outlined from `tools/brand/fonts/Overpass[wght].ttf` (SIL Open Font License; the licence text
    is next to it), with the font's own kerning and -0.012 em tracking.

## Rules

**Clear space**
- **Space:** keep at least the height of the front peak's gap-to-summit distance clear round a lockup, about a
  quarter of the mark's height.
- **Never crowd:** don't let anything sit closer.

**Minimum sizes**
- **Game lockup:** 120 px wide.
- **Alpine Labs lockup:** 32 px tall. Below that, use the small version.
- **Game mark:** 32 px. Below that, use the plain pair.

**Colour**
- **One colour, always:** ink on light grounds, white on dark.
- **No orange:** it means planned work in the game's UI and is used for nothing else.
- **No added colours:** don't add green or blue to the marks; those colours also mean trail ratings.

**Don't**
- Redraw the peaks, change the 2:1 flanks, or move the lift or trail.
- Add outlines, shadows, gradients or glows.
- Set the wordmark in live type: use the outlined lockups.
- Separate the Alpine Labs A from the peaks, or use the tucked A without the peaks behind it.

**Trademark:** "Alpine" is a crowded name. A camera-gear company has traded as Alpine Labs. Get a professional
trademark search before the first public release.
