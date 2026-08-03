using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.AutoFishing;

public sealed partial class Plugin
{
    // ── PlayerInputController.Fishing(bool) ──────────────────────────────────

    private void EnsurePic()
    {
        if (_picFishing != null && _picInst != null) return;

        var type = StellarInterop.FindType("Panda.ZInput.PlayerInputController");
        if (type is null) { _services.Log.Warning("[Auto] PlayerInputController type not found"); return; }

        // Matched by parameter TYPE (bool) — kept as a type-constrained GetMethod, not FindMethod.
        _picFishing ??= type.GetMethod("Fishing", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(bool) }, null);

        // Instance is inherited from ZSingleton<T> (FlattenHierarchy) — GetSingleton walks the base chain.
        _picInst ??= StellarInterop.GetSingleton(type);
    }

    private void CallFishing(bool pressed)
    {
        if (_picFishing is null || _picInst is null) return;
        try
        {
            _picFishing.Invoke(_picInst, new object[] { pressed });
        }
        catch (Exception ex) { _services.Log.Warning($"[Auto] Fishing({pressed}) threw: {ex.Message}"); }
    }

    private void ReleaseFishing()
    {
        if (_fishingHeld)
        {
            // If releasing from the QTE drag phase, also clear Lua IsDraging state
            if (_prevStage == 9 || _prevStage == 10)
                CallLuaDrag(false);
            else
                CallFishing(false);
        }
        _fishingHeld = false;
    }

    // ── Unity Time.deltaTime ─────────────────────────────────────────────────────

    private float GetDeltaTime()
    {
        _unityTimeDeltaTime ??= StellarInterop.FindType("UnityEngine.Time")
            ?.GetProperty("deltaTime", BindingFlags.Public | BindingFlags.Static);
        if (_unityTimeDeltaTime != null)
        {
            try { return (float)_unityTimeDeltaTime.GetValue(null)!; } catch { }
        }
        return 1f / 30f; // fallback: assume 30 fps
    }

    // Mirrors fishing_btn_ctrl_view onDownEvent / onUpEvent for the QTE drag phase.
    // down=true:  DragFishingRod() RPC + SetIsDraging(true) + PlayerInputController:Fishing(true)
    // down=false: SetIsDraging(false) + PlayerInputController:Fishing(false)
    private void CallLuaDrag(bool down)
    {
        string chunk = down
            ? "pcall(function() local vm=Z.VMMgr.GetVM('fishing') if vm then vm.DragFishingRod() end local fd=Z.DataMgr.Get('fishing_data') if fd then fd:SetIsDraging(true) end Z.PlayerInputController:Fishing(true) end)"
            : "pcall(function() local fd=Z.DataMgr.Get('fishing_data') if fd then fd:SetIsDraging(false) end Z.PlayerInputController:Fishing(false) end)";
        _services.Lua.DoString(chunk);
    }

    // ── Automation toggle ────────────────────────────────────────────────────

    private void ToggleAuto()
    {
        _autoEnabled = !_autoEnabled;
        if (!_autoEnabled)
        {
            ReleaseFishing();
            _prevStage = -1;
            _monthlyRestartPending = false;
        }
        _services.Log.Info($"[Auto] automation {(_autoEnabled ? "started" : "stopped")}");
        _window.MarkDirty();
    }

    // ── Fishing tick ─────────────────────────────────────────────────────────

    private void OnFishingTick(FishingState s)
    {
        _fishing = s;
        _window.MarkDirty();
        ++_tickCount;

        if (!s.IsFishing)
        {
            if (_prevStage != -1)
            {
                if (_autoEnabled) ToggleAuto();
                else { ReleaseFishing(); _prevStage = -1; }
                _rodEquipped = false;
            }
            _monthlyRestartPending = false;
            return;
        }

        // Pending restart after monthly card was closed — runs even while !_autoEnabled
        if (_monthlyRestartPending)
        {
            _monthlyRestartTimer += GetDeltaTime();
            if (_monthlyRestartTimer >= 1.5f)
            {
                _monthlyRestartPending = false;
                _autoEnabled = true;
                _prevStage = -1;
                _services.Log.Info("[Auto] monthly card closed — automation restarted");
                _window.MarkDirty();
            }
            return;
        }

        if (!_autoEnabled) return;

        if (_monthlyCardInterrupt) CheckMonthlyCardInterrupt();
        if (!_autoEnabled) return; // monthly card interrupt may have paused us

        if (s.Stage != _prevStage)
        {
            bool wasActive = _prevStage == 4 || _prevStage == 5 ||
                             _prevStage == 8 || _prevStage == 9 || _prevStage == 10;
            if (wasActive && (s.Stage == 0 || s.Stage == 1))
            {
                _fishMissed++;
                _window.MarkDirty();
            }

            // Keep drag held across Hit(8)→Control(9)→PullUp(10) so the rod is never momentarily released.
            bool isControlCycle = (s.Stage == 8 || s.Stage == 9 || s.Stage == 10) &&
                                  (_prevStage == 8 || _prevStage == 9 || _prevStage == 10);
            if (!isControlCycle)
            {
                ReleaseFishing();
            }
            _prevStage = s.Stage;
            OnStageEnter(s.Stage);
        }

        UpdateAutomation(s);
    }

    private void CheckMonthlyCardInterrupt()
    {
        _monthlyCheckAccum += GetDeltaTime();
        if (_monthlyCheckAccum < 0.5f) return;
        _monthlyCheckAccum = 0f;

        // Store the sentinel as a real Lua boolean (true/nil) so TryReadGlobalBool (type-strict) reads it.
        _services.Lua.DoString("pcall(function() local a=(Z.UIMgr):IsActive('monthly_reward_card_window') local b=(Z.UIMgr):IsActive('com_rewards_window') rawset(_G,'_pf_monthly',(a or b) and true or nil) end)");
        if (!(_services.Lua.TryReadGlobalBool("_pf_monthly", out var monthlyActive) && monthlyActive)) return;

        _services.Log.Info("[Auto] monthly card / item reward window detected — pausing automation");
        ReleaseFishing();
        _autoEnabled = false;
        _prevStage = -1;
        _services.Lua.DoString("pcall(function() if (Z.UIMgr):IsActive('monthly_reward_card_window') then (Z.UIMgr):CloseView('monthly_reward_card_window') end if (Z.UIMgr):IsActive('com_rewards_window') then (Z.UIMgr):CloseView('com_rewards_window') end end)");
        _monthlyRestartPending = true;
        _monthlyRestartTimer = 0f;
        _window.MarkDirty();
    }

    private void OnStageEnter(int stage)
    {
        _services.Log.Info($"[Auto] stage → {stage} ({FishingState.StageName(stage)})");
        EnsurePic();

        switch (stage)
        {
            case 0:  // WaitRod — show selector at 1s, auto-set first rod at 2.5s
                _rodSelectorTimer = 0f;
                _rodSelectorFired = false;
                _rodUseFired      = false;
                break;

            case 1:  // Ready — start 2s delay before casting
                _castDelayTimer = 0f;
                _castFired      = false;
                break;

            case 4:  // BiteHook — hold button so native updateBiteHook fires when bite window opens
                CallFishing(true);
                _fishingHeld = true;
                break;

            case 5:  // WaitHarvesting — native changeFishingStage(5) just fired confirming bite
                _services.Lua.DoString("pcall(function() local vm = Z.VMMgr.GetVM('fishing') if vm ~= nil then vm.HarvestingFishingRod() end end)");
                break;

            case 8:  // Hit — start drag early so we never miss the Control window
                FishingTickPatch.ResetTension();
                CallLuaDrag(true);
                _fishingHeld = true;
                break;

            case 9:  // Control — drag already held from stage 8; reset tension if coming from elsewhere
                if (_prevStage != 8 && _prevStage != 10) FishingTickPatch.ResetTension();
                if (!_fishingHeld) { CallLuaDrag(true); _fishingHeld = true; }
                break;

            case 10: // PullUp — hold already active from stage 8/9 Lua drag
                if (!_fishingHeld) { CallLuaDrag(true); _fishingHeld = true; }
                break;

            case 13: // FinishShowLoop — result window open; reset timer for auto-continue
                _fishCaught++;
                _finishDelayTimer = 0f;
                _finishClicked    = false;
                break;
        }
    }

    private void UpdateAutomation(FishingState s)
    {
        // Keep rod-equipped status current throughout all fishing stages
        if (_services.Lua.Ready && _tickCount % 30 == 0)
        {
            _services.Lua.DoString("pcall(function() local fd=Z.DataMgr.Get('fishing_data') local r=fd and fd.FishingRod local has=(r~=nil and r~=0 and r~='') and true or nil rawset(_G,'_pf_hasrod',has) end)");
            _rodEquipped = _services.Lua.TryReadGlobalBool("_pf_hasrod", out var hasRod) && hasRod;
            _window.MarkDirty();
        }

        switch (s.Stage)
        {
            case 0: // WaitRod — show selector at 1s, try to set rod at 2.5s
                if (!_autoRodChange) break;
                _rodSelectorTimer += GetDeltaTime();
                if (!_rodSelectorFired && _rodSelectorTimer >= 1f)
                {
                    _rodSelectorFired = true;
                    _services.Lua.DoString("pcall(function() local v=Z.UIMgr:GetView('fishing_main_window') if v then v:showItemSelectView(1103) end end)");
                }
                if (_rodSelectorFired && !_rodUseFired && _rodSelectorTimer >= 2.5f)
                {
                    _rodUseFired = true;
                    _rodChanged++;
                    // E.BackPackItemPackageType.Item=1, E.FishingItemType.FishingRod=1103 (plain Lua numbers).
                    // Access Z.ContainerMgr.CharSerialize.itemPackage.packages[1] directly —
                    // avoid nested pcalls which return IL2CPP booleans (always truthy in Lua).
                    // _pf_s tracks progress as a Lua number: 1=pkg, 2=loop, 3=rod found+set
                    _services.Lua.DoString("pcall(function() " +
                        "local pkg=Z.ContainerMgr.CharSerialize.itemPackage.packages[1] " +
                        "if not pkg then return end " +
                        "local tbl=Z.TableMgr.GetTable('ItemTableMgr') " +
                        "local bestUuid=nil local bestQuality=-1 " +
                        "for _,item in pairs(pkg.items) do " +
                            "local cfg=tbl and tbl.GetRow(item.configId) " +
                            "if cfg and cfg.Type==1103 then " +
                                "local q=cfg.Quality or 0 " +
                                "if q>bestQuality then bestQuality=q bestUuid=item.uuid end " +
                            "end " +
                        "end " +
                        "if bestUuid then " +
                            "local fvm=Z.VMMgr.GetVM('fishing') " +
                            "local cs=Z.CancelSource.Rent() " +
                            "if fvm then fvm.SetFishingRodAsync(bestUuid,cs:CreateToken()) end " +
                            "local mv=Z.UIMgr:GetView('fishing_main_window') " +
                            "if mv and mv.itemSelectView_ then mv.itemSelectView_:DeActive() end " +
                        "end " +
                    "end)");
                }
                break;

            case 1:
                if (!_castFired)
                {
                    _castDelayTimer += GetDeltaTime();
                    if (_castDelayTimer >= 2f)
                    {
                        _castFired = true;
                        if (_rodEquipped)
                            _services.Lua.DoString("pcall(function() local vm=Z.VMMgr.GetVM('fishing') if vm~=nil then vm.ThrowFishingRod() end end)");
                        else
                            _services.Log.Warning("[Auto] Stage 1: no rod equipped — skipping cast");
                    }
                }
                break;

            case 4:
                // Pulse false→true every 30 frames. Zero lastStageSyncTime_ first so the
                // 0.5s rate-limiter doesn't block syncFishingStageToServer(5) on bite.
                if (_tickCount % 30 == 0)
                {
                    PlayerFishingProxy.From(FishingTickPatch.LastInstance)?.ZeroStageSyncTime();
                    CallFishing(false);
                    CallFishing(true);
                    _fishingHeld = true;
                }
                break;

            case 9:
            case 10:
                // rawset bypasses __newindex on _G; compare against E.FishingDirection
                // constants in Lua so _pf_zone is always a plain Lua number (1/2/3),
                // which the type-strict TryReadGlobalNumber reads back cleanly.
                _services.Lua.DoString("pcall(function() local fd=Z.DataMgr.Get('fishing_data') if not fd then return end local tf=fd.TargetFish if not tf then return end local d=tf.dir local E_dir=E.FishingDirection local z=2 if d==E_dir.Left then z=1 elseif d==E_dir.Right then z=3 end rawset(_G,'_pf_zone',z) if d and d~=0 then fd.QTEData.playerSwingDir_=d end end)");
                // _pf_zone is a plain Lua number (1/2/3); default to 2 (Middle) on miss or 0, matching the old reader.
                FishingTickPatch.LastFishZone =
                    _services.Lua.TryReadGlobalNumber("_pf_zone", out var zone) && (int)zone != 0 ? (int)zone : 2;
                if (PlayerFishingProxy.From(FishingTickPatch.LastInstance) is { } fs)
                {
                    float target = FishingTickPatch.LastFishZone switch { 1 => -1f, 3 => 1f, _ => 0f };
                    float spd    = fs.QteInputMoveSpeed is > 0f and < 20f ? fs.QteInputMoveSpeed : 1f;
                    float step   = spd * GetDeltaTime();
                    float diff   = target - fs.CurFishingDir;
                    float next   = MathF.Abs(diff) <= step ? target
                                   : fs.CurFishingDir + MathF.Sign(diff) * step;
                    fs.CurFishingDir = MathF.Max(-1f, MathF.Min(1f, next));
                }

                // Tension hysteresis: hold drag to raise tension+progress,
                // release when tension hits the ceiling, re-hold when it drops to the floor.
                if (_fishingHeld && s.RodTension >= _tensionHigh)
                {
                    CallLuaDrag(false);
                    _fishingHeld = false;
                }
                else if (!_fishingHeld && s.RodTension <= _tensionLow)
                {
                    CallLuaDrag(true);
                    _fishingHeld = true;
                }
                break;

            case 13: // FinishShowLoop — click continue after 2s delay
                if (!_finishClicked)
                {
                    _finishDelayTimer += GetDeltaTime();
                    if (_finishDelayTimer >= 5f)
                    {
                        _finishClicked = true;
                        _services.Lua.DoString("pcall(function() local vm=Z.VMMgr.GetVM('fishing') if not vm then return end vm.EnterFishingState() Z.AudioMgr:PlayBGM('BGM_Sys_Fishing_None') Z.AudioMgr:PlayBGM('UnMuteAll_BGM') Z.UIMgr:CloseView('fishing_obtain_window') vm.FishingSuccessShowEnd() end)");
                    }
                }
                break;
        }
    }
}
