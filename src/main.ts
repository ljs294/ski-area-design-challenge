import './style.css';

// Safely import Electron IPC if running inside the Electron shell
let ipcRenderer: any = null;
try {
  const electron = require('electron');
  ipcRenderer = electron.ipcRenderer;
} catch (e) {
  console.log('Running in browser mode. Electron APIs are disabled.');
}

window.addEventListener('DOMContentLoaded', () => {
  // Wire up Menu Buttons
  document.getElementById('menu-new-game')?.addEventListener('click', () => {
    alert('Feature coming soon: New Resort');
  });

  document.getElementById('menu-select-map')?.addEventListener('click', () => {
    alert('Feature coming soon: GIS Bounding Box Selector');
  });

  document.getElementById('menu-load-game')?.addEventListener('click', () => {
    alert('Feature coming soon: Load saved files');
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

  // Settings: Background Art Style Toggle
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

  // Settings: Display Mode Toggle (Light/Dark/System)
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

  // Settings: Seasonal Backgrounds Toggle
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

  // Wire up Season Selector
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
