using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "AutoFishing";

    private readonly IPluginServices  _services;
    private readonly ILocalization   _loc;
    private readonly IConfigSection  _cfg;
    private readonly IWindowControl  _window;
    private readonly IDisposable     _launcherEntry;

    private FishingState _fishing;
    private int          _prevStage = -1;
    private int          _tickCount;

    // PlayerInputController.Fishing(bool) — ECS input signal
    private object?     _picInst;
    private MethodInfo? _picFishing;
    private bool        _fishingHeld;

    // UnityEngine.Time.deltaTime — cached for per-tick rod steering
    private PropertyInfo? _unityTimeDeltaTime;

    // Stage 0 (WaitRod) — show selector at 1s, auto-set first rod at 2.5s
    private float _rodSelectorTimer;
    private bool  _rodSelectorFired;
    private bool  _rodUseFired;

    // Stage 1 (Ready) delayed cast
    private float _castDelayTimer;
    private bool  _castFired;
    private bool  _rodEquipped;

    // Stage 13 (FinishShowLoop) auto-continue
    private float _finishDelayTimer;
    private bool  _finishClicked;

    private bool  _autoEnabled;
    private bool  _autoRodChange = true;

    // Monthly-card interrupt: pause automation, close the popup, restart after 1.5 s
    private bool  _monthlyCardInterrupt = true;
    private float _monthlyCheckAccum;
    private bool  _monthlyRestartPending;
    private float _monthlyRestartTimer;

    private float _tensionHigh = 25f;
    private float _tensionLow  = 8f;

    private int _fishCaught;
    private int _fishMissed;
    private int _rodChanged;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _cfg = _services.Config.GetSection("settings");
        _autoRodChange       = _cfg.Get<bool> ("auto_rod_change",        true);
        _monthlyCardInterrupt = _cfg.Get<bool> ("monthly_card_interrupt", true);
        _tensionHigh          = _cfg.Get<float>("tension_high",           25f);
        _tensionLow           = _cfg.Get<float>("tension_low",            8f);
        _services.Log.Info("[AutoFishing] constructed");

        // Help popover first, so the main window's "?" Funcs always have it (Plugin.Help.cs); then the main window
        // (Plugin.Window.cs).
        _helpWindow = RegisterHelpWindow();
        _window = RegisterMainWindow();
        _services.Framework.Update += OnUpdate;

        // Title stays the fixed literal "Auto Fishing" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Auto Fishing",
            IconPng: PluginIconPng(LauncherIconPx),   // same art as the header tile (Plugin.Style.Blocks.cs)
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("af.launcher.title"),
          // In-world tool: only surface the launcher tile in the World phase.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _services.ClientState.Login += OnLogin;

        if (FishingTickPatch.Install(_services.Harmony.Create(), OnFishingTick, _services.Log.Info))
            _services.Log.Info("[AutoFishing] PlayerFishingSystem.Tick hooked");
        else
            _services.Log.Warning("[AutoFishing] PlayerFishingSystem.Tick hook failed — will retry on login");
    }

    public void Dispose()
    {
        ReleaseFishing();
        FishingTickPatch.Uninstall();
        _services.ClientState.Login -= OnLogin;
        _services.Framework.Update -= OnUpdate;
        if (_tensionSavePending) { _tensionSavePending = false; SaveTension(); }   // flush a debounced slider save
        _launcherEntry.Dispose();
        _helpWindow.Remove();
        _window.Remove();
    }

    private void OnLogin()
    {
        _services.ClientState.Login -= OnLogin;

        if (!FishingTickPatch.IsInstalled)
        {
            if (FishingTickPatch.Install(_services.Harmony.Create(), OnFishingTick, _services.Log.Info))
                _services.Log.Info("[AutoFishing] PlayerFishingSystem.Tick hooked (deferred)");
            else
                _services.Log.Warning("[AutoFishing] PlayerFishingSystem.Tick hook failed");
        }
    }

    private string StageLabel(int stage) => stage switch
    {
        0  => _loc.T("af.stage.equipping"),
        1  => _loc.T("af.stage.ready"),
        2  => _loc.T("af.stage.casting"),
        3  => _loc.T("af.stage.waiting"),
        4  => _loc.T("af.stage.hooking"),
        5  => _loc.T("af.stage.harvesting"),
        6  => _loc.T("af.stage.missed"),
        7  => _loc.T("af.stage.cancelled"),
        8  => _loc.T("af.stage.fishOn"),
        9  => _loc.T("af.stage.reeling"),
        10 => _loc.T("af.stage.reeling"),
        11 => _loc.T("af.stage.qteDone"),
        12 => _loc.T("af.stage.preview"),
        13 => _loc.T("af.stage.caught"),
        14 => _loc.T("af.stage.wrapping"),
        15 => _loc.T("af.stage.done"),
        _  => _loc.TFormat("af.stage.unknown", stage),
    };

    private void SaveTension()
    {
        _cfg.Set<float>("tension_high", _tensionHigh);
        _cfg.Set<float>("tension_low",  _tensionLow);
        _cfg.Save();
    }
}
