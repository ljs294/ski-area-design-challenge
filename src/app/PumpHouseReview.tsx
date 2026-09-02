import type { BuildingSiteAnalysisResult } from '../buildingSiteAnalysis';
import { formatBuildingHeight, gableRidgeHeightM } from '../buildingUnits';
import { canConfirmBuilding, type BuildingReviewDraft } from './buildingControllerModel';
import type { Units } from './SettingsContext';

function formatVolume(value: number, units: Units): string {
  if (units === 'imperial') return `${(value * 1.30795062).toFixed(1)} yd³`;
  return `${value.toFixed(1)} m³`;
}

function formatElevation(value: number, units: Units): string {
  return units === 'imperial' ? `${(value * 3.280839895).toFixed(1)} ft` : `${value.toFixed(2)} m`;
}

function dimensionValue(valueM: number, units: Units): string {
  return units === 'imperial' ? (valueM * 3.280839895).toFixed(1) : valueM.toFixed(2);
}

function dimensionUnit(units: Units): string {
  return units === 'imperial' ? 'ft' : 'm';
}

function DimensionField({ label, valueM, units, onChange }: {
  label: string;
  valueM: number;
  units: Units;
  onChange: (valueM: number) => void;
}) {
  const value = dimensionValue(valueM, units);
  const factor = units === 'imperial' ? 3.280839895 : 1;
  return <label className="lake-depth-row pump-house-dimension-field"><span className="lift-stat-label">{label}</span>
    <span className="lake-depth-input-wrap"><input className="lake-depth-input" type="number"
      min="0.1" step={units === 'imperial' ? '1' : '0.1'} value={value}
      aria-label={`${label} in ${dimensionUnit(units)}`} onChange={(event) => {
        const next = Number(event.target.value);
        if (Number.isFinite(next) && next > 0) onChange(next / factor);
      }} />
      <span>{dimensionUnit(units)}</span></span></label>;
}

function SiteStatus({ draft }: { draft: BuildingReviewDraft }) {
  if (draft.siteStatus === 'pending') return <div className="pump-house-site-status site-hint"
    data-testid="pump-house-site-pending">Analyzing site… Confirmation will be available when analysis finishes.</div>;
  if (draft.siteStatus === 'error') return <div className="pump-house-site-status lift-warning"
    data-testid="pump-house-site-error">{draft.siteError ?? 'This site is not valid for a pump house.'}</div>;
  if (draft.hasCollision) return <div className="pump-house-site-status lift-warning"
    data-testid="pump-house-site-error">This pump house overlaps another player building.</div>;
  return <div className="pump-house-site-status site-success" data-testid="pump-house-site-valid">
    Site valid. The complete footprint is inside prepared terrain.</div>;
}

