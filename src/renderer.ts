import type { GameState, ResortNode, Lift, Trail, Skier, ActiveTool, TerrainDB } from './types';
import { computeHillshade } from './hillshade';
import { drawContours } from './contours';

export interface Camera {
  x: number;
  y: number;
  zoom: number;
}

const MAP_SIZE = 2000; // Physical dimensions of the map area, in game-world units

/**
 * Main renderer class for drawing the mountain simulation onto the 2D HTML5 canvas.
 */
export class GameRenderer {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private terrainLayer: HTMLCanvasElement | null = null;
  private terrainLayerKey: string | null = null;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d')!;
  }

  /**
   * Precompute the hillshade + contour composite for the given terrain onto
   * an offscreen canvas. Call this once when terrain changes (e.g. right
   * after ingest, before entering the game view) — NOT per frame.
   */
  public setTerrain(terrain: TerrainDB | null): void {
    if (!terrain) {
      this.terrainLayer = null;
      this.terrainLayerKey = null;
      return;
    }
    if (this.terrainLayerKey === terrain.key) return; // already cached

    this.terrainLayer = this.buildTerrainLayer(terrain);
    this.terrainLayerKey = terrain.key;
  }

  private buildTerrainLayer(terrain: TerrainDB): HTMLCanvasElement {
    const layer = document.createElement('canvas');
    layer.width = MAP_SIZE;
    layer.height = MAP_SIZE;
    const lctx = layer.getContext('2d')!;

    // 1. Flat base fill
    lctx.fillStyle = '#f4f3ec';
    lctx.fillRect(0, 0, MAP_SIZE, MAP_SIZE);

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

    // 3. Contour isolines on top
    drawContours(lctx, terrain.displayHeights, terrain.displayGridSize, MAP_SIZE);

    // 4. Outer boundary
    lctx.strokeStyle = 'rgba(42, 42, 42, 0.15)';
    lctx.lineWidth = 1;
    lctx.strokeRect(0, 0, MAP_SIZE, MAP_SIZE);

    return layer;
  }

  /**
   * Primary render loop call
   */
  public draw(
    state: GameState, 
    camera: Camera, 
    hoveredId: string | null, 
    selectedId: string | null,
    dragStartPoint: { x: number; y: number } | null,
    currentMouseWorld: { x: number; y: number } | null
  ): void {
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

    // 1. Draw the precomputed terrain layer (hillshade + contours), or an
    // empty placeholder grid if no terrain has been ingested yet.
    this.setTerrain(state.terrain);
    if (this.terrainLayer) {
      ctx.drawImage(this.terrainLayer, 0, 0);
    } else {
      this.drawEmptyGrid();
    }

    // 2. Draw Active Placement Preview (if drawing a trail or lift)
    if (dragStartPoint && currentMouseWorld && state.activeTool !== 'select') {
      this.drawPlacementPreview(state.activeTool, dragStartPoint, currentMouseWorld);
    }

    // 3. Draw Trails (vector lines)
    this.drawTrails(state.trails, hoveredId, selectedId);

    // 4. Draw Lifts (double cable lines & terminals)
    this.drawLifts(state.lifts, state.nodes, hoveredId, selectedId);

    // 5. Draw Resort Nodes (intersections, lodges, etc.)
    this.drawNodes(state.nodes, hoveredId, selectedId);

    // 6. Draw Skiers (moving dots)
    this.drawSkiers(state.skiers);

    ctx.restore();
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
    ctx.fillText('No Terrain Ingested. Click "Ingest Custom Mountain" below to load map.', 1000, 1000);
  }

  /**
   * Draw preview line while user dragging to build trail/lift
   */
  private drawPlacementPreview(tool: ActiveTool, start: { x: number; y: number }, end: { x: number; y: number }): void {
    const ctx = this.ctx;
    ctx.lineWidth = 3;
    ctx.setLineDash([6, 6]);

    if (tool.startsWith('trail')) {
      if (tool === 'trail-green') ctx.strokeStyle = 'rgba(16, 185, 129, 0.6)';
      else if (tool === 'trail-blue') ctx.strokeStyle = 'rgba(59, 130, 246, 0.6)';
      else ctx.strokeStyle = 'rgba(243, 244, 246, 0.6)';
    } else if (tool.startsWith('lift')) {
      ctx.strokeStyle = 'rgba(168, 85, 247, 0.6)';
    }

    ctx.beginPath();
    ctx.moveTo(start.x, start.y);
    ctx.lineTo(end.x, end.y);
    ctx.stroke();
    ctx.setLineDash([]); // Reset
  }

  /**
   * Render trails (colored vector paths)
   */
  private drawTrails(trails: Trail[], hoveredId: string | null, selectedId: string | null): void {
    const ctx = this.ctx;

    for (const trail of trails) {
      const isHovered = hoveredId === trail.id;
      const isSelected = selectedId === trail.id;

      // Color mapping
      let color = '#3b82f6'; // Blue
      if (trail.difficulty === 'green') color = '#10b981'; // Emerald
      else if (trail.difficulty === 'black') color = '#f3f4f6'; // White/Black diamond
      else if (trail.difficulty === 'double_black') color = '#ef4444'; // Red/Expert

      // Shadow overlay for selected/hovered trails
      if (isSelected || isHovered) {
        ctx.strokeStyle = isSelected ? 'rgba(99, 102, 241, 0.4)' : 'rgba(255, 255, 255, 0.15)';
        ctx.lineWidth = isSelected ? 8 : 6;
        ctx.beginPath();
        this.traceTrailPath(trail);
        ctx.stroke();
      }

      // Base trail line
      ctx.strokeStyle = color;
      ctx.lineWidth = trail.difficulty === 'black' || trail.difficulty === 'double_black' ? 2.5 : 3;
      
      // Black diamond dashes pattern
      if (trail.difficulty === 'black') {
        ctx.strokeStyle = '#111827';
        ctx.lineWidth = 3.5;
        ctx.beginPath();
        this.traceTrailPath(trail);
        ctx.stroke();
        
        ctx.strokeStyle = '#f3f4f6';
        ctx.lineWidth = 1.5;
        ctx.setLineDash([5, 5]);
      } else if (trail.difficulty === 'double_black') {
        ctx.strokeStyle = '#111827';
        ctx.lineWidth = 4;
        ctx.beginPath();
        this.traceTrailPath(trail);
        ctx.stroke();
        
        ctx.strokeStyle = '#ef4444';
        ctx.lineWidth = 2;
        ctx.setLineDash([2, 4]);
      }

      ctx.beginPath();
      this.traceTrailPath(trail);
      ctx.stroke();
      ctx.setLineDash([]); // Reset

      // Small directional flow arrow in the middle
      if (trail.points.length > 2) {
        const midIndex = Math.floor(trail.points.length / 2);
        const p1 = trail.points[midIndex - 1];
        const p2 = trail.points[midIndex];
        this.drawArrow(p1.x, p1.y, p2.x, p2.y, color);
      }
    }
  }

  private traceTrailPath(trail: Trail): void {
    const ctx = this.ctx;
    if (trail.points.length === 0) return;
    
    ctx.moveTo(trail.points[0].x, trail.points[0].y);
    for (let i = 1; i < trail.points.length; i++) {
      ctx.lineTo(trail.points[i].x, trail.points[i].y);
    }
  }

  /**
   * Render lifts (terminals, haul ropes, and chairs)
   */
  private drawLifts(lifts: Lift[], nodes: ResortNode[], hoveredId: string | null, selectedId: string | null): void {
    const ctx = this.ctx;

    for (const lift of lifts) {
      const isHovered = hoveredId === lift.id;
      const isSelected = selectedId === lift.id;

      const startNode = nodes.find(n => n.id === lift.sourceNodeId);
      const endNode = nodes.find(n => n.id === lift.targetNodeId);
      if (!startNode || !endNode) continue;

      // Glow support
      if (isSelected || isHovered) {
        ctx.strokeStyle = isSelected ? 'rgba(168, 85, 247, 0.3)' : 'rgba(255, 255, 255, 0.1)';
        ctx.lineWidth = isSelected ? 8 : 6;
        ctx.beginPath();
        ctx.moveTo(startNode.x, startNode.y);
        ctx.lineTo(endNode.x, endNode.y);
        ctx.stroke();
      }

      // Cable (double line)
      ctx.lineWidth = 1.2;
      ctx.strokeStyle = lift.isClosed ? '#4b5563' : '#a855f7';
      
      // Left cable offset
      const dx = endNode.x - startNode.x;
      const dy = endNode.y - startNode.y;
      const len = Math.sqrt(dx * dx + dy * dy);
      const nx = -dy / len * 4;
      const ny = dx / len * 4;

      ctx.beginPath();
      ctx.moveTo(startNode.x + nx, startNode.y + ny);
      ctx.lineTo(endNode.x + nx, endNode.y + ny);
      ctx.stroke();

      ctx.beginPath();
      ctx.moveTo(startNode.x - nx, startNode.y - ny);
      ctx.lineTo(endNode.x - nx, endNode.y - ny);
      ctx.stroke();

      // Render towers along the line
      const numTowers = Math.max(2, Math.floor(len / 180));
      ctx.fillStyle = '#6b7280';
      for (let i = 1; i < numTowers; i++) {
        const t = i / numTowers;
        const tx = startNode.x + dx * t;
        const ty = startNode.y + dy * t;
        
        ctx.beginPath();
        ctx.arc(tx, ty, 3.5, 0, Math.PI * 2);
        ctx.fill();
        
        // Tower crossbar
        ctx.strokeStyle = '#4b5563';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(tx + nx * 1.5, ty + ny * 1.5);
        ctx.lineTo(tx - nx * 1.5, ty - ny * 1.5);
        ctx.stroke();
      }

      // Draw chairs moving along cables
      for (const chair of lift.chairs) {
        const cx = startNode.x + dx * chair.progress;
        const cy = startNode.y + dy * chair.progress;
        
        // Offset chair to side cable
        const ox = chair.direction === 'up' ? nx : -nx;
        const oy = chair.direction === 'up' ? ny : -ny;

        ctx.fillStyle = lift.isClosed ? '#374151' : '#ec4899'; // pink chairs!
        ctx.fillRect(cx + ox - 2.5, cy + oy - 2.5, 5, 5);
        
        // Draw passengers count indicator (small dot overlay)
        if (chair.passengers.length > 0) {
          ctx.fillStyle = '#fff';
          ctx.fillRect(cx + ox - 1.5, cy + oy - 1.5, 3, 3);
        }
      }
    }
  }

  /**
   * Render nodes (entrance, lodges, terminals)
   */
  private drawNodes(nodes: ResortNode[], hoveredId: string | null, selectedId: string | null): void {
    const ctx = this.ctx;

    for (const node of nodes) {
      const isHovered = hoveredId === node.id;
      const isSelected = selectedId === node.id;

      // Selection ring
      if (isSelected || isHovered) {
        ctx.strokeStyle = isSelected ? '#a855f7' : 'rgba(255, 255, 255, 0.4)';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(node.x, node.y, 14, 0, Math.PI * 2);
        ctx.stroke();
      }

      // Render base geometries based on node type
      if (node.type === 'lodge') {
        // Red lodge box
        ctx.fillStyle = '#ef4444';
        ctx.fillRect(node.x - 9, node.y - 9, 18, 18);
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 1.5;
        ctx.strokeRect(node.x - 9, node.y - 9, 18, 18);
        
        // Text label
        ctx.fillStyle = '#fff';
        ctx.font = '10px Inter';
        ctx.textAlign = 'center';
        ctx.fillText('LODGE', node.x, node.y - 12);
      } 
      else if (node.type === 'entrance') {
        // Cyan parking entrance box
        ctx.fillStyle = '#22d3ee';
        ctx.beginPath();
        ctx.moveTo(node.x, node.y - 10);
        ctx.lineTo(node.x + 9, node.y + 6);
        ctx.lineTo(node.x - 9, node.y + 6);
        ctx.closePath();
        ctx.fill();
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 1.2;
        ctx.stroke();

        ctx.fillStyle = '#fff';
        ctx.font = '10px Inter';
        ctx.textAlign = 'center';
        ctx.fillText('BASE', node.x, node.y - 13);
      }
      else if (node.type === 'lift_terminal') {
        // Purple terminal ring
        ctx.fillStyle = '#6366f1';
        ctx.beginPath();
        ctx.arc(node.x, node.y, 6, 0, Math.PI * 2);
        ctx.fill();
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 1;
        ctx.stroke();
      }
      else {
        // Tiny intersection dot
        ctx.fillStyle = '#9ca3af';
        ctx.beginPath();
        ctx.arc(node.x, node.y, 4, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }

  /**
   * Render skier agents (efficiently batching square rendering)
   */
  private drawSkiers(skiers: Skier[]): void {
    const ctx = this.ctx;
    
    // Group skiers by state for batch color styling
    const groups: { [key: string]: { x: number; y: number }[] } = {
      skiing: [],
      queuing: [],
      riding: [],
      resting: []
    };

    for (const skier of skiers) {
      if (skier.state === 'skiing') groups.skiing.push(skier);
      else if (skier.state === 'queuing') groups.queuing.push(skier);
      else if (skier.state === 'riding') groups.riding.push(skier);
      else if (skier.state === 'resting') groups.resting.push(skier);
    }

    // Draw Skiing agents (Gliding Downhill - Bright Cyan/Cyan)
    ctx.fillStyle = '#22d3ee';
    for (const pos of groups.skiing) {
      ctx.fillRect(pos.x - 1.5, pos.y - 1.5, 3, 3);
    }

    // Draw Queuing agents (Waiting at bottom - Orange alert)
    ctx.fillStyle = '#f59e0b';
    for (const pos of groups.queuing) {
      ctx.fillRect(pos.x - 1.5, pos.y - 1.5, 3, 3);
    }

    // Draw Riding agents (Faded Pink)
    ctx.fillStyle = 'rgba(236, 72, 153, 0.7)';
    for (const pos of groups.riding) {
      ctx.fillRect(pos.x - 1.2, pos.y - 1.2, 2.4, 2.4);
    }

    // Draw Resting agents in lodge (Yellow dots inside box)
    ctx.fillStyle = '#fbbf24';
    for (const pos of groups.resting) {
      ctx.fillRect(pos.x - 1.5, pos.y - 1.5, 3, 3);
    }
  }

  /**
   * Helper to draw skier flow vectors
   */
  private drawArrow(x1: number, y1: number, x2: number, y2: number, color: string): void {
    const ctx = this.ctx;
    const angle = Math.atan2(y2 - y1, x2 - x1);
    
    ctx.save();
    ctx.translate((x1 + x2) / 2, (y1 + y2) / 2);
    ctx.rotate(angle);
    
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.moveTo(-5, -3);
    ctx.lineTo(3, 0);
    ctx.lineTo(-5, 3);
    ctx.fill();
    
    ctx.restore();
  }
}
