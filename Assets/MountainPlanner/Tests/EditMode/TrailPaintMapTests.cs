using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation.Design;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    // The paint on the GPU is the paint on the CPU (owner's findings, 2026-10-20: a second painted area made the first
    // vanish, a third painted ground never painted): after each frame's upload, the texture's bytes are the CPU copy's.
    // The fault showed only with a render thread, as the player runs: release.ps1 runs these again with -force-gfx-mt.
    public sealed class TrailPaintMapTests
    {
        static void AssertGpuIsCpu(TrailPaintMap map, string when)
        {
            map.Flush();
            var cpu = map.CpuBytes.ToArray();
            var request = AsyncGPUReadback.Request(map.GpuTexture, 0);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, when);
            var gpu = request.GetData<byte>();
            Assert.AreEqual(cpu.Length, gpu.Length, when);
            int wrong = 0, first = -1;
            for (int i = 0; i < cpu.Length; i++)
                if (cpu[i] != gpu[i]) { wrong++; if (first < 0) first = i; }
            Assert.AreEqual(0, wrong, $"{when}: {wrong} bytes differ, the first at {first} ({map.GpuTexture.width}x{map.GpuTexture.height})");
        }

        static void Area(TrailPaintMap map, Vector2 from, Vector2 step, int stamps, bool erase = false)
        {
            for (int k = 0; k < stamps; k++) map.Stamp(from + step * k, from + step * (k + 1), 10, erase);
        }

        static TrailPaintMap Map()
        {
            var map = new TrailPaintMap();
            map.Align(new EditLattice(-2000, 2000), 0, 0);
            return map;
        }

        [Test]
        public void TheGpuHoldsTheCpuPaintAfterEveryUpload()
        {
            using var map = Map();
            // A first area, painted stretch by stretch as a drag does.
            Area(map, Vector2.zero, new Vector2(5, 0), 20);
            AssertGpuIsCpu(map, "the first area");
            // A second area well apart: the window grows.
            Area(map, new Vector2(300, 250), new Vector2(5, 0), 20);
            AssertGpuIsCpu(map, "the second area while dragging");
            // Letting go replays every stroke (Redraw clears and stamps again): the whole trail is dirty at once.
            map.Clear();
            Area(map, Vector2.zero, new Vector2(5, 0), 20);
            Area(map, new Vector2(300, 250), new Vector2(5, 0), 20);
            AssertGpuIsCpu(map, "the second area let go");
            // A third, as the owner's third stroke.
            map.Clear();
            Area(map, Vector2.zero, new Vector2(5, 0), 20);
            Area(map, new Vector2(300, 250), new Vector2(5, 0), 20);
            Area(map, new Vector2(-200, -100), new Vector2(0, -5), 40);
            AssertGpuIsCpu(map, "the third area let go");
        }

        [Test]
        public void SmallChangesAndReplaysInARowStayExact()
        {
            using var map = Map();
            Area(map, Vector2.zero, new Vector2(5, 0), 20);
            Area(map, new Vector2(500, -300), new Vector2(0, 5), 20);
            AssertGpuIsCpu(map, "two areas");
            // A drag's frames: one small change each.
            for (int k = 0; k < 10; k++)
            {
                map.Stamp(new Vector2(100 + k * 3, 0), new Vector2(103 + k * 3, 0), 10, false);
                AssertGpuIsCpu(map, $"drag frame {k}");
            }
            // Two full replays on successive frames, then rubbing out.
            for (int k = 0; k < 2; k++)
            {
                map.Clear();
                Area(map, Vector2.zero, new Vector2(5, 0), 26);
                Area(map, new Vector2(500, -300), new Vector2(0, 5), 20);
                AssertGpuIsCpu(map, $"replay {k}");
            }
            Area(map, new Vector2(40, 0), new Vector2(2, 0), 5, erase: true);
            AssertGpuIsCpu(map, "rubbed out");
            // A replay, then a stroke that grows the window again.
            map.Clear();
            Area(map, Vector2.zero, new Vector2(5, 0), 26);
            AssertGpuIsCpu(map, "replayed");
            Area(map, new Vector2(-900, 700), new Vector2(5, 0), 20);
            AssertGpuIsCpu(map, "grown after a replay");
        }
    }
}