function AnalysisSummary({ analysis, units }: { analysis: BuildingSiteAnalysisResult; units: Units }) {
  return <div className="pump-house-analysis" data-testid="pump-house-analysis">
    <div className="readout-line"><span className="lift-stat-label">Finished floor</span>
      <span className="lift-stat-value">{formatElevation(analysis.finishedFloorElevationM, units)}</span></div>
    {analysis.terrainGraded && <>
      <div className="readout-line"><span className="lift-stat-label">Cut</span>
        <span className="lift-stat-value">{formatVolume(analysis.earthwork.cutM3, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Fill</span>
        <span className="lift-stat-value">{formatVolume(analysis.earthwork.fillM3, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Balance</span>
        <span className="lift-stat-value">{formatVolume(analysis.earthwork.balanceM3, units)}</span></div>
      <div className="site-hint">Flattened site includes a 6 ft apron around the building.</div>
    </>}
    {!analysis.terrainGraded && <div className="site-hint">
      Level structure on slope uses concrete foundation walls over unchanged terrain; eight perimeter samples are retained.</div>}
  </div>;
}

export interface PumpHouseReviewProps {
  draft: BuildingReviewDraft;
  units: Units;
  building?: boolean;
  onPatch: (patch: Partial<BuildingReviewDraft>) => void;
  onConfirm: () => void | Promise<void>;
  onCancel: () => void;
}

/** Review form for an analyzed pump-house placement. */
export function PumpHouseReview({ draft, units, building = false, onPatch, onConfirm, onCancel }: PumpHouseReviewProps) {
  const ridgeHeight = gableRidgeHeightM(draft.dimensions.widthM, draft.dimensions.eaveHeightM, 4, 12);
  const confirmDisabled = building || !canConfirmBuilding(draft);
  return <div className="site-control site-control-wide pump-house-review" data-testid="pump-house-review">
    <div className="dock-head"><span className="dock-head-title">Review pump house</span>
      <button className="settings-close-x" aria-label="Close" onClick={onCancel}>×</button></div>
    <label className="name-entry-row"><span className="lift-stat-label">Name</span>
      <input className="name-entry-input lift-name-input" data-testid="pump-house-name"
        aria-label="Pump house name" value={draft.name}
        onChange={(event) => onPatch({ name: event.target.value })} /></label>
    <div className="pump-house-fields">
      <DimensionField label="Length" valueM={draft.dimensions.lengthM} units={units}
        onChange={(valueM) => onPatch({ dimensions: { ...draft.dimensions, lengthM: valueM } })} />
      <DimensionField label="Width" valueM={draft.dimensions.widthM} units={units}
        onChange={(valueM) => onPatch({ dimensions: { ...draft.dimensions, widthM: valueM } })} />
      <DimensionField label="Eave height" valueM={draft.dimensions.eaveHeightM} units={units}
        onChange={(valueM) => onPatch({ dimensions: { ...draft.dimensions, eaveHeightM: valueM } })} />
      <label className="lake-depth-row pump-house-dimension-field"><span className="lift-stat-label">Heading</span>
        <span className="lake-depth-input-wrap"><input className="lake-depth-input" type="number" min="0" max="359.999"
          step="0.1" value={draft.bearingDeg.toFixed(1)} aria-label="Heading in degrees"
          onChange={(event) => { const value = Number(event.target.value);
            if (Number.isFinite(value)) onPatch({ bearingDeg: value }); }} />
          <span>°</span></span></label>
    </div>
    <fieldset className="pump-house-foundation" data-testid="pump-house-foundation">
      <legend>Foundation mode</legend>
      <label><input type="radio" name="pump-house-foundation-mode" value="flattened"
        checked={draft.foundationMode === 'flattened'}
        onChange={() => onPatch({ foundationMode: 'flattened' })} />
        <span><strong>Flatten site</strong><small>Grade a level pad with a 6 ft working apron.</small></span></label>
      <label><input type="radio" name="pump-house-foundation-mode" value="slope"
        checked={draft.foundationMode === 'slope'}
        onChange={() => onPatch({ foundationMode: 'slope' })} />
        <span><strong>Level structure on slope</strong><small>Use concrete foundation walls over unchanged terrain.</small></span></label>
    </fieldset>
    <div className="lift-stats pump-house-fixed-specs">
      <div className="readout-line"><span className="lift-stat-label">Roof</span>
        <span className="lift-stat-value">Fixed gable · 4:12 pitch</span></div>
      <div className="readout-line"><span className="lift-stat-label">Ridge height</span>
        <span className="lift-stat-value">{formatBuildingHeight(ridgeHeight, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Pump equipment</span>
        <span className="lift-stat-value">1,000 hp / 85% efficiency</span></div>
      <div className="readout-line"><span className="lift-stat-label">Capital cost</span>
        <span className="lift-stat-value">TBD</span></div>
      <div className="readout-line"><span className="lift-stat-label">Maintenance</span>
        <span className="lift-stat-value">TBD</span></div>
    </div>
    <div className="network-section-title">Site validity</div>
    <SiteStatus draft={draft} />
    {draft.siteAnalysis && draft.siteStatus === 'ok' && <AnalysisSummary analysis={draft.siteAnalysis} units={units} />}
    {draft.confirmationError && <div className="lift-warning" data-testid="pump-house-confirmation-error">
      {draft.confirmationError}</div>}
    <div className="site-actions"><button className="site-btn" onClick={onCancel}>Cancel</button>
      <button className="site-btn site-btn-primary" data-testid="confirm-pump-house"
        disabled={confirmDisabled} onClick={onConfirm}>{building ? 'Building…' : 'Build pump house'}</button></div>
  </div>;
}
