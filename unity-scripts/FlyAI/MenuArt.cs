using System;
using UnityEngine;

namespace FlyAI
{
    // The main menu's pictures, painted in code (no image files): a bright hallway with a rough person chasing a
    // fly with a swatter, and the fly icon used in the logo. Everything is drawn once with simple shapes.
    // Positions are in a 1600 x 900 picture; the menu scales it to cover the screen.
    public static class MenuArt
    {
        public const float W = 1600f;
        public const float H = 900f;

        public static readonly Rect ArmRect = new Rect(640f, 120f, 640f, 360f);
        public static readonly Vector2 Shoulder = new Vector2(1215f, 315f);

        public static Texture2D Hall { get; private set; }
        public static Texture2D ArmAndSwatter { get; private set; }
        public static Texture2D FlyWingsUp { get; private set; }
        public static Texture2D FlyWingsDown { get; private set; }
        public static bool Ready => Hall != null;

        public static void Build()
        {
            if (Ready) return;
            Hall = PaintHall();
            ArmAndSwatter = PaintArm();
            FlyWingsUp = PaintFly(true);
            FlyWingsDown = PaintFly(false);
        }

        public static void Release()
        {
            foreach (var texture in new[] { Hall, ArmAndSwatter, FlyWingsUp, FlyWingsDown })
                if (texture != null) UnityEngine.Object.Destroy(texture);
            Hall = ArmAndSwatter = FlyWingsUp = FlyWingsDown = null;
        }

        // --- A tiny software painter -------------------------------------------------------------------------

        internal sealed class Shape
        {
            public Rect bounds;
            public Func<float, float, float> distance; // Negative inside, in picture units.
        }

        internal sealed class Canvas
        {
            readonly int width;
            readonly int height;
            readonly float scale;
            readonly Vector2 origin;
            readonly Color[] pixels;

            public Canvas(Rect area, float scale)
            {
                this.scale = scale;
                origin = area.position;
                width = Mathf.CeilToInt(area.width * scale);
                height = Mathf.CeilToInt(area.height * scale);
                pixels = new Color[width * height];
            }

            void Blend(int x, int y, Color color, float alpha)
            {
                ref var p = ref pixels[y * width + x];
                var outAlpha = alpha + p.a * (1f - alpha);
                if (outAlpha <= 0f) return;
                p.r = (color.r * alpha + p.r * p.a * (1f - alpha)) / outAlpha;
                p.g = (color.g * alpha + p.g * p.a * (1f - alpha)) / outAlpha;
                p.b = (color.b * alpha + p.b * p.a * (1f - alpha)) / outAlpha;
                p.a = outAlpha;
            }

            void Visit(Rect bounds, Action<int, int, float, float> action)
            {
                var x0 = Mathf.Max(0, Mathf.FloorToInt((bounds.xMin - origin.x) * scale));
                var x1 = Mathf.Min(width - 1, Mathf.CeilToInt((bounds.xMax - origin.x) * scale));
                var y0 = Mathf.Max(0, Mathf.FloorToInt((bounds.yMin - origin.y) * scale));
                var y1 = Mathf.Min(height - 1, Mathf.CeilToInt((bounds.yMax - origin.y) * scale));
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                        action(x, y, origin.x + (x + 0.5f) / scale, origin.y + (y + 0.5f) / scale);
            }

            public void Fill(Shape shape, Func<float, float, Color> color)
            {
                Visit(shape.bounds, (x, y, vx, vy) =>
                {
                    var alpha = Mathf.Clamp01(0.5f - shape.distance(vx, vy) * scale);
                    if (alpha <= 0f) return;
                    var c = color(vx, vy);
                    Blend(x, y, c, alpha * c.a);
                });
            }

            public void Fill(Shape shape, Color color) => Fill(shape, (x, y) => color);

            public void Stroke(Shape shape, float lineWidth, Color color)
            {
                var pad = lineWidth;
                var bounds = new Rect(shape.bounds.x - pad, shape.bounds.y - pad, shape.bounds.width + pad * 2f, shape.bounds.height + pad * 2f);
                Visit(bounds, (x, y, vx, vy) =>
                {
                    var alpha = Mathf.Clamp01(0.5f - (Mathf.Abs(shape.distance(vx, vy)) - lineWidth * 0.5f) * scale);
                    if (alpha > 0f) Blend(x, y, color, alpha * color.a);
                });
            }

