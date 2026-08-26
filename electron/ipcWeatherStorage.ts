import { app, ipcMain } from 'electron';
import fs from 'fs';
import path from 'path';
import {
  WEATHER_DELETE_CHANNEL,
  WEATHER_LOAD_CHANNEL,
  WEATHER_SAVE_CHANNEL,
  type WeatherDeleteRequest,
  type WeatherDeleteResponse,
  type WeatherLoadRequest,
  type WeatherLoadResponse,
  type WeatherSaveRequest,
  type WeatherSaveResponse,
} from '../src/ipcContract';
import { isWeatherDataPackage } from '../src/weather/weatherModel';

const fsp = fs.promises;

function weatherDir(): string {
  const dir = path.join(app.getPath('userData'), 'weather');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function safePath(terrainKey: string): string | null {
  const dir = weatherDir();
  const resolved = path.resolve(dir, `${terrainKey}.json`);
  return resolved.startsWith(dir + path.sep) ? resolved : null;
}

export function registerWeatherStorageHandlers(): void {
  ipcMain.handle(WEATHER_SAVE_CHANNEL, async (_event, request: WeatherSaveRequest): Promise<WeatherSaveResponse> => {
    if (!isWeatherDataPackage(request.weatherPackage)) return { ok: false, error: 'Invalid offline weather package.' };
    const destination = safePath(request.weatherPackage.manifest.terrainKey);
    if (!destination) return { ok: false, error: 'Invalid terrain key.' };
    try {
      const temporary = `${destination}.${process.pid}-${Date.now()}.tmp`;
      await fsp.writeFile(temporary, JSON.stringify(request.weatherPackage), 'utf8');
      await fsp.rename(temporary, destination);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : 'Unable to save offline weather package.' };
    }
  });
  ipcMain.handle(WEATHER_LOAD_CHANNEL, async (_event, request: WeatherLoadRequest): Promise<WeatherLoadResponse> => {
    const source = safePath(request.terrainKey);
    if (!source) return null;
    try {
      const parsed: unknown = JSON.parse(await fsp.readFile(source, 'utf8'));
      return isWeatherDataPackage(parsed) ? parsed : null;
    } catch { return null; }
  });
  ipcMain.handle(WEATHER_DELETE_CHANNEL, async (_event, request: WeatherDeleteRequest): Promise<WeatherDeleteResponse> => {
    const destination = safePath(request.terrainKey);
    if (!destination) return { ok: false, error: 'Invalid terrain key.' };
    try { await fsp.rm(destination, { force: true }); return { ok: true }; }
    catch (error) { return { ok: false, error: error instanceof Error ? error.message : 'Unable to remove offline weather package.' }; }
  });
}
