import './style.css';
import { GisSelector } from './gisSelector';
import { ContentManager } from './contentManager';
import { NA_MOUNTAIN_PRESETS } from './mountainPresets';
import { GameRenderer, MAP_SIZE } from './renderer';
import type { Camera } from './renderer';
import { CONTOUR_TIERS, pickActiveTier } from './contours';
import type { AreaSizeMeters, TerrainDB } from './types';

const METERS_TO_FEET = 3.280839895;

// Safely import Electron IPC if running inside the Electron shell
let ipcRenderer: any = null;
try {
  const electron = require('electron');
  ipcRenderer = electron.ipcRenderer;
} catch (e) {
  console.log('Running in browser mode. Electron APIs are disabled.');
}

// ========================
// View State Machine
// ========================
type ViewState = 'menu' | 'selection' | 'content-manager' | 'game';

// ========================
// Game Globals
// ========================
let gisSelector: GisSelector | null = null;
let contentManager: ContentManager | null = null;
let gameRenderer: GameRenderer | null = null;
let currentTerrain: TerrainDB | null = null;
let camera: Camera = { x: 0, y: 0, zoom: 1 };

// Canvas interaction state
let isPanning = false;
let panStartMouse: { x: number; y: number } | null = null;
let panStartCamera: { x: number; y: number } | null = null;
let lastMouseScreen: { x: number; y: number } | null = null;

// ========================
// Utility Functions
// ========================

function showView(view: ViewState) {
  const splash = document.getElementById('splash-container');
  const bgImage = document.getElementById('background-image-container');
  const bgArt = document.getElementById('background-artwork');
  const snowPile = document.getElementById('snow-pile');
  const gis = document.getElementById('gis-selector-container');
  const contentManagerEl = document.getElementById('content-manager-container');
  const game = document.getElementById('game-ui-container');

  // Hide everything first
  splash?.classList.add('hidden');
  gis?.classList.add('hidden');
  contentManagerEl?.classList.add('hidden');
  game?.classList.add('hidden');
  if (bgImage) bgImage.style.display = 'none';
  if (bgArt) bgArt.style.display = 'none';
  if (snowPile) snowPile.style.display = 'none';

  if (view === 'menu') {
    splash?.classList.remove('hidden');
    // Restore the theme-dependent background visibility
    const style = localStorage.getItem('menu-theme-style') || 'poster';
    if (style === 'poster') {
      if (bgImage) bgImage.style.display = 'block';
    } else {
      if (bgArt) bgArt.style.display = 'block';
    }
    if (snowPile) snowPile.style.display = 'block';
  } else if (view === 'selection') {
    gis?.classList.remove('hidden');
    // Initialize Leaflet map (first time only)
    initGISMap();
  } else if (view === 'content-manager') {
    contentManagerEl?.classList.remove('hidden');
    if (!contentManager) {
      contentManager = new ContentManager((data: TerrainDB) => {
        currentTerrain = data;
        showView('game');
      });
    }
    contentManager.refresh();
  } else if (view === 'game') {
    game?.classList.remove('hidden');
    initGameCanvas();
  }
}

function screenToWorld(screenX: number, screenY: number): { x: number; y: number } {
  return {
    x: (screenX - camera.x) / camera.zoom,
    y: (screenY - camera.y) / camera.zoom
  };
}

// ========================
// GIS Selector Logic
// ========================

let gisMapInitialized = false;

function initGISMap() {
  if (gisMapInitialized) {
    // Map already created; just invalidate size for re-layout since its
    // container was hidden (display:none) while another view was active.
    setTimeout(() => gisSelector?.refreshSize(), 100);
    return;
  }

  gisSelector = new GisSelector((data: TerrainDB) => {
    // Terrain ingested! Transition to game
    currentTerrain = data;
    showView('game');
  });

  gisSelector.initMap('gis-map');
  gisMapInitialized = true;
}

