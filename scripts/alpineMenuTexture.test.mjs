import { describe, expect, it } from 'vitest';
import { bakeAlpineTile } from './alpineMenuTexture.mjs';

function fixture({ x = 2663, y = 5767, z = 14, height = () => 2200, color = [170, 166, 155] } = {}) {
  const size = 16, width = size * 3;
  const cover = new Uint8ClampedArray(width * width * 4), terrain = new Uint8ClampedArray(cover.length);
  for (let row = 0; row < width; row++) for (let col = 0; col < width; col++) {
    const i = (row * width + col) * 4;
    cover.set([...color, 255], i);
    const encoded = height((x - 1) * size + col, (y - 1) * size + row) + 32768;
    terrain.set([Math.floor(encoded / 256), Math.floor(encoded % 256), Math.round((encoded % 1) * 256), 255], i);
  }
  return { size, z, x, y, cover, terrain };
}

describe('alpine menu material bake', () => {
  it('is deterministic and leaves source cover and elevation unchanged', () => {
    const input = fixture();
    const cover = input.cover.slice(), terrain = input.terrain.slice();
    expect(bakeAlpineTile(input)).toEqual(bakeAlpineTile(input));
    expect(input.cover).toEqual(cover);
    expect(input.terrain).toEqual(terrain);
  });

  it('snow covers upper open bowls, with exposed steep rock and evergreen valleys', () => {
    const red = input => bakeAlpineTile(input)[(8 * 16 + 8) * 4];
    expect(red(fixture())).toBeGreaterThan(235);
    expect(red(fixture({ height: () => 1200, color: [82, 114, 89] }))).toBeLessThan(70);
    expect(red(fixture({ height: col => 2200 + (col - 2663 * 16 - 8) * 400 }))).toBeLessThan(185);
  });

  it('does not turn high elevation water into snow', () => {
    const output = bakeAlpineTile(fixture({ color: [82, 140, 164] }));
    expect(output[0]).toBeLessThan(100);
    expect(output[2]).toBeGreaterThan(output[0]);
  });

  it.each([[1, 0, 14], [0, 1, 14], [1, 0, 18], [0, 1, 18]])(
    'produces identical overlapping pixels across adjacent tiles (%i, %i), zoom %i', (dx, dy, z) => {
    const height = (col, row) => 1700 + (col - 42608) * 0.7 + (row - 92272) * 0.4;
    const a = bakeAlpineTile({ ...fixture({ height, z }), padding: 2 });
    const b = bakeAlpineTile({ ...fixture({ x: 2663 + dx, y: 5767 + dy, height, z }), padding: 2 });
    // Compare the same global pixels in both gutters, not adjacent pixel values.
    for (let along = 2; along < 18; along++) for (let across = 0; across < 4; across++) {
      const ai = ((dy ? 16 + across : along) * 20 + (dx ? 16 + across : along)) * 4;
      const bi = ((dy ? across : along) * 20 + (dx ? across : along)) * 4;
      expect(a.slice(ai, ai + 4)).toEqual(b.slice(bi, bi + 4));
    }
  });
});
