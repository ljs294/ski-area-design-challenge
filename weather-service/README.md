# Weather package builder

`npm run weather:service` starts the local package-builder API on
`http://127.0.0.1:8787`. It defaults to `WEATHER_SERVICE_MODE=fixture`, which
is deterministic development data and is deliberately marked `limited` in
every manifest. It never contacts a provider.

`npm run dev:weather-lab` starts this fixture service automatically unless a
healthy service is already listening on the configured URL. Open
`http://localhost:5173/#weather-lab` after Vite reports that it is ready.

## API

- `POST /v1/weather-package-jobs` creates an asynchronous build job.
- `GET` or `DELETE /v1/weather-package-jobs/:id` reads progress or cancels it.
- `GET /v1/weather-package-jobs/:id/manifest` reads the completed v2 manifest.
- `GET /v1/weather-package-jobs/:id/chunks/:chunkId` streams the exact gzip
  bytes named by the manifest checksum.
- `POST /v1/weather-packages` remains a compatibility endpoint and waits for a
  job, returning decoded historical years. New callers should use job chunks.

Errors always have `{ error: { code, message, retryable, details? } }`.

## Live hosting configuration

Set `WEATHER_SERVICE_MODE=live`. The game/browser never receives these values;
they belong only to the project-hosted builder:

- `MERRA2_SUBSET_URL`: authenticated project-owned MERRA-2 subset gateway.
  It accepts `{ provider, version, year, latitude, longitude, variables }` and
  returns `{ gridCell?, hours: [...] }` with all UTC hours of the requested
  year.
- `MERRA2_BEARER_TOKEN`: optional service-to-service token for that gateway.
- `GHCNH_ADAPTER_URL`: optional project-owned station-search gateway. It
  returns `{ stations }`; the builder applies only stations that pass its
  completeness and QC thresholds.
- `DAYMET_SINGLE_PIXEL_URL`: optional override of Daymet's lower-48 single
  pixel CSV endpoint. `DAYMET_NCSS_URL` is required for Alaska/Hawaii and must
  return equivalent CSV daily fields from a NetCDF subset route.

The service caches normalized source subsets under
`weather-service/.weather-cache` (or `WEATHER_CACHE_DIR`) and stores completed
packages content-addressably. It writes chunks atomically and writes the ready
manifest last. Runtime clients install those chunks locally and never call this
service during play.
