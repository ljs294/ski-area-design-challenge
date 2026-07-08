import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { TerrainDB, ClimateProfile, ClimateMonth } from './types';

// Bounding box size: 4km (approximately 0.036 degrees latitude)
const BOX_LAT_SIZE = 0.036;

/**
 * Generate a procedural climate profile based on Latitude and Altitude
 */
export function generateProceduralClimate(latitude: number, avgAltitudeMeters: number): ClimateProfile {
  const monthly: ClimateMonth[] = [];
  const isNorthernHemisphere = latitude >= 0;
  const absLat = Math.abs(latitude);
  
  // Base temperature based on latitude (polar is colder, equator is hotter)
  // Latitude 0: 80°F, Latitude 45: 50°F, Latitude 90: -10°F
  const latBaseTemp = 85 - (absLat * 1.0); 

  // Elevation temperature lapse rate: roughly -6.5°C per 1000m (-3.5°F per 1000ft, which is ~11.5°F per 1000m)
  const altitudeCooling = (avgAltitudeMeters / 1000) * 11.5;
  const localBaseTemp = latBaseTemp - altitudeCooling;

  for (let month = 0; month < 12; month++) {
    // Temperature swing throughout the year
    // Northern hemisphere coldest in Jan (month 0), warmest in Jul (month 6)
    // Southern hemisphere reversed
    let seasonalFactor = Math.sin(((month - 3) / 12) * Math.PI * 2); // -1 in Jan, 1 in Jul
    if (!isNorthernHemisphere) {
      seasonalFactor = -seasonalFactor;
    }
    
    // Average monthly temp
    const avgTemp = localBaseTemp + (seasonalFactor * 25); // +/- 25 degrees seasonal swing
    const tempHigh = Math.round(avgTemp + 8);
    const tempLow = Math.round(avgTemp - 8);
    
    // Snow probability: high if low temperature is below freezing (32°F)
    let snowProbability = 0;
    if (tempLow < 32) {
      // Colder means more likely to be snow instead of rain
      const coldFactor = (32 - tempLow) / 30; // 0 at 32°F, 1.0 at 2°F
      snowProbability = Math.min(0.9, 0.1 + coldFactor * 0.7);
    }
    
    // Winter months have higher average wind speeds
    let avgWindSpeed = 12 + Math.abs(seasonalFactor) * 8; // 12km/h to 20km/h
    
    monthly.push({
      tempHigh,
      tempLow,
      snowProbability,
      avgWindSpeed: Math.round(avgWindSpeed)
    });
  }

  return { monthly };
}

/**
 * Bilinearly interpolate height Z at coordinates (u, v) in range [0, 1]
 */
export function getInterpolatedHeight(u: number, v: number, grid: number[], gridSize: number): number {
  // Clamp coordinates to [0, 1]
  u = Math.max(0, Math.min(1, u));
  v = Math.max(0, Math.min(1, v));

  const col = u * (gridSize - 1);
  const row = v * (gridSize - 1);

  const col0 = Math.floor(col);
  const col1 = Math.min(gridSize - 1, col0 + 1);
  const row0 = Math.floor(row);
  const row1 = Math.min(gridSize - 1, row0 + 1);

  const tx = col - col0;
  const ty = row - row0;

  const h00 = grid[row0 * gridSize + col0];
  const h10 = grid[row0 * gridSize + col1];
  const h01 = grid[row1 * gridSize + col0];
  const h11 = grid[row1 * gridSize + col1];

  const h0 = h00 * (1 - tx) + h10 * tx;
  const h1 = h01 * (1 - tx) + h11 * tx;

  return h0 * (1 - ty) + h1 * ty;
}

/**
 * Handles map selection, location searching, Leaflet layout, and API calling
 */
export class TerrainManager {
  private map: L.Map | null = null;
  private selectionBox: L.Rectangle | null = null;
  private onDataIngestedCallback: (data: TerrainDB) => void;
  
  constructor(onDataIngested: (data: TerrainDB) => void) {
    this.onDataIngestedCallback = onDataIngested;
  }

