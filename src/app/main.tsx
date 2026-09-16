import { Profiler, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './app.css';
import './ui.css';
import { installIntegratedBenchmarkBootstrap } from '../integratedBenchmarkScenario';
import { recordIntegratedReactProfile } from './integratedReactProfiler';

installIntegratedBenchmarkBootstrap(window.location.search);

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('#root not found');

createRoot(rootEl).render(
  <StrictMode>
    {import.meta.env.VITE_INTEGRATED_REACT_PROFILING === '1'
      ? <Profiler id="App" onRender={recordIntegratedReactProfile}><App /></Profiler>
      : <App />}
  </StrictMode>
);
