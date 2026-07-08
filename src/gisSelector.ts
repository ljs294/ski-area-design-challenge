import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { AreaSizeMeters, TerrainDB } from './types';
import { SelectionBox } from './selectionBox';
import { ingestLiveArea, ingestPreset } from './terrainIngest';
import type { ElevationProgress } from './elevation';

const DEFAULT_CENTER: L.LatLngExpression = [39.1866, -106.8182]; // Aspen Mountain
const DEFAULT_SIZE_METERS: AreaSizeMeters = 4000;

/**
 * Handles the map-based area picker: Leaflet lifecycle, location search,
 * the repositionable selection box, and kicking off terrain ingest.
 */
export class GisSelector {
  private map: L.Map | null = null;
  private selectionBox: SelectionBox | null = null;
  private lastSearchedName: string | null = null;
  private onDataIngestedCallback: (data: TerrainDB) => void;

  constructor(onDataIngested: (data: TerrainDB) => void) {
    this.onDataIngestedCallback = onDataIngested;
  }

  public initMap(containerId: string): void {
    if (this.map) return;

    this.map = L.map(containerId, {
      zoomControl: true,
      attributionControl: false,
    }).setView(DEFAULT_CENTER, 13);

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 18,
    }).addTo(this.map);

    this.selectionBox = new SelectionBox(this.map, {
      initialCenter: DEFAULT_CENTER,
      initialSizeMeters: DEFAULT_SIZE_METERS,
      onChange: () => this.updateStatusText(),
    });

    this.updateStatusText();
    this.updateSizeButtonsUI(DEFAULT_SIZE_METERS);

    const ingestBtn = document.getElementById('btn-ingest-data') as HTMLButtonElement | null;
    if (ingestBtn) {
      ingestBtn.disabled = false;
      ingestBtn.classList.remove('disabled');
    }
  }

  /**
   * Leaflet needs to recompute its size after its container regains
   * visibility (e.g. re-entering this screen from the main menu).
   */
  public refreshSize(): void {
    this.map?.invalidateSize();
  }

  public setSize(size: AreaSizeMeters): void {
    this.selectionBox?.setSizeMeters(size);
    this.updateSizeButtonsUI(size);
  }

  private updateSizeButtonsUI(size: AreaSizeMeters): void {
    document.querySelectorAll('.size-btn').forEach((btn) => {
      const btnSize = Number(btn.getAttribute('data-size'));
      btn.classList.toggle('active', btnSize === size);
    });
  }

  private updateStatusText(): void {
    if (!this.selectionBox) return;
    const center = this.selectionBox.getCenter();
    const statusText = document.getElementById('map-status-text');
    if (statusText) {
      statusText.innerText = `Selected area centered at ${center.lat.toFixed(4)}, ${center.lng.toFixed(4)}`;
    }
  }

  public async searchLocation(query: string): Promise<boolean> {
    if (!query || !this.map || !this.selectionBox) return false;

    try {
      // Restrict search to North America using viewbox (lon_min, lat_min, lon_max, lat_max)
      const viewbox = '-170,15,-50,75';
      const url = `https://nominatim.openstreetmap.org/search?format=json&q=${encodeURIComponent(query)}&viewbox=${viewbox}&bounded=1`;
      const response = await fetch(url);
      const results = await response.json();

      if (results && results.length > 0) {
        const bestResult = results[0];
        const lat = parseFloat(bestResult.lat);
        const lon = parseFloat(bestResult.lon);
        this.lastSearchedName = typeof bestResult.display_name === 'string'
          ? bestResult.display_name.split(',')[0]
          : null;
        this.map.setView([lat, lon], 13);
        this.selectionBox.setCenter([lat, lon]);
        return true;
      }
    } catch (e) {
      console.error('Geocoding search failed:', e);
    }
    return false;
  }

  public async downloadSelectedArea(): Promise<void> {
    if (!this.selectionBox) return;

    const statusText = document.getElementById('map-status-text');
    const ingestBtn = document.getElementById('btn-ingest-data') as HTMLButtonElement | null;
    const progressContainer = document.getElementById('gis-progress-container');
    const progressFill = document.getElementById('gis-progress-fill');

    if (ingestBtn) {
      ingestBtn.disabled = true;
      ingestBtn.classList.add('disabled');
    }
    progressContainer?.classList.remove('hidden');
    if (progressFill) progressFill.style.width = '0%';
    if (statusText) statusText.innerText = 'Downloading elevation data...';

    const center = this.selectionBox.getCenter();
    const bounds = this.selectionBox.getBounds();
    const sizeMeters = this.selectionBox.getSizeMeters();
    const southWest = bounds.getSouthWest();
    const northEast = bounds.getNorthEast();
    const mountainName =
      this.lastSearchedName || `Mountain (${center.lat.toFixed(2)}, ${center.lng.toFixed(2)})`;

    try {
      const terrain = await ingestLiveArea(
        { south: southWest.lat, north: northEast.lat, west: southWest.lng, east: northEast.lng },
        { latitude: center.lat, longitude: center.lng },
        sizeMeters,
        mountainName,
        (progress: ElevationProgress) => {
          const pct = Math.round((progress.completedBatches / progress.totalBatches) * 100);
          if (progressFill) progressFill.style.width = `${pct}%`;
          if (statusText) {
            statusText.innerText = progress.rateLimited
              ? `Downloading elevation data... (${progress.completedBatches}/${progress.totalBatches}) — elevation provider is rate-limiting, retrying automatically`
              : `Downloading elevation data... (${progress.completedBatches}/${progress.totalBatches})`;
          }
        }
      );

      if (statusText) statusText.innerText = 'Download successful!';
      this.onDataIngestedCallback(terrain);
    } catch (e) {
      console.error(e);
      if (statusText) statusText.innerText = 'Failed to load terrain. Check connection.';
      if (ingestBtn) {
        ingestBtn.disabled = false;
        ingestBtn.classList.remove('disabled');
      }
      progressContainer?.classList.add('hidden');
    }
  }

  public async loadPresetMountain(presetId: string): Promise<void> {
    const terrain = await ingestPreset(presetId);
    this.onDataIngestedCallback(terrain);
  }
}
