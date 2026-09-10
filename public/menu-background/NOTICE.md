# Crystal Mountain menu background

Ground cover: © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021), processed by ESA WorldCover consortium.

ESA WorldCover 10 m 2021 v200, Zanaga et al. (2022), https://doi.org/10.5281/zenodo.7254221.
Licensed under Creative Commons Attribution 4.0 International: https://creativecommons.org/licenses/by/4.0/.
Source and terms: https://esa-worldcover.org/en/data-access.
Modifications: WMTS map colors grouped and recolored for a decorative cartographic background. Transparent product-footprint pixels in coarse overview tiles receive a neutral color. This package is not intended for analysis.

Elevation: Mapzen Terrain Tiles, accessed from https://registry.opendata.aws/terrain-tiles/.
US coverage includes USGS 3DEP/NED public-domain data; coarse overview coverage incorporates additional datasets.
Full source attribution: https://github.com/tilezen/joerd/blob/master/docs/attribution.md.
The bundled TERRAIN-SOURCES.md reproduces the upstream attribution notice.

The manifest lists every tile's SHA-256 checksum. Regenerate with `npm run prepare:menu-background`.

The displayed cover tiles depict a fixed decorative alpine morning. A two-pixel blur with neighboring-tile gutters softens the input classification edges. The offline material bake then adds evergreen and rock color variation using deterministic global Web Mercator coordinates, plus ivory snow based on elevation and slope. Snow is artistic, not observed snow cover or gameplay weather. Steep slopes retain exposed rock and water retains its own color. Fine texture fades out in coarse overview tiles. Missing neighbors at the package boundary extend the edge pixel; X coordinates wrap at the world boundary.

Preserved recolored cover and original terrain tiles remain available for reproducible, offline regeneration with `npm run smooth:menu-background`. Elevation data is never blurred or modified. `manifest.json` records the bake version and updated SHA-256 checksums for the derived `smooth-cover` tiles.
