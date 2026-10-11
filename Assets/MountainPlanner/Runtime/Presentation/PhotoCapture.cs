using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Photo mode's save (S10, G3; task P2-07): the scene alone, at the screen's resolution or twice it, as a PNG.
    ///
    /// The camera renders once more into its own texture (URP's render request), so the UI is never in the photo and
    /// nothing on screen has to hide. The pixels come back with an asynchronous GPU readback straight into a native
    /// buffer, and the PNG is encoded and written on a worker thread: the main thread pays only that one extra render
    /// (four times the pixels at 2×), never a stall on the GPU, the encode or the disk.
    /// </summary>
    public static class PhotoCapture
    {
        /// <summary>The largest side a photo can have (the GPU's texture limit); a bigger request is scaled down to fit.</summary>
        public static int MaxSide => Mathf.Min(SystemInfo.maxTextureSize, 16384);

        /// <summary>The photo's size for this camera at <paramref name="scale"/> (1 or 2), kept under <see cref="MaxSide"/>.</summary>
        public static Vector2Int Size(int width, int height, int scale)
        {
            float w = width * (float)scale, h = height * (float)scale;
            float fit = Mathf.Min(1f, MaxSide / Mathf.Max(w, h));
            return new Vector2Int(Mathf.Max(1, Mathf.FloorToInt(w * fit)), Mathf.Max(1, Mathf.FloorToInt(h * fit)));
        }

        /// <summary>
        /// Renders <paramref name="camera"/> at <paramref name="scale"/> × its pixel size and saves a PNG to
        /// <paramref name="path"/>. Call on the main thread; the task completes once the file is written (or throws why
        /// it couldn't be).
        /// </summary>
        public static Task<Vector2Int> Save(Camera camera, int scale, string path)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            var size = Size(camera.pixelWidth, camera.pixelHeight, Mathf.Clamp(scale, 1, 2));
            var done = new TaskCompletionSource<Vector2Int>();
            var desc = new RenderTextureDescriptor(size.x, size.y, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.D32_SFloat_S8_UInt) { msaaSamples = 1 };
            var target = RenderTexture.GetTemporary(desc);
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderTexture.ReleaseTemporary(target);
                done.SetException(new NotSupportedException("This render pipeline can't render a photo."));
                return done.Task;
            }
            RenderPipeline.SubmitRenderRequest(camera, request);

            var pixels = new NativeArray<byte>(size.x * size.y * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            AsyncGPUReadback.RequestIntoNativeArray(ref pixels, target, 0, TextureFormat.RGBA32, readback =>
            {
                RenderTexture.ReleaseTemporary(target);
                if (readback.hasError)
                {
                    pixels.Dispose();
                    done.SetException(new IOException("The picture couldn't be read back from the GPU."));
                    return;
                }
                // Encode and write off the main thread; the buffer is ours until then.
                Task.Run(() =>
                {
                    try
                    {
                        Opaque(pixels);
                        using (var png = ImageConversion.EncodeNativeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)size.x, (uint)size.y))
                        {
                            string folder = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                            {
                                var bytes = png.AsReadOnlySpan();
                                file.Write(bytes);
                            }
                        }
                        done.SetResult(size);
                    }
                    catch (Exception e) { done.SetException(e); }
                    finally { pixels.Dispose(); }
                });
            });
            return done.Task;
        }

        /// <summary>A photo has no see-through pixels, whatever the renderer left in alpha.</summary>
        static void Opaque(NativeArray<byte> rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        }

        /// <summary>
        /// "Jackson Hole 2026-01-15 15-30-00.png" in <paramref name="folder"/>: the area, then the date and time the
        /// photo shows (the view's own clock), with anything Windows can't put in a name left out; " (2)" and on when that
        /// name is taken.
        /// </summary>
        public static string FileName(string folder, string area, int year, int month, int day, int secondOfDay)
        {
            string stem = $"{area} {year:D4}-{month:D2}-{day:D2} {secondOfDay / 3600:D2}-{secondOfDay / 60 % 60:D2}-{secondOfDay % 60:D2}";
            stem = string.Concat(stem.Split(Path.GetInvalidFileNameChars())).Trim();
            if (stem.Length == 0) stem = "Photo";
            string path = Path.Combine(folder, stem + ".png");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}).png");
            return path;
        }
    }
}
