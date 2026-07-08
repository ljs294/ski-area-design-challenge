import type { TerrainDB } from './types';
import { listTerrains, loadTerrain, deleteTerrain } from './terrainStorageClient';
import { hydrateTerrainRecord } from './terrainIngest';

const SOURCE_LABELS: Record<string, string> = {
  live: 'Downloaded',
  preset: 'Preset (procedural)',
  'preset-real': 'Preset (real data)',
};

/**
 * Lists terrain data saved on this machine (src/terrainStorageClient.ts),
 * with Load and Delete actions per entry.
 */
export class ContentManager {
  private onTerrainSelected: (data: TerrainDB) => void;

  constructor(onTerrainSelected: (data: TerrainDB) => void) {
    this.onTerrainSelected = onTerrainSelected;
  }

  public async refresh(): Promise<void> {
    const list = document.getElementById('content-list');
    if (!list) return;

    list.innerHTML = '<div class="content-list-empty">Loading…</div>';

    const summaries = await listTerrains();

    if (summaries.length === 0) {
      list.innerHTML = '<div class="content-list-empty">No terrain saved yet — download one from New Resort.</div>';
      return;
    }

    const sorted = [...summaries].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));

    list.innerHTML = '';
    for (const summary of sorted) {
      const row = document.createElement('div');
      row.className = 'content-row';

      const sizeKm = (summary.areaSizeMeters / 1000).toFixed(0);
      const created = new Date(summary.updatedAt).toLocaleString();
      const sourceLabel = SOURCE_LABELS[summary.sourceType] ?? summary.sourceType;

      row.innerHTML = `
        <div class="content-row-main">
          <div class="content-row-name">${summary.mountainName}</div>
          <div class="content-row-meta">
            <span class="content-badge">${sourceLabel}</span>
            <span>${sizeKm}km area</span>
            <span>${summary.latitude.toFixed(4)}, ${summary.longitude.toFixed(4)}</span>
            <span>${created}</span>
          </div>
        </div>
        <div class="content-row-actions">
          <button class="content-load-btn" data-key="${summary.key}">Load</button>
          <button class="content-delete-btn" data-key="${summary.key}">Delete</button>
        </div>
      `;

      list.appendChild(row);
    }

    list.querySelectorAll<HTMLButtonElement>('.content-load-btn').forEach((btn) => {
      btn.addEventListener('click', () => this.handleLoad(btn.dataset.key!));
    });
    list.querySelectorAll<HTMLButtonElement>('.content-delete-btn').forEach((btn) => {
      btn.addEventListener('click', () => this.handleDelete(btn.dataset.key!));
    });
  }

  private async handleLoad(key: string): Promise<void> {
    const record = await loadTerrain(key);
    if (!record) {
      console.error('Failed to load terrain record:', key);
      return;
    }
    this.onTerrainSelected(hydrateTerrainRecord(record));
  }

  private async handleDelete(key: string): Promise<void> {
    if (!window.confirm('Delete this saved terrain? This cannot be undone.')) return;
    await deleteTerrain(key);
    await this.refresh();
  }
}
