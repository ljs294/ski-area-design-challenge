const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { app } = require('electron');

const configuredRoot = process.env.SAVE_LOADING_ELECTRON_USER_DATA;
if (!configuredRoot) throw new Error('Electron isolation root was not supplied before main import.');
const root = path.resolve(configuredRoot);
if (app.getPath('userData') === root) throw new Error('Electron isolation root was not supplied before main import.');
app.setPath('userData', root);
if (app.getPath('userData') !== root) throw new Error('Electron userData path was not set to the isolated root.');
const main = path.resolve(process.env.SAVE_LOADING_ELECTRON_MAIN || path.resolve(process.cwd(), 'dist-electron/main.js'));
if (!main.endsWith(`${path.sep}main.js`) || !fs.existsSync(main)) throw new Error(`Electron main bundle is unavailable: ${main}`);
const bundledService = path.resolve(path.dirname(main), '..', 'weather-service');
if (!fs.existsSync(bundledService)) {
  const sourceService = path.resolve(process.cwd(), 'weather-service');
  if (!fs.existsSync(sourceService)) throw new Error(`Weather service fixture is unavailable: ${sourceService}`);
  fs.symlinkSync(sourceService, bundledService, 'junction');
}
void import(pathToFileURL(main).href).catch((error) => {
  console.error('Unable to import Electron main bundle:', error);
  app.exit(1);
});
