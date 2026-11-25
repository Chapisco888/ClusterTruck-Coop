using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ClusterTruckCoop
{
    // Breakable walls (destructive) and props, identified the same way on every machine.
    static class Breakables
    {
        static readonly Dictionary<int, Component> byId = new Dictionary<int, Component>();
        static readonly Dictionary<Component, int> idOf = new Dictionary<Component, int>();
        static readonly HashSet<int> done = new HashSet<int>();
        public static readonly List<KeyValuePair<int, Vector3>> broken = new List<KeyValuePair<int, Vector3>>(); // host: this round
        static readonly MethodInfo explodeWall = AccessTools.Method(typeof(destructive), "Explode");
        static readonly MethodInfo explodeProp = AccessTools.Method(typeof(prop), "Explode");
        static int scanned = -99;

        // Once per round, when the level has settled (nothing has moved yet: everyone is held at the start).
        public static void Scan(int epoch)
        {
            if (scanned == epoch) return;
            scanned = epoch;
            byId.Clear(); idOf.Clear(); done.Clear(); broken.Clear();
            foreach (var c in Object.FindObjectsOfType<destructive>()) Add(c);
            foreach (var c in Object.FindObjectsOfType<prop>()) Add(c);
            if (CoopPlugin.qa) CoopPlugin.I.Qa("EVENT quebraveis na fase: " + byId.Count);
        }

        static void Add(Component c)
        {
            Vector3 p = c.transform.position;
            int id = Trucks.Fnv(Trucks.Path(c.transform) + "@" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.y) + "," + Mathf.RoundToInt(p.z));
            while (byId.ContainsKey(id)) id++;
            byId[id] = c;
            idOf[c] = id;
        }

        // Every Explode, local or replayed. The host records it; anywhere it marks the piece as gone.
        public static void OnExplode(Component c, Vector3 pos)
        {
            int id;
            if (!idOf.TryGetValue(c, out id) || !done.Add(id)) return;
            if (CoopPlugin.mode == CoopPlugin.Mode.Host) broken.Add(new KeyValuePair<int, Vector3>(id, pos));
            if (CoopPlugin.qa) CoopPlugin.I.Qa("EVENT quebrou " + c.GetType().Name + " id=" + id);
        }

        // Client: a break from the host.
        public static void Apply(int id, Vector3 pos)
        {
            Component c;
            if (scanned != CoopPlugin.epoch || done.Contains(id) || !byId.TryGetValue(id, out c) || c == null) return;
            (c is destructive ? explodeWall : explodeProp).Invoke(c, new object[] { pos });
        }
    }
}
