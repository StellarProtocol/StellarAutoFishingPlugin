using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.AutoFishing;

// Typed proxy for Panda.ZGame.PlayerFishingSystem.
// Field offsets are resolved once at runtime by name via il2cpp_class_get_field_from_name,
// so there are no hardcoded hex constants and no unsafe pointer arithmetic.
internal readonly struct PlayerFishingProxy
{
    private readonly IntPtr _ptr;

    private PlayerFishingProxy(IntPtr ptr) => _ptr = ptr;

    // ── Factory ──────────────────────────────────────────────────────────────

    internal static PlayerFishingProxy? From(object? instance)
    {
        if (instance is not Il2CppObjectBase obj) return null;
        var ptr = IL2CPP.Il2CppObjectBaseToPtr(obj);
        if (ptr == IntPtr.Zero) return null;
        // klass* is always the first IntPtr in every Il2CppObject
        EnsureResolved(Marshal.ReadIntPtr(ptr));
        return new PlayerFishingProxy(ptr);
    }

    // ── PlayerFishingSystem fields ────────────────────────────────────────────

    internal float CurFishingDir
    {
        get => ReadFloat(_ptr, _f.CurFishingDir);
        set => WriteFloat(_ptr, _f.CurFishingDir, value);
    }

    internal float QteInputMoveSpeed => ReadFloat(_ptr, _f.QteInputMoveSpeed);
    internal bool  IsFishing         => ReadByte(_ptr, _f.IsFishing) != 0;
    internal int   CurState          => ReadInt(_ptr, _f.CurState);

    // Zero lastStageSyncTime_ so the rate-limiter doesn't block stage 5 sync
    internal void ZeroStageSyncTime()
    {
        if (_f.LastStageSyncTime >= 0)
            Marshal.WriteInt64(_ptr + _f.LastStageSyncTime, 0L);
    }

    // ── Field offset registry ─────────────────────────────────────────────────

    private static Fields _f;
    private static bool   _resolved;

    private struct Fields
    {
        internal int CurFishingDir;
        internal int QteInputMoveSpeed;
        internal int IsFishing;
        internal int CurState;
        internal int LastStageSyncTime;
    }

    private static void EnsureResolved(IntPtr klass)
    {
        if (_resolved) return;
        _f.CurFishingDir     = Offset(klass, "curFishingDir");
        _f.QteInputMoveSpeed = Offset(klass, "qteInputMoveSpeed_");
        _f.IsFishing         = Offset(klass, "isFishing_");
        _f.CurState          = Offset(klass, "curState_");
        _f.LastStageSyncTime = Offset(klass, "lastStageSyncTime_");
        _resolved            = true;
    }

    // Ask IL2CPP for the field by name, return its byte offset in the object layout
    private static int Offset(IntPtr klass, string name)
    {
        var field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
        return field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);
    }

    // ── Safe reads / writes via Marshal (no unsafe keyword) ───────────────────

    private static float ReadFloat(IntPtr ptr, int off) =>
        off < 0 ? 0f : BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off));

    private static void WriteFloat(IntPtr ptr, int off, float v)
    {
        if (off >= 0) Marshal.WriteInt32(ptr + off, BitConverter.SingleToInt32Bits(v));
    }

    private static int  ReadInt(IntPtr ptr, int off) =>
        off < 0 ? 0 : Marshal.ReadInt32(ptr + off);

    private static byte ReadByte(IntPtr ptr, int off) =>
        off < 0 ? (byte)0 : Marshal.ReadByte(ptr + off);
}
