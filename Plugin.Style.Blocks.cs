using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

// More house-kit building blocks (Stellar Design Language §4; ported from CooldownBar / EntityInspector
// Plugin.Style.Blocks.cs): embedded art, the accent icon tile, the clickable On/Off chip, the round-knob slider, the stat
// tile, the status dot, and a cached int → string so the per-refresh text Funcs never allocate (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.AutoFishing.<file>") and kept: ImageElement
    // Funcs are polled. A missing resource caches null (the element then draws nothing) instead of re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.AutoFishing." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // The ONE place the plugin's icon art is chosen — the header tile and the launcher tile both read it, so swapping in
    // the new icon (being drawn separately) is a one-line change here + the csproj EmbeddedResource.
    private static byte[]? PluginIconPng() => CachedPng("fishing-icon.png");

    // ── accent icon tile ──────────────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the flat white glyph centred (art can't follow the theme, so the colour
    // lives in the tile). 40 px tile → 24 px glyph, 8 px all round; radius 9 (≈ 22 % of the side). The fixed-width Cell
    // pins the column (Cell / Selectable force-expand a child's width — the icon-squish gotcha); the Row keeps it square.
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(Func<byte[]?> png, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // ── On/Off chip ───────────────────────────────────────────────────────────────────────────────────────────────
    // A clickable 64×24 chip in a fixed-width cell: Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px
    // strut, label]. Two COMPLETE chips (on / off) swapped by a ConditionalElement wrapping the whole chip (bold can't
    // toggle live; never a Conditional inside a bubble's measured child), both the SAME width and sharing one HoverFlag, so
    // a click never moves the row. Bubble.MaxWidth pins the width (a MaxWidth-0 bubble under a Conditional caps itself at
    // the 100 px default — MinimalNameplate, measured 64×24 in both states in-game 2026-11-07).
    // On = bold status green on a 22 % green wash; Off = muted on a faint text wash (approved prototype .chip.on/.off).
    private const float ChipHeight = 24f;
    private const float OnOffChipW = 64f;

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // height strut = the side (house rule 4: no stacked spacers)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };

    private HudElement OnOffChip(Func<bool> get, Action<bool> set)
    {
        var hover = new HoverFlag();
        var onText = new ContrastCache();
        void Click() => set(!get());
        ColorRgba OnFill()  => Over(SuccessColor() with { A = ChipWashAlpha }, PanelColor());
        ColorRgba OffFill() => Over(TextColor() with { A = OffChipTextMix }, PanelColor());
        return new CellElement(new ConditionalElement(get,
            ChipShell(DropBold(new TextElement(() => "On", Color: () => onText.Get(SuccessColor(), OnFill(), SolidMinContrast),
                    Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                OnFill, hover, Click, OnOffChipW),
            ChipShell(new TextElement(() => "Off", Color: () => MutedColor(),
                    Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true),
                OffFill, hover, Click, OnOffChipW)), Width: OnOffChipW);
    }

    // ── round-knob slider ─────────────────────────────────────────────────────────────────────────────────────────
    // SquareHandle = a knob exactly HandleSize across (the default handle stretches to the row height = a capsule).
    // Width 0 = the track flexes to fill its row cell (the prototype's flex:1 track).
    private const float SliderKnobSize = 14f;

    private static SliderElement RoundSlider(Func<float> get, Action<float> set, float min, float max)
        => new(get, set, min, max) { SquareHandle = true, HandleSize = SliderKnobSize };

    // ── stat tile ─────────────────────────────────────────────────────────────────────────────────────────────────
    // A raised 46 px tile: a big bold number over a small muted label, both centred. The number/label boxes have the SAME
    // fixed width in every tile, so the three weighted cells prefer equal widths and split the row evenly (weighted
    // cells only split equally when their children prefer the same width — the PillPair note).
    private const float StatTileH     = 46f;
    private const float StatTextW     = 76f;

    private HudElement StatTile(Func<string> number, Func<ColorRgba> numberColor, string label)
        => new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, StatTileH),
            new ColumnElement(new HudElement[]
            {
                new TextElement(number, Color: () => numberColor(), Width: StatTextW, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 17) { Bold = true },
                new TextElement(() => label, Color: () => MutedColor(), Width: StatTextW, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 11) { RichEmoji = true },
            }, Gap: 0.01f),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };

    // ── status dot ────────────────────────────────────────────────────────────────────────────────────────────────
    // A 7 px round dot (radius 7/2 − 1 keeps the sliced bubble exactly 7 — RoundRadius) in a live colour.
    private const float DotSize = 7f;

    private HudElement StatusDot(Func<ColorRgba> color) => new BubbleElement(new SpacerElement(DotSize, DotSize))
    {
        FillColorFn = () => Fade(color()), CornerRadius = RoundRadius(DotSize), PaddingX = 0f, PaddingY = 0f,
    };

    // ── cached int → string ───────────────────────────────────────────────────────────────────────────────────────
    // The counters / slider values are polled every refresh; this hands back the same string until the number changes.
    private sealed class IntText
    {
        private readonly string _suffix;
        private int _n = int.MinValue;
        private string _s = string.Empty;

        public IntText(string suffix = "") => _suffix = suffix;

        public string Get(int n)
        {
            if (n != _n) { _n = n; _s = n.ToString(CultureInfo.InvariantCulture) + _suffix; }
            return _s;
        }
    }
}
