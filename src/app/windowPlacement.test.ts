import { describe, expect, it } from 'vitest';
import { clampWindow, placeWindow, placeWindowBelowAnchor } from './windowPlacement';
describe('floating window placement', () => {
  it('opens at one origin and stacks adjacent windows to the right', () => {
    const viewport = { width: 1920, height: 1040 }, size = { width: 340, height: 400 };
    const first = placeWindow(size, [], viewport), second = placeWindow(size, [first], viewport);
    expect(first).toEqual({ ...size, x: 8, y: 8 });
    expect(second).toEqual({ ...size, x: 356, y: 8 });
  });
  it('wraps to another row when the next column does not fit', () => {
    expect(placeWindow({ width: 340, height: 220 }, [{ x: 8, y: 8, width: 340, height: 300 }],
      { width: 600, height: 700 })).toEqual({ x: 8, y: 316, width: 340, height: 220 });
  });
  it('keeps dragged windows and their actions inside a scaled viewport', () => {
    expect(clampWindow({ x: 900, y: 600, width: 340, height: 500 }, { width: 850, height: 440 }))
      .toEqual({ x: 502, y: 8, width: 340, height: 424 });
  });
  it('opens an anchored window below the anchor with aligned right edges', () => {
    expect(placeWindowBelowAnchor({ width: 340, height: 400 },
      { x: 1690, y: 8, width: 30, height: 30 }, { width: 1920, height: 1040 }))
      .toEqual({ x: 1380, y: 44, width: 340, height: 400 });
  });
});
