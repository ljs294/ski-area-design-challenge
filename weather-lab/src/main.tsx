import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { WeatherLabApp } from './WeatherLabApp.tsx';
import 'maplibre-gl/dist/maplibre-gl.css';
import './styles.css';
createRoot(document.getElementById('root')!).render(<StrictMode><WeatherLabApp/></StrictMode>);
