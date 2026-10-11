using System;
using System.Collections;
using System.IO;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-07 acceptance: a photo saves as a PNG at the screen's size or twice it, off the main thread (the frames
    /// keep coming while it encodes and writes), and depth of field draws without errors.
    /// </summary>
    public sealed class PhotoPlayTests
    {
        GameObject _camera, _scene;
        string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "mp-photo-play-" + Guid.NewGuid().ToString("N"));
            _camera = new GameObject("Photo camera", typeof(Camera));
            var cam = _camera.GetComponent<Camera>();
            cam.transform.position = new Vector3(0, 2, -6);
            cam.transform.LookAt(Vector3.zero);
            cam.farClipPlane = 200;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            _scene = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var far = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            far.transform.SetParent(_scene.transform);
            far.transform.position = new Vector3(3, 1, 40);
            far.transform.localScale = Vector3.one * 8;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.Destroy(_camera);
            UnityEngine.Object.Destroy(_scene);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        /// <summary>A PNG's width and height, from its IHDR chunk.</summary>
        static Vector2Int PngSize(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            Assert.That(b.Length, Is.GreaterThan(24));
            Assert.That(b[1] == 'P' && b[2] == 'N' && b[3] == 'G', "a PNG");
            int Be(int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
            return new Vector2Int(Be(16), Be(20));
        }

        [UnityTest]
        public IEnumerator SavesAtTheScreensSizeAndTwiceIt([Values(1, 2)] int scale)
        {
            yield return null;
            var cam = _camera.GetComponent<Camera>();
            string path = Path.Combine(_folder, $"photo-{scale}.png");
            var saving = PhotoCapture.Save(cam, scale, path);
            int frames = 0;
            float started = Time.realtimeSinceStartup;
            while (!saving.IsCompleted && Time.realtimeSinceStartup - started < 20)
            {
                yield return null;
                frames++;
            }
            Assert.That(saving.IsCompleted, "saved within 20 s");
            if (saving.IsFaulted) throw saving.Exception.GetBaseException();
            Assert.That(frames, Is.GreaterThan(0), "the readback and encode don't hold the frame");
            var want = PhotoCapture.Size(cam.pixelWidth, cam.pixelHeight, scale);
            Assert.That(saving.Result, Is.EqualTo(want));
            Assert.That(PngSize(path), Is.EqualTo(want));
        }

        [UnityTest]
        public IEnumerator FocusPresetsDrawWithoutErrors()
        {
            var cam = _camera.GetComponent<Camera>();
            using (var focus = new PhotoFocus(cam))
            {
                Assert.That(focus.Available, "the focus shader is supported here");
                focus.FocusDistance = 6;
                foreach (var mode in new[] { PhotoFocusMode.Natural, PhotoFocusMode.Miniature, PhotoFocusMode.Off })
                {
                    focus.SetMode(mode);
                    for (int i = 0; i < 3; i++) yield return null;
                    Assert.That(focus.Mode, Is.EqualTo(mode));
                }
                // And into a 2× photo, which renders through the same pass.
                focus.SetMode(PhotoFocusMode.Miniature);
                var saving = PhotoCapture.Save(cam, 2, Path.Combine(_folder, "focus.png"));
                float started = Time.realtimeSinceStartup;
                while (!saving.IsCompleted && Time.realtimeSinceStartup - started < 20) yield return null;
                Assert.That(saving.IsCompletedSuccessfully, saving.Exception?.GetBaseException().Message);
            }
            LogAssert.NoUnexpectedReceived();
        }
    }
}
