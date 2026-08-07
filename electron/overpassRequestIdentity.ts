import type { WebRequest } from 'electron';
import { OVERPASS_ENDPOINTS, OVERPASS_REPOSITORY_URL } from '../src/overpassConfig';

export function buildOverpassUserAgent(appVersion: string): string {
  return `Mountain-Planner/${appVersion} (+${OVERPASS_REPOSITORY_URL})`;
}

/**
 * Chromium does not allow renderer fetches to set User-Agent. Install the
 * identity at Electron's network boundary and scope it to the exact provider
 * endpoints so unrelated application traffic remains untouched.
 */
export function registerOverpassRequestIdentity(
  webRequest: WebRequest,
  appVersion: string,
): void {
  const userAgent = buildOverpassUserAgent(appVersion);

  webRequest.onBeforeSendHeaders(
    { urls: [...OVERPASS_ENDPOINTS] },
    (details, callback) => {
      const requestHeaders = Object.fromEntries(
        Object.entries(details.requestHeaders)
          .filter(([name]) => name.toLowerCase() !== 'user-agent'),
      );
      requestHeaders['User-Agent'] = userAgent;
      callback({ requestHeaders });
    },
  );
}
