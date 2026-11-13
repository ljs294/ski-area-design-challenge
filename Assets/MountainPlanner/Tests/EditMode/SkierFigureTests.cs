using System.IO;
using MountainPlanner.Editor;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Presentation.Skiers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.22: the shipped skier figures read, skin like Blender, and the per-frame sorting holds.</summary>
    public sealed class SkierFigureTests
    {
        static SkierFigureData Shipped()
        {
            Assume.That(File.Exists(SkierImport.Target), "the skier figures are not imported");
            var bytes = File.ReadAllBytes(SkierImport.Target);
            Assume.That(bytes.Length > 200 && !System.Text.Encoding.ASCII.GetString(bytes, 0, 7).StartsWith("version"),
                        "skiers.bytes is a Git LFS pointer (git lfs checkout)");
            return SkierFigureData.Read(bytes);
        }

        [Test]
        public void ShippedFiguresSkinLikeBlender()
        {
            var data = Shipped();
            int n = SkierReference.Check(data, File.ReadAllText(SkierImport.Summary));
            Assert.That(n, Is.GreaterThanOrEqualTo(12));
            Assert.That(data.Kinds.Length, Is.EqualTo(2));
            Assert.That(data.UntilMetres.Length, Is.EqualTo(SkierFigureJob.Lods));
            foreach (var kind in data.Kinds)
                for (int l = 1; l < SkierFigureJob.Lods; l++)
                    Assert.That(kind.VertexCount(l), Is.LessThan(kind.VertexCount(l - 1)), $"{kind.Name} LOD{l} lighter than LOD{l - 1}");
        }

        [Test]
        public void EveryClipSlotResolves()
        {
            var data = Shipped();
            var shader = Resources.Load<Shader>(SkierFigures.ShaderResource);
            Assert.That(shader, Is.Not.Null, "the figure shader");
            using var figures = new SkierFigures(data, shader, 8);
            for (int k = 0; k < SkierFigureJob.Kinds; k++)
                for (int s = 0; s < SkierFigureJob.Slots; s++)
                {
                    var r = figures.ClipRange(k, s);
                    Assert.That(r.y, Is.GreaterThan(0), $"kind {k} slot {s}");
                    Assert.That(r.x + r.y, Is.LessThanOrEqualTo(data.Kinds[k].Frames));
                }
        }

        [Test]
        public void FramesWrapAndBlend()
        {
            SkierFigureJob.Frames(new int2(100, 32), 0.99f, out uint a, out uint b, out float t);
            Assert.That(a, Is.EqualTo(131u));
            Assert.That(b, Is.EqualTo(100u), "the last frame blends into the first");
            Assert.That(t, Is.InRange(0.6f, 0.75f));
            SkierFigureJob.Frames(new int2(7, 1), 0.4f, out a, out b, out t);
            Assert.That((a, b, t), Is.EqualTo((7u, 7u, 0f)), "a still pose");
            SkierFigureJob.Frames(new int2(0, 16), 2.25f, out a, out _, out _);
            Assert.That(a, Is.EqualTo(4u), "phase past 1 wraps");
        }

        [Test]
        public void OneGuestInFourRidesABoard()
        {
            int boards = 0;
            for (uint s = 1; s <= 20000; s++) if (SkierPlaceJob.IsBoarder(s)) boards++;
            Assert.That(boards / 20000f, Is.InRange(0.23f, 0.27f));
        }

        // Owner demo finding (2026-11-13): skiers in a line faced sideways (at the neighbour in their row), and the line
        // stood on the terminal's centreline instead of under the loaded rope's chairs.
        [TestCase(2)] [TestCase(4)] [TestCase(6)]
        public void ALineFacesItsFrontAndStandsUnderTheLoadedRope(int seats)
        {
            var lift = new LineLayout.LiftInput
            {
                Bottom = Vector2.zero, Top = new Vector2(0, 800), Seats = seats, Built = true, LoadSide = 2.5f,
                Behind = 4, Ahead = 6, HalfWidth = 4, TopBehind = 4, TopAhead = 6, TopHalfWidth = 4, LaneHalfWidth = 5.3f,
            };
            using var layout = LineLayout.Build(1, new[] { lift }, null, (x, z) => 0f, null);
            var s = layout.LiftSlots[0];
            Assert.That(layout.Facing.Length, Is.EqualTo(layout.Slots.Length));
            // The front rows: every skier faces up the lift (+z), and the row is centred 2.5 m right (+x) of the centreline.
            float sum = 0;
            for (int c = 0; c < seats; c++)
            {
                var f = layout.Facing[s.x + c];
                Assert.That(f.y, Is.EqualTo(1f).Within(1e-4f), $"column {c} faces the lift");
                sum += layout.Slots[s.x + c].x;
            }
            Assert.That(sum / seats, Is.EqualTo(2.5f).Within(1e-3f), "the front row stands under the loaded rope");
            // Every place faces somewhere flat and unit, and a row's skiers all face the same way.
            for (int k = 0; k < s.y; k++) Assert.That(math.length(layout.Facing[s.x + k]), Is.EqualTo(1f).Within(1e-3f), $"place {k}");
            for (int r = 0; r + seats <= s.y && r < 10 * seats; r += seats)
                for (int c = 1; c < seats; c++)
                    Assert.That(math.dot(layout.Facing[s.x + r + c], layout.Facing[s.x + r]), Is.GreaterThan(0.999f), $"row {r / seats} column {c}");
        }
    }
}
