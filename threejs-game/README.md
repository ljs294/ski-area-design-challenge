# Standalone Three.js edition

This folder contains the separately launchable Three.js design fork. It has its own Electron main process, renderer entrypoint, launcher, build output, application-data directory, fork saves, and portable-executable configuration. The original MapLibre game remains at the repository root.

From this folder on Windows, double-click `run-threejs.bat`. From the repository root, the equivalent command is:

```powershell
npm.cmd run dev:three
```

Build and test only this edition with:

```powershell
npm.cmd run build:three
npm.cmd --prefix threejs-game test
```

Create its configured portable Windows executable with:

```powershell
npm.cmd --prefix threejs-game run package
```

The standalone app intentionally imports renderer-neutral domain, persistence, terrain-preparation, and reusable React UI modules from the parent repository. Three.js scene, host, entrypoint, and Electron ownership stay inside this folder.
