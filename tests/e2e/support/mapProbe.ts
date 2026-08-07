import { expect } from '@playwright/test';
import type { Page } from '@playwright/test';

/** The live map, as `MapView` publishes it for deterministic browser tests. */
interface ProbeMap {
  getStyle(): { layers?: { id: string }[] };
  getLayoutProperty(layerId: string, name: string): unknown;
  project(lngLat: [number, number]): { x: number; y: number };
  jumpTo(options: { center: [number, number]; zoom: number }): void;
  getSource(id: string): { serialize(): { data?: unknown } } | undefined;
}

type CountedSource = { setData(data: unknown): unknown };

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

export const sourceFeatureCount = (page: Page, sourceId: string): Promise<number> =>
  page.evaluate((id) => {
    const source = (window as unknown as { appMap: ProbeMap }).appMap.getSource(id);
    const data = source?.serialize().data as { features?: unknown[] } | undefined;
    return data?.features?.length ?? 0;
  }, sourceId);

/** Count source writes without changing their behavior. Install only after the
 * style has created the sources being measured. */
export const countSourceUpdates = (page: Page, sourceIds: string[]): Promise<void> =>
  page.evaluate((ids) => {
    const target = window as unknown as {
      appMap: ProbeMap; appSourceUpdateCounts: Record<string, number>;
    };
    target.appSourceUpdateCounts = Object.fromEntries(ids.map((id) => [id, 0]));
    for (const id of ids) {
      const source = target.appMap.getSource(id) as unknown as CountedSource | undefined;
      if (!source) throw new Error(`Missing source ${id}`);
      const setData = source.setData.bind(source);
      source.setData = (data) => {
        target.appSourceUpdateCounts[id] += 1;
        return setData(data);
      };
    }
  }, sourceIds);

export const sourceUpdateCounts = (page: Page): Promise<Record<string, number>> =>
  page.evaluate(() => (window as unknown as {
    appSourceUpdateCounts: Record<string, number>;
  }).appSourceUpdateCounts);

export const setCaptureTransients = (page: Page, hidden: boolean): Promise<void> =>
  page.evaluate((nextHidden) => {
    (window as unknown as { appSetCaptureTransients: (value: boolean) => void })
      .appSetCaptureTransients(nextHidden);
  }, hidden);

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
