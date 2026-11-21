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

        // Add up to two more themes here. The menu can pick one or choose at random.
        public static readonly RoomTheme[] All = { Main };

        public static RoomTheme Random() => All[UnityEngine.Random.Range(0, All.Length)];

        static Color Hex(string value)
        {
            ColorUtility.TryParseHtmlString(value, out var color);
            return color;
        }
    }
}
