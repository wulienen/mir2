using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Client.Utils
{
    internal static class FontManager
    {
        public const string HarmonyOSFontName = "HarmonyOS Sans SC Medium";

        private const uint FontResourcePrivate = 0x10;
        private static bool _initialized;
        private static readonly PrivateFontCollection PrivateFonts = new();
        private static FontFamily _harmonyOSFamily;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            string fontPath = Path.Combine(Application.StartupPath, "Data", "Fonts", "HarmonyOS_Sans_SC_Medium.ttf");
            if (!File.Exists(fontPath))
                return;

            try
            {
                PrivateFonts.AddFontFile(fontPath);
                _harmonyOSFamily = PrivateFonts.Families.FirstOrDefault(family =>
                    string.Equals(family.Name, HarmonyOSFontName, StringComparison.OrdinalIgnoreCase));
                AddFontResourceEx(fontPath, FontResourcePrivate, IntPtr.Zero);
            }
            catch
            {
                // The normal installed-font fallback is handled by ResolveFontName.
            }
        }

        public static string ResolveFontName(string configuredFontName)
        {
            string[] candidates =
            {
                configuredFontName,
                HarmonyOSFontName,
                "Microsoft YaHei UI",
                "SimSun"
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                if (string.Equals(candidate, HarmonyOSFontName, StringComparison.OrdinalIgnoreCase) &&
                    _harmonyOSFamily != null)
                    return HarmonyOSFontName;

                try
                {
                    using FontFamily testFamily = new FontFamily(candidate);
                    if (string.Equals(testFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch
                {
                    // Continue to the next fallback font.
                }
            }

            return FontFamily.GenericSansSerif.Name;
        }

        public static FontFamily ResolveFontFamily(string fontName)
        {
            if (string.Equals(fontName, HarmonyOSFontName, StringComparison.OrdinalIgnoreCase) &&
                _harmonyOSFamily != null)
                return _harmonyOSFamily;

            try
            {
                return new FontFamily(fontName);
            }
            catch
            {
                return FontFamily.GenericSansSerif;
            }
        }
    }
}
