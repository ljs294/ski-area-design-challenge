// Electron utilityProcess.fork starts CommonJS reliably in both development
// and packaged builds. The weather service itself remains an ESM module.
void import('./server.mjs').then(({ listenWeatherService }) => {
  listenWeatherService();
}).catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.stack ?? error.message : String(error)}\n`);
  process.exitCode = 1;
});
