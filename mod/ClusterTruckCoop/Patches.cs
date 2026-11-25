using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ClusterTruckCoop
{
    [HarmonyPatch(typeof(car), "Start")]
    static class CarStartPatch
    {
        static void Postfix(car __instance) => Trucks.Register(__instance);
    }

    [HarmonyPatch(typeof(Manager), "ClearLevel")]
    static class ClearLevelPatch
    {
        static void Postfix() => Trucks.Clear();
    }

    // Every campaign (re)start funnels through here: host announces it, client only runs it when told to.
    [HarmonyPatch(typeof(Manager), "StartLevel", new[] { typeof(int), typeof(bool), typeof(LeaderboardGhostHandler.GhostMethod), typeof(bool) })]
    static class StartLevelPatch
    {
        static bool Prefix() => !CoopPlugin.ClientLocked;

        static void Postfix(int i)
        {
            if (CoopPlugin.mode == CoopPlugin.Mode.Host) CoopPlugin.OnHostLevelStarted(i);
        }
    }

    // Client may not change level by itself (menu, restart, next level): the host decides.
    [HarmonyPatch]
    static class ClientLevelLockPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Manager), "Play", Type.EmptyTypes);
            yield return AccessTools.Method(typeof(Manager), "Play", new[] { typeof(CustomMapInfo) });
            yield return AccessTools.Method(typeof(Manager), "NewGame");
        }

        static bool Prefix() => !CoopPlugin.ClientLocked;
    }

    // Race rule: nobody moves on until every player in the level reached the goal.
    [HarmonyPatch]
    static class NextLevelPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Manager), "NextLevel");
            yield return AccessTools.Method(typeof(GameManager), "NextLevel");
        }

        static bool Prefix() => !CoopPlugin.ClientLocked && !CoopPlugin.PartnerBlocksNext;
    }

    // rysshax=true is the game's own reload right after spawning the player (part of starting a level);
    // the client must run it like the host does, otherwise its player spawns off the start truck.
    [HarmonyPatch(typeof(Manager), "RestartLevel")]
    static class ManagerRestartPatch
    {
        static bool Prefix(bool rysshax, out bool __state)
        {
            // The Steam stats callback can rewrite info.currentLevel between the start and this reload; keep the chosen level.
            if (CoopPlugin.mode == CoopPlugin.Mode.Host && rysshax) info.currentLevel = CoopPlugin.HostLevel;
            __state = CoopPlugin.mode == CoopPlugin.Mode.Client && rysshax && !CoopPlugin.applyingRemote;
            if (__state) CoopPlugin.applyingRemote = true;
            return !CoopPlugin.ClientLocked;
        }

        static void Postfix(bool __state) { if (__state) CoopPlugin.applyingRemote = false; }
    }

    [HarmonyPatch(typeof(GameManager), "RestartLevel")]
    static class RestartPatch
    {
        static bool Prefix(GameManager __instance)
        {
            bool hostWaits = CoopPlugin.mode == CoopPlugin.Mode.Host && Gm.IsDone(__instance) && !__instance.won && CoopPlugin.PartnerAlive;
            if (!CoopPlugin.ClientLocked && !hostWaits) return true;
            Gm.AllowAnotherPress(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "ResetMe")]
    static class ResetPatch
    {
        static void Postfix() => CoopPlugin.resetEpoch = CoopPlugin.epoch;
    }

    [HarmonyPatch(typeof(GameManager), "WinLevel")]
    static class WinPatch
    {
        static void Postfix() { if (CoopPlugin.I != null && CoopPlugin.mode != CoopPlugin.Mode.Off) CoopPlugin.I.Qa("EVENT vitoria local"); }
    }

    [HarmonyPatch(typeof(GameManager), "LoseLevel")]
    static class LosePatch
    {
        static void Prefix(GameManager __instance)
        {
            if (CoopPlugin.I == null || CoopPlugin.mode == CoopPlugin.Mode.Off || Gm.IsDone(__instance)) return;
            var p = __instance.GetComponentInChildren<player>();
            var rig = p != null ? p.GetComponent<Rigidbody>() : null;
            CoopPlugin.I.Qa("EVENT morte local pos=" + (rig != null ? rig.position + " vel=" + rig.velocity : "-") + " via " + new System.Diagnostics.StackTrace(2, false));
        }
    }

    // The game re-enables bloom/blur every frame from its own options; presets "Baixo" and below keep them off.
    [HarmonyPatch(typeof(cameraEffects), "Update")]
    static class GfxBloomPatch
    {
        static void Postfix(cameraEffects __instance)
        {
            if (Gfx.level < 2) return;
            if (__instance.myBloom != null && __instance.myBloom.enabled) __instance.myBloom.enabled = false;
            if (__instance.blur != null && __instance.blur.enabled) __instance.blur.enabled = false;
        }
    }

    // The game's options menu rewrites the shadow distance: put the preset back on top.
    [HarmonyPatch(typeof(options), "Apply")]
    static class GfxOptionsPatch
    {
        static void Postfix() { Gfx.Quality(); }
    }

    // Walls and props: the host's trucks break them; client trucks are kinematic and pass through without breaking anything,
    // so every break on the host is sent and replayed on the clients.
    [HarmonyPatch]
    static class BreakPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(destructive), "Explode");
            yield return AccessTools.Method(typeof(prop), "Explode");
        }

        static void Prefix(Component __instance, Vector3 pos) => Breakables.OnExplode(__instance, pos);
    }
}

namespace ClusterTruckCoop
{
    // Everyone opens the game with at least 50k points for the ability shop (spent points come back on the next launch).
    // Steam stats arrive later and overwrite the points with the stored value, so top up again after that.
    [HarmonyPatch(typeof(SteamStatsAndAchievements), "OnUserStatsReceived")]
    static class StartPoints
    {
        const float Amount = 50000f;

        public static void TopUp()
        {
            if (PlayerPrefs.GetFloat("points", 0f) < Amount) PlayerPrefs.SetFloat("points", Amount); // read by pointsHandler.Start
            if (pointsHandler.Points > 0f && pointsHandler.Points < Amount) pointsHandler.AddPoints(Amount - pointsHandler.Points); // already loaded
        }

        static void Postfix()
        {
            if (pointsHandler.Points < Amount) pointsHandler.AddPoints(Amount - pointsHandler.Points);
            Debug.Log("[ClusterTruckCoop] pontos da loja apos stats Steam: " + pointsHandler.Points);
        }
    }
}
