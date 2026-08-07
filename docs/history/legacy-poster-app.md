# Legacy poster application backup

The obsolete poster/Leaflet application and curated preset payload were verified in a remote Git reference before deletion work was authorized.

## Verified backup

| Field | Value |
| --- | --- |
| Repository | [ljs294/ski-area-design-challenge](https://github.com/ljs294/ski-area-design-challenge) |
| Remote reference | `refs/heads/legacy/v0.1` |
| Immutable commit | [`3f5eb2378342053906719c75650d785ea2249241`](https://github.com/ljs294/ski-area-design-challenge/commit/3f5eb2378342053906719c75650d785ea2249241) |
| Verification | `git ls-remote` returned the exact SHA for the remote reference, and the referenced tree was inspected for the legacy files and assets |

The verified tree contains the poster menu entrypoint and renderer, GIS selection and content-management code, curated-preset tooling, and preset terrain assets that form the obsolete vertical. This remote branch is the recovery point for that code and data after removal from the supported application branch.

The obsolete vertical was removed from the supported `refactor` branch during benchmark B3 on 2026-08-06. The deletion covered the poster entrypoint and stylesheet, canvas renderer and its contour/label/hillshade/tile-index helpers, Leaflet GIS selector and selection box, content manager, curated preset catalog/downloader, and all tracked preset terrain assets.

The recovery reference does not authorize force-pushing, deleting the backup reference, or creating/pushing a remote tag.
