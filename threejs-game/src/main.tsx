import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { SettingsProvider } from '../../src/app/SettingsContext';
import { ThreeApp } from './ThreeApp';
import '../../src/app/app.css';
import '../../src/app/ui.css';

const root = document.getElementById('root');
if (!root) throw new Error('#root not found');
createRoot(root).render(<StrictMode><SettingsProvider><ThreeApp /></SettingsProvider></StrictMode>);
