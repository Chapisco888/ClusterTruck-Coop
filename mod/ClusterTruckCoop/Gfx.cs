using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ClusterTruckCoop
{
    // Graphics presets. The game ships a single quality level ("Fantastic"), so lighter ones are made here by
    // switching off image effects and shortening shadows. "Original" never touches anything.
    static class Gfx
    {
        public static int level;
        public static readonly string[] names = { "Original", "Medio", "Baixo", "Minimo" };

        // image effects each preset switches off, by component name (a preset includes the ones above it)
        static readonly string[][] off =
        {
            new string[0],
            new[] { "SESSAO", "VelocityBuffer", "FrustumJitter", "TemporalReprojection" },
            new[] { "SENaturalBloomAndDirtyLens", "LerpableBlur", "VignetteAndChromaticAberration" },
            new string[0], // Minimo = Baixo without shadows; switching ColorCorrectionCurves off too left the screen plain white
        };

        static readonly List<Behaviour> disabled = new List<Behaviour>(); // what we turned off, to give back on a higher preset
        static bool touched, saved;
        static int oCascades, oPixel;

        static AnisotropicFiltering oAniso;

        public static void Set(int l)
        {
            l = Mathf.Clamp(l, 0, names.Length - 1);
            if (l < level)
            {
                foreach (var b in disabled) if (b != null) b.enabled = true;
                disabled.Clear();
            }
            level = l;
            if (l > 0) touched = true;
            Quality();
            Enforce();
        }

        public static void Quality()
        {
            if (!touched) return;
            if (!saved)
            {
                saved = true;
                oCascades = QualitySettings.shadowCascades; oPixel = QualitySettings.pixelLightCount; oAniso = QualitySettings.anisotropicFiltering;
            }
            int d = options.shadowD; // the game's own slider stays the upper limit
            QualitySettings.shadowDistance = level == 0 ? d : level == 1 ? Mathf.Min(d, 100) : level == 2 ? Mathf.Min(d, 50) : 0;
            QualitySettings.shadowCascades = level == 0 ? oCascades : level == 1 ? 2 : 1;
            QualitySettings.pixelLightCount = level >= 2 ? 1 : oPixel;
            QualitySettings.anisotropicFiltering = level >= 2 ? AnisotropicFiltering.Disable : oAniso;
        }

        // Called twice a second: cameras come and go with the levels, and the game re-enables effects when one spawns.
        public static void Enforce()
        {
            if (level == 0) return;
            if (disabled.Count > 32) disabled.RemoveAll(x => x == null);
            foreach (var cam in Camera.allCameras)
                foreach (var b in cam.GetComponents<Behaviour>())
                    if (b.enabled && Kills(b.GetType().Name))
                    {
                        b.enabled = false;
                        if (!disabled.Contains(b)) disabled.Add(b);
                    }
        }

        static bool Kills(string name)
        {
            for (int l = 1; l <= level; l++)
                if (System.Array.IndexOf(off[l], name) >= 0) return true;
            return false;
        }

        // QA: what the level actually renders with, to know what a preset can turn off
        public static string Dump()
        {
            var sb = new StringBuilder("GFX preset=" + names[level] + " shadowDist=" + QualitySettings.shadowDistance + " cascades=" + QualitySettings.shadowCascades
                + " pixelLights=" + QualitySettings.pixelLightCount + " vsync=" + QualitySettings.vSyncCount + " res=" + Screen.width + "x" + Screen.height);
            foreach (var cam in Camera.allCameras)
            {
                sb.Append(" || cam " + cam.name + " hdr=" + cam.hdr + ":");
                foreach (var b in cam.GetComponents<Behaviour>()) sb.Append(" " + b.GetType().Name + (b.enabled ? "+" : "-"));
            }
            return sb.ToString();
        }
    }
}

namespace ClusterTruckCoop
{
    // QA: the game's uGUI main menu tree, to know where a new entry can be cloned from
    static class MenuDump
    {
        public static string Tree(UnityEngine.Transform t, int depth)
        {
            var sb = new System.Text.StringBuilder("\n" + new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : " (off)"));
            var rt = t as UnityEngine.RectTransform;
            if (rt != null) sb.Append(" pos=" + rt.anchoredPosition + " size=" + rt.sizeDelta);
            foreach (var c in t.GetComponents<UnityEngine.Component>())
            {
                if (c is UnityEngine.Transform) continue;
                sb.Append(" [" + c.GetType().Name);
                var tx = c as UnityEngine.UI.Text;
                if (tx != null) sb.Append(" '" + tx.text + "' font=" + (tx.font != null ? tx.font.name : "-") + " size=" + tx.fontSize);
                var b = c as UnityEngine.UI.Button;
                if (b != null) for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++) sb.Append(" ->" + b.onClick.GetPersistentTarget(i) + "." + b.onClick.GetPersistentMethodName(i));
                var et = c as UnityEngine.EventSystems.EventTrigger;
                if (et != null && et.triggers != null) foreach (var e in et.triggers) for (int i = 0; i < e.callback.GetPersistentEventCount(); i++) sb.Append(" " + e.eventID + "->" + e.callback.GetPersistentTarget(i) + "." + e.callback.GetPersistentMethodName(i));
                sb.Append("]");
            }
            if (depth < 4) foreach (UnityEngine.Transform ch in t) sb.Append(Tree(ch, depth + 1));
            return sb.ToString();
        }
    }
}
