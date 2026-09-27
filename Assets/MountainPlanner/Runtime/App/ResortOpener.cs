using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>What the opener is doing, for the loading status line (U6).</summary>
    public readonly struct OpenProgress
    {
        public readonly string Detail;
        public readonly float Fraction;

        public OpenProgress(string detail, float fraction)
        {
            Detail = detail;
            Fraction = fraction;
        }
    }

    /// <summary>A resort on screen.</summary>
    public sealed class OpenedResort
    {
        public PackageManifest Manifest;
        public CacheManifest Cache;
        public GameObject Root;
        public Dictionary<(int, int), Terrain> Tiles;
        public ITerrainSurface Surface;
        public LocalFrame Frame;
        public double Seconds;
    }

    /// <summary>
    /// Opens a downloaded resort from disk (0.3 §4.3 step 4; task 06). No network: everything comes from
    /// the package. Heights decode on worker threads while the main thread creates a few Terrain tiles
    /// per frame, so the window stays responsive and the status line keeps moving.
    /// </summary>
    public static class ResortOpener
    {
        public const int TilesPerFrame = 4;

        public static async Task<OpenedResort> OpenAsync(string packageFolder, Transform parent, TerrainDetail detail,
                                                         IProgress<OpenProgress> progress, CancellationToken ct = default, Material material = null)
        {
            var clock = Stopwatch.StartNew();
            progress?.Report(new OpenProgress("Opening: reading the package", 0));
            var manifest = await Task.Run(() => ResortPackage.ReadManifest(packageFolder), ct);

            if (!TerrainCache.IsCurrent(packageFolder, manifest))
            {
                var tileProgress = new Progress<CacheProgress>(p =>
                    progress?.Report(new OpenProgress($"Preparing terrain: tile {p.Tile} of {p.Tiles}", 0.5f * p.Tile / p.Tiles)));
                await Task.Run(() => TerrainCache.Build(packageFolder, manifest, tileProgress, ct), ct);
            }
            var cache = await Task.Run(() => TerrainCache.ReadManifest(packageFolder), ct);
            var frame = new LocalFrame(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY));

            var root = new GameObject($"Resort: {manifest.Site.Name}");
            if (parent != null) root.transform.SetParent(parent, false);
            var layers = new[] { SnowGround.CreateLayer() };
            var tiles = new Dictionary<(int, int), Terrain>();

            // Decode every tile on worker threads (in order of distance from the centre, so the view fills in
            // from the middle); create Terrains on the main thread as they become ready.
            var order = cache.Tiles.OrderBy(t => Distance(t, cache, frame)).ToList();
            var decoded = order.Select(t => Task.Run(() => TerrainTiles.LoadHeights(packageFolder, t), ct)).ToList();
            float start = progress == null ? 0 : 0.5f;
            double waited = 0, created = 0;
            var part = Stopwatch.StartNew();
            for (int n = 0; n < order.Count; n++)
            {
                part.Restart();
                float[,] heights = await decoded[n];
                waited += part.Elapsed.TotalSeconds;
                part.Restart();
                var tile = order[n];
                tiles[(tile.Column, tile.Row)] = TerrainTiles.Create(root.transform, cache, tile, heights, frame, material, layers, detail);
                created += part.Elapsed.TotalSeconds;
                progress?.Report(new OpenProgress($"Opening terrain: tile {n + 1} of {order.Count}", start + (1 - start) * (n + 1) / order.Count));
                if ((n + 1) % TilesPerFrame == 0) await Task.Yield();
            }
            TerrainTiles.ConnectNeighbours(tiles);
            UnityEngine.Debug.Log($"[ResortOpener] {order.Count} tiles: waiting for decode {waited:F2} s, creating terrains {created:F2} s, total {clock.Elapsed.TotalSeconds:F2} s");

            var ring = SiteSquare.Create(frame.Origin, manifest.Site.SizeMetres / 1000.0).Ring;
            var (w, s) = frame.ToLocal(new AlbersPoint(ring.West, ring.South));
            var first = cache.Tiles.OrderBy(t => t.Row).ThenBy(t => t.Column).First();
            var (tw, tn) = frame.ToLocal(new AlbersPoint(first.West, first.North));
            var surface = new UnityTerrainSurface(tiles, (float)tw, (float)tn, (float)cache.TileMetres, new Rect((float)w, (float)s, (float)ring.Width, (float)ring.Height));
            progress?.Report(new OpenProgress("Ready", 1));
            return new OpenedResort
            {
                Manifest = manifest, Cache = cache, Root = root, Tiles = tiles, Surface = surface, Frame = frame, Seconds = clock.Elapsed.TotalSeconds,
            };
        }

        static double Distance(CacheTile t, CacheManifest cache, LocalFrame frame)
        {
            var (x, z) = frame.ToLocal(new AlbersPoint(t.West + cache.TileMetres / 2, t.North - cache.TileMetres / 2));
            return x * x + z * z;
        }
    }
}
