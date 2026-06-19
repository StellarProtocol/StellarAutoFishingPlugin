using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.AutoFishing;

internal static class FishingTickPatch
{
    private const string TargetType = "Panda.ZGame.PlayerFishingSystem";

    internal static bool    IsInstalled    => _harmony != null;
    internal static object? LastInstance   { get; private set; }
    internal static float   LastRodTension { get; private set; }
    internal static int     LastFishZone   { get; set; } = 2; // E.FishingDirection.Middle=2

    internal static void ResetTension() => LastRodTension = 0f;

    private static Harmony?              _harmony;
    private static Action<FishingState>? _onTick;
    private static Action<string>?       _onLog;

    internal static bool Install(string harmonyId, Action<FishingState> onTick, Action<string> onLog)
    {
        _onLog = onLog;

        var type = FindType(TargetType);
        if (type is null) { onLog("[FishingTickPatch] PlayerFishingSystem type not found"); return false; }

        var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
        if (tick is null) { onLog("[FishingTickPatch] Tick method not found"); return false; }

        _onTick  = onTick;
        _harmony = new Harmony(harmonyId);
        _harmony.Patch(tick, postfix: new HarmonyMethod(typeof(FishingTickPatch), nameof(Postfix)));

        var tensionMethod = type.GetMethod("FishingRodTensionChange",
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(float) }, null);
        if (tensionMethod != null)
            _harmony.Patch(tensionMethod, postfix: new HarmonyMethod(typeof(FishingTickPatch), nameof(TensionPostfix)));
        else
            onLog("[FishingTickPatch] FishingRodTensionChange not found (tension unavailable)");

        onLog("[FishingTickPatch] installed");
        return true;
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        _onTick  = null;
        _onLog   = null;
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
