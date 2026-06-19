using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "AutoFishing";

    private const string HarmonyId = "stellar.auto-fishing-plugin";

    private readonly IPluginServices  _services;
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

    // Lua direct call — LuaState.DoString(chunk)
    private object?     _luaState;
    private MethodInfo? _luaDoString;

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

    // Offset of m_value in IL2CPP boxed primitive (resolved once from klass)
    private int _luaBoxedValueOff = -1;

    private bool _autoEnabled;
    private bool _autoRodChange = true;

    private float _tensionHigh = 25f;
    private float _tensionLow  = 8f;

    private int _fishCaught;
    private int _fishMissed;
    private int _rodChanged;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _cfg = _services.Config.GetSection("settings");
        _autoRodChange = _cfg.Get<bool> ("auto_rod_change", true);
        _tensionHigh   = _cfg.Get<float>("tension_high",    25f);
        _tensionLow    = _cfg.Get<float>("tension_low",     8f);
        _services.Log.Info("[AutoFishing] constructed");

        _window = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "auto-fishing.fishing",
                Title:       "Fishing Automation",
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 300f, 20f, 280f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false },
            Root: new ColumnElement(new HudElement[]
            {
                new ConditionalElement(
                    () => _autoEnabled,
                    new ButtonElement(
                        Label:   () => "Stop",
                        OnClick: ToggleAuto,
                        Style:   MenuButtonStyle.Filled,
                        Width:   260f)),
                new ConditionalElement(
                    () => !_autoEnabled,
                    new ButtonElement(
                        Label:   () => "Start",
                        OnClick: ToggleAuto,
                        Enabled: () => _fishing.IsFishing,
                        Width:   260f)),
                new TextElement(
                    () => _fishing.IsFishing ? " " : "Enter a fishing spot to start.",
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new SeparatorElement(),
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => "Status", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 65f),
                    new CellElement(new TextElement(
                        () => _autoEnabled ? StageLabel(_fishing.Stage) : "Stopped",
                        Emphasis: true,
                        FontSize: 14,
                        Color: () => _autoEnabled
                            ? new ColorRgba(0.2f, 0.85f, 0.2f, 1f)
                            : new ColorRgba(0.9f, 0.2f, 0.2f, 1f)), Weight: 1f),
                    new SpacerElement(Height: 24f),
                }, Gap: 8f),
                new SeparatorElement(),
                new TextElement(() => "Settings", Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(
                        Label: () => "",
                        Get:   () => _autoRodChange,
                        Set:   v  => { _autoRodChange = v; _cfg.Set<bool>("auto_rod_change", v); _cfg.Save(); }),
                    new TextElement(() => "Auto Rod Change"),
                }, Gap: 6f),
                new TextElement(
                    () => "Auto-equip the best rod from bag. If off, automation waits until you equip one manually.",
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new SeparatorElement(),
                new TextElement(() => "Tension Control", Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => "Release at", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
                    new CellElement(new SliderElement(
                        Get: () => _tensionHigh,
                        Set: v => { _tensionHigh = MathF.Max(v, _tensionLow + 1f); SaveTension(); },
                        Min: 0f, Max: 100f), Weight: 1f),
                    new CellElement(new TextElement(() => $"{_tensionHigh:F0}%"), Width: 36f),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => "Pull at", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
                    new CellElement(new SliderElement(
                        Get: () => _tensionLow,
                        Set: v => { _tensionLow = MathF.Min(v, _tensionHigh - 1f); SaveTension(); },
                        Min: 0f, Max: 100f), Weight: 1f),
                    new CellElement(new TextElement(() => $"{_tensionLow:F0}%"), Width: 36f),
                }, Gap: 6f),
                new SeparatorElement(),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "Caught",     Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_fishCaught}"),
                }, Gap: 8f),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "Missed",     Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_fishMissed}"),
                }, Gap: 8f),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "Rod Change", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_rodChanged}"),
                }, Gap: 8f),
            }, Gap: 8f),
            OnClose: () => { if (_autoEnabled) ToggleAuto(); _window!.SetVisible(false); }));

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Auto Fishing",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin });

        InputSimPatch.Install(HarmonyId, _services.Log.Info);

        _services.ClientState.Login += OnLogin;

        if (FishingTickPatch.Install(HarmonyId, OnFishingTick, _services.Log.Info))
            _services.Log.Info("[AutoFishing] PlayerFishingSystem.Tick hooked");
        else
            _services.Log.Warning("[AutoFishing] PlayerFishingSystem.Tick hook failed — will retry on login");
    }

    public void Dispose()
    {
        ReleaseFishing();
        InputSimPatch.Uninstall();
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
            if (FishingTickPatch.Install(HarmonyId, OnFishingTick, _services.Log.Info))
                _services.Log.Info("[AutoFishing] PlayerFishingSystem.Tick hooked (deferred)");
            else
                _services.Log.Warning("[AutoFishing] PlayerFishingSystem.Tick hook failed");
        }
    }

    private static string StageLabel(int stage) => stage switch
    {
        0  => "Equipping rod",
        1  => "Ready",
        2  => "Casting",
        3  => "Waiting for bite",
        4  => "Hooking",
        5  => "Harvesting",
        6  => "Missed",
        7  => "Cancelled",
        8  => "Fish on!",
        9  => "Reeling in",
        10 => "Reeling in",
        11 => "QTE done",
        12 => "Catch preview",
        13 => "Caught!",
        14 => "Wrapping up",
        15 => "Done",
        _  => $"Stage {stage}",
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
