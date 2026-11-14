using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

// The Auto Fishing window, house style (approved redesign 2026-11-15, _scratch\prototypes\autofishing-redesign-APPROVED.md,
// footer option A). Top → bottom:
//   ToolHeader [40 px accent icon tile | bold title over ONE live status line | Start/Stop pill (72 px both states)]
//   SESSION      three stat tiles (Caught / Missed / Rods used)
//   REEL TENSION two round-knob slider rows [label | slider | value | ?]
//   OPTIONS      two rows [label | On/Off chip | ?]
//   footer       a 16 px line, always reserved: ~3 s feedback from where the counters increment, blank otherwise.
// Every line is pinned (struts, fixed cells, both pill/chip variants the same size), so the window keeps ONE height in
// every state. The window has no per-frame work of its own: the framework's 10 Hz scheduled refresh polls the Funcs, and
// they only hand back cached strings (Plugin-Performance-Rules §2/§5).
public sealed partial class Plugin
{
    private const float WindowWidth     = 320f;
    private const float StartStopLabelW = 48f;    // + 2 × 12 padding = the 72 px pill, Start and Stop alike
    private const float StatusLineH     = 16f;
    private const float FooterLineH     = 16f;
    private const float SettingRowH     = 28f;
    private const float SliderLabelW    = 74f;
    private const float SliderValueW    = 38f;
    private const float FooterShowS     = 3f;

    // Help popover texts (approved spec). Subtitles = "<SECTION> · HELP".
    private const string TensionHelpSub = "REEL TENSION · HELP";
    private const string OptionsHelpSub = "OPTIONS · HELP";
    private const string ReleaseHelp =
        "While reeling, the rod is let go when the tension bar reaches this value, so the line doesn't snap. Lower is safer but slower. Default 25%.";
    private const string PullHelp =
        "Once tension drops to this value, the rod is held again to keep reeling. Must stay below Release at. Default 8%.";
    private const string RodChangeHelp =
        "Equips the best rod from your bag when yours breaks or none is equipped. If off, fishing waits until you equip one yourself.";
    private const string LoginPopupHelp =
        "Closes the daily login card and reward popups when they appear, then resumes fishing after a moment.";

    private enum RunState : byte { NotAtSpot, Ready, Running, Paused }

    // Paused = the monthly-card interrupt closed a popup and the 1.5 s restart is pending (_autoEnabled is false then).
    private RunState State => !_fishing.IsFishing ? RunState.NotAtSpot
                            : _monthlyRestartPending ? RunState.Paused
                            : _autoEnabled ? RunState.Running
                            : RunState.Ready;

    private IWindowControl RegisterMainWindow() => _services.Windows.Register(new WindowRegistration(
        Spec: new WindowSpec(
            Id:          "auto-fishing.fishing",
            Title:       "Auto Fishing",
            DefaultRect: new WindowRect(_services.Framework.ScreenWidth - WindowWidth - 20f, 20f, WindowWidth, 0f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        { Draggable = true, Closable = true, StartVisible = false,
          // Built while hidden so the first open doesn't pay for the tree (Plugin-Performance-Rules §5). Every Func is
          // safe without game state (it only reads plugin fields).
          Prebuild = true,
          // Gameplay tool: draw only while in-world (fishing happens in the World phase), hidden during loading screens.
          ShouldRender = () => _services.ClientState.Phase == GamePhase.World && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
        Root: new ColumnElement(new HudElement[]
        {
            BuildHeader(),
            SectionPanel("Session", "since you pressed Start", new HudElement[] { BuildStats() }),
            SectionPanel("Reel tension", "drag to tune", new HudElement[]
            {
                SliderRow("Release at", RoundSlider(() => _tensionHigh,
                        v => { _tensionHigh = MathF.Max(v, _tensionLow + 1f); TensionChanged(); }, 0f, 100f),
                    () => _highText.Get((int)MathF.Round(_tensionHigh)), ReleaseHelp),
                SliderRow("Pull at", RoundSlider(() => _tensionLow,
                        v => { _tensionLow = MathF.Min(v, _tensionHigh - 1f); TensionChanged(); }, 0f, 100f),
                    () => _lowText.Get((int)MathF.Round(_tensionLow)), PullHelp),
            }),
            SectionPanel("Options", "", new HudElement[]
            {
                OptionRow("Auto rod change", () => _autoRodChange,
                    v => { _autoRodChange = v; _cfg.Set<bool>("auto_rod_change", v); _cfg.Save(); }, RodChangeHelp),
                OptionRow("Close daily login popup", () => _monthlyCardInterrupt,
                    v => { _monthlyCardInterrupt = v; _cfg.Set<bool>("monthly_card_interrupt", v); _cfg.Save(); }, LoginPopupHelp),
            }),
            FixedLine(FooterLineH, new TextElement(FooterText, Color: () => FooterColor(), NoWrap: true, FontSize: 12)
                { RichEmoji = true, Ellipsize = true }),
        }, Gap: 8f),
        OnClose: () => { StopAuto(); CloseHelp(); _window!.SetVisible(false); }));

    // ── header ────────────────────────────────────────────────────────────────────────────────────────────────────
    // The text column sits in a weighted cell, so a long status ellipsizes instead of pushing the pill.
    private HudElement BuildHeader() => new RowElement(new HudElement[]
    {
        IconBox(() => HeaderIconPng(ToolGlyphSize), ToolIconSize, ToolGlyphSize),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Auto Fishing", Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
            FixedLine(StatusLineH, BuildStatusLine()),
        }, Gap: 2f), Weight: 1f),
        new ConditionalElement(() => _autoEnabled || _monthlyRestartPending,
            CautionPill(() => "Stop", StopAuto, StartStopLabelW),
            PrimaryPill(() => "Start", StartAuto, StartStopLabelW, enabled: () => _fishing.IsFishing)),
    }, Gap: 10f);

