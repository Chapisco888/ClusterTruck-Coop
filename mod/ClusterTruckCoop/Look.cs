using System.Collections.Generic;
using UnityEngine;

namespace ClusterTruckCoop
{
    // A player's appearance: 9 bytes that travel with the state packet.
    //   0 skin, 1 eyes, 2 hair, 3 shirt, 4 pants  -> palette index (shirt 255 = the player's slot colour)
    //   5 head, 6 face, 7 back, 8 outfit          -> item worn in that slot (0 = nothing, else Items index + 1)
    static class Look
    {
        public const int Size = 9, Slots = 4;
        public const byte Auto = 255;

        public static readonly Color[] skins =
        {
            C(255, 209, 159), C(241, 194, 125), C(224, 172, 105), C(198, 134, 66), C(141, 85, 36), C(92, 56, 30),
        };
        public static readonly Color[] colors =
        {
            C(41, 41, 41), C(240, 240, 240), C(124, 88, 35), C(230, 190, 60), C(220, 60, 50), C(255, 115, 15),
            C(250, 220, 40), C(80, 200, 70), C(115, 167, 196), C(21, 96, 189), C(140, 80, 200), C(240, 110, 180),
        };
        static readonly Color[] slotColors = { C(255, 115, 15), C(25, 190, 255), C(75, 230, 50), C(240, 50, 215), C(255, 230, 50), C(150, 100, 255), C(240, 240, 240) };

        static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        public class Part { public PrimitiveType shape; public Vector3 pos, scale, euler; public Color color; }
        public class Item
        {
            public string name; public int price, slot, bone; public bool shirtless; public Part[] parts = new Part[0];
        }

        static Part P(PrimitiveType t, float x, float y, float z, float sx, float sy, float sz, Color c, float rx = 0f, float ry = 0f, float rz = 0f)
            => new Part { shape = t, pos = new Vector3(x, y, z), scale = new Vector3(sx, sy, sz), euler = new Vector3(rx, ry, rz), color = c };

        const PrimitiveType Cube = PrimitiveType.Cube, Cyl = PrimitiveType.Cylinder, Ball = PrimitiveType.Sphere;
        static readonly Color black = C(25, 25, 25), red = C(210, 40, 40), gold = C(245, 200, 40), white = C(240, 240, 240), blue = C(40, 90, 200), brown = C(110, 70, 30);

        // Positions are metres from the bone (0 = base of the head, 1 = upper chest) in the rest pose: +y up, +z the way the body faces.
        // Never reorder: saved purchases and the network use the index.
        public static readonly Item[] Items =
        {
            new Item { name = "Bone", price = 1000, slot = 0, bone = 0, parts = new[] {
                P(Ball, 0, 0.25f, 0.05f, 0.31f, 0.2f, 0.36f, red), P(Cube, 0, 0.24f, 0.27f, 0.22f, 0.02f, 0.16f, red) } },
            new Item { name = "Chapeu de festa", price = 2000, slot = 0, bone = 0, parts = new[] {
                P(Cyl, 0, 0.33f, 0.05f, 0.2f, 0.03f, 0.2f, C(240, 110, 180)), P(Cyl, 0, 0.4f, 0.05f, 0.14f, 0.04f, 0.14f, gold),
                P(Cyl, 0, 0.47f, 0.05f, 0.08f, 0.04f, 0.08f, C(240, 110, 180)), P(Ball, 0, 0.53f, 0.05f, 0.07f, 0.07f, 0.07f, white) } },
            new Item { name = "Cartola", price = 3000, slot = 0, bone = 0, parts = new[] {
                P(Cyl, 0, 0.3f, 0.05f, 0.44f, 0.01f, 0.44f, black), P(Cyl, 0, 0.42f, 0.05f, 0.27f, 0.12f, 0.27f, black),
                P(Cyl, 0, 0.335f, 0.05f, 0.28f, 0.02f, 0.28f, red) } },
            new Item { name = "Coroa", price = 10000, slot = 0, bone = 0, parts = new[] {
                P(Cyl, 0, 0.32f, 0.05f, 0.27f, 0.03f, 0.27f, gold),
                P(Cube, 0.1f, 0.38f, 0.05f, 0.05f, 0.08f, 0.05f, gold, 0, 45), P(Cube, -0.1f, 0.38f, 0.05f, 0.05f, 0.08f, 0.05f, gold, 0, 45),
                P(Cube, 0, 0.38f, 0.15f, 0.05f, 0.08f, 0.05f, gold, 0, 45), P(Cube, 0, 0.38f, -0.05f, 0.05f, 0.08f, 0.05f, gold, 0, 45),
                P(Ball, 0, 0.36f, 0.185f, 0.04f, 0.04f, 0.04f, red) } },
            new Item { name = "Oculos escuros", price = 2000, slot = 1, bone = 0, parts = new[] {
                P(Cube, 0.065f, 0.165f, 0.2f, 0.1f, 0.055f, 0.03f, black), P(Cube, -0.065f, 0.165f, 0.2f, 0.1f, 0.055f, 0.03f, black),
                P(Cube, 0, 0.175f, 0.2f, 0.05f, 0.015f, 0.02f, black) } },
            new Item { name = "Bigode", price = 1500, slot = 1, bone = 0, parts = new[] {
                P(Cube, 0.035f, 0.085f, 0.21f, 0.08f, 0.025f, 0.03f, black, 0, 0, -15), P(Cube, -0.035f, 0.085f, 0.21f, 0.08f, 0.025f, 0.03f, black, 0, 0, 15) } },
            new Item { name = "Mochila", price = 2000, slot = 2, bone = 1, parts = new[] {
                P(Cube, 0, -0.14f, -0.27f, 0.3f, 0.34f, 0.14f, blue), P(Cube, 0, -0.19f, -0.35f, 0.2f, 0.14f, 0.04f, gold) } },
            new Item { name = "Capa", price = 5000, slot = 2, bone = 1, parts = new[] {
                P(Cube, 0, -0.33f, -0.22f, 0.42f, 0.74f, 0.02f, red, -5), P(Cube, 0, 0.03f, -0.08f, 0.36f, 0.05f, 0.22f, red) } },
            new Item { name = "Gravata", price = 1000, slot = 3, bone = 1, parts = new[] {
                P(Cube, 0, -0.02f, 0.15f, 0.06f, 0.05f, 0.03f, red), P(Cube, 0, -0.16f, 0.155f, 0.05f, 0.24f, 0.02f, red) } },
            new Item { name = "Sem camisa", price = 1500, slot = 3, bone = 1, shirtless = true },
            new Item { name = "Cachecol", price = 2500, slot = 3, bone = 1, parts = new[] {
                P(Cyl, 0, 0.06f, 0.02f, 0.26f, 0.04f, 0.26f, C(80, 200, 70)), P(Cube, 0.07f, -0.08f, 0.16f, 0.07f, 0.24f, 0.02f, C(80, 200, 70)) } },
        };

