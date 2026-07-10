import type { TerrainDB, RoadClass } from './types';
import { computeHillshade } from './hillshade';
import { buildContourTiers, blendedTiers, type ContourTier } from './contours';
import { queryTileIndex, type TileIndex, type Segment } from './tileIndex';
import { drawWorldLabels, type WorldLabel } from './labels';
import type { RoadSegment, WaterLineSegment, ProjectedPeak } from './vectorFeatures';
import type { WorldPoint } from './geo';

export interface Camera {
  x: number;
  y: number;
  zoom: number;
}

export const MAP_SIZE = 2000; // Physical dimensions of the map area, in game-world units

// Minor lines are light and thin; major (index) lines are a dark warm
// brown — the traditional USGS index-contour color — and noticeably
// thicker, so the major/minor hierarchy reads at a glance.
const MINOR_CONTOUR_COLOR = 'rgba(60, 55, 48, 0.20)';
const MAJOR_CONTOUR_COLOR = 'rgba(59, 36, 19, 0.8)';
const MINOR_LINE_WIDTH = 0.8;
const MAJOR_LINE_WIDTH = 2.4;

const LABEL_FONT = '11px "Outfit", sans-serif';
const LABEL_COLOR = 'rgba(59, 36, 19, 0.95)';
const LABEL_HALO_COLOR = 'rgba(244, 243, 236, 0.9)';

const METERS_TO_FEET = 3.280839895;

// Land cover — flat semi-transparent tints baked into the base raster
// (blended before the hillshade multiply pass, so relief still reads
// through them) rather than sprite-scattered trees/rocks. Cheap: one-time
// composite cost, no new per-frame rendering.
const LAND_COVER_COLORS: Record<'forest' | 'grass' | 'scrub' | 'rock', string> = {
  forest: 'rgba(45, 74, 43, 0.38)',
  grass: 'rgba(150, 168, 92, 0.22)',
  scrub: 'rgba(122, 132, 78, 0.24)',
  rock: 'rgba(110, 105, 98, 0.30)',
};

// Water polygons are baked into the raster too, but drawn *after* the
// hillshade pass (flat, not tinted through it) so lakes read as a clean
// distinct color rather than mottled by the relief shading underneath.
const WATER_FILL_COLOR = 'rgba(94, 142, 173, 0.92)';
const WATER_OUTLINE_COLOR = 'rgba(48, 79, 105, 0.9)';

// Rivers/streams and roads are drawn per-frame as tile-indexed vector
// strokes (same crisp-at-any-zoom approach as contour lines), since unlike
// water polygons they're 1D and need to stay legible at every zoom level.
const WATER_LINE_COLOR = 'rgba(70, 120, 155, 0.85)';
const RIVER_LINE_WIDTH = 2.2;
const STREAM_LINE_WIDTH = 1.1;

const ROAD_COLORS: Record<RoadClass, string> = {
  major: 'rgba(140, 70, 25, 0.9)',
  minor: 'rgba(120, 95, 60, 0.8)',
  path: 'rgba(120, 95, 60, 0.45)',
};
const ROAD_WIDTHS: Record<RoadClass, number> = {
  major: 3.2,
  minor: 1.8,
  path: 0.9,
};

const PEAK_MARKER_COLOR = 'rgba(50, 45, 40, 0.9)';
const PEAK_MARKER_HALO_COLOR = 'rgba(244, 243, 236, 0.9)';
const PEAK_MARKER_SCREEN_RADIUS = 4;
const PEAK_LABEL_FONT = 'bold 11px "Outfit", sans-serif';
const PEAK_LABEL_COLOR = 'rgba(40, 32, 25, 0.95)';
// World-unit offset placing the label above the marker — scales slightly
// with zoom rather than staying a fixed screen gap, an accepted
// simplification (same tradeoff every other world-space label here makes).
const PEAK_LABEL_Y_OFFSET = -16;

const ROAD_LABEL_STYLE = { font: '10px "Outfit", sans-serif', color: 'rgba(90, 55, 20, 0.95)', haloColor: 'rgba(244, 243, 236, 0.9)', haloWidth: 2.5 };
const WATER_LABEL_STYLE = { font: 'italic 10px "Outfit", sans-serif', color: 'rgba(35, 70, 100, 0.95)', haloColor: 'rgba(244, 243, 236, 0.9)', haloWidth: 2.5 };