  public initMap(containerId: string): void {
    if (this.map) return;
    
    // Default to Aspen Mountain coordinates
    const defaultCenter: L.LatLngExpression = [39.1866, -106.8182];
    
    this.map = L.map(containerId, {
      zoomControl: true,
      attributionControl: false
    }).setView(defaultCenter, 13);
    
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 18,
    }).addTo(this.map);

    this.updateSelectionBox();

    // Listen for map movements to update the bounding box center
    this.map.on('move', () => {
      this.updateSelectionBox();
    });
  }

  private getBounds(): L.LatLngBounds | null {
    if (!this.map) return null;
    const center = this.map.getCenter();
    const latSize = BOX_LAT_SIZE;
    // Account for longitude shrinkage based on latitude
    const lonSize = BOX_LAT_SIZE / Math.cos(center.lat * Math.PI / 180);
    
    const southWest = L.latLng(center.lat - latSize / 2, center.lng - lonSize / 2);
    const northEast = L.latLng(center.lat + latSize / 2, center.lng + lonSize / 2);
    
    return L.latLngBounds(southWest, northEast);
  }

  private updateSelectionBox(): void {
    if (!this.map) return;
    
    const bounds = this.getBounds();
    if (!bounds) return;

    if (this.selectionBox) {
      this.selectionBox.setBounds(bounds);
    } else {
      this.selectionBox = L.rectangle(bounds, {
        color: '#6366f1',
        weight: 2,
        fillColor: '#6366f1',
        fillOpacity: 0.15,
        interactive: false
      }).addTo(this.map);
    }
    
    const statusText = document.getElementById('map-status-text');
    const ingestBtn = document.getElementById('btn-ingest-data') as HTMLButtonElement;
    
    const zoom = this.map.getZoom();
    if (zoom < 11) {
      if (statusText) statusText.innerText = 'Zoom in closer to select region!';
      if (ingestBtn) {
        ingestBtn.classList.add('disabled');
        ingestBtn.disabled = true;
      }
    } else {
      const center = this.map.getCenter();
      if (statusText) statusText.innerText = `Selected bounds centered at: ${center.lat.toFixed(4)}, ${center.lng.toFixed(4)}`;
      if (ingestBtn) {
        ingestBtn.classList.remove('disabled');
        ingestBtn.disabled = false;
      }
    }
  }

  public async searchLocation(query: string): Promise<boolean> {
    if (!query || !this.map) return false;
    
    try {
      const response = await fetch(`https://nominatim.openstreetmap.org/search?format=json&q=${encodeURIComponent(query)}`);
      const results = await response.json();
      
      if (results && results.length > 0) {
        const bestResult = results[0];
        const lat = parseFloat(bestResult.lat);
        const lon = parseFloat(bestResult.lon);
        this.map.setView([lat, lon], 13);
        return true;
      }
    } catch (e) {
      console.error('Geocoding search failed:', e);
    }
    return false;
  }

  public async downloadActiveTerrain(customName?: string): Promise<void> {
    if (!this.map) return;
    
    const bounds = this.getBounds();
    if (!bounds) return;
    
    const statusText = document.getElementById('map-status-text');
    if (statusText) statusText.innerText = 'Downloading elevation data...';
    
    const ingestBtn = document.getElementById('btn-ingest-data') as HTMLButtonElement;
    if (ingestBtn) {
      ingestBtn.disabled = true;
      ingestBtn.classList.add('disabled');
    }

    const southWest = bounds.getSouthWest();
    const northEast = bounds.getNorthEast();
    
    // We fetch a 20x20 grid to avoid URL limits and API throttling
    const resolution = 20;
    const latitudes: number[] = [];
    const longitudes: number[] = [];
    
    for (let r = 0; r < resolution; r++) {
      // Row (v coordinate) maps from South to North
      const latFraction = r / (resolution - 1);
      const lat = southWest.lat + (northEast.lat - southWest.lat) * latFraction;
      
      for (let c = 0; c < resolution; c++) {
        // Col (u coordinate) maps from West to East
        const lonFraction = c / (resolution - 1);
        const lon = southWest.lng + (northEast.lng - southWest.lng) * lonFraction;
        
        latitudes.push(lat);
        longitudes.push(lon);
      }
    }
    
    // Call the free Open-Meteo Elevation API
    try {
      // Batch coordinate call
      const latString = latitudes.join(',');
      const lonString = longitudes.join(',');
      const response = await fetch(`https://api.open-meteo.com/v1/elevation?latitude=${latString}&longitude=${lonString}`);
      const data = await response.json();
      
      if (data && data.elevation) {
        const rawHeights: number[] = data.elevation;
        
        // Calculate average altitude to anchor the climate profile
        const sum = rawHeights.reduce((a, b) => a + b, 0);
        const avgAlt = sum / rawHeights.length;
        
        // Generate name based on search query or lat/lon
        const center = this.map.getCenter();
        const mountainName = customName || `Mountain (${center.lat.toFixed(2)}, ${center.lng.toFixed(2)})`;
        
        const climate = generateProceduralClimate(center.lat, avgAlt);
        
        const terrainDB: TerrainDB = {
          latitude: center.lat,
          longitude: center.lng,
          widthMeters: 4000,
          heightMeters: 4000,
          gridSize: resolution,
          heights: rawHeights,
          climate,
          mountainName
        };
        
        // Save to IndexedDB
        this.saveTerrainToIndexedDB(terrainDB);
        
        if (statusText) statusText.innerText = 'Download successful!';
        this.onDataIngestedCallback(terrainDB);
      } else {
        throw new Error('Invalid elevation API response');
      }
    } catch (e) {
      console.error(e);
      if (statusText) statusText.innerText = 'Failed to load terrain. Check connection.';
      if (ingestBtn) {
        ingestBtn.disabled = false;
        ingestBtn.classList.remove('disabled');
      }
    }
  }

  // Pre-packaged offline maps to support offline play
  public loadPresetMountain(preset: 'chamonix' | 'aspen'): void {
    let lat = 0, lon = 0, name = '';
    let flatHeights: number[] = [];
    
    if (preset === 'chamonix') {
      lat = 45.9227;
      lon = 6.8685;
      name = 'Chamonix-Mont-Blanc';
      // Procedural height grid peaking in center
      flatHeights = this.generateProceduralHeights(1030, 2400, 20); 
    } else {
      lat = 39.1866;
      lon = -106.8182;
      name = 'Aspen Mountain';
      // Procedural ridge height grid
      flatHeights = this.generateProceduralHeights(2400, 3418, 20);
    }
    
    const sum = flatHeights.reduce((a, b) => a + b, 0);
    const avgAlt = sum / flatHeights.length;
    const climate = generateProceduralClimate(lat, avgAlt);
    
    const terrainDB: TerrainDB = {
      latitude: lat,
      longitude: lon,
      widthMeters: 4000,
      heightMeters: 4000,
      gridSize: 20,
      heights: flatHeights,
      climate,
      mountainName: name
    };
    
    this.saveTerrainToIndexedDB(terrainDB);
    this.onDataIngestedCallback(terrainDB);
  }

  private generateProceduralHeights(minAlt: number, maxAlt: number, resolution: number): number[] {
    const heights: number[] = [];
    // Generates a nice ridge sloping from top-left (high) to bottom-right (low) with some noise
    for (let r = 0; r < resolution; r++) {
      const yFrac = r / (resolution - 1);
      for (let c = 0; c < resolution; c++) {
        const xFrac = c / (resolution - 1);
        
        // Base slope (slopes from north/west down to south/east)
        const slope = (1.0 - yFrac) * 0.6 + (1.0 - xFrac) * 0.4;
        
        // Add a valley/gully effect
        const valley = Math.sin(xFrac * Math.PI) * 0.15;
        
        // Add some noise
        const noise = Math.sin(xFrac * 6) * Math.cos(yFrac * 6) * 0.05 + 
                      Math.sin(xFrac * 15) * 0.02;
        
        const hFraction = Math.max(0, Math.min(1, slope - valley + noise));
        const z = minAlt + (maxAlt - minAlt) * hFraction;
        heights.push(Math.round(z));
      }
    }
    return heights;
  }

  // IndexedDB Storage Methods
  private saveTerrainToIndexedDB(data: TerrainDB): void {
    const request = indexedDB.open('MountainPlannerDB', 1);
    
    request.onupgradeneeded = (e: any) => {
      const db = e.target.result;
      if (!db.objectStoreNames.contains('maps')) {
        db.createObjectStore('maps', { keyPath: 'mountainName' });
      }
    };
    
    request.onsuccess = (e: any) => {
      const db = e.target.result;
      const tx = db.transaction('maps', 'readwrite');
      const store = tx.objectStore('maps');
      store.put(data);
    };
    
    request.onerror = (e) => {
      console.error('IndexedDB save error:', e);
    };
  }

  public static loadLastTerrainFromIndexedDB(onLoaded: (data: TerrainDB) => void): void {
    const request = indexedDB.open('MountainPlannerDB', 1);
    
    request.onupgradeneeded = (e: any) => {
      const db = e.target.result;
      if (!db.objectStoreNames.contains('maps')) {
        db.createObjectStore('maps', { keyPath: 'mountainName' });
      }
    };
    
    request.onsuccess = (e: any) => {
      const db = e.target.result;
      const tx = db.transaction('maps', 'readonly');
      const store = tx.objectStore('maps');
      const query = store.getAll();
      
      query.onsuccess = () => {
        if (query.result && query.result.length > 0) {
          // Return the most recently saved map
          onLoaded(query.result[query.result.length - 1]);
        }
      };
    };
  }
}
