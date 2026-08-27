import { useEffect, useRef, useState } from 'react';
import { createJacksonClimateModel, createJacksonObserved2019, createJacksonRun, WEATHER_DIFFICULTY_PRESETS } from '../../weather-engine/src/index.ts';
import type { WeatherLabResultV1 } from '../../weather-engine/src/index.ts';
import type { WeatherWorkerRequest, WeatherWorkerResponse } from './protocol.ts';
import { WeatherChart } from './WeatherChart.tsx';

const MONTHS = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
function download(name: string, text: string, type: string) { const anchor = document.createElement('a'); anchor.href = URL.createObjectURL(new Blob([text], { type })); anchor.download = name; anchor.click(); URL.revokeObjectURL(anchor.href); }

export function WeatherLabApp() {
  const workerRef = useRef<Worker | null>(null); const activeId = useRef<string | null>(null);
  const [seed, setSeed] = useState('Historical'); const [profile, setProfile] = useState<keyof typeof WEATHER_DIFFICULTY_PRESETS>('historical');
  const [status, setStatus] = useState('Ready — committed Jackson fixture'); const [progress, setProgress] = useState(0);
  const [result, setResult] = useState<WeatherLabResultV1 | null>(null); const [month, setMonth] = useState(1);
  const makeWorker = () => { workerRef.current?.terminate(); const worker = new Worker(new URL('./weather.worker.ts', import.meta.url), { type: 'module' }); workerRef.current = worker;
    worker.onerror = () => { if (activeId.current) setStatus('Worker failed; it will be replaced on the next run.'); worker.terminate(); workerRef.current = null; };
    worker.onmessage = (event: MessageEvent<WeatherWorkerResponse>) => { const message = event.data; if (message.requestId !== activeId.current) return;
      if (message.type === 'started') setStatus('Generating coherent hourly atmosphere…');
      if (message.type === 'progress') setProgress(message.completedHours / message.totalHours);
      if (message.type === 'completed') { setResult(message.result); setStatus('Completed'); setProgress(1); activeId.current = null; }
      if (message.type === 'cancelled') { setStatus('Cancelled'); activeId.current = null; makeWorker(); }
      if (message.type === 'failed') { setStatus(`Failed: ${message.message}`); activeId.current = null; makeWorker(); }
    }; return worker; };
  useEffect(() => { const worker = makeWorker(); return () => worker.terminate(); }, []);
  const run = () => { const worker = workerRef.current ?? makeWorker(); const requestId = crypto.randomUUID(); activeId.current = requestId; setResult(null); setProgress(0);
    const model = createJacksonClimateModel(); const request = createJacksonRun(seed); request.difficultyProfile = WEATHER_DIFFICULTY_PRESETS[profile];
    worker.postMessage({ type: 'run', requestId, run: request, model, observed: createJacksonObserved2019() } satisfies WeatherWorkerRequest); };
  const cancel = () => { if (activeId.current) workerRef.current?.postMessage({ type: 'cancel', requestId: activeId.current } satisfies WeatherWorkerRequest); };
  const exportCsv = () => result && download('weather-lab-jackson-2019.csv', ['at,temperatureC,precipitationMm,snowfallCm,windSpeedKph,condition', ...result.simulated.map((hour) => [hour.at,hour.temperatureC,hour.precipitationMm,hour.snowfallCm,hour.windSpeedKph,hour.condition].join(','))].join('\n'), 'text/csv');
  return <main><header><p className="eyebrow">MOUNTAIN PLANNER · STANDALONE</p><h1>Weather Model Lab</h1><p>Independent, deterministic validation for a full local calendar year.</p></header>
    <section className="controls" aria-label="Run controls"><label>Location<input value="Jackson, New Hampshire" disabled/></label><label>Validation year<input value="2019" disabled/></label>
      <label>World seed<input value={seed} onChange={(event) => setSeed(event.target.value)}/></label><label>Difficulty<select value={profile} onChange={(event) => setProfile(event.target.value as keyof typeof WEATHER_DIFFICULTY_PRESETS)}>{Object.keys(WEATHER_DIFFICULTY_PRESETS).map((key) => <option key={key}>{key}</option>)}</select></label>
      <button onClick={run} disabled={activeId.current != null}>Run year</button><button className="secondary" onClick={cancel} disabled={activeId.current == null}>Cancel</button></section>
    <section className="status"><span>{status}</span><progress value={progress} max={1}/><small>Development fixture: Jackson artifact · Station KMWN · prior-30 excludes 2019 · not a live-provider comparison</small></section>
    {result && <><nav className="months" aria-label="Month">{MONTHS.map((label,index) => <button className={month === index + 1 ? 'active' : ''} onClick={() => setMonth(index + 1)} key={label}>{label}</button>)}</nav>
      <section className="panel"><div className="panel-title"><div><h2>{MONTHS[month - 1]} temperature</h2><p><i className="observed"/> observed <i className="simulated"/> simulated</p></div><div><button className="secondary" onClick={() => download('weather-lab-jackson-2019.json', JSON.stringify(result), 'application/json')}>Export JSON</button> <button className="secondary" onClick={exportCsv}>Export CSV</button></div></div>
        <WeatherChart simulated={result.simulated} observed={result.observed} month={month}/></section>
      <section className="summary"><article><span>Truth hash</span><strong>{result.truthHash.slice(0,16)}</strong></article><article><span>Forecast issues</span><strong>{result.forecasts.length}</strong></article><article><span>Warnings</span><strong>{result.warnings.length}</strong></article></section>
      <section className="panel"><h2>Monthly comparison</h2><table><thead><tr><th>Metric</th><th>Observed</th><th>Simulated</th><th>Status</th></tr></thead><tbody>{result.monthly[month-1].metrics.map((metric) => <tr key={`${metric.variable}-${metric.metric}`}><td>{metric.variable} · {metric.metric}</td><td>{metric.observed?.toFixed(2) ?? 'Unavailable'}</td><td>{metric.simulated?.toFixed(2) ?? 'Unavailable'}</td><td><span className={`badge ${metric.status}`}>{metric.status}</span></td></tr>)}</tbody></table></section>
      <section className="panel"><h2>Diagnostics</h2><p>Condition occupancy, transition counts, spell lengths, and forecast-versus-truth data are included in the deterministic JSON export. Forecast payloads contain only issued values and coarse long-range signals.</p></section></>}
  </main>;
}
