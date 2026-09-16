import { expect, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';
import { jumpTo, layerIds, pointAt, sourceFeatureCount, visibilityOf } from '../support/mapProbe';

const run = (id: string, name: string, x: number) => ({ id, name,
  parts: [{ polygon: [[[x - .00012, 46.903], [x + .00012, 46.903], [x + .00012, 46.907], [x - .00012, 46.907], [x - .00012, 46.903]]],
    centerline: [[x, 46.907], [x, 46.905], [x, 46.903]], centerlineElevM: [1500, 1300, 1200] }],
  brushWidthM: 24, areaM2: 11000, lengthM: 445, verticalM: 300, avgSlopeDeg: 20, maxSlopeDeg: 25,
  difficulty: 'blue', status: 'complete', createdAt: '2026-01-01T00:00:00.000Z' });

for (const theme of ['light', 'dark']) test(`docked bar, connected tabs and pinned inspectors in ${theme}`, async ({ page }, info) => {
  await seedPreparedResort(page, { trails: [run('summit', 'Summit Ridge', -121.494), run('timber', 'Timberline', -121.492)] });
  await page.evaluate((theme) => localStorage.setItem('skiapp:settings', JSON.stringify({ theme, reducedMotion: true })), theme);
  await page.reload();
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
  await jumpTo(page, [-121.495, 46.905], 16);
  const bar = await page.locator('.game-toolbar').boundingBox();
  expect(bar!.x).toBe(0); expect(bar!.width).toBeLessThan(1920); expect(bar!.width).toBeGreaterThan(300); expect(bar!.y + bar!.height).toBe(1080);
  expect(bar!.height).toBe(38);
  const alpha = await page.locator('.game-toolbar').evaluate((element) => {
    const color = getComputedStyle(element).backgroundColor;
    return color.startsWith('rgba') ? Number(color.split(',').at(-1)!.replace(')', '')) : 1;
  });
  expect(alpha).toBeCloseTo(.68, 2);
  const cameraButtons = page.locator('.maplibregl-ctrl-bottom-right .maplibregl-ctrl-group button, .view3d-map-control button');
  await expect(cameraButtons).toHaveCount(4);
  const cameraBoxes = await cameraButtons.evaluateAll((elements) => elements.map((element) => {
    const box = element.getBoundingClientRect(); return { top: box.top, bottom: box.bottom };
  }));
  cameraBoxes.sort((a, b) => a.top - b.top);
  for (let index = 1; index < cameraBoxes.length; index++) expect(cameraBoxes[index].top).toBeGreaterThanOrEqual(cameraBoxes[index - 1].bottom);
  await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
  await expect(page.locator('.game-window-header')).not.toContainText('⠿');
  const liftTab = page.getByRole('tab', { name: 'Lifts', exact: true });
  await expect(liftTab).toHaveAttribute('aria-selected', 'true');
  await liftTab.focus(); await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Trails', exact: true })).toBeFocused();
  await expect(page.getByRole('tab', { name: 'Trails', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByTitle('View Summit Ridge', { exact: true }).click();
  await page.getByRole('button', { name: 'Pin Summit Ridge', exact: true }).click();
  await page.getByTitle('View Timberline', { exact: true }).click();
  await expect(page.getByRole('region', { name: 'Summit Ridge', exact: true })).toBeVisible();
  const summit = await page.getByRole('region', { name: 'Summit Ridge', exact: true }).boundingBox();
  await expect.poll(async () => (await page.getByRole('region', { name: 'Timberline', exact: true }).boundingBox())!.x)
    .toBeGreaterThanOrEqual(summit!.x + summit!.width + 7);
  await page.getByRole('button', { name: 'Close Timberline', exact: true }).click();
  await page.getByRole('button', { name: 'Dashboards', exact: true }).click();
  await expect(page.getByRole('tablist', { name: 'Dashboard views' }).getByRole('tab')).toHaveCount(4);
  await expect(page.getByRole('region', { name: 'Summit Ridge', exact: true })).toBeVisible();
  const handle = page.getByLabel('Move Dashboards', { exact: true });
  const initial = await page.getByRole('region', { name: 'Dashboards', exact: true }).boundingBox();
  await handle.focus(); await page.keyboard.press('ArrowDown');
  await expect.poll(async () => (await page.getByRole('region', { name: 'Dashboards', exact: true }).boundingBox())!.y).toBe(initial!.y + 8);
  await page.keyboard.press('ArrowUp');
  await page.getByRole('button', { name: 'Fit dashboard', exact: true }).click();
  await expect.poll(() => page.evaluate(() => !(window as unknown as { appMap: import('maplibre-gl').Map }).appMap.isMoving())).toBe(true);
  await page.screenshot({ path: info.outputPath(`review-${theme}.png`) });
});

test('game popup frames use the shared toolbox radius', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });

  const statusRadius = await page.locator('.game-toolbar').evaluate((element) => ({
    topLeft: getComputedStyle(element).borderTopLeftRadius,
    topRight: getComputedStyle(element).borderTopRightRadius,
    bottomRight: getComputedStyle(element).borderBottomRightRadius,
    bottomLeft: getComputedStyle(element).borderBottomLeftRadius,
  }));

  await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
  const toolbox = page.getByRole('region', { name: 'Toolbox', exact: true });
  await expect(toolbox).toBeVisible();
  const toolboxRadius = await toolbox.evaluate((element) => getComputedStyle(element).borderRadius);
  expect(toolboxRadius).toBe('4px');
  expect(statusRadius).toEqual({ topLeft: '0px', topRight: toolboxRadius, bottomRight: '0px', bottomLeft: '0px' });

  const layersToggle = page.getByRole('button', { name: 'Layers', exact: true });
  await layersToggle.click();
  const layers = page.locator('.game-window--menu[aria-label="Layers"]');
  await expect(layers).toBeVisible();
  const [layersToggleBox, layersBox] = await Promise.all([layersToggle.boundingBox(), layers.boundingBox()]);
  expect(layersBox!.y).toBeCloseTo(layersToggleBox!.y + layersToggleBox!.height + 6, 1);
  expect(layersBox!.x + layersBox!.width).toBeCloseTo(layersToggleBox!.x + layersToggleBox!.width, 1);
  const mapNodes = layers.getByRole('checkbox', { name: 'Map nodes', exact: true });
  await expect(mapNodes).toBeChecked();
  await mapNodes.click();
  await expect(mapNodes).not.toBeChecked();
  await expect.poll(() => page.evaluate(() => (window as unknown as { appMap: import('maplibre-gl').Map })
    .appMap.getLayoutProperty('ski-nodes', 'visibility'))).toBe('none');
  await mapNodes.click();
  await expect(mapNodes).toBeChecked();
  await expect.poll(() => page.evaluate(() => (window as unknown as { appMap: import('maplibre-gl').Map })
    .appMap.getLayoutProperty('ski-nodes', 'visibility'))).toBe('visible');
  await layers.getByLabel('Move Layers', { exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await layers.getByRole('button', { name: 'Close Layers', exact: true }).click();
  await expect(layers).toHaveCount(0);
  await layersToggle.click();
  const reopenedLayersBox = await layers.boundingBox();
  expect(reopenedLayersBox!.x + reopenedLayersBox!.width).toBeCloseTo(layersToggleBox!.x + layersToggleBox!.width, 1);
  await layers.getByRole('button', { name: 'Close Layers', exact: true }).click();

  const appearanceToggle = page.getByRole('button', { name: 'Theme and map colors', exact: true });
  await appearanceToggle.click();
  const appearance = page.getByRole('dialog', { name: 'Theme and map colors', exact: true });
  await expect(appearance).toBeVisible();
  expect(await appearance.evaluate((element) => getComputedStyle(element).borderRadius)).toBe(toolboxRadius);
  const [toggleBox, appearanceBox] = await Promise.all([appearanceToggle.boundingBox(), appearance.boundingBox()]);
  expect(appearanceBox!.y).toBeGreaterThanOrEqual(toggleBox!.y + toggleBox!.height);
  expect(appearanceBox!.x + appearanceBox!.width).toBeCloseTo(toggleBox!.x + toggleBox!.width, 1);
  for (const label of ['Cupertino', 'Mountain View', 'Classic', 'Night Vision', 'Wireframe', 'Blueprint', 'Custom']) {
    await expect(appearance.getByRole('button', { name: label, exact: true })).toBeVisible();
  }
  await appearance.getByRole('button', { name: 'Blueprint', exact: true }).click();
  await expect(appearance.getByRole('button', { name: 'Blueprint', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('skiapp:settings') ?? '{}').mapColorPreset)).toBe('blueprint');
  await appearance.getByRole('button', { name: 'Custom', exact: true }).click();
  await expect(appearance.getByLabel('Land', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');

  await page.getByRole('button', { name: 'Menu', exact: true }).click();
  const menu = page.locator('.game-menu-pop');
  await expect(menu).toBeVisible();
  expect(await menu.evaluate((element) => getComputedStyle(element).borderRadius)).toBe(toolboxRadius);
  await page.keyboard.press('Escape');

  const settingsToggle = page.getByRole('button', { name: 'Settings', exact: true });
  await settingsToggle.click();
  const settings = page.getByRole('dialog', { name: 'Settings', exact: true });
  await expect(settings).toBeVisible();
  expect(await settings.evaluate((element) => getComputedStyle(element).borderRadius)).toBe(toolboxRadius);
  const [settingsToggleBox, settingsBox] = await Promise.all([settingsToggle.boundingBox(), settings.boundingBox()]);
  expect(settingsBox!.y).toBeCloseTo(settingsToggleBox!.y + settingsToggleBox!.height + 6, 1);
  expect(settingsBox!.x + settingsBox!.width).toBeCloseTo(settingsToggleBox!.x + settingsToggleBox!.width, 1);
});

const MAP_NODE_LAYERS = [
  'ski-nodes', 'ski-node-labels', 'trail-junctions', 'trail-junction-labels',
] as const;

const mapNodeTrail = (id: string, name: string, x: number) => ({
  id, name,
  parts: [{
    polygon: [[[x - 0.0003, 46.9035], [x + 0.0003, 46.9035],
      [x + 0.0003, 46.9065], [x - 0.0003, 46.9035]]],
    centerline: [[x, 46.906], [x, 46.904]], centerlineElevM: [1030, 1000],
    segments: [{ id: `${id}:segment`, centerline: [[x, 46.906], [x, 46.904]],
      centerlineElevM: [1030, 1000], fromJunctionId: id === 'map-node-west'
        ? 'junction-seeded' : `${id}:start`, toJunctionId: `${id}:end` }],
  }],
  brushWidthM: 40, areaM2: 20_000, lengthM: 222, verticalM: 30,
  avgSlopeDeg: 8, maxSlopeDeg: 12, difficulty: 'blue', status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
});

test('Map nodes hides all persistent node layers through style reload while snap feedback remains available',
  async ({ page }) => {
    await seedPreparedResort(page, {
      trails: [mapNodeTrail('map-node-west', 'West Run', -121.496),
        mapNodeTrail('map-node-east', 'East Run', -121.494)],
      nodes: [{ id: 'map-node', name: 'Midway Hut', point: [-121.495, 46.905],
        elevM: 1015, createdAt: '2026-01-01T00:00:00.000Z' }],
      junctions: [{ id: 'junction-seeded', point: [-121.496, 46.906], elevM: 1030,
        createdAt: '2026-01-01T00:00:00.000Z' }],
    });
    await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
    await jumpTo(page, [-121.495, 46.905], 16);

    await expect.poll(() => sourceFeatureCount(page, 'node-paths')).toBeGreaterThanOrEqual(2);
    const sourceCount = await sourceFeatureCount(page, 'node-paths');
    const sourceBefore = await page.evaluate(() => {
      const source = (window as unknown as { appMap: {
        getSource(id: string): { serialize(): { data?: unknown } } | undefined;
      } }).appMap.getSource('node-paths');
      return (source?.serialize().data as { features?: Array<{ properties?: { id?: string } }> } | undefined)
        ?.features?.map((feature) => feature.properties?.id) ?? [];
    });
    expect(sourceBefore).toEqual(expect.arrayContaining(['map-node', 'junction-seeded']));

    await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
    await page.getByRole('button', { name: 'Layers', exact: true }).click();
    const layers = page.locator('.game-window--menu[aria-label="Layers"]');
    await expect(layers).toBeVisible();
    const mapNodes = layers.getByRole('checkbox', { name: 'Map nodes', exact: true });
    await expect(mapNodes).toBeChecked();
    await mapNodes.uncheck();
    for (const id of MAP_NODE_LAYERS) await expect.poll(() => visibilityOf(page, id)).toBe('none');

    await page.evaluate(() => new Promise<void>((resolve) => {
      const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
      const style = map.getStyle();
      map.once('style.load', () => resolve());
      map.setStyle({ version: 8, glyphs: style.glyphs, sources: {},
        layers: [{ id: 'mp-paper', type: 'background',
          paint: { 'background-color': '#e8e5dc' } }] }, { diff: false });
    }));
    await expect.poll(() => layerIds(page)).toEqual(expect.arrayContaining([...MAP_NODE_LAYERS]));
    for (const id of MAP_NODE_LAYERS) await expect.poll(() => visibilityOf(page, id)).toBe('none');
    await expect.poll(() => sourceFeatureCount(page, 'node-paths')).toBe(sourceCount);

    await layers.getByRole('button', { name: 'Close Layers', exact: true }).click();
    await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
    await page.getByRole('tab', { name: 'Trails', exact: true }).click();
    await page.getByRole('button', { name: /Draw path/ }).click();
    const trailPoint = await pointAt(page, [-121.496, 46.905]);
    await page.mouse.move(trailPoint.x, trailPoint.y);
    await expect.poll(async () => page.evaluate(() => {
      const source = (window as unknown as { appMap: {
        getSource(id: string): { serialize(): { data?: unknown } } | undefined;
      } }).appMap.getSource('node-path-draft');
      const data = source?.serialize().data as { features?: Array<{ properties?: { kind?: string } }> } | undefined;
      return data?.features?.some((feature) => feature.properties?.kind === 'highlight') ?? false;
    })).toBe(true);
    for (const id of MAP_NODE_LAYERS) await expect.poll(() => visibilityOf(page, id)).toBe('none');

    await page.keyboard.press('Escape');
  });

test('dashboard orientation lock preserves pan and zoom, and scale remains reachable', async ({ page }, info) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
  await page.evaluate(() => (window as unknown as { appMap: import('maplibre-gl').Map }).appMap.jumpTo({ pitch: 45, bearing: 25, zoom: 15 }));
  await page.getByRole('button', { name: 'Dashboards', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { appMap: import('maplibre-gl').Map }).appMap.getPitch())).toBe(0);
  await page.evaluate(() => (window as unknown as { appMap: import('maplibre-gl').Map }).appMap.jumpTo({ center: [-121.496, 46.906], zoom: 16 }));
  const dashboardCamera = await page.evaluate(() => { const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    return { center: map.getCenter().toArray(), zoom: map.getZoom() }; });
  await page.getByRole('button', { name: 'Close Dashboards', exact: true }).click();
  const camera = await page.evaluate(() => { const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    return { center: map.getCenter().toArray(), zoom: map.getZoom(), pitch: map.getPitch(), bearing: map.getBearing() }; });
  expect(camera.pitch).toBe(45); expect(camera.bearing).toBe(25);
  expect(camera.zoom).toBeCloseTo(dashboardCamera.zoom, 6);
  expect(camera.center[0]).toBeCloseTo(dashboardCamera.center[0], 6);
  expect(camera.center[1]).toBeCloseTo(dashboardCamera.center[1], 6);
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  const scale = page.getByRole('slider', { name: 'Interface scale' });
  await scale.fill('150');
  await expect(scale).toHaveAttribute('aria-valuetext', '150%');
  await page.getByRole('button', { name: 'Close settings', exact: true }).click();
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
  const panel = await page.getByRole('region', { name: 'Toolbox', exact: true }).boundingBox();
  expect(panel!.x + panel!.width).toBeLessThanOrEqual(1280);
  expect(panel!.y + panel!.height).toBeLessThanOrEqual(720 - 57);
  await page.screenshot({ path: info.outputPath('compact-150.png') });
});
