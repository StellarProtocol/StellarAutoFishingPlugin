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

        _window = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "auto-fishing.fishing",
                Title:       _loc.T("af.window.title"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 300f, 20f, 280f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Gameplay tool: draw only while in-world (fishing happens in the World phase), hidden during loading screens.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: new ColumnElement(new HudElement[]
            {
                new ConditionalElement(
                    () => _autoEnabled,
                    new ButtonElement(
                        Label:   () => _loc.T("af.stop"),
                        OnClick: ToggleAuto,
                        Style:   MenuButtonStyle.Filled,
                        Width:   260f)),
                new ConditionalElement(
                    () => !_autoEnabled,
                    new ButtonElement(
                        Label:   () => _loc.T("af.start"),
                        OnClick: ToggleAuto,
                        Enabled: () => _fishing.IsFishing,
                        Width:   260f)),
                new TextElement(
                    () => _fishing.IsFishing ? " " : _loc.T("af.enterSpot"),
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new SeparatorElement(),
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => _loc.T("af.status"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 65f),
                    new CellElement(new TextElement(
                        () => _autoEnabled ? StageLabel(_fishing.Stage) : _loc.T("af.stopped"),
                        Emphasis: true,
                        FontSize: 14,
                        Color: () => _autoEnabled
                            ? new ColorRgba(0.2f, 0.85f, 0.2f, 1f)
                            : new ColorRgba(0.9f, 0.2f, 0.2f, 1f)), Weight: 1f),
                    new SpacerElement(Height: 24f),
                }, Gap: 8f),
                new SeparatorElement(),
                new TextElement(() => _loc.T("af.settings"), Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(
                        Label: () => "",
                        Get:   () => _autoRodChange,
                        Set:   v  => { _autoRodChange = v; _cfg.Set<bool>("auto_rod_change", v); _cfg.Save(); }),
                    new TextElement(() => _loc.T("af.autoRod")),
                }, Gap: 6f),
                new TextElement(
                    () => _loc.T("af.autoRod.hint"),
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(
                        Label: () => "",
                        Get:   () => _monthlyCardInterrupt,
                        Set:   v  => { _monthlyCardInterrupt = v; _cfg.Set<bool>("monthly_card_interrupt", v); _cfg.Save(); }),
                    new TextElement(() => _loc.T("af.monthlyCard")),
                }, Gap: 6f),
                new TextElement(
                    () => _loc.T("af.monthlyCard.hint"),
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new SeparatorElement(),
                new TextElement(() => _loc.T("af.tension"), Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => _loc.T("af.tension.release"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
                    new CellElement(new SliderElement(
                        Get: () => _tensionHigh,
                        Set: v => { _tensionHigh = MathF.Max(v, _tensionLow + 1f); SaveTension(); },
                        Min: 0f, Max: 100f), Weight: 1f),
                    new CellElement(new TextElement(() => $"{_tensionHigh:F0}%"), Width: 36f),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => _loc.T("af.tension.pull"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
                    new CellElement(new SliderElement(
                        Get: () => _tensionLow,
                        Set: v => { _tensionLow = MathF.Min(v, _tensionHigh - 1f); SaveTension(); },
                        Min: 0f, Max: 100f), Weight: 1f),
                    new CellElement(new TextElement(() => $"{_tensionLow:F0}%"), Width: 36f),
                }, Gap: 6f),
                new SeparatorElement(),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("af.caught"),     Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_fishCaught}"),
                }, Gap: 8f),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("af.missed"),     Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_fishMissed}"),
                }, Gap: 8f),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("af.rodChange"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_rodChanged}"),
                }, Gap: 8f),
            }, Gap: 8f),
            OnClose: () => { if (_autoEnabled) ToggleAuto(); _window!.SetVisible(false); }));

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   _loc.T("af.launcher.title"),
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile title live on a language change (Title alone is a captured string).
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
        _launcherEntry.Dispose();
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

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.AutoFishing.fishing-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