function populatePresetCards() {
  const grid = document.getElementById('preset-cards-grid');
  if (!grid) return;

  grid.innerHTML = '';

  for (const preset of NA_MOUNTAIN_PRESETS) {
    const vertDrop = preset.maxAltitude - preset.minAltitude;
    const card = document.createElement('div');
    card.className = 'preset-card';
    card.innerHTML = `
      <div class="preset-card-name">${preset.name}</div>
      <div class="preset-card-location">${preset.state}, ${preset.country}</div>
      <div class="preset-card-stats">
        <span class="preset-stat">Vert: <span>${vertDrop}m</span></span>
        <span class="preset-stat">Base: <span>${preset.minAltitude}m</span></span>
        <span class="preset-stat">Summit: <span>${preset.maxAltitude}m</span></span>
      </div>
      <div class="preset-card-desc">${preset.description}</div>
    `;

    card.addEventListener('click', () => {
      if (!gisSelector) {
        gisSelector = new GisSelector((data: TerrainDB) => {
          currentTerrain = data;
          showView('game');
        });
      }
      gisSelector.loadPresetMountain(preset.id).catch((e) => console.error('Failed to load preset:', e));
    });

    grid.appendChild(card);
  }
}

// ========================
// Game Canvas — simple pan/zoom terrain viewer
// ========================

let gameCanvas: HTMLCanvasElement | null = null;

function render() {
  if (!gameCanvas || !gameRenderer) return;
  gameRenderer.draw(currentTerrain, camera);
  updateContourIntervalReadout();
}

function initGameCanvas() {
  const nameEl = document.getElementById('hud-resort-name');
  if (nameEl) nameEl.textContent = currentTerrain?.mountainName || '';

  if (gameCanvas) {
    // Already initialized — just resize/recenter for the (possibly new) terrain.
    resizeGameCanvas();
    render();
    return;
  }

  const canvas = document.getElementById('game-canvas') as HTMLCanvasElement;
  if (!canvas) return;
  gameCanvas = canvas;
  gameRenderer = new GameRenderer(canvas);

  resizeGameCanvas();
  window.addEventListener('resize', () => {
    resizeGameCanvas();
    render();
    if (lastMouseScreen) updateElevationReadout(screenToWorld(lastMouseScreen.x, lastMouseScreen.y));
  });

  // ---- Pan (left-drag) & zoom (wheel) ----

  canvas.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    isPanning = true;
    panStartMouse = { x: e.clientX, y: e.clientY };
    panStartCamera = { x: camera.x, y: camera.y };
  });

  window.addEventListener('mousemove', (e) => {
    if (isPanning && panStartMouse && panStartCamera) {
      camera.x = panStartCamera.x + (e.clientX - panStartMouse.x);
      camera.y = panStartCamera.y + (e.clientY - panStartMouse.y);
      render();
    }

    const rect = canvas.getBoundingClientRect();
    if (e.clientX < rect.left || e.clientX > rect.right || e.clientY < rect.top || e.clientY > rect.bottom) {
      lastMouseScreen = null;
      updateElevationReadout(null);
      return;
    }
    lastMouseScreen = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    updateElevationReadout(screenToWorld(lastMouseScreen.x, lastMouseScreen.y));
  });

  canvas.addEventListener('mouseleave', () => {
    lastMouseScreen = null;
    updateElevationReadout(null);
  });

  window.addEventListener('mouseup', () => {
    isPanning = false;
  });

  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    const rect = canvas.getBoundingClientRect();
    const mouseX = e.clientX - rect.left;
    const mouseY = e.clientY - rect.top;

    // Proportional to scroll magnitude so a bigger wheel/trackpad gesture
    // zooms further in one event, not just a fixed step per event.
    const zoomFactor = Math.exp(-e.deltaY * 0.001);
    const newZoom = Math.max(0.15, Math.min(5, camera.zoom * zoomFactor));

    // Zoom toward the mouse cursor
    const worldBefore = screenToWorld(mouseX, mouseY);
    camera.zoom = newZoom;
    const worldAfter = screenToWorld(mouseX, mouseY);
    camera.x += (worldAfter.x - worldBefore.x) * camera.zoom;
    camera.y += (worldAfter.y - worldBefore.y) * camera.zoom;
    lastMouseScreen = { x: mouseX, y: mouseY };
    updateElevationReadout(screenToWorld(mouseX, mouseY));
    render();
  }, { passive: false });

  render();
}

