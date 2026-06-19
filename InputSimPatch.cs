using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Stellar.AutoFishing;

internal static class InputSimPatch
{
    internal static bool IsInstalled => _harmony != null;

    private static Harmony? _harmony;
    private static Action<string>? _log;

    private static readonly HashSet<int> _oneShots    = new();
    private static readonly HashSet<int> _heldButtons = new();
    private static readonly Dictionary<int, float> _axes = new();

    // Returns bitmask: 1=Rewired.Player patched, 2=ZInputManager patched
    internal static int Install(string harmonyId, Action<string> log)
    {
        _log     = log;
        _harmony = new Harmony(harmonyId + ".input");
        int patched = 0;

        // Path A — Rewired.Player (Rewired action IDs, e.g. FishingClick=143, Move_H=60)
        var playerType = FindType("Rewired.Player");
        if (playerType != null)
        {
            var gbd = playerType.GetMethod("GetButtonDown",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            var gb  = playerType.GetMethod("GetButton",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            var ga  = playerType.GetMethod("GetAxis",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (gbd != null && gb != null && ga != null)
            {
                _harmony.Patch(gbd, postfix: new HarmonyMethod(typeof(InputSimPatch), nameof(PostfixGetButtonDown)));
                _harmony.Patch(gb,  postfix: new HarmonyMethod(typeof(InputSimPatch), nameof(PostfixGetButton)));
                _harmony.Patch(ga,  postfix: new HarmonyMethod(typeof(InputSimPatch), nameof(PostfixGetAxis)));
                patched |= 1;
            }
        }

        // Path B — Panda.ZInput.ZInputManager (InputActionIds, e.g. FishingClick=92)
        var zimType = FindType("Panda.ZInput.ZInputManager");
        if (zimType != null)
        {
            var gbd = zimType.GetMethod("GetButtonDown",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            var gb  = zimType.GetMethod("GetButton",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            var ga  = zimType.GetMethod("GetAxis",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (gbd != null && gb != null && ga != null)
            {
                _harmony.Patch(gbd, postfix: new HarmonyMethod(typeof(InputSimPatch), nameof(PostfixGetButtonDown)));
                _harmony.Patch(gb,  postfix: new HarmonyMethod(typeof(InputSimPatch), nameof(PostfixGetButton)));
                _harmony.Patch(ga,  postfix: new HarmonyMethod(typeof(InputSimPatch), nameof(PostfixGetAxis)));
                patched |= 2;
            }
        }

        if (patched == 0) { _harmony.UnpatchSelf(); _harmony = null; }
        return patched;
    }

    internal static void Uninstall() { _harmony?.UnpatchSelf(); _harmony = null; ClearAll(); }

    // Fire a button for the current frame (cleared by ClearAll on next stage change).
    internal static void FireOnce(int actionId)  => _oneShots.Add(actionId);

    // Hold a button until stage changes (ClearAll).
    internal static void Hold(int actionId)      => _heldButtons.Add(actionId);
    internal static void Release(int actionId)   { _heldButtons.Remove(actionId); _oneShots.Remove(actionId); }

    internal static void SetAxis(int actionId, float value) => _axes[actionId] = value;
    internal static void ClearAxis(int actionId) => _axes.Remove(actionId);

    internal static void ClearAll() { _oneShots.Clear(); _heldButtons.Clear(); _axes.Clear(); }

    private static void PostfixGetButtonDown(int actionId, ref bool __result)
    {
        if (_oneShots.Remove(actionId))
        {
            _log?.Invoke($"[InputSim] GetButtonDown({actionId}) → injected");
            __result = true;
        }
    }

    private static void PostfixGetButton(int actionId, ref bool __result)
    {
        if (_heldButtons.Contains(actionId) || _oneShots.Contains(actionId))
            __result = true;
    }

    private static void PostfixGetAxis(int actionId, ref float __result)
    {
        if (_axes.TryGetValue(actionId, out var v)) __result = v;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t is not null) return t;
        }
        return null;
    }
}
