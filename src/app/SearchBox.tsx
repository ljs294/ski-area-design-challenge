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

export interface ReverseResult {
  city: string | null;
  state: string | null;
  /** "City, State" when both are known, else whichever half exists, else ''. */
  place: string;
}

/**
 * Reverse-geocode a coordinate to a coarse City, State via Nominatim (keyless).
 * zoom=10 keeps the result at the town/county level. Returns null offline.
 */
export async function reverseGeocode(lat: number, lng: number): Promise<ReverseResult | null> {
  const url = `https://nominatim.openstreetmap.org/reverse?format=json&zoom=10&lat=${lat}&lon=${lng}`;
  const resp = await fetch(url, { headers: { 'Accept-Language': 'en' } }).catch(() => null);
  if (!resp || !resp.ok) return null;
  const data = (await resp.json().catch(() => null)) as {
    address?: Record<string, string>;
  } | null;
  const a = data?.address ?? {};
  const city = a.city ?? a.town ?? a.village ?? a.hamlet ?? a.county ?? null;
  const state = a.state ?? a.region ?? null;
  const place = [city, state].filter(Boolean).join(', ');
  return { city, state, place };
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