// Elevation-tinted terrain base (onX Backcountry-style: green valleys ->
// tan subalpine -> pale rock/alpine near the summit), multiplied with the
// hillshade below for relief. Real hydrology (streams) would need a new
// data source (USGS NHD flowlines) — out of scope here, this is purely a
// recoloring of the existing elevation data.
const ELEVATION_COLOR_STOPS: { t: number; rgb: [number, number, number] }[] = [
  { t: 0.0, rgb: [149, 163, 115] },
  { t: 0.35, rgb: [175, 181, 130] },
  { t: 0.55, rgb: [199, 186, 140] },
  { t: 0.75, rgb: [217, 207, 183] },
  { t: 1.0, rgb: [236, 233, 224] },
];

function elevationRampColor(t: number): [number, number, number] {
  const stops = ELEVATION_COLOR_STOPS;
  if (t <= stops[0].t) return stops[0].rgb;
  const last = stops[stops.length - 1];
  if (t >= last.t) return last.rgb;

  for (let i = 0; i < stops.length - 1; i++) {
    const a = stops[i];
    const b = stops[i + 1];
    if (t >= a.t && t <= b.t) {
      const f = (t - a.t) / (b.t - a.t);
      return [
        a.rgb[0] + (b.rgb[0] - a.rgb[0]) * f,
        a.rgb[1] + (b.rgb[1] - a.rgb[1]) * f,
        a.rgb[2] + (b.rgb[2] - a.rgb[2]) * f,
      ];
    }
  }
  return last.rgb;
}

/** Traces a (possibly multi-ring) polygon path — ring 0 is the outer
 * boundary, further rings are holes — ready for an evenodd fill/stroke. */
function fillPolygonPath(ctx: CanvasRenderingContext2D, rings: WorldPoint[][]): void {
  ctx.beginPath();
  for (const ring of rings) {
    if (ring.length === 0) continue;
    ctx.moveTo(ring[0].x, ring[0].y);
    for (let i = 1; i < ring.length; i++) ctx.lineTo(ring[i].x, ring[i].y);
    ctx.closePath();
  }
}

/**
 * Renders a terrain's hillshade + contour composite onto a 2D canvas, with
 * pan/zoom applied via the given Camera. Purely a map viewer — no game
 * state, entities, or simulation drawn here.
 */
