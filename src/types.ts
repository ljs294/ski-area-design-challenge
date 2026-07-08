// Mountain Planner - TypeScript Type Definitions

export interface ClimateMonth {
  tempHigh: number; // Fahrenheit
  tempLow: number;  // Fahrenheit
  snowProbability: number; // 0.0 - 1.0
  avgWindSpeed: number; // km/h
}

export interface ClimateProfile {
  monthly: ClimateMonth[];
}

export type AreaSizeMeters = 2000 | 4000 | 8000;

// Persisted shape — written to disk exactly as-is. Only the raw sampled grid
// is stored; the display grid is always recomputed on load (deterministic,
// cheap, and avoids multi-megabyte save files).
export interface TerrainRecord {
  schemaVersion: 2;
  key: string; // stable slug, see terrainStorageClient.ts
  mountainName: string;
  latitude: number; // center
  longitude: number; // center
  areaSizeMeters: AreaSizeMeters;
  sampleGridSize: number; // fixed 64
  sampleHeights: number[]; // row-major, sampleGridSize^2, meters
  climate: ClimateProfile;
  sourceType: 'live' | 'preset' | 'preset-real';
  createdAt: string; // ISO
  updatedAt: string; // ISO
}

// Runtime shape used by GameState/renderer — a TerrainRecord plus the
// bicubic-upscaled display grid built by terrainIngest.ts after download/load.
export interface TerrainDB extends TerrainRecord {
  displayGridSize: number; // fixed 512
  displayHeights: number[]; // bicubic-upscaled, row-major, displayGridSize^2
  widthMeters: number; // = areaSizeMeters
  heightMeters: number; // = areaSizeMeters
}

// Lightweight listing entry (no height data) for a future "Load Game" screen.
export type TerrainSummary = Pick<
  TerrainRecord,
  'key' | 'mountainName' | 'latitude' | 'longitude' | 'areaSizeMeters' | 'sourceType' | 'createdAt' | 'updatedAt'
>;

export type NodeType = 'entrance' | 'peak' | 'intersection' | 'lodge' | 'lift_terminal';

export interface ResortNode {
  id: string;
  name: string;
  type: NodeType;
  x: number; // game map coordinate
  y: number; // game map coordinate
  z: number; // elevation derived from TerrainDB
}

export type LiftType = 'surface' | 'fixed_grip' | 'detachable';

export interface Lift {
  id: string;
  name: string;
  type: LiftType;
  sourceNodeId: string;
  targetNodeId: string;
  speed: number; // pixels/s (or meters/s)
  capacity: number; // skiers per chair
  chairSpacing: number; // distance in pixels between chairs
  cost: number;
  maintenanceCost: number; // daily maintenance cost
  maxWindSpeed: number; // km/h, shutdown limit
  isClosed: boolean;
  
  // Simulation variables
  queue: string[]; // Skier IDs waiting at base
  chairs: {
    id: number;
    progress: number; // 0.0 - 1.0 along the line
    passengers: string[]; // Skier IDs riding this chair
    direction: 'up' | 'down';
  }[];
  
  // Future toggle
  isBidirectional: boolean;
}

export type TrailDifficulty = 'green' | 'blue' | 'black' | 'double_black';

export interface Trail {
  id: string;
  name: string;
  difficulty: TrailDifficulty;
  points: { x: number; y: number; z: number }[]; // sampled coordinates
  sourceNodeId: string;
  targetNodeId: string;
  length: number; // pixels (horizontal run)
  avgSlope: number; // grade percentage
  maxSlope: number; // grade percentage
  cost: number;
  maintenanceCost: number; // daily cost
  
  // Simulation variables
  snowDepth: number; // in cm
  groomingQuality: number; // 0.0 - 1.0 (effects crash rate and skier preference)
  skierDensities: { skierId: string; progress: number }[]; // Skier IDs and progress along trail
}

export type SkierState = 'spawning' | 'queuing' | 'riding' | 'skiing' | 'resting' | 'leaving' | 'deciding';
export type SkierSkill = 'beginner' | 'intermediate' | 'expert';

export interface Skier {
  id: string;
  skill: SkierSkill;
  energy: number; // 0 - 100 (depletes, restored at lodge)
  satisfaction: number; // 0 - 100 (impacted by lines, snow quality)
  cash: number; // budget remaining
  
  state: SkierState;
  currentNodeId: string;
  currentEdgeId: string; // Lift ID or Trail ID
  currentEdgeProgress: number; // 0.0 - 1.0 along lift/trail
  
  // Routing
  targetLodgeId?: string;
  routeQueue: string[]; // list of node IDs to visit sequentially
  lastActivityTime: number;
  recentTrails: string[]; // Trail IDs skied recently (to prevent repeats)
  
  // Graphic animation position
  x: number;
  y: number;
  z: number;
}

export interface Ledger {
  revenue: number;
  expenses: number;
  net: number;
}

export type ActiveTool = 
  | 'select' 
  | 'lift-surface' 
  | 'lift-fixed' 
  | 'lift-detachable' 
  | 'trail-green' 
  | 'trail-blue' 
  | 'trail-black' 
  | 'lodge' 
  | 'snowmaker';

export interface GameState {
  cash: number;
  nodes: ResortNode[];
  lifts: Lift[];
  trails: Trail[];
  skiers: Skier[];
  
  // Simulation parameters
  timeSpeed: number; // 0 = paused, 1 = 1x, 2 = 2x, 5 = 5x
  gameTimeMinutes: number; // time of day: 480 to 1020 (8:00 AM to 5:00 PM)
  gameMonth: number; // 0 = Jan, 11 = Dec
  gameDay: number;
  
  // Weather
  temperature: number;
  windSpeed: number;
  isSnowing: boolean;
  
  // Management
  groomerCount: number;
  patrolCount: number;
  ticketPrice: number;
  
  // Current UI Tool
  activeTool: ActiveTool;
  
  // Ingested Terrain
  terrain: TerrainDB | null;
  
  // Financial Records
  ledger: Ledger;
}
