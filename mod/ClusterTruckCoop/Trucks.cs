using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ClusterTruckCoop
{
    // Host streams cab + trailer rigidbodies; the client steers its copies toward that state each physics step.
    class TruckSync
    {
        public int id;
        public car c;
        public Rigidbody a, b;
        bool has;
        float ht; // host clock of this sample
        byte flags;
        Vector3 pa, va, wa, pb, vb, wb;
        Quaternion ra, rb;

        public const int Size = 4 + 1 + 2 * (12 + 16 + 12 + 12);

        static bool Alive(Rigidbody r) => r != null && r.gameObject.activeInHierarchy;

        public void Write(Buf w)
        {
            w.Write(id);
            w.Write((byte)((Alive(a) ? 1 : 0) | (Alive(b) ? 2 : 0)));
            WriteRb(w, a);
            WriteRb(w, b);
        }

        static void WriteRb(Buf w, Rigidbody r)
        {
            Vector3 p = Vector3.zero, v = Vector3.zero, av = Vector3.zero;
            Quaternion q = Quaternion.identity;
            if (r != null) { p = r.position; q = r.rotation; v = r.velocity; av = r.angularVelocity; }
            w.Write(p.x); w.Write(p.y); w.Write(p.z);
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
            w.Write(av.x); w.Write(av.y); w.Write(av.z);
        }

        public void Read(BinaryReader r, float hostT)
        {
            byte f = r.ReadByte();
            Vector3 p1, v1, w1, p2, v2, w2;
            Quaternion q1, q2;
            ReadRb(r, out p1, out q1, out v1, out w1);
            ReadRb(r, out p2, out q2, out v2, out w2);
            if (has && hostT < ht) return; // reordered on the way: keep the newer sample
            if (Bad(p1) || Bad(p2) || Bad(v1) || Bad(v2) || Bad(w1) || Bad(w2)) return; // never feed NaN into the physics scene
            flags = f; pa = p1; va = v1; wa = w1; pb = p2; vb = v2; wb = w2;
            ra = Norm(q1); rb = Norm(q2);
            ht = hostT;
            has = true;
        }

        // rotation q turned by world angular velocity w (rad/s) for t seconds
        static Quaternion Turn(Quaternion q, Vector3 w, float t)
        {
            float m = w.magnitude;
            return m * t < 1e-5f ? q : Quaternion.AngleAxis(m * t * Mathf.Rad2Deg, w / m) * q;
        }

        static Quaternion Norm(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 1e-4f || float.IsNaN(m) ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        static bool Bad(Vector3 v) => float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z);

        static void ReadRb(BinaryReader r, out Vector3 p, out Quaternion q, out Vector3 v, out Vector3 w)
        {
            p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            w = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        public void TakeOver()
        {
            if (c != null) c.enabled = false; // its forces/velocity writes fight the host data
            foreach (var r in new[] { a, b })
                if (r != null)
                {
                    r.isKinematic = true;
                    r.interpolation = RigidbodyInterpolation.Interpolate;
                    var dmg = r.GetComponent<carCheckDamage>();
                    if (dmg != null) dmg.enabled = false; // explosions come from the host flags only
                }
        }

        public void ReleaseToPhysics()
        {
            if (c != null) c.enabled = true;
            foreach (var r in new[] { a, b })
                if (r != null)
                {
                    r.isKinematic = false;
                    if (r.GetComponent<carCheckDamage>() != null) r.GetComponent<carCheckDamage>().enabled = true;
                }
        }

        public void Apply()
        {
            if (!has) return;
            float age = Trucks.Age(ht), dt = Trucks.StepReal;
            moveA = ApplyRb(a, (flags & 1) != 0, pa + va * age, Turn(ra, wa, age), va, wa, dt);
            moveB = ApplyRb(b, (flags & 2) != 0, pb + vb * age, Turn(rb, wb, age), vb, wb, dt);
            if (moveA.ok && Time.fixedDeltaTime > 0f) Trucks.jerk = Mathf.Max(Trucks.jerk, ((moveA.p1 - moveA.p0) / Time.fixedDeltaTime - va).magnitude);
        }

        // The pose change this physics step will make: lets us know the velocity of any point on the truck.
        struct Move { public bool ok; public Vector3 p0, p1; public Quaternion q0, q1; }
        Move moveA, moveB;

        // Kinematic + MovePosition keeps the trailer and wheel joints stable.
        // (Steering dynamic bodies by velocity/teleport tore those joints apart and produced NaN.)
        // Each step the truck first moves on by its own velocity, then closes 20% of the gap to the host pose:
        // a late or jittery packet becomes a gentle drift instead of a jump that throws the player (snap only if way off).
        const float Blend = 0.2f, SnapDist = 3f;

        static Move ApplyRb(Rigidbody r, bool hostAlive, Vector3 p, Quaternion q, Vector3 v, Vector3 w, float dt)
        {
            var m = new Move();
            if (r == null || !r.gameObject.activeInHierarchy) return m;
            if (!hostAlive)
            {
                var dmg = r.GetComponent<carCheckDamage>();
                if (dmg != null) dmg.Explode();
                r.gameObject.SetActive(false);
                return m;
            }
            Vector3 pred = r.position + v * dt;
            Quaternion qPred = Turn(r.rotation, w, dt);
            bool snap = (pred - p).sqrMagnitude > SnapDist * SnapDist;
            if (snap && Trucks.snaps++ == 0 && CoopPlugin.qa) Trucks.firstSnap = "pos=" + r.position + " target=" + p + " v=" + v + " dt=" + dt + " ts=" + Time.timeScale + " fdt=" + Time.fixedDeltaTime + " t=" + Time.unscaledTime;
            // a snap must not fling a player standing on it: no carry velocity for that step
            m = new Move { ok = !snap, p0 = r.position, q0 = r.rotation, p1 = snap ? p : Vector3.Lerp(pred, p, Blend), q1 = snap ? q : Quaternion.Slerp(qPred, q, Blend) };
            r.MovePosition(m.p1);
            r.MoveRotation(m.q1);
            return m;
        }

        public bool PointVelocity(Rigidbody r, Vector3 x, out Vector3 v)
        {
            Move m = r == a ? moveA : moveB;
            v = Vector3.zero;
            if (!m.ok) return false;
            Vector3 x1 = m.p1 + m.q1 * (Quaternion.Inverse(m.q0) * (x - m.p0));
            v = (x1 - x) / Time.fixedDeltaTime;
            return true;
        }

        // QA: how far the client copy is from where the host data says it should be now
        public float Error() => has && a != null && a.gameObject.activeInHierarchy && (flags & 1) != 0
            ? Vector3.Distance(a.position, pa + va * Trucks.Age(ht)) : 0f;
        public bool HasData => has;
    }

    static class Trucks
    {
        public static readonly List<TruckSync> list = new List<TruckSync>();
        static readonly Dictionary<int, TruckSync> byId = new Dictionary<int, TruckSync>();
        static readonly Dictionary<string, int> seen = new Dictionary<string, int>();
        static int dropped;
        public static float jerk;
        public static int snaps;
        public static string firstSnap = "";

        static readonly Dictionary<Rigidbody, TruckSync> byRb = new Dictionary<Rigidbody, TruckSync>();

        public static void Clear() { list.Clear(); byId.Clear(); byRb.Clear(); seen.Clear(); dropped = 0; }

        // Same id on both machines: hierarchy path (no scene-root index) + how many times that path has spawned.
        public static void Register(car c)
        {
            string path = Path(c.transform);
            int n;
            seen.TryGetValue(path, out n);
            seen[path] = n + 1;
            int id = Fnv(path + "|" + n);
            while (byId.ContainsKey(id)) id++;
            var s = new TruckSync { id = id, c = c, a = c.mainRig, b = c.secondRig };
            list.Add(s);
            byId[id] = s;
            if (s.a != null) byRb[s.a] = s;
            if (s.b != null) byRb[s.b] = s;
            if (CoopPlugin.mode == CoopPlugin.Mode.Client) s.TakeOver();
        }

        // Host clock -> local clock: the smallest (arrival - host time) seen is the base latency; packets above it were
        // delayed by jitter, so ages computed against it don't jump around. Creeps up slowly in case the route got slower.
        static float offset;
        static bool hasOffset;
        public static void Sample(float hostT)
        {
            float s = Time.unscaledTime - hostT;
            if (!hasOffset || s < offset) { offset = s; hasOffset = true; }
        }
        public static float Age(float hostT) => Mathf.Clamp(Time.unscaledTime - offset - hostT, 0f, 0.25f);
        public static float StepReal => Time.fixedDeltaTime / Mathf.Max(Time.timeScale, 0.01f); // real seconds per physics step

        public static void ReadInto(BinaryReader r, float hostT)
        {
            int id = r.ReadInt32();
            TruckSync s;
            if (byId.TryGetValue(id, out s)) s.Read(r, hostT);
            else { r.BaseStream.Seek(TruckSync.Size - 4, SeekOrigin.Current); dropped++; }
        }

        public static void ApplyRemote()
        {
            if (Time.timeScale <= 0f) return; // paused / start hold: Unity still calls FixedUpdate (with a 0.001 step), nothing may move
            offset += 0.02f * StepReal;
            foreach (var s in list) s.Apply();
        }

        // Kinematic trucks don't drag the player along like the vanilla dynamic ones, so do the friction by hand:
        // a player standing on a synced truck moves with it horizontally (and keeps that momentum when jumping).
        public static string CarryPlayer(player p)
        {
            if (p == null || p.dead || !p.canMove) return "parado";
            var rig = p.GetComponent<Rigidbody>();
            RaycastHit hit;
            if (rig == null || !Physics.Raycast(rig.position, Vector3.down, out hit, 2f, ~0, QueryTriggerInteraction.Ignore)) return "no ar";
            if (!CoopPlugin.qa && (hit.rigidbody == null || !byRb.ContainsKey(hit.rigidbody))) return null; // the texts below are QA only
            if (hit.rigidbody == null) return "sobre " + hit.collider.name + " d=" + hit.distance.ToString("F2");
            TruckSync s;
            Vector3 v;
            if (!byRb.TryGetValue(hit.rigidbody, out s)) return "sobre rb " + hit.rigidbody.name;
            if (!s.PointVelocity(hit.rigidbody, hit.point, out v)) return "caminhao sem dados";
            float vy = rig.velocity.y;
            // A kinematic truck that bobs up (host suspension settling) makes PhysX pop the player off at 10+ m/s.
            // Standing on it without having jumped, never rise faster than the truck surface.
            bool popped = hit.distance < 0.9f && p.sinceLastJump > 0.3f && vy > v.y + 1.5f;
            if (popped) vy = v.y;
            rig.velocity = new Vector3(v.x, vy, v.z);
            return !CoopPlugin.qa ? null : (popped ? "carregado(pop contido)" : "carregado") + " d=" + hit.distance.ToString("F2") + " v=" + v.magnitude.ToString("F1");
        }

        public static string Summary()
        {
            int with = 0;
            float maxErr = 0f;
            int over2 = 0;
            string worst = "-";
            foreach (var s in list)
            {
                if (s.HasData) with++;
                float e = s.Error();
                if (e > 2f) over2++;
                if (e > maxErr) { maxErr = e; worst = s.a != null ? s.a.position + " k=" + s.a.isKinematic : "-"; }
            }
            float j = jerk; jerk = 0f;
            int sn = snaps; snaps = 0;
            return string.Format("trucks={0} synced={1} unknownIds={2} maxErr={3:F2} over2m={4} jerk={6:F1} snaps={7} {8} worst={5}", list.Count, with, dropped, maxErr, over2, worst, j, sn, sn > 0 ? firstSnap : "");
        }

        public static string Path(Transform t)
        {
            var sb = new StringBuilder();
            for (; t != null; t = t.parent)
                sb.Insert(0, "/" + t.name + (t.parent != null ? "#" + t.GetSiblingIndex() : ""));
            return sb.ToString();
        }

        public static int Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in s) { h ^= ch; h *= 16777619; }
                return (int)h;
            }
        }
    }
}
