// Pure rendering function: draw bucketed elevation isolines from a dense
// row-major height grid directly onto a canvas 2D context. Reads the grid
// by direct index (no bilinear interpolation) since the display grid is
// already dense enough to scan at a stride.

export interface ContourOptions {
  intervalMeters?: number; // spacing between minor contour lines
  primaryIntervalMeters?: number; // spacing between brighter index lines
  minorColor?: string;
  majorColor?: string;
  scanStride?: number; // sample every Nth grid cell along each axis
}

export function drawContours(
  ctx: CanvasRenderingContext2D,
  heights: number[],
  gridSize: number,
  mapSize: number,
  options: ContourOptions = {}
): void {
  const stride = options.scanStride ?? 4;
  const interval = options.intervalMeters ?? 40;
  const primaryInterval = options.primaryIntervalMeters ?? 200;
  const minorColor = options.minorColor ?? 'rgba(42, 42, 42, 0.05)';
  const majorColor = options.majorColor ?? 'rgba(42, 42, 42, 0.16)';

  const lastIndex = gridSize - 1;
  const scanCount = Math.floor(lastIndex / stride) + 1;

  const gridIndexAt = (step: number) => Math.min(lastIndex, step * stride);
  const canvasCoordAt = (gridIndex: number) => (gridIndex / lastIndex) * mapSize;

  ctx.lineWidth = 1;

  for (let sr = 0; sr < scanCount; sr++) {
    const r = gridIndexAt(sr);
    const y = canvasCoordAt(r);

    for (let sc = 0; sc < scanCount; sc++) {
      const c = gridIndexAt(sc);
      const x = canvasCoordAt(c);
      const h = heights[r * gridSize + c];

      if (sc < scanCount - 1) {
        const cRight = gridIndexAt(sc + 1);
        const xRight = canvasCoordAt(cRight);
        const hRight = heights[r * gridSize + cRight];

        if (Math.floor(h / interval) !== Math.floor(hRight / interval)) {
          const crossVal = Math.floor(Math.max(h, hRight) / interval) * interval;
          const isPrimary = crossVal % primaryInterval === 0;
          ctx.strokeStyle = isPrimary ? majorColor : minorColor;
          ctx.beginPath();
          ctx.moveTo(x, y);
          ctx.lineTo(xRight, y);
          ctx.stroke();
        }
      }

      if (sr < scanCount - 1) {
        const rDown = gridIndexAt(sr + 1);
        const yDown = canvasCoordAt(rDown);
        const hDown = heights[rDown * gridSize + c];

        if (Math.floor(h / interval) !== Math.floor(hDown / interval)) {
          const crossVal = Math.floor(Math.max(h, hDown) / interval) * interval;
          const isPrimary = crossVal % primaryInterval === 0;
          ctx.strokeStyle = isPrimary ? majorColor : minorColor;
          ctx.beginPath();
          ctx.moveTo(x, y);
          ctx.lineTo(x, yDown);
          ctx.stroke();
        }
      }
    }
  }
}
