# UI parity: the HUD against the mockup

Zero-dependency Node 22 tools (task P2-02). See [docs/plans/hud-mockup-parity.md](../../docs/plans/hud-mockup-parity.md).

- `node tools/ui-parity/parity.mjs [--unity <capture folder>] [--out <folder>] [--only a,b] [--mockup-only]`:
  renders each state of [states.mjs](states.mjs) from `docs/plans/prototypes/ui-layout.html#solo&demo=p2,…` in headless Edge
  (a fresh profile, driven over the DevTools protocol), measures every `data-ui` part, compares it with the game's
  `-uicapture` `layout.json`, and writes `report.md` and side-by-side sheets. Exit code 1 when anything differs.
- `node tools/ui-parity/extract-icons.mjs`: the mockup's `<symbol>` set to the game's vector-image layers.

`demo.bat` 48 runs the captures and the comparison.