    // Status line (plain text, no pill), one line: "Go to a fishing spot to start" (muted) when not at a spot, else
    // [dot] <word in the state colour> <muted " · detail">.
    private HudElement BuildStatusLine() => new ConditionalElement(() => _fishing.IsFishing,
        new RowElement(new HudElement[]
        {
            StatusDot(StatusDotColor),
            new CellElement(new RowElement(new HudElement[]
            {
                new TextElement(StatusWord, Color: () => StatusWordColor(), NoWrap: true, FontSize: 12) { RichEmoji = true },
                new CellElement(new TextElement(StatusDetail, Color: () => MutedColor(), NoWrap: true, FontSize: 12)
                    { RichEmoji = true, Ellipsize = true }, Weight: 1f),
            }, Gap: 0.01f), Weight: 1f),
        }, Gap: 5f),
        new TextElement(() => "Go to a fishing spot to start", Color: () => MutedColor(), NoWrap: true, FontSize: 12)
            { RichEmoji = true, Ellipsize = true });

    private string StatusWord() => State switch
    {
        RunState.Running => "Running",
        RunState.Paused  => "Paused",
        _                => "Ready",
    };

    private string StatusDetail() => State switch
    {
        RunState.Running => StageDetail(_fishing.Stage),
        RunState.Paused  => " · Closing a popup",
        _                => " · press Start",
    };

    private ColorRgba StatusDotColor() => State switch
    {
        RunState.Running => SuccessColor(),
        RunState.Paused  => CautionColor(),
        _                => MutedColor(),
    };

    private readonly ContrastCache _statusWordInk = new();

    private ColorRgba StatusWordColor() => State switch
    {
        RunState.Running => _statusWordInk.Get(SuccessColor(), WindowBg(), SolidMinContrast),
        RunState.Paused  => _statusWordInk.Get(CautionColor(), WindowBg(), SolidMinContrast),
        _                => MutedColor(),
    };

    // " · <stage>" composed once per stage (StageLabel, Plugin.cs), not per refresh.
    private static readonly string[] StageDetails = BuildStageDetails();
    private int _otherStage = int.MinValue;
    private string _otherStageDetail = string.Empty;

    private static string[] BuildStageDetails()
    {
        var a = new string[16];
        for (int i = 0; i < a.Length; i++) a[i] = " · " + StageLabel(i);
        return a;
    }

    private string StageDetail(int stage)
    {
        if ((uint)stage < (uint)StageDetails.Length) return StageDetails[stage];
        if (stage != _otherStage) { _otherStage = stage; _otherStageDetail = " · " + StageLabel(stage); }
        return _otherStageDetail;
    }

    // ── session stats ─────────────────────────────────────────────────────────────────────────────────────────────
    private readonly IntText _caughtText = new(), _missedText = new(), _rodsText = new();
    private readonly IntText _highText = new("%"), _lowText = new("%");
    private readonly ContrastCache _caughtInk = new();