function updateElevationReadout(world: { x: number; y: number } | null) {
  const elevationEl = document.getElementById('status-elevation');
  if (!elevationEl) return;

  const elevationMeters = world ? gameRenderer?.elevationAt(world.x, world.y) : null;
  if (elevationMeters == null) {
    elevationEl.textContent = 'Elevation: —';
    return;
  }
  const feet = Math.round(elevationMeters * METERS_TO_FEET);
  elevationEl.textContent = `Elevation: ${feet.toLocaleString()} ft`;
}

function updateContourIntervalReadout() {
  const el = document.getElementById('status-contour-interval');
  if (!el) return;
  const tier = pickActiveTier(CONTOUR_TIERS, camera.zoom);
  el.textContent = `Contours: ${tier.majorFt}ft / ${tier.minorFt}ft`;
}

function resizeGameCanvas() {
  const canvas = gameCanvas;
  const container = document.getElementById('game-ui-container');
  if (!canvas || !container) return;

  canvas.width = container.clientWidth;
  canvas.height = container.clientHeight;

  // Re-center the camera on the terrain
  const zoom = 0.5;
  camera = {
    x: canvas.width / 2 - (MAP_SIZE * zoom) / 2,
    y: canvas.height / 2 - (MAP_SIZE * zoom) / 2,
    zoom,
  };
}

// ========================
// DOMContentLoaded Wiring
// ========================

