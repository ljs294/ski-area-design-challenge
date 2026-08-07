/**
 * Dependency-neutral Overpass configuration shared by the browser renderer
 * and Electron's network layer. Keep provider order intentional: requests
 * fall through sequentially when an instance is unavailable.
 */
export const OVERPASS_ENDPOINTS = [
  'https://overpass-api.de/api/interpreter',
  'https://maps.mail.ru/osm/tools/overpass/api/interpreter',
  'https://overpass.private.coffee/api/interpreter',
] as const;

export const OVERPASS_REPOSITORY_URL =
  'https://github.com/ljs294/ski-area-design-challenge';
