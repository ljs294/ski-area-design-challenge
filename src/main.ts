import './style.css';
import { GisSelector } from './gisSelector';
import { ContentManager } from './contentManager';
import { NA_MOUNTAIN_PRESETS } from './mountainPresets';
import { GameRenderer } from './renderer';
import type { Camera } from './renderer';
import { SimulationEngine } from './simulation';
import type { AreaSizeMeters, GameState, TerrainDB, ActiveTool } from './types';

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
let currentView: ViewState = 'menu';

// ========================
// Game Globals
// ========================
let gisSelector: GisSelector | null = null;
let contentManager: ContentManager | null = null;
let gameRenderer: GameRenderer | null = null;
let gameState: GameState | null = null;
let camera: Camera = { x: 0, y: 0, zoom: 1 };
let animFrameId: number | null = null;

// Canvas interaction state
let isDragging = false;
let isPanning = false;
let dragStartWorld: { x: number; y: number } | null = null;
let currentMouseWorld: { x: number; y: number } | null = null;
let panStartMouse: { x: number; y: number } | null = null;
let panStartCamera: { x: number; y: number } | null = null;
let hoveredId: string | null = null;
let selectedId: string | null = null;

// ========================
// Utility Functions
// ========================

function showView(view: ViewState) {
  currentView = view;
  
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

    // Stop the game loop if running
    if (animFrameId !== null) {
      cancelAnimationFrame(animFrameId);
      animFrameId = null;
    }
  } else if (view === 'selection') {
    gis?.classList.remove('hidden');
    // Initialize Leaflet map (first time only)
    initGISMap();
  } else if (view === 'content-manager') {
    contentManagerEl?.classList.remove('hidden');
    if (!contentManager) {
      contentManager = new ContentManager((data: TerrainDB) => {
        gameState = createDefaultGameState(data);
        showView('game');
      });
    }
    contentManager.refresh();
  } else if (view === 'game') {
    game?.classList.remove('hidden');
    // Start the game loop
    initGameCanvas();
    startGameLoop();
  }
}

function screenToWorld(screenX: number, screenY: number): { x: number; y: number } {
  return {
    x: (screenX - camera.x) / camera.zoom,
    y: (screenY - camera.y) / camera.zoom
  };
}

function createDefaultGameState(terrain: TerrainDB): GameState {
  return {
    cash: 50000,
    nodes: [
      {
        id: 'entrance-1',
        name: 'Main Base Area',
        type: 'entrance',
        x: 1000,
        y: 1800,
        z: 0
      }
    ],
    lifts: [],
    trails: [],
    skiers: [],
    timeSpeed: 1,
    gameTimeMinutes: 480, // 8:00 AM
    gameMonth: 0, // January
    gameDay: 1,
    temperature: 28,
    windSpeed: 12,
    isSnowing: false,
    groomerCount: 1,
    patrolCount: 1,
    ticketPrice: 50,
    activeTool: 'select',
    terrain,
    ledger: { revenue: 0, expenses: 0, net: 0 }
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
    gameState = createDefaultGameState(data);
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
          gameState = createDefaultGameState(data);
          showView('game');
        });
      }
      gisSelector.loadPresetMountain(preset.id).catch((e) => console.error('Failed to load preset:', e));
    });

    grid.appendChild(card);
  }
}

// ========================
// Game Canvas & Loop
// ========================

