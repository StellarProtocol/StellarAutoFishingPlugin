using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

/// <summary>
/// Per-setting help, house style (Maestro Plugin.Help*.cs → CooldownBar Plugin.Settings.Help.cs). The window framework has
/// no hover tooltip, so every setting row ends in a round 24 px "?" (Neutral, lighter on hover, accent-tinted while its
/// tip is open) that opens ONE shared, title-bar-less GlassMenu popover anchored under it:
///   [32 px accent tile with a white bold "?"] [bold setting name / muted "SECTION · HELP"] … [round ✕]
///   then the help text in one Tile box, 360 wide.
/// Each "?" passes its title / body / subtitle; the popover's text Funcs read whichever set is current, so clicking
/// another "?" while open swaps the text and re-anchors it. Re-clicking the open "?" closes it. All texts are constant
/// literals (the plugin is English-only) → compared by reference, no allocation per refresh.
/// </summary>
public sealed partial class Plugin
{
    private const float HelpSize     = 24f;    // the round "?" — exactly 24 × 24 (RoundRadius)
    private const float HelpTipWidth = 360f;
    private const float HelpIconSize = 32f;

    private readonly IWindowControl _helpWindow;   // registered in the ctor, before the main window
    private WindowRect _helpRect;                  // where the popover should sit (re-applied for a few ticks after opening)
    private int _helpRepositionTicks;
    private readonly HoverFlag _helpCloseHover = new();
    // The open tip's texts ("" body = closed).
    private string _helpTitle = string.Empty, _helpBody = string.Empty, _helpSubtitle = string.Empty;

    // Content = [24 px strut, the "?" in a 24 px box] side by side in ONE Row, so the circle is exactly its side (a
    // stacked spacer drew Maestro's "?" 24 × 26). CenterGlyphs centres the drawn "?" in its box.
    private HudElement HelpDot(string title, string body, string subtitle)
    {
        var hover = new HoverFlag();
        var text = new ContrastCache();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => text.Get(TextColor(), NeutralFill(), PillMinContrast), Width: HelpSize,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // the real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => hover.On = over,
                OnClickWithRect = r => ToggleHelp(r, title, body, subtitle),
            })
        {
            // Accent-tinted only while THIS dot's tip is the one open.
            FillColorFn = () => Fade(_helpWindow.IsShown && ReferenceEquals(_helpBody, body)
                                     ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                                     : hover.On ? HoverFill(NeutralFill())
                                     : NeutralFill()),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private IWindowControl RegisterHelpWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "auto-fishing.help",
            Title:       "Help",
            DefaultRect: new WindowRect(_services.Framework.ScreenWidth - HelpTipWidth - 20f, 20f, HelpTipWidth, 0f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            Draggable = true, Closable = true, StartVisible = false,
            // No title bar: the header carries the title and a close; the whole frame is the drag handle.
            ShowTitleBar = false,
            // Help for the in-world Auto Fishing window — only while in the world and not on a loading screen.
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildHelpRoot(),
        OnClose: CloseHelp));

    private HudElement BuildHelpRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            HelpIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _helpTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(() => _helpSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            HelpCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(() => _helpBody, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // 32 px accent tile with a white bold "?" (the icon-tile look, drawn with text instead of a PNG).
    private HudElement HelpIcon()
    {
        var text = new SolidLabelCache();
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpIconSize),
                new TextElement(() => "?", Color: () => text.Get(AccentColor()),
                    Width: HelpIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(HelpIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
        }, Width: HelpIconSize);
    }

    // Round 24 px ✕ that closes the tip (plain text — a bold ✕ can draw blank): muted glyph at rest, a soft round wash +
    // text colour on hover. Rest fill is an explicit transparent (a null FillColorFn = the accent default blob).
    private HudElement HelpCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _helpCloseHover.On ? TextColor() : MutedColor(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseHelp) { Bare = true, OnHover = over => _helpCloseHover.On = over })
    {
        FillColorFn = () => _helpCloseHover.On ? Fade(Over(TextColor() with { A = HoverMix }, WindowBg())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Toggle the tip: re-clicking the same "?" closes it; otherwise (closed, or another "?") show this tip's text
    // anchored under the "?" that was clicked.
    private void ToggleHelp(WindowRect r, string title, string body, string subtitle)
    {
        if (_helpWindow.IsShown && ReferenceEquals(_helpBody, body)) { CloseHelp(); return; }
        _helpTitle = title;
        _helpBody = body;
        _helpSubtitle = subtitle;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        // Below the "?", right-aligned to it, so the popover opens to the LEFT over the row instead of off the window.
        float x = MathF.Max(10f, r.X + r.Width - HelpTipWidth);
        float y = MathF.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _helpRect = new WindowRect(x, y, HelpTipWidth, 0f);
        _helpWindow.SetVisible(true);
        _helpWindow.SetRect(_helpRect);
        // Front: the main window may have been raised after the tip, and when switching between "?"s the tip is already
        // visible (no remount → no fresh stacking position) — CLAUDE.md Rule 5 exception.
        _helpWindow.BringToFront();
        _helpWindow.MarkDirty();   // re-pull title / body for the new tip this frame
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored
        // rect for the next few ticks so it lands under the "?" on the first open too.
        _helpRepositionTicks = 4;
    }

    private void CloseHelp()
    {
        _helpRepositionTicks = 0;
        _helpBody = string.Empty;
        if (_helpWindow.IsShown) _helpWindow.SetVisible(false);
    }

    // OnUpdate: re-assert the tip position while the reposition window is open (early-out otherwise).
    private void HelpRepositionTick()
    {
        if (_helpRepositionTicks <= 0) return;
        _helpRepositionTicks--;
        if (_helpWindow.IsShown) _helpWindow.SetRect(_helpRect);
    }
}
