using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ClusterTruckCoop
{
    // End-of-level ranking: fastest finisher first, with the game's own points. Session totals live in memory;
    // every finished level is also appended to ClusterTruckCoop_ranking.csv next to the game exe.
    static class Race
    {
        public class Entry { public object player; public int slot; public string name; public float time, points; }

        class Total { public string label; public int wins; public float points, time; public int levels; }

        static int rankedEpoch = -1;
        static int rankedLevel;
        static readonly List<Entry> current = new List<Entry>();
        static readonly Dictionary<object, Total> totals = new Dictionary<object, Total>();

        public static string FormatTime(float t) => t.ToString("F3").Replace('.', ':');

        public static void Reset()
        {
            rankedEpoch = -1;
            current.Clear();
            totals.Clear();
        }

        // Called every frame while everybody has finished; records the level once per epoch.
        public static void Finish(int epoch, int level, List<Entry> entries)
        {
            if (!Record(epoch, level, entries)) return;
            if (CoopPlugin.I != null) CoopPlugin.I.Qa("EVENT ranking fase " + level + ": " + string.Join(" | ", current.ConvertAll(e => e.name + " " + FormatTime(e.time) + " " + e.points.ToString("F0") + "pts").ToArray()));
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ClusterTruckCoop_ranking.csv");
                bool header = !File.Exists(path);
                using (var f = new StreamWriter(path, true))
                {
                    if (header) f.WriteLine("data,fase,posicao,jogador,tempo_s,pontos");
                    for (int i = 0; i < current.Count; i++)
                        f.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4:F3},{5:F0}",
                            DateTime.Now, level, i + 1, current[i].name.Replace(",", " "), current[i].time, current[i].points));
                }
            }
            catch (Exception e) { Debug.Log("[ClusterTruckCoop] nao salvou ranking: " + e.Message); }
        }

        internal static bool Record(int epoch, int level, List<Entry> entries)
        {
            current.Clear();
            current.AddRange(entries);
            current.Sort((a, b) => a.time.CompareTo(b.time));
            if (rankedEpoch == epoch) return false; // points may still tick up on the score screen; totals use the first snapshot
            rankedEpoch = epoch;
            rankedLevel = level;
            for (int i = 0; i < current.Count; i++)
            {
                var e = current[i];
                Total t;
                if (!totals.TryGetValue(e.player, out t)) totals[e.player] = t = new Total();
                t.label = e.name + " (#" + (e.slot + 1) + ")";
                if (i == 0) t.wins++;
                t.points += e.points;
                t.time += e.time;
                t.levels++;
            }
            return true;
        }

        static GUIStyle big, row;

        public static void DrawRanking(bool isHost)
        {
            if (current.Count == 0) return;
            if (big == null)
            {
                big = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                row = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            }
            float w = 520f, h = 150f + 28f * (current.Count + totals.Count);
            var area = new Rect((Screen.width - w) / 2f, Screen.height * 0.18f, w, h);
            CoopPlugin.Panel(area);
            GUILayout.BeginArea(new Rect(area.x + 16, area.y + 10, w - 32, h - 20));
            GUILayout.Label("RANKING - FASE " + rankedLevel, big);
            for (int i = 0; i < current.Count; i++)
            {
                var e = current[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1) + "o  " + e.name, row, GUILayout.Width(220));
                GUILayout.Label(FormatTime(e.time) + " s", row, GUILayout.Width(130));
                GUILayout.Label(e.points.ToString("F0") + " pts", row);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
            GUILayout.Label("Sessao (vitorias / pontos / tempo total):", row);
            foreach (var kv in totals)
                GUILayout.Label("   " + kv.Value.label + ":  " + kv.Value.wins + " vit.  /  " + kv.Value.points.ToString("F0") + " pts  /  " + FormatTime(kv.Value.time) + " s", row);
            GUILayout.Space(6);
            GUILayout.Label(isHost ? "Use o botao de proxima fase para continuar" : "Aguardando o host avancar", row);
            GUILayout.EndArea();
        }
    }
}
