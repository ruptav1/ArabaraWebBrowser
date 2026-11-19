using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Arabara
{
    internal static class UiFonts
    {
        private const uint Private = 0x10;
        private static readonly PrivateFontCollection collection = new PrivateFontCollection();
        private static readonly List<string> registered = new List<string>();
        private static FontFamily bodyFamily, displayFamily;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int AddFontResourceEx(string path, uint flags, IntPtr reserved);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool RemoveFontResourceEx(string path, uint flags, IntPtr reserved);

        public static string BodyName { get { return bodyFamily == null ? "Consolas" : bodyFamily.Name; } }
        public static string DisplayName { get { return displayFamily == null ? "Consolas" : displayFamily.Name; } }

        public static void Initialize()
        {
            Load("WhiteRabbit.ttf");
            Load("Skynet College.ttf");
            bodyFamily = collection.Families.FirstOrDefault(f => f.Name == "White Rabbit");
            displayFamily = collection.Families.FirstOrDefault(f => f.Name == "Skynet College");
        }

        private static void Load(string name)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts", name);
            if (!File.Exists(path)) return;
            // Register with GDI for native controls and GDI+ for the painted start page.
            // FR_PRIVATE keeps the font inside this process; nothing is installed globally.
            if (AddFontResourceEx(path, Private, IntPtr.Zero) == 0) return;
            registered.Add(path);
            collection.AddFontFile(path);
        }

        public static Font Body(float size)
        { return bodyFamily == null ? new Font("Consolas", size) : new Font(bodyFamily, size, FontStyle.Regular); }
        public static Font Display(float size)
        { return displayFamily == null ? new Font("Consolas", size, FontStyle.Bold) : new Font(displayFamily, size, FontStyle.Regular); }

        // Wide geometric wordmark face from Windows (10 1709+); nothing is downloaded.
        public static Font Wordmark(float size)
        {
            var font = new Font("Bahnschrift SemiBold", size, FontStyle.Regular);
            if (font.Name == "Bahnschrift SemiBold") return font;
            font.Dispose();
            return new Font("Segoe UI Semibold", size, FontStyle.Regular);
        }

        public static void Shutdown()
        {
            collection.Dispose();
            foreach (string path in registered) RemoveFontResourceEx(path, Private, IntPtr.Zero);
            registered.Clear();
        }
    }
}
