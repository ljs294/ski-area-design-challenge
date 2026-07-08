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

export interface TerrainDB {
  latitude: number;
  longitude: number;
  widthMeters: number;
  heightMeters: number;
  gridSize: number; // resolution of grid, e.g. 50
  heights: number[]; // 1D array representing 2D grid: heights[row * gridSize + col]
  climate: ClimateProfile;
  mountainName: string;
}

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
