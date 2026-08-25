export type RenderQuality = 'performance' | 'standard' | 'high' | 'ultra';

export interface RenderProfile {
  quality: RenderQuality;
  maxBackingPixels: number;
  terrainMaxZoom: 14 | 15;
  tileLod: {
    maxZoomLevelsOnScreen: number;
    tileCountMaxMinRatio: number;
  };
  imageryMaxSide: 2048 | 3072 | 4000;
  coverMode: 'raster' | 'vector';
  coverVertexBudget: 0 | 150_000 | 250_000;
  derivedCacheBytes: number;
  tileWorkerCount: 1 | 2;
  hillshade: 'none' | 'overhead' | 'full';
  contourLabels: boolean;
  menu: 'css' | 'still' | 'drift-30' | 'drift-60';
}

const MIB = 1024 * 1024;

const PROFILES: Record<RenderQuality, RenderProfile> = {
  performance: {
    quality: 'performance',
    maxBackingPixels: 4 * MIB,
    terrainMaxZoom: 14,
    tileLod: { maxZoomLevelsOnScreen: 12, tileCountMaxMinRatio: 1.5 },
    imageryMaxSide: 2048,
    coverMode: 'raster',
    coverVertexBudget: 0,
    derivedCacheBytes: 64 * MIB,
    tileWorkerCount: 1,
    hillshade: 'none',
    contourLabels: false,
    menu: 'css',
  },
  standard: {
    quality: 'standard',
    maxBackingPixels: 8 * MIB,
    terrainMaxZoom: 15,
    tileLod: { maxZoomLevelsOnScreen: 9, tileCountMaxMinRatio: 2 },
    imageryMaxSide: 3072,
    coverMode: 'vector',
    coverVertexBudget: 150_000,
    derivedCacheBytes: 128 * MIB,
    tileWorkerCount: 1,
    hillshade: 'overhead',
    contourLabels: true,
    menu: 'still',
  },
  high: {
    quality: 'high',
    maxBackingPixels: 12 * MIB,
    terrainMaxZoom: 15,
    tileLod: { maxZoomLevelsOnScreen: 4, tileCountMaxMinRatio: 3 },
    imageryMaxSide: 4000,
    coverMode: 'vector',
    coverVertexBudget: 250_000,
    derivedCacheBytes: 192 * MIB,
    tileWorkerCount: 2,
    hillshade: 'full',
    contourLabels: true,
    menu: 'drift-30',
  },
  ultra: {
    quality: 'ultra',
    maxBackingPixels: 16 * MIB,
    terrainMaxZoom: 15,
    tileLod: { maxZoomLevelsOnScreen: 2, tileCountMaxMinRatio: 6 },
    imageryMaxSide: 4000,
    coverMode: 'vector',
    coverVertexBudget: 250_000,
    derivedCacheBytes: 256 * MIB,
    tileWorkerCount: 2,
    hillshade: 'full',
    contourLabels: true,
    menu: 'drift-60',
  },
};

export function isRenderQuality(value: unknown): value is RenderQuality {
  return value === 'performance' || value === 'standard' || value === 'high' || value === 'ultra';
}

export function renderProfileFor(quality: RenderQuality): RenderProfile {
  return PROFILES[quality];
}

function preferredPixelRatio(quality: RenderQuality, devicePixelRatio: number): number {
  switch (quality) {
    case 'performance': return Math.min(devicePixelRatio, 1);
    case 'standard': return Math.min(devicePixelRatio, 2);
    case 'high': return Math.min(2.5, Math.max(1.5, devicePixelRatio));
    case 'ultra': return Math.min(3, Math.max(2, devicePixelRatio));
  }
}

export interface PixelRatioOptions {
  width?: number;
  height?: number;
  devicePixelRatio?: number;
  maxCanvasSize?: number;
}

/** Resolve a deterministic backing-store ratio without selecting a tier automatically. */
export function pixelRatioFor(quality: RenderQuality, options: PixelRatioOptions = {}): number {
  const browserWindow = typeof window === 'undefined' ? undefined : window;
  const width = Math.max(1, options.width ?? browserWindow?.innerWidth ?? 1);
  const height = Math.max(1, options.height ?? browserWindow?.innerHeight ?? 1);
  const devicePixelRatio = Math.max(0.01, options.devicePixelRatio ?? browserWindow?.devicePixelRatio ?? 1);
  const maxCanvasSize = Math.max(1, options.maxCanvasSize ?? 4096);
  const profile = renderProfileFor(quality);
  return Math.min(
    preferredPixelRatio(quality, devicePixelRatio),
    Math.sqrt(profile.maxBackingPixels / (width * height)),
    maxCanvasSize / width,
    maxCanvasSize / height,
  );
}

export function pixelRatioForElement(quality: RenderQuality, element: HTMLElement): number {
  return pixelRatioFor(quality, { width: element.clientWidth, height: element.clientHeight });
}