            // A soft round light.
            public void Light(float cx, float cy, float radius, Color color, float strength)
            {
                Visit(new Rect(cx - radius, cy - radius, radius * 2f, radius * 2f), (x, y, vx, vy) =>
                {
                    var d = Mathf.Sqrt((vx - cx) * (vx - cx) + (vy - cy) * (vy - cy)) / radius;
                    if (d < 1f) Blend(x, y, color, strength * Mathf.Exp(-d * d * 3f));
                });
            }

            public void Vignette(float strength)
            {
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var dx = (x / (float)width - 0.5f) * 2f;
                        var dy = (y / (float)height - 0.5f) * 2f;
                        var k = 1f - strength * Mathf.Pow(Mathf.Clamp01((dx * dx + dy * dy) / 2f), 1.6f);
                        ref var p = ref pixels[y * width + x];
                        p.r *= k;
                        p.g *= k;
                        p.b *= k;
                    }
            }

            public Texture2D ToTexture(string name)
            {
                var flipped = new Color[pixels.Length];
                for (var y = 0; y < height; y++) Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                texture.SetPixels(flipped);
                texture.Apply();
                return texture;
            }
        }

        internal static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);

        internal static Func<float, float, Color> VerticalGradient(float y0, Color c0, float y1, Color c1) =>
            (x, y) => Color.Lerp(c0, c1, Mathf.Clamp01((y - y0) / (y1 - y0)));

        static Func<float, float, Color> HorizontalGradient(float x0, Color c0, float x1, Color c1) =>
            (x, y) => Color.Lerp(c0, c1, Mathf.Clamp01((x - x0) / (x1 - x0)));

        internal static Shape Ellipse(float cx, float cy, float rx, float ry, float angle = 0f)
        {
            var ux = Mathf.Cos(angle);
            var uy = Mathf.Sin(angle);
            var r = Mathf.Max(rx, ry) + 2f;
            return new Shape
            {
                bounds = new Rect(cx - r, cy - r, r * 2f, r * 2f),
                distance = (x, y) =>
                {
                    var lx = (x - cx) * ux + (y - cy) * uy;
                    var ly = -(x - cx) * uy + (y - cy) * ux;
                    return (Mathf.Sqrt(lx * lx / (rx * rx) + ly * ly / (ry * ry)) - 1f) * Mathf.Min(rx, ry);
                },
            };
        }

        internal static Shape Capsule(float ax, float ay, float bx, float by, float radius)
        {
            var abx = bx - ax;
            var aby = by - ay;
            var lengthSquared = Mathf.Max(0.0001f, abx * abx + aby * aby);
            var pad = radius + 2f;
            return new Shape
            {
                bounds = new Rect(Mathf.Min(ax, bx) - pad, Mathf.Min(ay, by) - pad, Mathf.Abs(abx) + pad * 2f, Mathf.Abs(aby) + pad * 2f),
                distance = (x, y) =>
                {
                    var t = Mathf.Clamp01(((x - ax) * abx + (y - ay) * aby) / lengthSquared);
                    var dx = x - (ax + abx * t);
                    var dy = y - (ay + aby * t);
                    return Mathf.Sqrt(dx * dx + dy * dy) - radius;
                },
            };
        }

        internal static Shape RoundRect(float cx, float cy, float halfWidth, float halfHeight, float radius, float angle = 0f)
        {
            var ux = Mathf.Cos(angle);
            var uy = Mathf.Sin(angle);
            var r = Mathf.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight) + 2f;
            return new Shape
            {
                bounds = new Rect(cx - r, cy - r, r * 2f, r * 2f),
                distance = (x, y) =>
                {
                    var lx = Mathf.Abs((x - cx) * ux + (y - cy) * uy) - (halfWidth - radius);
                    var ly = Mathf.Abs(-(x - cx) * uy + (y - cy) * ux) - (halfHeight - radius);
                    var ox = Mathf.Max(lx, 0f);
                    var oy = Mathf.Max(ly, 0f);
                    return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(lx, ly), 0f) - radius;
                },
            };
        }

        // A convex polygon.
        internal static Shape Polygon(params Vector2[] points)
        {
            var centre = Vector2.zero;
            foreach (var point in points) centre += point / points.Length;
            var normals = new Vector2[points.Length];
            var min = points[0];
            var max = points[0];
            for (var i = 0; i < points.Length; i++)
            {
                var edge = points[(i + 1) % points.Length] - points[i];
                var normal = new Vector2(edge.y, -edge.x).normalized;
                if (Vector2.Dot(normal, centre - points[i]) > 0f) normal = -normal;
                normals[i] = normal;
                min = Vector2.Min(min, points[i]);
                max = Vector2.Max(max, points[i]);
            }
            return new Shape
            {
                bounds = new Rect(min.x - 2f, min.y - 2f, max.x - min.x + 4f, max.y - min.y + 4f),
                distance = (x, y) =>
                {
                    var worst = float.MinValue;
                    for (var i = 0; i < points.Length; i++)
                        worst = Mathf.Max(worst, (x - points[i].x) * normals[i].x + (y - points[i].y) * normals[i].y);
                    return worst;
                },
            };
        }

        static Shape Box(float x0, float y0, float x1, float y1) =>
            Polygon(new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1));

        // --- The hallway -----------------------------------------------------------------------------------------

        static Vector2 WallPoint(bool left, float t, float h)
        {
            var x = left ? Mathf.Lerp(0f, 620f, t) : Mathf.Lerp(1600f, 980f, t);
            return new Vector2(x, Mathf.Lerp(Mathf.Lerp(900f, 560f, t), Mathf.Lerp(0f, 300f, t), h));
        }

        static Shape WallQuad(bool left, float t0, float t1, float h0, float h1) =>
            Polygon(WallPoint(left, t0, h0), WallPoint(left, t1, h0), WallPoint(left, t1, h1), WallPoint(left, t0, h1));

        static Texture2D PaintHall()
        {
            var canvas = new Canvas(new Rect(0f, 0f, W, H), 0.7f);
            var cream = C(0.97f, 0.93f, 0.84f);
            var wood = C(0.55f, 0.38f, 0.25f);

            canvas.Fill(Box(0f, 0f, W, H), C(0.92f, 0.9f, 0.84f));
            canvas.Fill(Polygon(new Vector2(0f, 0f), new Vector2(W, 0f), new Vector2(980f, 300f), new Vector2(620f, 300f)),
                VerticalGradient(0f, C(0.93f, 0.92f, 0.88f), 300f, C(1f, 0.99f, 0.94f)));
            canvas.Fill(Polygon(new Vector2(0f, 900f), new Vector2(W, 900f), new Vector2(980f, 560f), new Vector2(620f, 560f)),
                VerticalGradient(560f, C(0.9f, 0.72f, 0.5f), 900f, C(0.62f, 0.43f, 0.27f)));
            canvas.Fill(Polygon(new Vector2(0f, 0f), new Vector2(620f, 300f), new Vector2(620f, 560f), new Vector2(0f, 900f)),
                HorizontalGradient(0f, C(0.95f, 0.9f, 0.8f), 620f, cream));
            canvas.Fill(Polygon(new Vector2(W, 0f), new Vector2(980f, 300f), new Vector2(980f, 560f), new Vector2(W, 900f)),
                HorizontalGradient(980f, cream, W, C(0.93f, 0.88f, 0.78f)));
            canvas.Fill(Box(620f, 300f, 980f, 560f), C(0.99f, 0.96f, 0.89f));

            // Floor boards running into the distance, and light pools under the ceiling lights.
            for (var k = -9; k <= 9; k++)
                canvas.Stroke(Capsule(800f + k * 130f, 900f, 800f + k * 20f, 560f, 0.5f), 1.4f, C(0.3f, 0.18f, 0.1f, 0.22f));
            for (var i = 1; i <= 6; i++)
            {
                var y = 560f + 340f * Mathf.Pow(i / 7f, 1.8f);
                canvas.Stroke(Capsule(Mathf.Lerp(620f, 0f, (y - 560f) / 340f), y, Mathf.Lerp(980f, W, (y - 560f) / 340f), y, 0.5f), 1.2f, C(0.3f, 0.18f, 0.1f, 0.18f));
            }

            // Both walls: skirting, panelling and crown moulding.
            foreach (var left in new[] { true, false })
            {
                canvas.Fill(WallQuad(left, 0f, 1f, 0.045f, 0.32f), C(0.9f, 0.84f, 0.72f));
                canvas.Fill(WallQuad(left, 0f, 1f, 0f, 0.045f), wood);
                canvas.Fill(WallQuad(left, 0f, 1f, 0.955f, 1f), C(1f, 0.99f, 0.96f));
                canvas.Stroke(WallQuad(left, 0f, 1f, 0.045f, 0.32f), 1.5f, C(0.5f, 0.4f, 0.3f, 0.35f));
            }

            // Left wall: a door and a picture.
            canvas.Fill(WallQuad(true, 0.40f, 0.64f, 0.045f, 0.84f), C(0.35f, 0.22f, 0.12f));
            canvas.Fill(WallQuad(true, 0.41f, 0.63f, 0.05f, 0.82f), C(0.6f, 0.42f, 0.26f));
            canvas.Fill(WallQuad(true, 0.43f, 0.61f, 0.1f, 0.42f), C(0.52f, 0.35f, 0.21f));
            canvas.Fill(WallQuad(true, 0.43f, 0.61f, 0.48f, 0.78f), C(0.52f, 0.35f, 0.21f));
            var handle = WallPoint(true, 0.615f, 0.44f);
            canvas.Fill(Ellipse(handle.x, handle.y, 6f, 6f), C(0.92f, 0.78f, 0.32f));
            canvas.Fill(WallQuad(true, 0.10f, 0.28f, 0.48f, 0.82f), C(0.3f, 0.2f, 0.12f));
            canvas.Fill(WallQuad(true, 0.11f, 0.27f, 0.51f, 0.79f), VerticalGradient(WallPoint(true, 0.2f, 0.79f).y, C(0.62f, 0.82f, 0.96f), WallPoint(true, 0.2f, 0.51f).y, C(0.42f, 0.68f, 0.4f)));

            // Right wall: a bright window and a second door.
            canvas.Fill(WallQuad(false, 0.12f, 0.5f, 0.4f, 0.9f), C(0.96f, 0.95f, 0.92f));
            canvas.Fill(WallQuad(false, 0.14f, 0.48f, 0.43f, 0.87f), C(0.88f, 0.95f, 1f));
            canvas.Fill(WallQuad(false, 0.305f, 0.315f, 0.43f, 0.87f), C(0.96f, 0.95f, 0.92f));
            canvas.Fill(WallQuad(false, 0.14f, 0.48f, 0.645f, 0.655f), C(0.96f, 0.95f, 0.92f));
            var windowCentre = WallPoint(false, 0.31f, 0.65f);
            canvas.Light(windowCentre.x, windowCentre.y, 330f, C(1f, 0.97f, 0.86f), 0.22f);
            canvas.Fill(WallQuad(false, 0.62f, 0.80f, 0.045f, 0.8f), C(0.35f, 0.22f, 0.12f));
            canvas.Fill(WallQuad(false, 0.63f, 0.79f, 0.05f, 0.78f), C(0.6f, 0.42f, 0.26f));

            // The far door.
            canvas.Fill(Box(740f, 380f, 860f, 560f), C(0.35f, 0.22f, 0.12f));
            canvas.Fill(Box(748f, 388f, 852f, 560f), C(0.6f, 0.42f, 0.26f));
            canvas.Fill(Ellipse(836f, 478f, 5f, 5f), C(0.92f, 0.78f, 0.32f));

            // Ceiling lights, and their glow on everything.
            foreach (var t in new[] { 0.2f, 0.48f, 0.74f })
            {
                var y = Mathf.Lerp(0f, 300f, t);
                var halfWidth = Mathf.Lerp(210f, 70f, t);
                var halfHeight = Mathf.Lerp(34f, 11f, t);
                canvas.Fill(RoundRect(800f, y, halfWidth, halfHeight, halfHeight * 0.45f), C(1f, 0.99f, 0.93f));
                canvas.Stroke(RoundRect(800f, y, halfWidth, halfHeight, halfHeight * 0.45f), 2f, C(0.78f, 0.75f, 0.68f));
                canvas.Light(800f, y + halfHeight, halfWidth * 2.6f, C(1f, 0.97f, 0.85f), 0.3f);
                var floorY = Mathf.Lerp(900f, 560f, t * 0.8f + 0.1f);
                canvas.Fill(Ellipse(800f, floorY, Mathf.Lerp(360f, 130f, t), Mathf.Lerp(46f, 16f, t)), C(1f, 0.97f, 0.85f, 0.2f));
            }

            // A plant in the front corner.
            canvas.Fill(Polygon(new Vector2(70f, 772f), new Vector2(152f, 772f), new Vector2(138f, 862f), new Vector2(84f, 862f)), C(0.6f, 0.34f, 0.22f));
            for (var i = 0; i < 7; i++)
            {
                var angle = -1.2f + i * 0.4f;
                canvas.Fill(Ellipse(111f + Mathf.Sin(angle) * 40f, 730f - Mathf.Cos(angle) * 22f, 17f, 52f, angle), C(0.25f + i % 3 * 0.05f, 0.55f, 0.28f));
            }

            PaintPerson(canvas);
            canvas.Vignette(0.2f);
            return canvas.ToTexture("Menu Hall");
        }

        // The person: simple shapes and a rough, gritted-teeth face. The raised arm and swatter are painted separately
        // (PaintArm) so they can swing.
        static void PaintPerson(Canvas canvas)
        {
            var skin = C(0.93f, 0.74f, 0.6f);
            var skinShade = C(0.84f, 0.62f, 0.5f);
            var hair = C(0.22f, 0.14f, 0.1f);
            var shirt = C(0.13f, 0.6f, 0.64f);
            var shirtDark = C(0.08f, 0.42f, 0.46f);
            var pants = C(0.2f, 0.27f, 0.45f);
            var shoes = C(0.2f, 0.13f, 0.09f);

            canvas.Fill(Ellipse(1300f, 876f, 200f, 24f), C(0f, 0f, 0f, 0.26f));

            // Back arm, swinging behind.
            canvas.Fill(Capsule(1385f, 320f, 1452f, 430f, 27f), shirtDark);
            canvas.Fill(Capsule(1452f, 430f, 1440f, 540f, 22f), skinShade);
            canvas.Fill(Ellipse(1438f, 550f, 24f, 26f), skinShade);

            // Legs, mid-stride, and shoes.
            canvas.Fill(Capsule(1335f, 520f, 1352f, 690f, 38f), C(0.17f, 0.23f, 0.4f));
            canvas.Fill(Capsule(1352f, 690f, 1390f, 850f, 36f), C(0.17f, 0.23f, 0.4f));
            canvas.Fill(Ellipse(1405f, 872f, 58f, 24f), shoes);
            canvas.Fill(Capsule(1255f, 520f, 1205f, 690f, 40f), pants);
            canvas.Fill(Capsule(1205f, 690f, 1168f, 850f, 38f), pants);
            canvas.Fill(Ellipse(1148f, 872f, 62f, 25f), shoes);

            // Torso and neck.
            canvas.Fill(RoundRect(1300f, 410f, 108f, 142f, 62f), HorizontalGradient(1195f, C(0.2f, 0.7f, 0.74f), 1405f, shirtDark));
            canvas.Fill(Box(1196f, 515f, 1404f, 535f), C(0.12f, 0.1f, 0.09f));
            canvas.Fill(Capsule(1296f, 300f, 1296f, 255f, 30f), skinShade);

            // Head: hair behind, face, fringe.
            canvas.Fill(Ellipse(1292f, 178f, 80f, 84f), hair);
            canvas.Fill(Ellipse(1286f, 203f, 65f, 73f), skin);
            canvas.Fill(Ellipse(1352f, 212f, 12f, 18f), skinShade);
            canvas.Fill(Ellipse(1290f, 138f, 72f, 34f), hair);
            canvas.Fill(Polygon(new Vector2(1218f, 150f), new Vector2(1262f, 160f), new Vector2(1236f, 178f)), hair);

            // Face: wide eyes looking left at the fly, angry brows, a nose, and gritted teeth.
            canvas.Fill(Ellipse(1256f, 192f, 17f, 14f), C(0.98f, 0.98f, 0.96f));
            canvas.Fill(Ellipse(1306f, 192f, 17f, 14f), C(0.98f, 0.98f, 0.96f));
            canvas.Fill(Ellipse(1248f, 193f, 7.5f, 8f), C(0.08f, 0.06f, 0.05f));
            canvas.Fill(Ellipse(1298f, 193f, 7.5f, 8f), C(0.08f, 0.06f, 0.05f));
            canvas.Fill(Capsule(1232f, 160f, 1274f, 177f, 5.5f), hair);
            canvas.Fill(Capsule(1290f, 177f, 1334f, 160f, 5.5f), hair);
            canvas.Fill(Ellipse(1262f, 220f, 10f, 15f), skinShade);
            canvas.Fill(Ellipse(1244f, 232f, 18f, 10f), C(0.95f, 0.5f, 0.45f, 0.35f));
            canvas.Fill(RoundRect(1272f, 252f, 31f, 12f, 6f), C(0.98f, 0.96f, 0.9f));
            for (var i = -2; i <= 2; i++) canvas.Fill(Capsule(1272f + i * 11f, 242f, 1272f + i * 11f, 262f, 1.2f), C(0.3f, 0.2f, 0.18f, 0.7f));
            canvas.Fill(Capsule(1242f, 252f, 1302f, 252f, 1.2f), C(0.3f, 0.2f, 0.18f, 0.7f));
            canvas.Stroke(RoundRect(1272f, 252f, 31f, 12f, 6f), 3f, C(0.35f, 0.18f, 0.15f));

            // Sweat and a few anger marks.
            canvas.Fill(Ellipse(1352f, 150f, 7f, 11f), C(0.65f, 0.85f, 1f, 0.9f));
            canvas.Fill(Ellipse(1228f, 130f, 5f, 8f), C(0.65f, 0.85f, 1f, 0.9f));
            canvas.Fill(Capsule(1378f, 122f, 1398f, 108f, 3f), C(0.9f, 0.2f, 0.15f));
            canvas.Fill(Capsule(1384f, 138f, 1408f, 134f, 3f), C(0.9f, 0.2f, 0.15f));
        }

        // The raised arm holding a swatter, pointing at the fly. The shoulder is where it swings from.
        static Texture2D PaintArm()
        {
            var canvas = new Canvas(ArmRect, 0.8f);
            var skin = C(0.93f, 0.74f, 0.6f);
            var shirt = C(0.13f, 0.6f, 0.64f);

            var shoulder = Shoulder;
            var elbow = new Vector2(1138f, 346f);
            var hand = new Vector2(1052f, 360f);
            var direction = new Vector2(-0.96f, -0.28f).normalized;
            var handleEnd = hand + direction * 190f;
            var headCentre = hand + direction * 270f;
            var angle = Mathf.Atan2(direction.y, direction.x);

            // A swish of motion behind the swatter head.
            for (var i = 0; i < 3; i++)
            {
                var offset = new Vector2(-direction.y, direction.x) * (-60f + i * 60f);
                var from = headCentre - direction * (110f + i * 10f) + offset * 0.9f;
                canvas.Fill(Capsule(from.x, from.y, from.x - direction.x * 70f, from.y - direction.y * 70f, 4f - i * 0.6f), C(1f, 1f, 1f, 0.55f - i * 0.1f));
            }

            canvas.Fill(Capsule(shoulder.x, shoulder.y, elbow.x, elbow.y, 28f), shirt);
            canvas.Fill(Capsule(elbow.x, elbow.y, hand.x, hand.y, 22f), skin);

            // Handle, then the swatter head: a frame with a mesh.
            canvas.Fill(Capsule(hand.x, hand.y, handleEnd.x, handleEnd.y, 7f), C(0.85f, 0.25f, 0.2f));
            var frame = RoundRect(headCentre.x, headCentre.y, 84f, 56f, 16f, angle);
            canvas.Fill(frame, C(0.45f, 0.86f, 0.45f, 0.85f));
            var across = new Vector2(-direction.y, direction.x);
            for (var i = -3; i <= 3; i++)
            {
                var along = direction * (i * 22f);
                var a = headCentre + along - across * 52f;
                var b = headCentre + along + across * 52f;
                canvas.Fill(Capsule(a.x, a.y, b.x, b.y, 1.3f), C(0.15f, 0.4f, 0.15f, 0.55f));
            }
            for (var i = -2; i <= 2; i++)
            {
                var across2 = across * (i * 20f);
                var a = headCentre + across2 - direction * 78f;
                var b = headCentre + across2 + direction * 78f;
                canvas.Fill(Capsule(a.x, a.y, b.x, b.y, 1.3f), C(0.15f, 0.4f, 0.15f, 0.55f));
            }
            canvas.Stroke(frame, 6f, C(0.82f, 0.2f, 0.15f));

            // The fist around the handle.
            canvas.Fill(Ellipse(hand.x, hand.y, 25f, 23f, angle), skin);
            canvas.Fill(Capsule(hand.x - 6f, hand.y - 12f, hand.x - 6f, hand.y + 12f, 4f), C(0.8f, 0.58f, 0.46f));
            return canvas.ToTexture("Menu Arm");
        }

        // The fly, seen from above and facing left. Two frames (wings up and down) make it flutter.
        static Texture2D PaintFly(bool wingsUp)
        {
            var canvas = new Canvas(new Rect(0f, 0f, 256f, 256f), 1f);
            var dark = C(0.16f, 0.15f, 0.15f);
            var wing = C(0.78f, 0.88f, 1f, 0.55f);
            var vein = C(0.35f, 0.45f, 0.6f, 0.7f);

            // Legs.
            foreach (var side in new[] { -1f, 1f })
            {
                canvas.Fill(Capsule(100f, 128f + side * 20f, 72f, 128f + side * 66f, 2.8f), dark);
                canvas.Fill(Capsule(118f, 128f + side * 22f, 112f, 128f + side * 72f, 2.8f), dark);
                canvas.Fill(Capsule(136f, 128f + side * 22f, 160f, 128f + side * 66f, 2.8f), dark);
            }

            // Wings.
            foreach (var side in new[] { -1f, 1f })
            {
                var angle = wingsUp ? side * 0.45f : side * 0.12f;
                var spread = wingsUp ? 74f : 66f;
                var wingShape = Ellipse(150f, 128f + side * (wingsUp ? 62f : 46f), spread, wingsUp ? 24f : 20f, angle + (side > 0f ? 0.35f : -0.35f));
                canvas.Fill(wingShape, wing);
                canvas.Stroke(wingShape, 2f, vein);
            }

            // Body.
            canvas.Fill(Ellipse(172f, 128f, 56f, 34f), dark);
            for (var i = 0; i < 3; i++) canvas.Fill(Capsule(150f + i * 18f, 98f, 150f + i * 18f, 158f, 2.2f), C(0.38f, 0.36f, 0.34f, 0.8f));
            canvas.Fill(Ellipse(112f, 128f, 40f, 33f), C(0.2f, 0.19f, 0.19f));
            canvas.Fill(Ellipse(104f, 118f, 16f, 9f), C(0.4f, 0.4f, 0.4f, 0.5f));
            canvas.Fill(Ellipse(62f, 128f, 26f, 26f), dark);
            canvas.Fill(Ellipse(56f, 110f, 14f, 16f), C(0.82f, 0.1f, 0.08f));
            canvas.Fill(Ellipse(56f, 146f, 14f, 16f), C(0.82f, 0.1f, 0.08f));
            canvas.Fill(Ellipse(51f, 105f, 4f, 5f), C(1f, 0.85f, 0.8f, 0.9f));
            canvas.Fill(Ellipse(51f, 141f, 4f, 5f), C(1f, 0.85f, 0.8f, 0.9f));
            canvas.Fill(Capsule(38f, 128f, 28f, 128f, 3f), dark);
            return canvas.ToTexture(wingsUp ? "Menu Fly Up" : "Menu Fly Down");
        }
    }
}
