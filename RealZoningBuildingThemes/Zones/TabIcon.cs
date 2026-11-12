using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Colossal.PSI.Environment;
using Colossal.UI;
using UnityEngine;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// The "Real Zoning" toolbar tab icon: the game's own tab icon it is based on (the office tab,
    /// a purple block), recoloured light red. The recolouring runs at startup on the player's own
    /// game files, so the mod ships no game artwork. Each colour in the icon keeps its place in
    /// the shading: the hue becomes red, saturation is softened and the shade is lifted, so the
    /// faces stay distinguishable. The result is served to the game's UI from ModsData through a
    /// "rzbt" host. If anything fails, the tab keeps the borrowed icon.
    /// </summary>
    public static class TabIcon
    {
        public const string Host = "rzbt";
        private const string FileName = "TabIcon.svg";
        private static readonly Regex s_Hex = new Regex("#([0-9a-fA-F]{6})\\b", RegexOptions.Compiled);

        public static string Folder => Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(RealZoningBuildingThemes), "ui");

        /// <summary>Writes the recoloured icon and returns its UI path, or null to keep <paramref name="gameIcon"/>.</summary>
        public static string Create(string gameIcon)
        {
            if (string.IsNullOrEmpty(gameIcon))
            {
                return null;
            }

            try
            {
                string relative = Regex.Replace(gameIcon, "^coui://[^/]+/", string.Empty);
                string source = Path.Combine(EnvPath.kContentPath, "Game", "UI", relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    Mod.log.Warn($"TabIcon: '{source}' not found; the tab keeps the game's icon.");
                    return null;
                }

                string svg = s_Hex.Replace(File.ReadAllText(source), m => "#" + LightRed(m.Groups[1].Value));
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, FileName), svg);
                UIManager.defaultUISystem.AddHostLocation(Host, Folder, false);
                Mod.log.Info($"TabIcon: made a light red copy of '{relative}'.");
                return $"coui://{Host}/{FileName}";
            }
            catch (Exception e)
            {
                Mod.log.Warn($"TabIcon: could not make the tab icon ({e.Message}); the tab keeps the game's icon.");
                return null;
            }
        }

        private static string LightRed(string hex)
        {
            var c = new Color32(
                byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber),
                255);
            Color.RGBToHSV(c, out _, out float s, out float v);
            Color red = Color.HSVToRGB(0f, s * 0.6f, v + (1f - v) * 0.3f);
            return ColorUtility.ToHtmlStringRGB(red).ToLowerInvariant();
        }
    }
}