    private HudElement BuildStats() => new RowElement(new HudElement[]
    {
        new CellElement(StatTile(() => _caughtText.Get(_fishCaught),
            () => _fishCaught > 0 ? _caughtInk.Get(SuccessColor(), TileColor(), SolidMinContrast) : TextColor(), "Caught"), Weight: 1f),
        new CellElement(StatTile(() => _missedText.Get(_fishMissed), () => TextColor(), "Missed"), Weight: 1f),
        new CellElement(StatTile(() => _rodsText.Get(_rodChanged), () => TextColor(), "Rods used"), Weight: 1f),
    }, Gap: 6f);

    // ── setting rows ──────────────────────────────────────────────────────────────────────────────────────────────
    // [muted label 74 | round-knob slider (flex) | value 38, right | ?], pinned to 28 px.
    private HudElement SliderRow(string label, SliderElement slider, Func<string> value, string help)
        => FixedLine(SettingRowH, new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => label, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Width: SliderLabelW),
            new CellElement(slider, Weight: 1f),
            new CellElement(new TextElement(value, Color: () => TextColor(), Align: TextAlign.Right, NoWrap: true), Width: SliderValueW),
            HelpDot(label, help, TensionHelpSub),
        }, Gap: 8f));

    // [label (flex) | 64×24 On/Off chip | ?], pinned to 28 px.
    private HudElement OptionRow(string label, Func<bool> get, Action<bool> set, string help)
        => FixedLine(SettingRowH, new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => label, Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            OnOffChip(get, set),
            HelpDot(label, help, OptionsHelpSub),
        }, Gap: 8f));

    // ── footer feedback (house rule 13) ───────────────────────────────────────────────────────────────────────────
    // Set from where the catch / miss / rod-change counters increment (Plugin.Fishing.cs); the message string is
    // composed there, once per event. Idle = a single space, so the TMP line keeps its height (the strut pins it too).
    private string _footerText = string.Empty;
    private bool   _footerGreen;
    private float  _footerAt = float.NegativeInfinity;
    private readonly ContrastCache _footerInk = new();

    private void ShowFooter(string text, bool green)
    {
        _footerText = text;
        _footerGreen = green;
        _footerAt = _services.Framework.TimeNow;
    }

    private bool FooterShowing() => _services.Framework.TimeNow - _footerAt < FooterShowS;

    private string FooterText() => FooterShowing() ? _footerText : " ";

    private ColorRgba FooterColor() => _footerGreen && FooterShowing()
        ? _footerInk.Get(SuccessColor(), WindowBg(), SolidMinContrast)
        : MutedColor();

    // ── Start / Stop ──────────────────────────────────────────────────────────────────────────────────────────────
    // A user press of Start opens a fresh session: the SESSION panel promises "since you pressed Start", so its counters
    // and any leftover footer message reset here. Only the pill comes through this method — the monthly-card auto-resume
    // (Plugin.Fishing.cs) re-enables automation directly, so a resume keeps the running session's counts.
    private void StartAuto()
    {
        if (_autoEnabled || !_fishing.IsFishing) return;
        _fishCaught = 0;
        _fishMissed = 0;
        _rodChanged = 0;
        _footerAt = float.NegativeInfinity;   // FooterShowing() false → idle line
        ToggleAuto();                          // MarkDirty → the tiles redraw at 0
    }

    // Stop also cancels a pending monthly-card restart (Paused shows "Stop" too, and must not resume afterwards).
    private void StopAuto()
    {
        _monthlyRestartPending = false;
        if (_autoEnabled) ToggleAuto();
        else _window.MarkDirty();
    }

    // ── tension save debounce ─────────────────────────────────────────────────────────────────────────────────────
    // A slider setter fires on EVERY drag frame: apply the value live, save ~0.5 s after the drag settles
    // (Plugin-Performance-Rules §5); Dispose flushes a pending save.
    private const float TensionSaveDelayS = 0.5f;
    private bool  _tensionSavePending;
    private float _tensionSaveAt;

    private void TensionChanged()
    {
        _tensionSavePending = true;
        _tensionSaveAt = _services.Framework.TimeNow + TensionSaveDelayS;
    }

    // Framework Update (world-gated): two early-out compares when idle.
    private void OnUpdate(float dt)
    {
        HelpRepositionTick();
        if (_tensionSavePending && _services.Framework.TimeNow >= _tensionSaveAt)
        {
            _tensionSavePending = false;
            SaveTension();
        }
    }
}
