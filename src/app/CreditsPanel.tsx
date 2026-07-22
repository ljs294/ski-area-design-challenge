// Human-readable credits for the free data services the app depends on. The
// map's compact ⓘ control (bottom-left) is the license-compliance surface; this
// panel is the discoverable version, opened from the in-game Menu. Strings mirror
// the `attribution` values registered on the map sources in analysisLayers.ts.

interface Credit {
  what: string;
  who: string;
}

const CREDITS: Credit[] = [
  { what: 'Basemap', who: '© OpenStreetMap contributors · © CARTO' },
  { what: 'Preview satellite imagery', who: '© Esri, Maxar, Earthstar Geographics' },
  { what: 'Matched local imagery', who: 'USDA / USGS NAIP orthoimagery · public domain' },
  { what: 'Detailed tree cover', who: 'Prepared from ESA WorldCover and USDA / USGS NAIP' },
  { what: 'Recovery land cover', who: '© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data' },
  { what: 'Local terrain / contours', who: 'Prepared from USGS 3DEP elevation · public domain' },
  { what: 'Elevation', who: 'USGS 3DEP' },
  { what: 'Place search / location', who: 'Nominatim · © OpenStreetMap contributors' },
];

export function CreditsPanel({ onClose }: { onClose: () => void }) {
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="settings-panel credits-panel" onClick={(e) => e.stopPropagation()}>
        <div className="settings-header">
          <h2 className="settings-title">Data &amp; API credits</h2>
          <button className="settings-close-x" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <p className="credits-intro">
          Ski Area Design Challenge is built on these free, open data services.
        </p>
        <div className="credits-list">
          {CREDITS.map((c) => (
            <div className="credits-row" key={c.what}>
              <span className="credits-what">{c.what}</span>
              <span className="credits-who">{c.who}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
