using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

internal static class FishingTickPatch
{
    private const string TargetType = "Panda.ZGame.PlayerFishingSystem";

    internal static bool    IsInstalled    => _installed;
    internal static object? LastInstance   { get; private set; }
    internal static float   LastRodTension { get; private set; }
    internal static int     LastFishZone   { get; set; } = 2; // E.FishingDirection.Middle=2

    internal static void ResetTension() => LastRodTension = 0f;

    private static bool                  _installed;
    private static Action<FishingState>? _onTick;
    private static Action<string>?       _onLog;

    internal static bool Install(Harmony harmony, Action<FishingState> onTick, Action<string> onLog)
    {
        _onLog = onLog;

        var type = StellarInterop.FindType(TargetType);
        if (type is null) { onLog("[FishingTickPatch] PlayerFishingSystem type not found"); return false; }

        var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
        if (tick is null) { onLog("[FishingTickPatch] Tick method not found"); return false; }

        _onTick  = onTick;
        harmony.Patch(tick, postfix: new HarmonyMethod(typeof(FishingTickPatch), nameof(Postfix)));

        // Matched by parameter TYPE (float), not just arity — kept as a type-constrained GetMethod
        // rather than StellarInterop.FindMethod (which matches name + count only).
        var tensionMethod = type.GetMethod("FishingRodTensionChange",
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(float) }, null);
        if (tensionMethod != null)
            harmony.Patch(tensionMethod, postfix: new HarmonyMethod(typeof(FishingTickPatch), nameof(TensionPostfix)));
        else
            onLog("[FishingTickPatch] FishingRodTensionChange not found (tension unavailable)");

        _installed = true;
        onLog("[FishingTickPatch] installed");
        return true;
    }

    internal static void Uninstall()
    {
        // Harmony teardown is owned by IHarmonyHost, which auto-unpatches every instance on plugin
        // dispose — so we must NOT unpatch here (that would double-unpatch). Only reset transient state.
        _installed = false;
        _onTick    = null;
        _onLog     = null;
    }

    private static void Postfix(Il2CppObjectBase __instance)
    {
        try
        {
            LastInstance = __instance;
            var fs = PlayerFishingProxy.From(__instance);
            if (fs is null) return;

            _onTick?.Invoke(new FishingState
            {
                IsFishing  = fs.Value.IsFishing,
                Stage      = fs.Value.CurState,
                RodTension = LastRodTension,
            });
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"[FishingTickPatch] Postfix error: {ex.Message}");
        }
    }

    private static void TensionPostfix(float tension)
    {
        LastRodTension = tension;
    }
}

internal readonly struct FishingState
{
    internal bool  IsFishing  { get; init; }
    internal int   Stage      { get; init; }
    internal float RodTension { get; init; }

    internal static string StageName(int stage) => stage switch
    {
        0  => "WaitRod",
        1  => "Ready",
        2  => "Casting",
        3  => "CastingWait",
        4  => "BiteHook",
        5  => "WaitHarvesting",
        6  => "Miss",
        7  => "Cancel",
        8  => "Hit",
        9  => "Control",
        10 => "PullUp",
        11 => "QteEnd",
        12 => "FinishShowStart",
        13 => "FinishShowLoop",
        14 => "FinishShowEnd",
        15 => "Leave",
        _  => $"Unknown({stage})",
    };
}