function initGameCanvas() {
  const canvas = document.getElementById('game-canvas') as HTMLCanvasElement;
  if (!canvas) return;

  // Size the canvas to fill the available area
  const resizeCanvas = () => {
    const container = document.getElementById('game-ui-container');
    if (!container) return;
    const hud = document.getElementById('game-hud');
    const toolbar = document.getElementById('game-toolbar');
    const hudH = hud?.offsetHeight || 0;
    const toolbarH = toolbar?.offsetHeight || 0;
    canvas.width = container.clientWidth;
    canvas.height = container.clientHeight - hudH - toolbarH;
  };
  
  resizeCanvas();
  window.addEventListener('resize', resizeCanvas);

  // Center the camera on the terrain
  camera = {
    x: canvas.width / 2 - 1000,
    y: canvas.height / 2 - 1000,
    zoom: 0.5
  };

  gameRenderer = new GameRenderer(canvas);

  // ---- Canvas Mouse Events ----

  canvas.addEventListener('mousedown', (e) => {
    if (e.button === 1 || (e.button === 0 && e.shiftKey)) {
      // Middle click or Shift+Left: start panning
      isPanning = true;
      panStartMouse = { x: e.clientX, y: e.clientY };
      panStartCamera = { x: camera.x, y: camera.y };
      canvas.style.cursor = 'grabbing';
      return;
    }

    if (e.button === 0 && gameState) {
      const rect = canvas.getBoundingClientRect();
      const world = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);

      if (gameState.activeTool !== 'select') {
        isDragging = true;
        dragStartWorld = world;
        currentMouseWorld = world;
      }
    }
  });

  canvas.addEventListener('mousemove', (e) => {
    if (isPanning && panStartMouse && panStartCamera) {
      camera.x = panStartCamera.x + (e.clientX - panStartMouse.x);
      camera.y = panStartCamera.y + (e.clientY - panStartMouse.y);
      return;
    }

    const rect = canvas.getBoundingClientRect();
    const world = screenToWorld(e.clientX - rect.left, e.clientY - rect.top);
    currentMouseWorld = world;
  });

  canvas.addEventListener('mouseup', (_e) => {
    if (isPanning) {
      isPanning = false;
      canvas.style.cursor = 'crosshair';
      return;
    }

    if (isDragging && dragStartWorld && currentMouseWorld && gameState) {
      // Placement completed — create the entity
      // (For now this is a stub; full placement logic comes in a later phase)
      isDragging = false;
      dragStartWorld = null;
    }
  });

  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    const rect = canvas.getBoundingClientRect();
    const mouseX = e.clientX - rect.left;
    const mouseY = e.clientY - rect.top;

    const zoomFactor = e.deltaY < 0 ? 1.1 : 0.9;
    const newZoom = Math.max(0.15, Math.min(5, camera.zoom * zoomFactor));

    // Zoom toward the mouse cursor
    const worldBefore = screenToWorld(mouseX, mouseY);
    camera.zoom = newZoom;
    const worldAfter = screenToWorld(mouseX, mouseY);
    camera.x += (worldAfter.x - worldBefore.x) * camera.zoom;
    camera.y += (worldAfter.y - worldBefore.y) * camera.zoom;
  }, { passive: false });
}

function startGameLoop() {
  if (animFrameId !== null) cancelAnimationFrame(animFrameId);

  function loop() {
    if (currentView !== 'game' || !gameState || !gameRenderer) return;

    // Run simulation ticks
    const alerts = SimulationEngine.tick(gameState);

    // Show alerts
    if (alerts.length > 0) {
      showAlerts(alerts);
    }

    // Update HUD
    updateHUD();

    // Draw
    gameRenderer.draw(
      gameState,
      camera,
      hoveredId,
      selectedId,
      isDragging ? dragStartWorld : null,
      isDragging ? currentMouseWorld : null
    );

    animFrameId = requestAnimationFrame(loop);
  }

  animFrameId = requestAnimationFrame(loop);
}

function updateHUD() {
  if (!gameState) return;

  const nameEl = document.getElementById('hud-resort-name');
  const cashEl = document.getElementById('hud-cash');
  const timeEl = document.getElementById('hud-time');
  const dateEl = document.getElementById('hud-date');
  const tempEl = document.getElementById('hud-temp');
  const windEl = document.getElementById('hud-wind');
  const skiersEl = document.getElementById('hud-skiers');

  if (nameEl) nameEl.textContent = gameState.terrain?.mountainName || '—';
  if (cashEl) cashEl.textContent = `$${gameState.cash.toLocaleString()}`;

  // Time format
  if (timeEl) {
    const hours = Math.floor(gameState.gameTimeMinutes / 60);
    const mins = Math.floor(gameState.gameTimeMinutes % 60);
    const ampm = hours >= 12 ? 'PM' : 'AM';
    const h12 = hours > 12 ? hours - 12 : hours;
    timeEl.textContent = `${h12}:${mins.toString().padStart(2, '0')} ${ampm}`;
  }

  // Date format
  if (dateEl) {
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    dateEl.textContent = `${months[gameState.gameMonth]} ${gameState.gameDay}`;
  }

  if (tempEl) {
    const snowIcon = gameState.isSnowing ? '❄️ ' : '';
    tempEl.textContent = `${snowIcon}${gameState.temperature}°F`;
  }
  if (windEl) windEl.textContent = `${Math.round(gameState.windSpeed)} km/h`;
  if (skiersEl) skiersEl.textContent = `${gameState.skiers.length}`;
}

function showAlerts(alerts: string[]) {
  const container = document.getElementById('alert-container');
  if (!container) return;

  for (const msg of alerts) {
    const toast = document.createElement('div');
    toast.className = 'alert-toast';
    toast.textContent = msg;
    container.appendChild(toast);

    // Auto-remove after 5 seconds
    setTimeout(() => {
      toast.remove();
    }, 5000);
  }
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

  // ---- Game Toolbar: Tool Buttons ----
  const toolButtons = document.querySelectorAll('.tool-btn');
  toolButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      const tool = btn.getAttribute('data-tool') as ActiveTool;
      if (!tool || !gameState) return;

      gameState.activeTool = tool;
      toolButtons.forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
    });
  });

  // ---- Game Toolbar: Speed Buttons ----
  const speedButtons = document.querySelectorAll('.speed-btn');
  speedButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      const speed = parseInt(btn.getAttribute('data-speed') || '1', 10);
      if (!gameState) return;

      gameState.timeSpeed = speed;
      speedButtons.forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
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