export class GameRenderer {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private terrainLayer: HTMLCanvasElement | null = null;
  private terrainLayerKey: string | null = null;
  private contourTiers: ContourTier[] | null = null;
  private currentTerrain: TerrainDB | null = null;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d')!;
  }

  /**
   * Precompute the hillshade raster and the contour vector tiers for the
   * given terrain. Call this once when terrain changes (e.g. right after
   * ingest, before entering the game view) — NOT per frame.
   */
  public setTerrain(terrain: TerrainDB | null): void {
    this.currentTerrain = terrain;
    if (!terrain) {
      this.terrainLayer = null;
      this.terrainLayerKey = null;
      this.contourTiers = null;
      return;
    }
    if (this.terrainLayerKey === terrain.key) return; // already cached

    this.terrainLayer = this.buildTerrainLayer(terrain);
    this.contourTiers = buildContourTiers(terrain.sampleHeights, terrain.sampleGridSize, MAP_SIZE);
    this.terrainLayerKey = terrain.key;
  }

  /** Elevation in meters at a world-space (0..MAP_SIZE) point, bilinearly
   * sampled from the display grid, or null if there's no terrain / the
   * point falls outside it. */
  public elevationAt(worldX: number, worldY: number): number | null {
    const terrain = this.currentTerrain;
    if (!terrain) return null;

    const gridSize = terrain.displayGridSize;
    const fx = (worldX / MAP_SIZE) * (gridSize - 1);
    const fy = (worldY / MAP_SIZE) * (gridSize - 1);
    if (fx < 0 || fy < 0 || fx > gridSize - 1 || fy > gridSize - 1) return null;

    const x0 = Math.floor(fx);
    const y0 = Math.floor(fy);
    const x1 = Math.min(gridSize - 1, x0 + 1);
    const y1 = Math.min(gridSize - 1, y0 + 1);
    const tx = fx - x0;
    const ty = fy - y0;

    const h = terrain.displayHeights;
    const h00 = h[y0 * gridSize + x0];
    const h10 = h[y0 * gridSize + x1];
    const h01 = h[y1 * gridSize + x0];
    const h11 = h[y1 * gridSize + x1];
    const top = h00 + (h10 - h00) * tx;
    const bottom = h01 + (h11 - h01) * tx;
    return top + (bottom - top) * ty;
  }

  private buildTerrainLayer(terrain: TerrainDB): HTMLCanvasElement {
    const layer = document.createElement('canvas');
    layer.width = MAP_SIZE;
    layer.height = MAP_SIZE;
    const lctx = layer.getContext('2d')!;

    // 1. Elevation-tinted base (green valley -> tan -> pale alpine rock)
    let hMin = Infinity;
    let hMax = -Infinity;
    for (const h of terrain.displayHeights) {
      if (h < hMin) hMin = h;
      if (h > hMax) hMax = h;
    }
    const range = hMax - hMin || 1;

    const baseCanvas = document.createElement('canvas');
    baseCanvas.width = terrain.displayGridSize;
    baseCanvas.height = terrain.displayGridSize;
    const bctx = baseCanvas.getContext('2d')!;
    const baseImageData = bctx.createImageData(terrain.displayGridSize, terrain.displayGridSize);
    for (let i = 0; i < terrain.displayHeights.length; i++) {
      const t = (terrain.displayHeights[i] - hMin) / range;
      const [r, g, b] = elevationRampColor(t);
      baseImageData.data[i * 4] = r;
      baseImageData.data[i * 4 + 1] = g;
      baseImageData.data[i * 4 + 2] = b;
      baseImageData.data[i * 4 + 3] = 255;
    }
    bctx.putImageData(baseImageData, 0, 0);
    lctx.drawImage(baseCanvas, 0, 0, MAP_SIZE, MAP_SIZE);

    // 1.5 Land cover tint — drawn before the hillshade pass so relief still
    // reads through forest/rock/grass, not as a flat overlay on top of it.
    for (const poly of terrain.hydratedFeatures.landCover) {
      lctx.fillStyle = LAND_COVER_COLORS[poly.landCoverClass];
      fillPolygonPath(lctx, poly.rings);
      lctx.fill('evenodd');
    }

    // 2. Hillshade — computed at native display-grid resolution, then
    // scaled up in a single drawImage call.
    const cellSizeMeters = terrain.areaSizeMeters / (terrain.displayGridSize - 1);
    const shade = computeHillshade(terrain.displayHeights, terrain.displayGridSize, cellSizeMeters);

    const shadeCanvas = document.createElement('canvas');
    shadeCanvas.width = terrain.displayGridSize;
    shadeCanvas.height = terrain.displayGridSize;
    const sctx = shadeCanvas.getContext('2d')!;
    const imageData = sctx.createImageData(terrain.displayGridSize, terrain.displayGridSize);
    for (let i = 0; i < shade.length; i++) {
      const gray = Math.round(shade[i] * 255);
      imageData.data[i * 4] = gray;
      imageData.data[i * 4 + 1] = gray;
      imageData.data[i * 4 + 2] = gray;
      imageData.data[i * 4 + 3] = 255;
    }
    sctx.putImageData(imageData, 0, 0);

    lctx.save();
    lctx.globalCompositeOperation = 'multiply';
    lctx.globalAlpha = 0.4;
    lctx.drawImage(shadeCanvas, 0, 0, MAP_SIZE, MAP_SIZE);
    lctx.restore();

    // 2.5 Water bodies — flat fill on top of the shaded relief (not tinted
    // through it like land cover), so lakes/rivers read as a clean, distinct
    // color rather than mottled by hillshade.
    for (const poly of terrain.hydratedFeatures.waterPolygons) {
      fillPolygonPath(lctx, poly.rings);
      lctx.fillStyle = WATER_FILL_COLOR;
      lctx.fill('evenodd');
      lctx.strokeStyle = WATER_OUTLINE_COLOR;
      lctx.lineWidth = 1.5;
      lctx.stroke();
    }

    // 3. Outer boundary
    lctx.strokeStyle = 'rgba(42, 42, 42, 0.15)';
    lctx.lineWidth = 1;
    lctx.strokeRect(0, 0, MAP_SIZE, MAP_SIZE);

    return layer;
  }

  /**
   * Primary render call — draws the cached hillshade raster under the given
   * camera transform, then strokes the contour tier matching the current
   * zoom as fresh vector lines (so they stay crisp at any zoom, unlike the
   * raster), or an empty placeholder grid if no terrain is set.
   */
  public draw(terrain: TerrainDB | null, camera: Camera): void {
    const ctx = this.ctx;
    const width = this.canvas.width;
    const height = this.canvas.height;

    // Clear canvas with the cream backdrop (visible when panned/zoomed past the terrain layer bounds)
    ctx.fillStyle = '#eae8de';
    ctx.fillRect(0, 0, width, height);

    ctx.save();

    // Apply camera transformation (pan and zoom)
    ctx.translate(camera.x, camera.y);
    ctx.scale(camera.zoom, camera.zoom);

    this.setTerrain(terrain);
    if (this.terrainLayer && this.currentTerrain) {
      ctx.drawImage(this.terrainLayer, 0, 0);
      const features = this.currentTerrain.hydratedFeatures;

      let blends: { tier: ContourTier; weight: number }[] = [];
      if (this.contourTiers) {
        // Near a zoom threshold this returns both the outgoing and
        // incoming tier with complementary weights, so the LOD switch
        // reads as a smooth cross-fade rather than a hard swap.
        blends = blendedTiers(this.contourTiers, camera.zoom);
        for (const { tier, weight } of blends) {
          if (weight <= 0.02) continue;
          this.drawContourTier(tier, camera, width, height, weight);
        }
      }

      // Water lines and roads draw over contours (so they read clearly
      // against the terrain) but under every label pass.
      this.drawWaterLines(features.waterLineIndex, camera, width, height);
      this.drawRoads(features.roadIndex, camera, width, height);
      this.drawPeakMarkers(features.peaks, camera);

      for (const { tier, weight } of blends) {
        if (weight <= 0.02) continue;
        this.drawContourLabels(tier, camera, width, height, weight);
      }
      drawWorldLabels(ctx, features.roadLabels, camera, width, height, 1, ROAD_LABEL_STYLE);
      drawWorldLabels(ctx, features.waterLabels, camera, width, height, 1, WATER_LABEL_STYLE);
      this.drawPeakLabels(features.peaks, camera, width, height);
    } else {
      this.drawEmptyGrid();
    }

    ctx.restore();
  }

  private drawContourTier(
    tier: ContourTier,
    camera: Camera,
    canvasWidth: number,
    canvasHeight: number,
    weight: number
  ): void {
    // Visible world-space bounds — segments are spatially tile-indexed
    // (contours.ts), so querying only touches segments near the viewport
    // instead of the full (potentially millions-long) segment list every
    // frame. That indexing, not this bounds math, is what keeps large,
    // dense grids fast to pan/zoom.
    const { minX, maxX, minY, maxY } = this.viewportWorldBounds(camera, canvasWidth, canvasHeight);

    const minorSegs = queryTileIndex(tier.minorIndex, minX, maxX, minY, maxY);
    const majorSegs = queryTileIndex(tier.majorIndex, minX, maxX, minY, maxY);

    const ctx = this.ctx;
    const prevAlpha = ctx.globalAlpha;
    ctx.globalAlpha = weight;
    this.strokeSegments(ctx, minorSegs, MINOR_CONTOUR_COLOR, MINOR_LINE_WIDTH / camera.zoom);
    this.strokeSegments(ctx, majorSegs, MAJOR_CONTOUR_COLOR, MAJOR_LINE_WIDTH / camera.zoom);
    ctx.globalAlpha = prevAlpha;
  }

  /** Viewport bounds in world space, for tile-index viewport queries. */
  private viewportWorldBounds(camera: Camera, canvasWidth: number, canvasHeight: number) {
    return {
      minX: -camera.x / camera.zoom,
      maxX: (canvasWidth - camera.x) / camera.zoom,
      minY: -camera.y / camera.zoom,
      maxY: (canvasHeight - camera.y) / camera.zoom,
    };
  }

  private drawWaterLines(index: TileIndex<WaterLineSegment>, camera: Camera, canvasWidth: number, canvasHeight: number): void {
    const { minX, maxX, minY, maxY } = this.viewportWorldBounds(camera, canvasWidth, canvasHeight);
    const segs = queryTileIndex(index, minX, maxX, minY, maxY);
    const rivers = segs.filter((s) => s.waterClass === 'river');
    const streams = segs.filter((s) => s.waterClass === 'stream');
    this.strokeSegments(this.ctx, streams, WATER_LINE_COLOR, STREAM_LINE_WIDTH / camera.zoom);
    this.strokeSegments(this.ctx, rivers, WATER_LINE_COLOR, RIVER_LINE_WIDTH / camera.zoom);
  }

  private drawRoads(index: TileIndex<RoadSegment>, camera: Camera, canvasWidth: number, canvasHeight: number): void {
    const { minX, maxX, minY, maxY } = this.viewportWorldBounds(camera, canvasWidth, canvasHeight);
    const segs = queryTileIndex(index, minX, maxX, minY, maxY);

    const byClass: Record<RoadClass, RoadSegment[]> = { major: [], minor: [], path: [] };
    for (const seg of segs) byClass[seg.roadClass].push(seg);

    // Paths/minor roads first, major roads last, so major roads paint on
    // top of any coincident lower-class segment at intersections.
    for (const roadClass of ['path', 'minor', 'major'] as RoadClass[]) {
      this.strokeSegments(this.ctx, byClass[roadClass], ROAD_COLORS[roadClass], ROAD_WIDTHS[roadClass] / camera.zoom);
    }
  }

  private drawPeakMarkers(peaks: ProjectedPeak[], camera: Camera): void {
    if (peaks.length === 0) return;
    const ctx = this.ctx;
    const radius = PEAK_MARKER_SCREEN_RADIUS / camera.zoom;

    ctx.fillStyle = PEAK_MARKER_COLOR;
    ctx.strokeStyle = PEAK_MARKER_HALO_COLOR;
    ctx.lineWidth = 1 / camera.zoom;
    for (const peak of peaks) {
      ctx.beginPath();
      ctx.moveTo(peak.x, peak.y - radius);
      ctx.lineTo(peak.x + radius, peak.y + radius);
      ctx.lineTo(peak.x - radius, peak.y + radius);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
    }
  }

  private drawPeakLabels(peaks: ProjectedPeak[], camera: Camera, canvasWidth: number, canvasHeight: number): void {
    if (peaks.length === 0) return;
    const labels: WorldLabel[] = peaks.map((peak) => ({
      x: peak.x,
      y: peak.y + PEAK_LABEL_Y_OFFSET,
      angle: 0,
      text:
        peak.elevationMeters != null
          ? `${peak.name} · ${Math.round(peak.elevationMeters * METERS_TO_FEET).toLocaleString()}ft`
          : peak.name,
    }));
    drawWorldLabels(this.ctx, labels, camera, canvasWidth, canvasHeight, 1, {
      font: PEAK_LABEL_FONT,
      color: PEAK_LABEL_COLOR,
      haloColor: LABEL_HALO_COLOR,
      haloWidth: 3,
    });
  }

  private strokeSegments(
    ctx: CanvasRenderingContext2D,
    segments: Segment[],
    color: string,
    lineWidth: number
  ): void {
    ctx.strokeStyle = color;
    ctx.lineWidth = lineWidth;
    ctx.beginPath();
    for (const seg of segments) {
      ctx.moveTo(seg.x1, seg.y1);
      ctx.lineTo(seg.x2, seg.y2);
    }
    ctx.stroke();
  }

  /**
   * Elevation labels for the major (index) lines of the active tier. Drawn
   * in an identity-transformed pass (screen space, not world space) so text
   * stays a fixed, legible size regardless of zoom, with world->screen
   * projected by hand from the camera.
   */
  private drawContourLabels(
    tier: ContourTier,
    camera: Camera,
    canvasWidth: number,
    canvasHeight: number,
    weight: number
  ): void {
    drawWorldLabels(this.ctx, tier.labels, camera, canvasWidth, canvasHeight, weight, {
      font: LABEL_FONT,
      color: LABEL_COLOR,
      haloColor: LABEL_HALO_COLOR,
      haloWidth: 3,
    });
  }

  private drawEmptyGrid(): void {
    const ctx = this.ctx;
    ctx.strokeStyle = 'rgba(42, 42, 42, 0.05)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= MAP_SIZE; i += 100) {
      ctx.beginPath(); ctx.moveTo(i, 0); ctx.lineTo(i, MAP_SIZE); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(0, i); ctx.lineTo(MAP_SIZE, i); ctx.stroke();
    }

    ctx.fillStyle = 'rgba(42, 42, 42, 0.3)';
    ctx.font = '14px Outfit';
    ctx.textAlign = 'center';
    ctx.fillText('No terrain loaded.', MAP_SIZE / 2, MAP_SIZE / 2);
  }
}
