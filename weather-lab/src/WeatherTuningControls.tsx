import { useId, useRef, useState } from 'react';
import { WEATHER_SIMULATION_TUNING_LIMITS } from '../../weather-engine/src/index.ts';
import type { WeatherSimulationTuningV1 } from '../../weather-engine/src/index.ts';
import { TUNING_CONTROLS } from './weatherLabViewModel.ts';

export type WeatherTuningPreset = 'historical' | 'smoothed' | 'baseline';

export interface WeatherTuningControlsProps {
  value: WeatherSimulationTuningV1;
  onChange: (value: WeatherSimulationTuningV1) => void;
  disabled?: boolean;
  onPreset?: (preset: WeatherTuningPreset) => void;
  onImportJson?: (text: string) => void;
  onExportJson?: () => void;
}

const GROUPS = ['Atmosphere', 'Precipitation and events', 'Forecast'] as const;

function displayedValue(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(2);
}

export function WeatherTuningControls({
  value,
  onChange,
  disabled = false,
  onPreset,
  onImportJson,
  onExportJson,
}: WeatherTuningControlsProps) {
  const prefix = useId();
  const fileInput = useRef<HTMLInputElement>(null);
  const [importError, setImportError] = useState<string | null>(null);

  const update = (key: Exclude<keyof WeatherSimulationTuningV1, 'version' | 'id'>, next: number | null) => {
    onChange({ ...value, id: 'custom', [key]: next });
  };

  const importFile = async (file: File | undefined) => {
    if (!file || !onImportJson) return;
    try {
      onImportJson(await file.text());
      setImportError(null);
    } catch (error) {
      setImportError(error instanceof Error ? error.message : String(error));
    } finally {
      if (fileInput.current) fileInput.current.value = '';
    }
  };

  return <section className="weather-tuning" aria-labelledby={`${prefix}-title`}>
    <div className="panel-title">
      <div>
        <h2 id={`${prefix}-title`}>Candidate tuning</h2>
        <p>Changes apply to the candidate run. Climate artifacts and the pinned baseline remain unchanged.</p>
      </div>
      <div className="weather-tuning-actions">
        {onPreset && <>
          <button type="button" className="secondary" disabled={disabled} onClick={() => onPreset('smoothed')}>Reset to smoothed</button>
          <button type="button" className="secondary" disabled={disabled} onClick={() => onPreset('historical')}>Reset to historical</button>
          <button type="button" className="secondary" disabled={disabled} onClick={() => onPreset('baseline')}>Match baseline</button>
        </>}
        {onImportJson && <>
          <button type="button" className="secondary" disabled={disabled} onClick={() => fileInput.current?.click()}>Import tuning</button>
          <input ref={fileInput} type="file" accept="application/json,.json" hidden onChange={(event) => void importFile(event.target.files?.[0])}/>
        </>}
        {onExportJson && <button type="button" className="secondary" disabled={disabled} onClick={onExportJson}>Export tuning</button>}
      </div>
    </div>
    {importError && <p role="alert">Tuning import failed: {importError}</p>}
    {GROUPS.map((group) => <fieldset key={group} disabled={disabled}>
      <legend>{group}</legend>
      <div className="weather-tuning-grid">
        {TUNING_CONTROLS.filter((control) => control.group === group).map((control) => {
          const id = `${prefix}-${control.key}`;
          const current = value[control.key];
          const inherited = current === null;
          const numericValue = inherited ? control.fallback : current;
          const [minimum, maximum] = WEATHER_SIMULATION_TUNING_LIMITS[control.key];
          return <div className="weather-tuning-control" key={control.key}>
            <label htmlFor={id}>{control.label}</label>
            <p id={`${id}-help`}>{control.help}</p>
            {control.nullable && <label className="weather-tuning-fitted">
              <input type="checkbox" checked={inherited} onChange={(event) => update(control.key, event.target.checked ? null : control.fallback)}/>
              Use fitted climate value
            </label>}
            <div>
              <input id={id} type="range" min={minimum} max={maximum} step={control.step} value={numericValue}
                disabled={disabled || inherited} aria-describedby={`${id}-help`} onChange={(event) => update(control.key, Number(event.target.value))}/>
              <output htmlFor={id}>{inherited ? 'Fitted' : displayedValue(numericValue)}</output>
            </div>
          </div>;
        })}
      </div>
    </fieldset>)}
  </section>;
}
