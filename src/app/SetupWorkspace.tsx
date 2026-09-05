import { useState } from 'react';
import type { MapViewChromeProps } from './MapViewChrome';
import { SearchBox } from './SearchBox';
import { SiteControl } from './SiteControl';
import { Icon } from './ui';

type Props = Pick<MapViewChromeProps, 'siteControl' | 'searchResult' | 'packageGate' | 'nameEntry' | 'setup'>;
const STEPS = ['Choose location', 'Define boundary', 'Prepare terrain', 'Name and enter'];
export function SetupWorkspace(props: Props) {
  const [reviewPrepared, setReviewPrepared] = useState(false);
  const gate = props.packageGate;
  const step = gate ? 2 : props.setup?.prepared ? reviewPrepared ? 2 : 3
    : props.siteControl?.mode === 'locked' ? 2 : props.siteControl?.mode === 'selecting' ? 1 : 0;
  const progress = gate?.progress;
  const percent = progress && progress.total > 0 ? Math.min(100, Math.round(progress.completed / progress.total * 100)) : 0;
  return <section className="setup-workspace" aria-label="New resort setup">
    <header><span className="ui-eyebrow">Mountain Planner</span><h1>New resort</h1><p>Find the mountain you want to make your own.</p></header>
    <ol className="setup-steps">{STEPS.map((label, index) => <li key={label} aria-current={index === step ? 'step' : undefined}
      className={index === step ? 'is-current' : index < step ? 'is-complete' : ''}><span>{index < step ? '✓' : index + 1}</span>{label}</li>)}</ol>
    <div className="setup-body">
      <h2>{STEPS[step]}</h2>
      {step === 0 && <>{props.searchResult && <SearchBox onResult={props.searchResult} />}
        <p>Search for a place or explore the map, then select your build site.</p>
        {props.siteControl && <SiteControl {...props.siteControl} />}</>}
      {step === 1 && props.siteControl && <SiteControl {...props.siteControl} />}
      {step === 2 && <>
        <p>{props.siteControl?.box && `${props.siteControl.box.widthKm.toFixed(1)} × ${props.siteControl.box.heightKm.toFixed(1)} km · `}
          {props.setup?.prepared ? 'Your mountain is ready.' : 'Prepare a local copy of your mountain to start designing.'}</p>
        {gate ? <div className="package-gate" aria-live="polite">
          <h3>{gate.mapContextError ? 'Map context unavailable' : gate.state === 'error' ? 'Preparation failed' : 'Preparing your mountain'}</h3>
          {gate.state === 'preparing' && !gate.mapContextError && <>
            <p>Preparing terrain, vegetation, and contours.</p>
            <progress aria-label="Terrain preparation" max={100} value={percent} /><p>{percent}% complete</p>
            <details><summary>Processing details</summary><p>{progress?.message ?? 'Starting preparation…'}</p>
              {progress && <p>Step {Math.min(progress.completed + 1, progress.total)} of {progress.total}</p>}</details>
            <button className="ui-button" onClick={gate.cancel}>Cancel</button>
          </>}
          {gate.mapContextError ? <><p role="alert">Roads and water could not be downloaded.</p>
            <details><summary>Details</summary>{gate.mapContextError}</details>
            <div className="ui-actions"><button className="ui-button" onClick={() => gate.decideMapContext('cancel')}>Cancel</button>
              <button className="ui-button" onClick={() => gate.decideMapContext('continue')}>Continue Without Map Context</button>
              <button className="ui-button ui-button-primary" onClick={() => gate.decideMapContext('retry')}>Retry Map Context</button></div></>
            : gate.state === 'error' && <><p role="alert">We could not prepare this mountain. Try again or adjust the boundary.</p><details><summary>Processing details</summary>{gate.error}</details><div className="ui-actions">
              <button className="ui-button" onClick={props.setup?.back}>Back to boundary</button>
              <button className="ui-button ui-button-primary" onClick={gate.prepare}>Prepare Resort Data</button></div></>}
        </div> : <div className="ui-actions"><button className="ui-button" onClick={props.setup?.back}>Back to boundary</button>
          <button className="ui-button ui-button-primary" onClick={props.setup?.prepared ? () => setReviewPrepared(false) : props.setup?.prepare}>
            {props.setup?.prepared ? 'Name your resort' : 'Prepare terrain'}<Icon name="arrow" /></button></div>}
      </>}
      {step === 3 && props.nameEntry && <div className="name-entry">
        <label className="name-entry-title" htmlFor="resort-name">Name your resort</label>
        <input id="resort-name" className="ui-input name-entry-input" placeholder="e.g. Crystal Peak Resort"
          value={props.nameEntry.value} autoFocus onChange={(event) => props.nameEntry?.change(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter' && !props.nameEntry?.saving) props.nameEntry?.submit(); }} />
        <div className="ui-actions"><button className="ui-button" disabled={props.nameEntry.saving} onClick={() => setReviewPrepared(true)}>Back</button>
          <button className="ui-button ui-button-primary" disabled={props.nameEntry.saving} onClick={props.nameEntry.submit}>
            {props.nameEntry.saving ? 'Creating…' : 'Start Designing'}<Icon name="arrow" /></button></div>
      </div>}
    </div>
  </section>;
}
