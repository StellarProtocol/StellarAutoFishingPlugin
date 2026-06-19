using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.AutoFishing;

// Fishing automation — input controller wiring, WorldProxy RPC, stage-enter dispatch, and per-tick
// rod-steering. Splits the bulk of fishing logic out of Plugin.cs without changing any behaviour.
public sealed partial class Plugin
{
    // ── PlayerInputController.Fishing(bool) ──────────────────────────────────

    private void EnsurePic()
    {
        if (_picFishing != null && _picInst != null) return;

        var type = FindType("Panda.ZInput.PlayerInputController");
        if (type is null) { _services.Log.Warning("[Auto] PlayerInputController type not found"); return; }

        _picFishing ??= type.GetMethod("Fishing", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(bool) }, null);

        if (_picInst is null)
        {
            // FlattenHierarchy: Instance is inherited from ZSingleton<T>, not declared on PlayerInputController
            var prop = type.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (prop is null)
            {
                var baseType = type.BaseType;
                prop = baseType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            }
            _picInst = prop?.GetValue(null);
        }
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
        if (_fishingHeld || _clickRelease > 0)
        {
            // If releasing from the QTE drag phase, also clear Lua IsDraging state
            if (_prevStage == 9 || _prevStage == 10)
                CallLuaDrag(false);
            else
                CallFishing(false);
        }
        _fishingHeld  = false;
        _clickRelease = 0;
    }

    // ── Lua global reader — LuaState["key"] indexer ─────────────────────────────

    private MethodInfo? _luaGetGlobal;

    private void EnsureLuaGlobalReader()
    {
        if (_luaGetGlobal != null || _luaState is null) return;
        var t = _luaState.GetType();
        // Search by name only — IL2CPP proxy may use Il2CppSystem.String not System.String,
        // so exact type-constrained GetMethod("GetNumber", null, new[]{typeof(string)}, null) fails.
        // Tolua# exposes: GetNumber, get_Item (indexer), GetString, GetBoolean — try each.
        foreach (var candidate in new[] { "GetNumber", "get_Item", "GetString", "GetFloat" })
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != candidate || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                _luaGetGlobal = m;
                return;
            }
        }
    }

    private int ReadLuaGlobalInt(string key)
    {
        if (_luaState is null || _luaGetGlobal is null) return 2;
        try
        {
            var v = _luaGetGlobal.Invoke(_luaState, new object[] { key });
            if (v == null) return 2;

            // Managed numeric types (standard tolua#)
            if (v is double d)  return (int)d is 0 ? 2 : (int)d;
            if (v is float  f)  return (int)f is 0 ? 2 : (int)f;
            if (v is int    i)  return i == 0 ? 2 : i;
            if (v is long   l)  return (int)l == 0 ? 2 : (int)l;
            // String path: if Lua stored tostring(z)
            if (v is string s && int.TryParse(s, out int si)) return si == 0 ? 2 : si;

            // IL2CPP tolua# always returns Il2CppSystem.Object — unwrap boxed primitive via IL2CPP field API.
            if (v is Il2CppObjectBase il2obj)
            {
                var nativePtr = IL2CPP.Il2CppObjectBaseToPtr(il2obj);
                if (nativePtr != IntPtr.Zero)
                {
                    EnsureBoxedValueOffset(nativePtr);
                    // Try as double first (Lua numbers are double in Lua 5.1/5.3)
                    double dv = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(nativePtr + _luaBoxedValueOff));
                    if (dv >= 1.0 && dv <= 3.0 && dv == System.Math.Floor(dv))
                        return (int)dv;
                    // Try as int32 (IL2CPP integer enum)
                    int iv = Marshal.ReadInt32(nativePtr + _luaBoxedValueOff);
                    if (iv >= 1 && iv <= 3)
                        return iv;
                }
            }
        }
        catch { }
        return 2;
    }

    // ── Unity Time.deltaTime ─────────────────────────────────────────────────────

    private float GetDeltaTime()
    {
        _unityTimeDeltaTime ??= FindType("UnityEngine.Time")
            ?.GetProperty("deltaTime", BindingFlags.Public | BindingFlags.Static);
        if (_unityTimeDeltaTime != null)
        {
            try { return (float)_unityTimeDeltaTime.GetValue(null)!; } catch { }
        }
        return 1f / 30f; // fallback: assume 30 fps
    }

    // ── Lua direct call — LuaState.DoString ──────────────────────────────────────

    private void EnsureLuaState()
    {
        if (_luaDoString != null) return;

        // Always use LuaInterface.LuaState.mainState — the Stellar Framework's own Lua bridge
        // uses this path ("via tolua# LuaState.mainState + DoString"). LuaClient.Instance.luaState
        // may be a different state where Z.DataMgr and other game globals are not registered.
        var lsType = FindType("LuaInterface.LuaState");
        if (lsType != null)
        {
            _luaState =
                lsType.GetProperty("mainState", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                ?? lsType.GetField("mainState",  BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
        }

        // Fallback: LuaClient.Instance.luaState
        if (_luaState is null)
        {
            var clientType = FindType("LuaClient");
            var clientInst = clientType
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (clientInst != null)
            {
                var t = clientInst.GetType();
                _luaState =
                    t.GetProperty("luaState", BindingFlags.Instance | BindingFlags.Public)?.GetValue(clientInst)
                    ?? t.GetField("luaState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(clientInst);
            }
        }

        if (_luaState is null) { _services.Log.Warning("[Auto] LuaState not found via any path"); return; }

        foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "DoString" || m.IsGenericMethod) continue;
            var ps = m.GetParameters();
            if (ps.Length < 2 || ps[0].ParameterType != typeof(string)) continue;
            if (m.ReturnType == typeof(void))
                _luaDoString ??= m;
        }

        // Verify we're on the game's main Lua state by probing well-known globals.
        // Also find set_Item so we can write globals from C# instead of DoString.
        if (_luaGetGlobal is null)
        {
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "get_Item" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                _luaGetGlobal = m;
                break;
            }
        }
    }

    private void CallLua(string chunk)
    {
        if (_luaState is null || _luaDoString is null) return;
        try
        {
            _luaDoString.Invoke(_luaState, new object[] { chunk, "plugin" });
        }
        catch (Exception ex) { _services.Log.Warning($"[Auto] CallLua threw: {ex.Message}"); }
    }

    private void EnsureBoxedValueOffset(IntPtr nativePtr)
    {
        if (_luaBoxedValueOff >= 0) return;
        var klass = Marshal.ReadIntPtr(nativePtr); // klass* always at offset 0
        var field = klass != IntPtr.Zero
            ? IL2CPP.il2cpp_class_get_field_from_name(klass, "m_value")
            : IntPtr.Zero;
        _luaBoxedValueOff = field != IntPtr.Zero
            ? (int)IL2CPP.il2cpp_field_get_offset(field)
            : 0x10; // universal fallback
    }

    private bool ReadLuaGlobalBool(string key)
    {
        if (_luaState is null || _luaGetGlobal is null) return false;
        try
        {
            var v = _luaGetGlobal.Invoke(_luaState, new object[] { key });
            if (v == null)       return false;
            if (v is bool   b)   return b;
            if (v is double d)   return d != 0.0;
            if (v is float  f)   return f != 0f;
            if (v is int    i)   return i != 0;
            if (v is long   l)   return l != 0;
            if (v is Il2CppObjectBase il2obj)
            {
                var nativePtr = IL2CPP.Il2CppObjectBaseToPtr(il2obj);
                if (nativePtr != IntPtr.Zero)
                {
                    EnsureBoxedValueOffset(nativePtr);
                    // Stored as Lua number (1/0) — read as double for exact match
                    double dv = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(nativePtr + _luaBoxedValueOff));
                    return dv != 0.0;
                }
            }
        }
        catch { }
        return false;
    }

    private string ReadLuaGlobalStr(string key)
    {
        if (_luaState is null || _luaGetGlobal is null) return "?";
        try
        {
            var v = _luaGetGlobal.Invoke(_luaState, new object[] { key });
            if (v == null) return "nil";
            if (v is string s) return s;
            if (v is Il2CppObjectBase il2obj)
            {
                var nativePtr = IL2CPP.Il2CppObjectBaseToPtr(il2obj);
                // IL2CPP System.String — read char data via ToString on the proxy
                if (nativePtr != IntPtr.Zero)
                    return il2obj.ToString() ?? "?";
            }
            return v.ToString() ?? "?";
        }
        catch { return "err"; }
    }

    // Per-tick variant — no log to avoid spam
    private void CallLuaSilent(string chunk)
    {
        if (_luaState is null || _luaDoString is null) return;
        try { _luaDoString.Invoke(_luaState, new object[] { chunk, "plugin" }); }
        catch (Exception ex) { _services.Log.Warning($"[Auto] CallLuaSilent threw: {ex.Message}"); }
    }

    // Mirrors fishing_btn_ctrl_view onDownEvent / onUpEvent for the QTE drag phase.
    // down=true:  DragFishingRod() RPC + SetIsDraging(true) + PlayerInputController:Fishing(true)
    // down=false: SetIsDraging(false) + PlayerInputController:Fishing(false)
    private void CallLuaDrag(bool down)
    {
        EnsureLuaState();
        string chunk = down
            ? "pcall(function() local vm=Z.VMMgr.GetVM('fishing') if vm then vm.DragFishingRod() end local fd=Z.DataMgr.Get('fishing_data') if fd then fd:SetIsDraging(true) end Z.PlayerInputController:Fishing(true) end)"
            : "pcall(function() local fd=Z.DataMgr.Get('fishing_data') if fd then fd:SetIsDraging(false) end Z.PlayerInputController:Fishing(false) end)";
        CallLua(chunk);
    }

    // ── Automation toggle ────────────────────────────────────────────────────

    private void ToggleAuto()
    {
        _autoEnabled = !_autoEnabled;
        if (!_autoEnabled)
        {
            ReleaseFishing();
            InputSimPatch.ClearAll();
            _prevStage = -1;
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

        if (_clickRelease > 0)
        {
            _clickRelease--;
            if (_clickRelease == 0) { CallFishing(false); _fishingHeld = false; }
        }

        if (!s.IsFishing)
        {
            if (_prevStage != -1)
            {
                if (_autoEnabled) ToggleAuto();
                else { ReleaseFishing(); InputSimPatch.ClearAll(); _prevStage = -1; }
                _rodEquipped = false;
            }
            return;
        }

        if (!_autoEnabled) return;

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
                InputSimPatch.ClearAll();
            }
            _prevStage = s.Stage;
            OnStageEnter(s.Stage);
        }

        UpdateAutomation(s);
    }

    private void OnStageEnter(int stage)
    {
        _services.Log.Info($"[Auto] stage → {stage} ({new FishingState { Stage = stage }.StageName})");
        EnsurePic();

        switch (stage)
        {
            case 0:  // WaitRod — show selector at 1s, auto-set first rod at 2.5s
                _rodSelectorTimer = 0f;
                _rodSelectorFired = false;
                _rodUseFired      = false;
                EnsureLuaState();
                break;

            case 1:  // Ready — start 2s delay before casting
                _castDelayTimer = 0f;
                _castFired      = false;
                EnsureLuaState();
                break;

            case 4:  // BiteHook — hold button so native updateBiteHook fires when bite window opens
                CallFishing(true);
                _fishingHeld = true;
                break;

            case 5:  // WaitHarvesting — native changeFishingStage(5) just fired confirming bite
                EnsureLuaState();
                CallLua("pcall(function() local vm = Z.VMMgr.GetVM('fishing') if vm ~= nil then vm.HarvestingFishingRod() end end)");
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
        if (_luaDoString != null && _tickCount % 30 == 0)
        {
            EnsureLuaGlobalReader();
            CallLuaSilent("pcall(function() local fd=Z.DataMgr.Get('fishing_data') local r=fd and fd.FishingRod local has=(r~=nil and r~=0 and r~='') and 1 or 0 rawset(_G,'_pf_hasrod',has) rawset(_G,'_pf_rod_dbg',tostring(r)) end)");
            _rodEquipped = ReadLuaGlobalBool("_pf_hasrod");
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
                    CallLua("pcall(function() local v=Z.UIMgr:GetView('fishing_main_window') if v then v:showItemSelectView(1103) end end)");
                }
                if (_rodSelectorFired && !_rodUseFired && _rodSelectorTimer >= 2.5f)
                {
                    _rodUseFired = true;
                    _rodChanged++;
                    // E.BackPackItemPackageType.Item=1, E.FishingItemType.FishingRod=1103 (plain Lua numbers).
                    // Access Z.ContainerMgr.CharSerialize.itemPackage.packages[1] directly —
                    // avoid nested pcalls which return IL2CPP booleans (always truthy in Lua).
                    // _pf_s tracks progress as a Lua number: 1=pkg, 2=loop, 3=rod found+set
                    CallLua("pcall(function() " +
                        "local pkg=Z.ContainerMgr.CharSerialize.itemPackage.packages[1] " +
                        "if not pkg then return end " +
                        "rawset(_G,'_pf_s',1) " +
                        "local tbl=Z.TableMgr.GetTable('ItemTableMgr') " +
                        "local bestUuid=nil local bestQuality=-1 " +
                        "for _,item in pairs(pkg.items) do " +
                            "rawset(_G,'_pf_s',2) " +
                            "local cfg=tbl and tbl.GetRow(item.configId) " +
                            "if cfg and cfg.Type==1103 then " +
                                "local q=cfg.Quality or 0 " +
                                "if q>bestQuality then bestQuality=q bestUuid=item.uuid end " +
                            "end " +
                        "end " +
                        "if bestUuid then " +
                            "rawset(_G,'_pf_s',3) " +
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
                            CallLua("pcall(function() local vm=Z.VMMgr.GetVM('fishing') if vm~=nil then vm.ThrowFishingRod() end end)");
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
                // constants in Lua so _pf_zone is always a plain Lua integer (1/2/3),
                // which get_Item returns as Double (not Il2CppSystem.Object).
                EnsureLuaState();
                EnsureLuaGlobalReader();
                CallLuaSilent("pcall(function() local fd=Z.DataMgr.Get('fishing_data') if not fd then return end local tf=fd.TargetFish if not tf then return end local d=tf.dir local E_dir=E.FishingDirection local z=2 if d==E_dir.Left then z=1 elseif d==E_dir.Right then z=3 end rawset(_G,'_pf_zone',z) if d and d~=0 then fd.QTEData.playerSwingDir_=d end end)");
                FishingTickPatch.LastFishZone = ReadLuaGlobalInt("_pf_zone");
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
                        EnsureLuaState();
                        CallLua("pcall(function() local vm=Z.VMMgr.GetVM('fishing') if not vm then return end vm.EnterFishingState() Z.AudioMgr:PlayBGM('BGM_Sys_Fishing_None') Z.AudioMgr:PlayBGM('UnMuteAll_BGM') Z.UIMgr:CloseView('fishing_obtain_window') vm.FishingSuccessShowEnd() end)");
                    }
                }
                break;
        }
    }
}
