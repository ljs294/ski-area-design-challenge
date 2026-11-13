using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MountainPlanner.Presentation.Skiers
{
    /// <summary>
    /// Task 4.22: the skier and snowboarder figures as the Blender pipeline bakes them (tools/assets/skiers/skierkit/bake.py,
    /// <c>skiers.bytes</c>): every LOD as a skinned triangle soup, every clip as bone matrices, and the keyed outfits. Plain
    /// data, read once; <see cref="SkierFigures"/> turns it into meshes and GPU buffers. Unity axes, metres: x the
    /// figure's right, y up, z forward, the origin on the snow between the boots (on the seat for a seated pose).
    /// </summary>
    public sealed class SkierFigureData
    {
        public const uint Version = 1;
        /// <summary>Bytes per vertex in the file: position (3 × f32), 4 bone indices, 4 weights (sum 255), material id, tone, 2 pad.</summary>
        public const int VertexBytes = 24;
        public const int MaterialIds = 16;

        public sealed class Clip
        {
            public string Name;
            public int Frames;
            public bool Loops;
            public int Pitch;
            /// <summary>The clip's first frame in the kind's frame list (<see cref="Kind.Matrices"/>).</summary>
            public int First;
        }

        public sealed class Kind
        {
            public string Name;
            public int Bones;
            /// <summary>Per LOD: the raw vertices, <see cref="VertexBytes"/> each, three to a face.</summary>
            public byte[][] Lods;
            public Clip[] Clips;
            public int Frames;
            /// <summary>Per frame, per bone: three rows (r0 r1 r2 t), so 12 floats per bone.</summary>
            public float[] Matrices;
            public int Outfits;
            /// <summary>Per outfit, per material id: sRGB packed r | g &lt;&lt; 8 | b &lt;&lt; 16.</summary>
            public uint[] Colours;

            public int VertexCount(int lod) => Lods[lod].Length / VertexBytes;

            public int ClipIndex(string name)
            {
                for (int i = 0; i < Clips.Length; i++) if (Clips[i].Name == name) return i;
                return -1;
            }
        }

        public float[] UntilMetres;
        public float Hysteresis;
        public Kind[] Kinds;

        public Kind Find(string name)
        {
            foreach (var k in Kinds) if (k.Name == name) return k;
            return null;
        }

        public static SkierFigureData Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 16) throw new InvalidDataException("skiers.bytes is empty");
            using var r = new BinaryReader(new MemoryStream(bytes), Encoding.ASCII);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "MPSK") throw new InvalidDataException("not a skier figure file");
            uint version = r.ReadUInt32();
            if (version != Version) throw new InvalidDataException($"skier figure file version {version}, expected {Version}");
            int kinds = (int)r.ReadUInt32();
            var data = new SkierFigureData();
            int lodCount = (int)r.ReadUInt32();
            data.UntilMetres = new float[lodCount];
            for (int i = 0; i < lodCount; i++) data.UntilMetres[i] = r.ReadSingle();
            data.Hysteresis = r.ReadSingle();
            data.Kinds = new Kind[kinds];
            for (int k = 0; k < kinds; k++)
            {
                var kind = new Kind { Name = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte())) };
                kind.Bones = (int)r.ReadUInt32();
                int lods = (int)r.ReadUInt32();
                int clips = (int)r.ReadUInt32();
                kind.Outfits = (int)r.ReadUInt32();
                if (lods != lodCount) throw new InvalidDataException($"{kind.Name}: {lods} LODs, the header says {lodCount}");
                kind.Lods = new byte[lods][];
                for (int l = 0; l < lods; l++)
                {
                    int n = (int)r.ReadUInt32();
                    if (n % 3 != 0) throw new InvalidDataException($"{kind.Name} LOD{l}: {n} vertices is not whole triangles");
                    kind.Lods[l] = r.ReadBytes(n * VertexBytes);
                }
                kind.Clips = new Clip[clips];
                int first = 0;
                for (int c = 0; c < clips; c++)
                {
                    var clip = new Clip { Name = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte())) };
                    clip.Frames = (int)r.ReadUInt32();
                    clip.Loops = r.ReadByte() != 0;
                    clip.Pitch = r.ReadByte();
                    r.ReadBytes(2);
                    clip.First = first;
                    first += clip.Frames;
                    kind.Clips[c] = clip;
                }
                kind.Frames = first;
                kind.Matrices = new float[first * kind.Bones * 12];
                for (int i = 0; i < kind.Matrices.Length; i++) kind.Matrices[i] = r.ReadSingle();
                kind.Colours = new uint[kind.Outfits * MaterialIds];
                for (int i = 0; i < kind.Colours.Length; i++) kind.Colours[i] = r.ReadUInt32();
                data.Kinds[k] = kind;
            }
            if (r.BaseStream.Position != bytes.Length)
                throw new InvalidDataException($"skiers.bytes has {bytes.Length - r.BaseStream.Position} bytes left over");
            return data;
        }

        /// <summary>One vertex skinned on the CPU the way the shader skins it (the import test and the reference points).</summary>
        public static void Skin(Kind kind, int lod, int vertex, int frame, out float x, out float y, out float z)
        {
            var v = kind.Lods[lod];
            int o = vertex * VertexBytes;
            float px = BitConverter.ToSingle(v, o), py = BitConverter.ToSingle(v, o + 4), pz = BitConverter.ToSingle(v, o + 8);
            x = y = z = 0;
            for (int i = 0; i < 4; i++)
            {
                int bone = v[o + 12 + i];
                float w = v[o + 16 + i] / 255f;
                if (w <= 0) continue;
                int m = (frame * kind.Bones + bone) * 12;
                var a = kind.Matrices;
                x += (a[m] * px + a[m + 1] * py + a[m + 2] * pz + a[m + 3]) * w;
                y += (a[m + 4] * px + a[m + 5] * py + a[m + 6] * pz + a[m + 7]) * w;
                z += (a[m + 8] * px + a[m + 9] * py + a[m + 10] * pz + a[m + 11]) * w;
            }
        }

        /// <summary>The names of every clip of a kind (logs).</summary>
        public static IEnumerable<string> ClipNames(Kind kind)
        {
            foreach (var c in kind.Clips) yield return c.Name;
        }
    }
}
