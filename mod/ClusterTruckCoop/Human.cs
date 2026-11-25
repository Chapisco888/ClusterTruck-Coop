using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ClusterTruckCoop
{
    // The other players' body: Quaternius "Animated Human" (CC0), baked by assets/export_human.py into vertex positions
    // per animation frame (this Unity can't load a skinned FBX at runtime). Missing file -> the old capsule is used.
    static class HumanModel
    {
        public const int Bones = 2; // 0 head, 1 chest: where accessories hang

        public class Clip
        {
            public float fps;
            public Vector3[][] frames;
            public Vector3[][] bonePos;     // [frame][bone], puppet space
            public Quaternion[][] boneRot;  // [frame][bone], relative to the rest pose (Idle frame 0)
        }

        public static bool ok;
        public static int nVerts;
        public static int[] vidx;      // per triangle corner: which vertex
        public static Vector2[] uv;    // per triangle corner
        public static readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();

        const float Height = 1.9f, FeetY = -1f; // the puppet's origin is the player's centre, 1 m above the feet

        public static void Load()
        {
            try
            {
                string dir = Path.Combine(Application.dataPath, "Managed");
                using (var r = new BinaryReader(File.OpenRead(Path.Combine(dir, "human.bin"))))
                {
                    if (new string(r.ReadChars(4)) != "CTH2") throw new Exception("formato desconhecido");
                    nVerts = r.ReadInt32();
                    int nLoops = r.ReadInt32();
                    vidx = new int[nLoops];
                    uv = new Vector2[nLoops];
                    for (int i = 0; i < nLoops; i++) { vidx[i] = r.ReadInt32(); uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle()); }
                    // the export's triangles come out counter-clockwise; Unity wants clockwise or the body renders inside out
                    for (int i = 0; i < nLoops; i += 3)
                    {
                        int t = vidx[i + 1]; vidx[i + 1] = vidx[i + 2]; vidx[i + 2] = t;
                        Vector2 u = uv[i + 1]; uv[i + 1] = uv[i + 2]; uv[i + 2] = u;
                    }
                    int nClips = r.ReadInt32();
                    for (int c = 0; c < nClips; c++)
                    {
                        string name = new string(r.ReadChars(r.ReadByte()));
                        float fps = r.ReadSingle();
                        int n = r.ReadInt32();
                        var clip = new Clip { fps = fps, frames = new Vector3[n][], bonePos = new Vector3[n][], boneRot = new Quaternion[n][] };
                        for (int f = 0; f < n; f++)
                        {
                            var v = clip.frames[f] = new Vector3[nVerts];
                            for (int i = 0; i < nVerts; i++) v[i] = V(r);
                            clip.bonePos[f] = new Vector3[Bones];
                            clip.boneRot[f] = new Quaternion[Bones];
                            for (int b = 0; b < Bones; b++)
                            {
                                clip.bonePos[f][b] = V(r);
                                Vector3 y = V(r), z = V(r);
                                clip.boneRot[f][b] = Quaternion.LookRotation(z, y);
                            }
                        }
                        clips[name] = clip;
                    }
                }
                // scale to player height, feet where the player's feet are; bone rotations relative to the rest pose
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var v in clips["Idle"].frames[0]) { lo = Mathf.Min(lo, v.y); hi = Mathf.Max(hi, v.y); }
                float s = Height / (hi - lo);
                var rest = (Quaternion[])clips["Idle"].boneRot[0].Clone();
                foreach (var c in clips.Values)
                    for (int f = 0; f < c.frames.Length; f++)
                    {
                        var fr = c.frames[f];
                        for (int i = 0; i < nVerts; i++) fr[i] = new Vector3(fr[i].x * s, (fr[i].y - lo) * s + FeetY, fr[i].z * s);
                        for (int b = 0; b < Bones; b++)
                        {
                            Vector3 p = c.bonePos[f][b];
                            c.bonePos[f][b] = new Vector3(p.x * s, (p.y - lo) * s + FeetY, p.z * s);
                            c.boneRot[f][b] = c.boneRot[f][b] * Quaternion.Inverse(rest[b]);
                        }
                    }
                ok = clips.ContainsKey("Walk") && clips.ContainsKey("Run") && clips.ContainsKey("Jump") && clips.ContainsKey("Death");
            }
            catch (Exception e) { Debug.Log("[ClusterTruckCoop] personagem nao carregou, usando capsula: " + e.Message); ok = false; }
        }

        static Vector3 V(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    // One player's animated mesh. anim: 0 idle, 1 walk, 2 run, 3 in the air (as sent by that player's own game).
    class HumanAnim
    {
        public readonly Mesh mesh = new Mesh();
        public readonly Vector3[] bonePos = new Vector3[HumanModel.Bones];
        public readonly Quaternion[] boneRot = new Quaternion[HumanModel.Bones];
        readonly Vector3[] pose = new Vector3[HumanModel.nVerts];
        readonly Vector3[] verts = new Vector3[HumanModel.vidx.Length], normals = new Vector3[HumanModel.vidx.Length];
        string cur = "Idle", prev;
        float t, tPrev, fade = 1f;
        const float FadeTime = 0.15f;

        public HumanAnim()
        {
            mesh.MarkDynamic();
            Tick(0, false, 0f);
            mesh.uv = HumanModel.uv;
            var tris = new int[verts.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = i;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 5f); // fixed: no per-frame bounds pass
        }

        static bool Loops(string clip) => clip != "Jump" && clip != "Death";

        public void Tick(int anim, bool dead, float dt)
        {
            string want = dead ? "Death" : anim == 3 ? "Jump" : anim == 2 ? "Run" : anim == 1 ? "Walk" : "Idle";
            if (want != cur) { prev = cur; tPrev = t; cur = want; t = 0f; fade = 0f; }
            t += dt;
            fade = Mathf.Min(1f, fade + dt / FadeTime);
            Sample(cur, t, fade, true);
            if (fade < 1f && prev != null) Sample(prev, tPrev, 1f - fade, false);

            var vi = HumanModel.vidx;
            for (int i = 0; i < verts.Length; i += 3)
            {
                Vector3 a = verts[i] = pose[vi[i]], b = verts[i + 1] = pose[vi[i + 1]], c = verts[i + 2] = pose[vi[i + 2]];
                normals[i] = normals[i + 1] = normals[i + 2] = Vector3.Cross(b - a, c - a).normalized; // flat shading, like the game
            }
            mesh.vertices = verts;
            mesh.normals = normals;
        }

        void Sample(string name, float time, float weight, bool first)
        {
            var c = HumanModel.clips[name];
            int n = c.frames.Length;
            float f = time * c.fps;
            // in the air: hold the pose in the middle of the jump instead of playing the landing
            f = Loops(name) ? f % n : Mathf.Min(f, name == "Jump" ? n * 0.5f : n - 1);
            int i0 = (int)f, i1 = Loops(name) ? (i0 + 1) % n : Mathf.Min(i0 + 1, n - 1);
            float k = f - i0;
            Vector3[] a = c.frames[i0], b = c.frames[i1];
            for (int i = 0; i < pose.Length; i++)
            {
                Vector3 p = (a[i] + (b[i] - a[i]) * k) * weight;
                pose[i] = first ? p : pose[i] + p;
            }
            for (int i = 0; i < HumanModel.Bones; i++)
            {
                Vector3 p = Vector3.Lerp(c.bonePos[i0][i], c.bonePos[i1][i], k) * weight;
                Quaternion q = Quaternion.Slerp(c.boneRot[i0][i], c.boneRot[i1][i], k);
                bonePos[i] = first ? p : bonePos[i] + p;
                boneRot[i] = first ? q : Quaternion.Slerp(boneRot[i], q, weight);
            }
        }
    }
}
