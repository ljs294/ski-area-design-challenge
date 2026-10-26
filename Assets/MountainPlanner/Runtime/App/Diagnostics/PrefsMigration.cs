using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MountainPlanner.App.Diagnostics
{
    /// <summary>
    /// The company became Alpine Labs (owner, 2026-10-12; task P2-10), and Unity keeps a player's prefs under
    /// HKCU\Software\&lt;company&gt;\&lt;product&gt;: the library folder, offline mode, theme, scale, units, key bindings,
    /// graphics and window mode. On the first start under the new name, every value the old key holds and the new key
    /// doesn't is copied as it is (same type, same bytes), and a marker keeps it from running again. The old key stays,
    /// so an older build still finds its settings. The data root (%LOCALAPPDATA%\SkiAreaDesignChallenge) never moved.
    /// Unity has already chosen the window by then, so the copied window mode is applied once the scene can.
    /// </summary>
    public static class PrefsMigration
    {
        public const string OldCompany = "Ski Area Design Challenge";
        /// <summary>A plain value name (Unity's own names end in _h&lt;hash&gt;, so PlayerPrefs never sees it): where it came from.</summary>
        public const string Marker = "MountainPlanner.PrefsCopiedFrom";

        /// <summary>True when this session copied the old prefs.</summary>
        public static bool Migrated { get; private set; }

        public static string KeyPath(string company, string product) => $@"Software\{company}\{product}";

        /// <summary>At boot: copies the old company's prefs once, when the company name changed.</summary>
        public static void Run()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return;
            if (Application.companyName == OldCompany) return;
            try
            {
                int copied = Copy(KeyPath(OldCompany, Application.productName), KeyPath(Application.companyName, Application.productName));
                Migrated = copied > 0;
                if (copied >= 0) Debug.Log($"[PrefsMigration] Copied {copied} settings from {OldCompany} to {Application.companyName}");
            }
            catch (Exception e) { Debug.LogWarning($"[PrefsMigration] Settings stay at their defaults: {e.Message}"); }
        }

        /// <summary>
        /// Copies every value of HKCU\<paramref name="from"/> that HKCU\<paramref name="to"/> lacks, then marks
        /// <paramref name="to"/>. The count copied; -1 when there was nothing to do (no old key, or already marked).
        /// </summary>
        public static int Copy(string from, string to)
        {
            if (!Open(from, false, out IntPtr source)) return -1;
            try
            {
                if (Open(to, false, out IntPtr existing))
                {
                    bool marked = Names(existing).Contains(Marker);
                    RegCloseKey(existing);
                    if (marked) return -1;
                }
                if (!Open(to, true, out IntPtr target)) throw new InvalidOperationException($"can't create HKCU\\{to}");
                try
                {
                    var present = new HashSet<string>(Names(target), StringComparer.OrdinalIgnoreCase);
                    int copied = 0;
                    foreach (string name in Names(source))
                    {
                        if (present.Contains(name) || name == Marker) continue;
                        if (!Read(source, name, out int type, out byte[] data)) continue;
                        Check(RegSetValueEx(target, name, 0, type, data, data.Length), "write " + name);
                        copied++;
                    }
                    byte[] marker = Encoding.Unicode.GetBytes(from + "\0");
                    Check(RegSetValueEx(target, Marker, 0, RegSz, marker, marker.Length), "mark");
                    return copied;
                }
                finally { RegCloseKey(target); }
            }
            finally { RegCloseKey(source); }
        }

        /// <summary>Before the first scene, after a copy: the window mode and size the player had chosen.</summary>
        public static void ApplyWindowMode()
        {
            if (!Migrated) return;
            const string Mode = "Screenmanager Fullscreen mode", Width = "Screenmanager Resolution Width", Height = "Screenmanager Resolution Height";
            if (!PlayerPrefs.HasKey(Mode)) return;
            var mode = (FullScreenMode)PlayerPrefs.GetInt(Mode, (int)Screen.fullScreenMode);
            int width = PlayerPrefs.GetInt(Width, Screen.width), height = PlayerPrefs.GetInt(Height, Screen.height);
            if (mode == Screen.fullScreenMode && width == Screen.width && height == Screen.height) return;
            Debug.Log($"[PrefsMigration] Window: {width}x{height} {mode}");
            Screen.SetResolution(width, height, mode);
        }

        /// <summary>Writes one raw value under HKCU\<paramref name="path"/> (tests).</summary>
        public static void WriteValue(string path, string name, int type, byte[] data)
        {
            if (!Open(path, true, out IntPtr key)) throw new InvalidOperationException($"can't create HKCU\\{path}");
            try { Check(RegSetValueEx(key, name, 0, type, data, data.Length), "write " + name); }
            finally { RegCloseKey(key); }
        }

        /// <summary>Reads one raw value under HKCU\<paramref name="path"/>; false when it isn't there (tests).</summary>
        public static bool ReadValue(string path, string name, out int type, out byte[] data)
        {
            type = 0;
            data = Array.Empty<byte>();
            if (!Open(path, false, out IntPtr key)) return false;
            try { return Read(key, name, out type, out data); }
            finally { RegCloseKey(key); }
        }

        /// <summary>Every value name under HKCU\<paramref name="path"/> with its registry type (problem reports, task P3-14).</summary>
        public static List<(string name, int type)> ListValues(string path)
        {
            var values = new List<(string, int)>();
            if (!Open(path, false, out IntPtr key)) return values;
            try
            {
                foreach (string name in Names(key))
                    if (Read(key, name, out int type, out _)) values.Add((name, type));
            }
            finally { RegCloseKey(key); }
            return values;
        }

        /// <summary>Removes HKCU\<paramref name="path"/> and everything under it (tests).</summary>
        public static void DeleteTree(string path) => RegDeleteTree(CurrentUser, path);

        // ---- advapi32: Unity's API level has no Microsoft.Win32.Registry ----

        static readonly IntPtr CurrentUser = new IntPtr(unchecked((int)0x80000001));
        const int RegSz = 1, KeyRead = 0x20019, KeyAll = 0xF003F, MoreData = 234, NoMoreItems = 259;

        static bool Open(string path, bool create, out IntPtr key)
        {
            int result = create
                ? RegCreateKeyEx(CurrentUser, path, 0, null, 0, KeyAll, IntPtr.Zero, out key, out _)
                : RegOpenKeyEx(CurrentUser, path, 0, KeyRead, out key);
            return result == 0;
        }

        static List<string> Names(IntPtr key)
        {
            var names = new List<string>();
            var name = new StringBuilder(16384);
            for (int i = 0; ; i++)
            {
                int length = name.Capacity;
                int result = RegEnumValue(key, i, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (result == NoMoreItems) break;
                Check(result, "list");
                names.Add(name.ToString(0, length));
            }
            return names;
        }

        static bool Read(IntPtr key, string name, out int type, out byte[] data)
        {
            int size = 0;
            data = Array.Empty<byte>();
            int result = RegQueryValueEx(key, name, IntPtr.Zero, out type, null, ref size);
            if (result != 0 && result != MoreData) return false;
            data = new byte[size];
            return RegQueryValueEx(key, name, IntPtr.Zero, out type, data, ref size) == 0;
        }

        static void Check(int result, string what)
        {
            if (result != 0) throw new InvalidOperationException($"registry {what} failed ({result})");
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(IntPtr key, string subKey, int options, int access, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegCreateKeyEx(IntPtr key, string subKey, int reserved, string className, int options, int access,
                                         IntPtr security, out IntPtr result, out int disposition);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegEnumValue(IntPtr key, int index, StringBuilder name, ref int nameLength, IntPtr reserved,
                                       IntPtr type, IntPtr data, IntPtr dataLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueEx(IntPtr key, string name, IntPtr reserved, out int type, byte[] data, ref int dataLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegSetValueEx(IntPtr key, string name, int reserved, int type, byte[] data, int dataLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegDeleteTree(IntPtr key, string subKey);

        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(IntPtr key);
    }
}