        public static byte[] Default() => new byte[] { 0, 0, 2, Auto, 9, 0, 0, 0, 0 };

        // Anything can arrive from the network: keep every byte inside its table.
        public static void Sanitize(byte[] l)
        {
            if (l[0] >= skins.Length) l[0] = 0;
            for (int i = 1; i < 5; i++) if (l[i] >= colors.Length && !(i == 3 && l[i] == Auto)) l[i] = 0;
            for (int s = 0; s < Slots; s++)
                if (l[5 + s] > Items.Length || (l[5 + s] > 0 && Items[l[5 + s] - 1].slot != s)) l[5 + s] = 0;
        }

        public static Item Worn(byte[] l, int slot) => l[5 + slot] == 0 ? null : Items[l[5 + slot] - 1];

        // The model's texture is five horizontal bands (top to bottom): skin, eyes, hair, shirt, pants.
        public static void Paint(Texture2D tex, byte[] l, int playerId)
        {
            Color skin = skins[l[0]];
            var outfit = Worn(l, 3);
            Color shirt = outfit != null && outfit.shirtless ? skin : l[3] == Auto ? slotColors[playerId % slotColors.Length] : colors[l[3]];
            Color[] bands = { skin, colors[l[1]], colors[l[2]], shirt, colors[l[4]] };
            int[] from = { 0, 6, 11, 17, 23, 32 };
            for (int b = 0; b < 5; b++)
                for (int row = from[b]; row < from[b + 1]; row++)
                    for (int x = 0; x < 32; x++) tex.SetPixel(x, 31 - row, bands[b]);
            tex.Apply();
        }

        // Builds the worn items as children of `root`, one holder per bone; the puppet moves the holders every frame.
        public static void Dress(byte[] l, Transform[] holders, Material template)
        {
            foreach (var h in holders)
                for (int i = h.childCount - 1; i >= 0; i--) Object.Destroy(h.GetChild(i).gameObject);
            for (int s = 0; s < Slots; s++)
            {
                var item = Worn(l, s);
                if (item == null) continue;
                foreach (var p in item.parts)
                {
                    var go = GameObject.CreatePrimitive(p.shape);
                    Object.Destroy(go.GetComponent<Collider>());
                    go.transform.SetParent(holders[item.bone], false);
                    go.transform.localPosition = p.pos;
                    go.transform.localScale = p.scale;
                    go.transform.localEulerAngles = p.euler;
                    var m = new Material(template) { mainTexture = null, color = p.color };
                    go.GetComponent<Renderer>().material = m;
                }
            }
        }

        public static string ToText(byte[] l)
        {
            var sb = new System.Text.StringBuilder();
            foreach (byte b in l) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] FromText(string s)
        {
            var l = Default();
            try { if (s != null && s.Length == Size * 2) for (int i = 0; i < Size; i++) l[i] = System.Convert.ToByte(s.Substring(i * 2, 2), 16); }
            catch (System.FormatException) { l = Default(); }
            Sanitize(l);
            return l;
        }
    }
}
