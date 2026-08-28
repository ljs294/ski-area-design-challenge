# Weather package builder

`npm run weather:service` starts the local package-builder API on
`http://127.0.0.1:8787`. It defaults to `WEATHER_SERVICE_MODE=fixture`, which
is deterministic development data and is deliberately marked `limited` in
every manifest. It never contacts a provider.

`npm run dev:weather-lab` starts the independent coordinate-first Lab and a
live service automatically unless a healthy service is already listening.
Set `WEATHER_SERVICE_MODE=fixture` explicitly for the committed Jackson
development workflow. Fixture data is never presented as a live comparison.

## API

- `POST /v1/weather-package-jobs` creates an asynchronous build job.
- `GET` or `DELETE /v1/weather-package-jobs/:id` reads progress or cancels it.
- `GET /v1/weather-package-jobs/:id/manifest` reads the completed v2 manifest.
- `GET /v1/weather-package-jobs/:id/chunks/:chunkId` streams the exact gzip
  bytes named by the manifest checksum.
- `POST /v1/weather-packages` remains a compatibility endpoint and waits for a
  job, returning decoded historical years. New callers should use job chunks.

Errors always have `{ error: { code, message, retryable, details? } }`.

The standalone Lab uses additive endpoints that do not modify installed game
weather packages:

- `GET /v1/weather-lab/location-context?latitude=…&longitude=…` probes Daymet
  land coverage/elevation, resolves the MERRA-2 grid cell, and returns
  eligible prior-30 validation years.
- `POST /v1/weather-lab/preparations` starts Daymet/MERRA-2 normalization and
  climate compilation. `GET` or `DELETE /v1/weather-lab/preparations/:id`
  reads progress or cancels it. Equivalent completed requests reuse a
  persisted preparation result across service instances.
- `GET /v1/weather-lab/models/:hash` and
  `/v1/weather-lab/observed-series/:hash` return immutable ready artifacts.

Observed Lab artifacts preserve Daymet daily liquid-equivalent precipitation
and temperature anchors, uses MERRA-2 for hourly atmospheric structure, and
records explicit provenance for thermodynamically derived snowfall.
Missing or suspect hourly observations remain missing rather than becoming
zero precipitation.

## Live hosting configuration

Set `WEATHER_SERVICE_MODE=live`. The game/browser never receives these values;
they belong only to the project-hosted builder:

The default source policy uses only public, no-cost NASA endpoints. NASA
Earth-science data are open access and, absent a specifically marked
restriction, are distributed under CC0; retain the recorded NASA/Daymet
citations when publishing derived data.

- The official NASA POWER hourly point API is the sole MERRA-2-based
  meteorology route and requires no account, API key, token, paid plan, or
  private service. Daymet remains authoritative for daily precipitation totals;
  NASA POWER supplies the within-day precipitation timing and hourly
  atmospheric fields. The endpoint is intentionally not environment-configurable.
- `DAYMET_SINGLE_PIXEL_URL`: optional override of Daymet's lower-48 single
  pixel CSV endpoint. `DAYMET_NCSS_URL` is required for Alaska/Hawaii and must
  return equivalent CSV daily fields from a NetCDF subset route.

The service caches gameplay sources under the existing cache and standalone
Lab sources/artifacts under `weather-lab-v1`. It writes ready manifests last.
Runtime game clients never call the Lab endpoints.
