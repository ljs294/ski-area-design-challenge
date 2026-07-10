import { useState } from 'react';

export interface GeocodeResult {
  lng: number;
  lat: number;
  label: string;
}

/**
 * Minimal place search via OpenStreetMap Nominatim (keyless). Returns the top
 * hit's coords + display name, or null. Nominatim's usage policy asks for low
 * volume + identifying UA; fine for the picker's occasional online use.
 */
export async function geocode(query: string): Promise<GeocodeResult | null> {
  const url = `https://nominatim.openstreetmap.org/search?format=json&limit=1&q=${encodeURIComponent(query)}`;
  const resp = await fetch(url, { headers: { 'Accept-Language': 'en' } }).catch(() => null);
  if (!resp || !resp.ok) return null;
  const results = (await resp.json()) as Array<{ lat: string; lon: string; display_name: string }>;
  if (!results.length) return null;
  const r = results[0];
  return { lng: parseFloat(r.lon), lat: parseFloat(r.lat), label: r.display_name };
}

export function SearchBox({ onResult }: { onResult: (r: GeocodeResult) => void }) {
  const [value, setValue] = useState('');
  const [status, setStatus] = useState<'idle' | 'searching' | 'notfound'>('idle');

  async function run() {
    const q = value.trim();
    if (!q) return;
    setStatus('searching');
    const r = await geocode(q);
    if (r) {
      setStatus('idle');
      onResult(r);
    } else {
      setStatus('notfound');
    }
  }

  return (
    <div className="search-box">
      <input
        className="search-input"
        type="text"
        placeholder="Search a place…"
        value={value}
        onChange={(e) => {
          setValue(e.target.value);
          if (status === 'notfound') setStatus('idle');
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') run();
        }}
      />
      <button className="search-btn" onClick={run} disabled={status === 'searching'}>
        {status === 'searching' ? '…' : 'Go'}
      </button>
      {status === 'notfound' && <span className="search-msg">No match</span>}
    </div>
  );
}
