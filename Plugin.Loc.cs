using System;

namespace Stellar.AutoFishing;

// Localized text for the house-style window (CLAUDE.md Rule 10: every user-facing string is an af.* key in ALL Lang/*.json).
// Plain labels are `() => _loc.T(key)` Funcs — T hands back the catalog's own string instance, so a poll allocates nothing
// and a live language switch shows on the next refresh. Text COMPOSED from catalog strings (" · <stage>", upper-cased
// captions, footer messages with a count) is built once and cached against _langGen, which ILocalization.LanguageChanged
// bumps — so it is rebuilt once per language switch, never per refresh (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    // Help popover keys: the title is the setting's own row label; subtitle = "<SECTION> · HELP".
    private const string KeyReleaseAt     = "af.tension.release";
    private const string KeyPullAt        = "af.tension.pull";
    private const string KeyAutoRod       = "af.option.autoRod";
    private const string KeyLoginPopup    = "af.option.loginPopup";
    private const string KeyTensionHelpSub = "af.help.tension.sub";
    private const string KeyOptionsHelpSub = "af.help.options.sub";
    private const string KeyReleaseHelp   = "af.help.release";
    private const string KeyPullHelp      = "af.help.pull";
    private const string KeyAutoRodHelp   = "af.help.autoRod";
    private const string KeyLoginHelp     = "af.help.loginPopup";

    private int _langGen;   // bumped on every live language switch; LocComposed caches compare against it

    private void OnLanguageChanged()
    {
        _langGen++;
        // The 10 Hz refresh would pick it up anyway; this just makes the switch land on the next frame.
        _window.MarkDirty();
        _helpWindow.MarkDirty();
    }

    // A string composed from catalog text, rebuilt only when _langGen moves (or Invalidate is called) — a poll hands back
    // the same instance.
    private sealed class LocComposed
    {
        private readonly Func<string> _build;
        private int _gen = -1;
        private string _s = string.Empty;

        public LocComposed(Func<string> build) => _build = build;

        public string Get(int gen)
        {
            if (gen != _gen) { _gen = gen; _s = _build(); }
            return _s;
        }

        public void Invalidate() => _gen = -1;
    }

    // Section captions are drawn UPPERCASE (house style); upper-case the catalog text once per language.
    private Func<string> UpperLoc(string key)
    {
        var cache = new LocComposed(() => _loc.T(key).ToUpperInvariant());
        return () => cache.Get(_langGen);
    }

    // ── status line ───────────────────────────────────────────────────────────────────────────────────────────────
    private LocComposed? _readyDetail, _pausedDetail;

    private string ReadyDetail()  => (_readyDetail  ??= new LocComposed(() => " · " + _loc.T("af.status.pressStart"))).Get(_langGen);
    private string PausedDetail() => (_pausedDetail ??= new LocComposed(() => " · " + _loc.T("af.status.closingPopup"))).Get(_langGen);

    // " · <stage>" composed once per stage per language (StageLabel, Plugin.cs), not per refresh.
    private readonly string[] _stageDetails = new string[16];
    private int _stageDetailsGen = -1;
    private int _otherStage = int.MinValue;
    private int _otherStageGen = -1;
    private string _otherStageDetail = string.Empty;

    private string StageDetail(int stage)
    {
        if ((uint)stage < (uint)_stageDetails.Length)
        {
            if (_stageDetailsGen != _langGen)
            {
                _stageDetailsGen = _langGen;
                for (int i = 0; i < _stageDetails.Length; i++) _stageDetails[i] = " · " + StageLabel(i);
            }
            return _stageDetails[stage];
        }
        if (stage != _otherStage || _otherStageGen != _langGen)
        {
            _otherStage = stage;
            _otherStageGen = _langGen;
            _otherStageDetail = " · " + StageLabel(stage);
        }
        return _otherStageDetail;
    }

    // ── footer messages ───────────────────────────────────────────────────────────────────────────────────────────
    // The event records WHAT happened (kind + the catch count); the text is composed on the next poll and again only if
    // the language switches while it shows — a 3 s message still follows a live language change.
    private enum FooterKind : byte { Caught, Missed, RodBroke }

    private FooterKind _footerKind;
    private int        _footerCount;
    private LocComposed? _footerComposed;

    private string ComposeFooter() => _footerKind switch
    {
        FooterKind.Caught => _loc.TFormat("af.footer.caught", _footerCount),
        FooterKind.Missed => _loc.T("af.footer.missed"),
        _                 => _loc.T("af.footer.rodBroke"),
    };

    private string FooterMessage() => (_footerComposed ??= new LocComposed(ComposeFooter)).Get(_langGen);
}