window.addEventListener('DOMContentLoaded', () => {
  // ---- Main Menu Buttons ----
  document.getElementById('menu-new-game')?.addEventListener('click', () => {
    showView('selection');
  });

  document.getElementById('menu-select-map')?.addEventListener('click', () => {
    showView('selection');
  });

  document.getElementById('menu-load-game')?.addEventListener('click', () => {
    alert('Feature coming soon: Load saved files');
  });

  document.getElementById('menu-content-manager')?.addEventListener('click', () => {
    showView('content-manager');
  });

  document.getElementById('content-manager-back-btn')?.addEventListener('click', () => {
    showView('menu');
  });

  // Settings Modal controls
  const settingsModal = document.getElementById('settings-modal');
  
  document.getElementById('menu-settings')?.addEventListener('click', () => {
    settingsModal?.classList.remove('hidden');
  });

  document.getElementById('settings-close')?.addEventListener('click', () => {
    settingsModal?.classList.add('hidden');
  });

  document.getElementById('menu-exit')?.addEventListener('click', () => {
    if (ipcRenderer) {
      ipcRenderer.send('exit-game');
    } else {
      alert('Exit Game is only supported in the desktop app wrapper.');
    }
  });

  // ---- GIS Selector Buttons ----
  populatePresetCards();

  document.getElementById('gis-back-btn')?.addEventListener('click', () => {
    showView('menu');
  });

  document.getElementById('gis-search-btn')?.addEventListener('click', () => {
    const input = document.getElementById('gis-search-input') as HTMLInputElement;
    if (input && gisSelector) {
      gisSelector.searchLocation(input.value);
    }
  });

  document.getElementById('gis-search-input')?.addEventListener('keydown', (e: KeyboardEvent) => {
    if (e.key === 'Enter') {
      const input = document.getElementById('gis-search-input') as HTMLInputElement;
      if (input && gisSelector) {
        gisSelector.searchLocation(input.value);
      }
    }
  });

  document.getElementById('btn-ingest-data')?.addEventListener('click', () => {
    gisSelector?.downloadSelectedArea();
  });

  // ---- GIS Selector: Area Size Presets ----
  document.querySelectorAll('.size-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      const size = Number(btn.getAttribute('data-size')) as AreaSizeMeters;
      gisSelector?.setSize(size);
    });
  });

  // ---- Quit to Menu ----
  document.getElementById('btn-quit-to-menu')?.addEventListener('click', () => {
    showView('menu');
  });

  // ========================================
  // Settings: Background Art Style Toggle
  // ========================================
  const styleButtons = document.querySelectorAll('.style-btn');
  const savedStyle = localStorage.getItem('menu-theme-style') || 'poster';
  applyStyle(savedStyle);

  styleButtons.forEach(btn => {
    btn.addEventListener('click', (e) => {
      const clickedBtn = e.currentTarget as HTMLButtonElement;
      const style = clickedBtn.getAttribute('data-style');
      if (style) {
        applyStyle(style);
        // Force background update
        const currentSeason = localStorage.getItem('menu-season') || 'winter';
        applySeason(currentSeason);
      }
    });
  });

  function applyStyle(style: string) {
    document.body.classList.remove('theme-poster', 'theme-minimalist');
    document.body.classList.add(`theme-${style}`);
    localStorage.setItem('menu-theme-style', style);

    styleButtons.forEach(btn => {
      if (btn.getAttribute('data-style') === style) {
        btn.classList.add('active');
      } else {
        btn.classList.remove('active');
      }
    });

    // Disable Season selectors in Minimalist mode
    const seasonRow = document.getElementById('season-setting-row');
    const seasonToggleRow = document.getElementById('season-toggle-setting-row');
    if (style === 'minimalist') {
      seasonRow?.classList.add('setting-disabled');
      seasonToggleRow?.classList.add('setting-disabled');
    } else {
      seasonRow?.classList.remove('setting-disabled');
      seasonToggleRow?.classList.remove('setting-disabled');
    }
  }

  // ========================================
  // Settings: Display Mode Toggle (Light/Dark/System)
  // ========================================
  const displayButtons = document.querySelectorAll('.display-btn');
  const savedMode = localStorage.getItem('menu-display-mode') || 'light';
  applyDisplayMode(savedMode);

  // Monitor OS preference changes dynamically
  const systemPrefersDark = window.matchMedia('(prefers-color-scheme: dark)');
  systemPrefersDark.addEventListener('change', () => {
    const currentMode = localStorage.getItem('menu-display-mode') || 'light';
    if (currentMode === 'system') {
      applyDisplayMode('system');
      const currentSeason = localStorage.getItem('menu-season') || 'winter';
      applySeason(currentSeason);
    }
  });

  displayButtons.forEach(btn => {
    btn.addEventListener('click', (e) => {
      const clickedBtn = e.currentTarget as HTMLButtonElement;
      const mode = clickedBtn.getAttribute('data-mode');
      if (mode) {
        applyDisplayMode(mode);
        // Force background update (to switch between day and night images)
        const currentSeason = localStorage.getItem('menu-season') || 'winter';
        applySeason(currentSeason);
      }
    });
  });

  function applyDisplayMode(mode: string) {
    document.body.classList.remove('mode-light', 'mode-dark');
    
    let resolvedMode = mode;
    if (mode === 'system') {
      resolvedMode = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    
    document.body.classList.add(`mode-${resolvedMode}`);
    localStorage.setItem('menu-display-mode', mode);

    displayButtons.forEach(btn => {
      if (btn.getAttribute('data-mode') === mode) {
        btn.classList.add('active');
      } else {
        btn.classList.remove('active');
      }
    });
  }

  // ========================================
  // Settings: Seasonal Backgrounds Toggle
  // ========================================
  const seasonToggleButtons = document.querySelectorAll('.season-toggle-btn');
  const savedSeasonToggle = localStorage.getItem('menu-season-toggle') || 'on';
  applySeasonToggle(savedSeasonToggle);

  seasonToggleButtons.forEach(btn => {
    btn.addEventListener('click', (e) => {
      const clickedBtn = e.currentTarget as HTMLButtonElement;
      const toggleVal = clickedBtn.getAttribute('data-season-toggle');
      if (toggleVal) {
        applySeasonToggle(toggleVal);
        // Re-apply the current season to force background update
        const currentSeason = localStorage.getItem('menu-season') || 'winter';
        applySeason(currentSeason);
      }
    });
  });

  function applySeasonToggle(val: string) {
    localStorage.setItem('menu-season-toggle', val);
    seasonToggleButtons.forEach(btn => {
      if (btn.getAttribute('data-season-toggle') === val) {
        btn.classList.add('active');
      } else {
        btn.classList.remove('active');
      }
    });
  }

  // ========================================
  // Wire up Season Selector
  // ========================================
  const seasonButtons = document.querySelectorAll('.season-btn');
  const savedSeason = localStorage.getItem('menu-season') || 'winter';
  applySeason(savedSeason);

  seasonButtons.forEach(btn => {
    btn.addEventListener('click', (e) => {
      const clickedBtn = e.currentTarget as HTMLButtonElement;
      const season = clickedBtn.getAttribute('data-season');
      if (season) {
        applySeason(season);
      }
    });
  });

  function applySeason(season: string) {
    // 1. Update body class (controls heights, snow caps, outlines)
    const artStyle = localStorage.getItem('menu-theme-style') || 'poster';
    const savedMode = localStorage.getItem('menu-display-mode') || 'light';
    
    let displayMode = savedMode;
    if (savedMode === 'system') {
      displayMode = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    
    document.body.className = `season-${season} theme-${artStyle} mode-${displayMode}`;
    
    // 2. Save choice to localStorage
    localStorage.setItem('menu-season', season);
    
    // 3. Update button UI states
    seasonButtons.forEach(btn => {
      if (btn.getAttribute('data-season') === season) {
        btn.classList.add('active');
      } else {
        btn.classList.remove('active');
      }
    });

    // 4. Update the high-resolution background image source
    const bgImage = document.getElementById('bg-image') as HTMLImageElement;
    if (bgImage) {
      const isSeasonToggleOn = (localStorage.getItem('menu-season-toggle') || 'on') === 'on';
      const isDarkMode = displayMode === 'dark';
      
      let targetSeason = season;
      if (!isSeasonToggleOn) {
        targetSeason = 'winter'; // Force/Lock to Winter scene
      }

      // Reset any existing CSS filters
      bgImage.classList.remove('filter-summer', 'filter-summer-night', 'filter-autumn', 'filter-autumn-night');

      // Map day/night background images
      if (isDarkMode) {
        if (targetSeason === 'summer') {
          bgImage.src = '/spring_night_background.png';
          bgImage.classList.add('filter-summer-night');
        } else if (targetSeason === 'autumn') {
          bgImage.src = '/spring_night_background.png';
          bgImage.classList.add('filter-autumn-night');
        } else {
          bgImage.src = `/${targetSeason}_night_background.png`;
        }
      } else {
        if (targetSeason === 'winter') {
          bgImage.src = '/ski_village_background.png';
        } else if (targetSeason === 'summer') {
          bgImage.src = '/spring_background.png';
          bgImage.classList.add('filter-summer');
        } else if (targetSeason === 'autumn') {
          bgImage.src = '/spring_background.png';
          bgImage.classList.add('filter-autumn');
        } else {
          bgImage.src = `/${targetSeason}_background.png`;
        }
      }
    }
  }

  // If not running in Electron, style the exit option slightly differently
  if (!ipcRenderer) {
    const exitItem = document.getElementById('menu-exit');
    if (exitItem) {
      const text = exitItem.querySelector('.slat-text') as HTMLElement;
      if (text) text.innerText = 'Exit Game (Web)';
    }
  }
});
