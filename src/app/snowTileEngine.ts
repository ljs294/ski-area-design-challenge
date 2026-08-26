import { sampleSnowGrid } from '../snow';
import type { SnowGrid } from '../types/snow';
import { snowRgba, type SnowDisplayMode } from './snowStyle';

function pixelLngLat(z: number, x: number, y: number, px: number, py: number): [number, number] {
  const n = 2 ** z;
  const xf = (x + (px + 0.5) / 256) / n;
  const yf = (y + (py + 0.5) / 256) / n;
  return [xf * 360 - 180, Math.atan(Math.sinh(Math.PI * (1 - 2 * yf))) * 180 / Math.PI];
}

export function renderSnowTilePixels(grid: SnowGrid, z: number, x: number, y: number,
  mode: SnowDisplayMode): Uint8ClampedArray {
  const data = new Uint8ClampedArray(256 * 256 * 4);
  for (let py = 0; py < 256; py++) for (let px = 0; px < 256; px++) {
    const [lng, lat] = pixelLngLat(z, x, y, px, py);
    const sample = sampleSnowGrid(grid, lng, lat);
    const rgba = sample ? snowRgba(sample.depthM, sample.surface, mode) : [0, 0, 0, 0];
    const index = (py * 256 + px) * 4;
    data[index] = rgba[0]; data[index + 1] = rgba[1];
    data[index + 2] = rgba[2]; data[index + 3] = rgba[3];
  }
  return data;
}
