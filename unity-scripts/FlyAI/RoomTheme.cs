using UnityEngine;

namespace FlyAI
{
    // One visual style. Colours are applied by what each scanned part is labelled.
    public class RoomTheme
    {
        public string name;
        public Color wall, floor, ceiling, trim, table, seat, soft, storage, appliance, other;
        public Color fog, ambient;
        public float visibleChairLengths;
        public float fogFadeDistance;
        public float hazeOpacity;
        public float lightDim;
        public float ambientDim;
        public float lightScale;
        public float exposure;
        public float contrast;
        public float saturation;
        public float bloom;
        public float vignette;

        // Optional asset overrides. Files in FlyAIProps/<folder>/, FlyAITextures/<folder>/ and
        // FlyAISounds/<folder>/ replace the house asset of the same name. Null means house assets only.
        public string folder;

        // Optional. Each room lamp's scanned colour is blended toward lightTint by lightTintAmount.
        // Null leaves lamp colours exactly as scanned.
        public Color? lightTint;
        public float lightTintAmount;

        // Optional colour for the light that sweeps through the window (car headlights in the house).
        // Null keeps the house colour.
        public Color? headlightColor;
    }

    public static class RoomThemes
    {
        public static readonly RoomTheme Main = new RoomTheme
        {
            name = "Old House",
            wall = Hex("#7d8272"),
            floor = Hex("#5a4331"),
            ceiling = Hex("#b5ae9d"),
            trim = Hex("#b8b09b"),
            table = Hex("#6d4a30"),
            seat = Hex("#7a5a3e"),
            soft = Hex("#5f6675"),
            storage = Hex("#58483a"),
            appliance = Hex("#92959a"),
            other = Hex("#7b7164"),
            fog = Hex("#07080b"),
            ambient = Hex("#50525a"),
            visibleChairLengths = 3f,
            fogFadeDistance = 3.6f,
            hazeOpacity = 0.13f,
            lightDim = 0.62f,
            ambientDim = 0.68f,
            lightScale = 1f,
            exposure = 0.1f,
            contrast = 12f,
            saturation = -4f,
            bloom = 0.55f,
            vignette = 0.3f,
        };

        // Stone chamber lit by torches: warm orange light, stone greys, dark wood, deep shadows.
        public static readonly RoomTheme Medieval = new RoomTheme
        {
            name = "Medieval",
            folder = "Medieval",
            wall = Hex("#6e6a63"),
            floor = Hex("#4f4438"),
            ceiling = Hex("#4a3b2e"),
            trim = Hex("#4d3826"),
            table = Hex("#4e3320"),
            seat = Hex("#5a3d25"),
            soft = Hex("#6b3a2c"),
            storage = Hex("#3f2c1e"),
            appliance = Hex("#5e5b55"),
            other = Hex("#6f604c"),
            fog = Hex("#0b0704"),
            ambient = Hex("#5c4634"),
            visibleChairLengths = 2.8f,
            fogFadeDistance = 3.4f,
            hazeOpacity = 0.16f,
            lightDim = 0.6f,
            ambientDim = 0.6f,
            lightScale = 1f,
            exposure = 0.05f,
            contrast = 18f,
            saturation = 6f,
            bloom = 0.75f,
            vignette = 0.38f,
            lightTint = Hex("#ff9442"),
            lightTintAmount = 0.6f,
            headlightColor = Hex("#ffae5c"),
        };

        // Space-station apartment: cool cyan-white light, gunmetal and white panels, neon glow.
        public static readonly RoomTheme Futuristic = new RoomTheme
        {
            name = "Futuristic",
            folder = "Futuristic",
            wall = Hex("#89919b"),
            floor = Hex("#3b4048"),
            ceiling = Hex("#c6ccd4"),
            trim = Hex("#d6dce4"),
            table = Hex("#98a0aa"),
            seat = Hex("#4a5060"),
            soft = Hex("#3a4a5e"),
            storage = Hex("#5b626c"),
            appliance = Hex("#b2bac4"),
            other = Hex("#6e7783"),
            fog = Hex("#04070b"),
            ambient = Hex("#4a5a6e"),
            visibleChairLengths = 3.4f,
            fogFadeDistance = 3.8f,
            hazeOpacity = 0.09f,
            lightDim = 0.66f,
            ambientDim = 0.7f,
            lightScale = 1f,
            exposure = 0.12f,
            contrast = 20f,
            saturation = -10f,
            bloom = 0.9f,
            vignette = 0.26f,
            lightTint = Hex("#bdeaff"),
            lightTintAmount = 0.7f,
            headlightColor = Hex("#62e0ff"),
        };

        // The theme menu lists these in order. Main must stay first: it is the default.
        public static readonly RoomTheme[] All = { Main, Medieval, Futuristic };

        public static RoomTheme Random() => All[UnityEngine.Random.Range(0, All.Length)];

        // Unknown or empty names fall back to the house.
        public static RoomTheme ByName(string themeName)
        {
            foreach (var theme in All)
                if (theme.name == themeName) return theme;
            return Main;
        }

        static Color Hex(string value)
        {
            ColorUtility.TryParseHtmlString(value, out var color);
            return color;
        }
    }
}
