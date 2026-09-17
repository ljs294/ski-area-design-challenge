import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { SettingsProvider } from '../SettingsContext';
import { ThreeApp } from './ThreeApp';
import '../app.css';
import '../ui.css';

const root = document.getElementById('root');
if (!root) throw new Error('#root not found');
createRoot(root).render(<StrictMode><SettingsProvider><ThreeApp /></SettingsProvider></StrictMode>);
