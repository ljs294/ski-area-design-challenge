import { expect } from '@playwright/test';
import type { Page } from '@playwright/test';

/** The live map, as `MapView` publishes it for deterministic browser tests. */
interface ProbeMap {
  getStyle(): { layers?: { id: string }[] };
  getLayoutProperty(layerId: string, name: string): unknown;
  project(lngLat: [number, number]): { x: number; y: number };
  jumpTo(options: { center: [number, number]; zoom: number }): void;
}

/** Every layer in the live style, bottom to top. */
export const layerIds = (page: Page): Promise<string[]> =>
  page.evaluate(() => {
    const map = (window as unknown as { appMap: ProbeMap }).appMap;
    return (map.getStyle().layers ?? []).map((layer) => layer.id);
  });

export const visibilityOf = (page: Page, layerId: string): Promise<unknown> =>
  page.evaluate(
    (id) => (window as unknown as { appMap: ProbeMap }).appMap.getLayoutProperty(id, 'visibility'),
    layerId,
  );

/** Put a coordinate under the middle of the viewport, without animating. */
export const jumpTo = (page: Page, center: [number, number], zoom: number): Promise<void> =>
  page.evaluate(
    (options) => (window as unknown as { appMap: ProbeMap }).appMap.jumpTo(options),
    { center, zoom },
  );

/** Viewport point for a map coordinate, offset by the canvas position. */
export async function pointAt(
  page: Page,
  lngLat: [number, number],
): Promise<{ x: number; y: number }> {
  const box = await page.locator('.maplibregl-canvas').boundingBox();
  expect(box, 'the map canvas must be laid out').not.toBeNull();
  const projected = await page.evaluate(
    (coordinate) => {
      const point = (window as unknown as { appMap: ProbeMap }).appMap.project(coordinate);
      return { x: point.x, y: point.y };
    },
    lngLat,
  );
  return { x: box!.x + projected.x, y: box!.y + projected.y };
}
