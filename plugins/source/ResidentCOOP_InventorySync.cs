// ResidentCOOP_InventorySync.cs
// Shared inventory synchronization.
//
// MODE: SHARED INVENTORY
//   - ALL items (herbs, ammo, keys, weapons, everything) are shared.
//   - When either player picks up anything, BOTH players get it.
//   - This is achieved via WorldSync broadcasting setItemGotFlag.
//   - This plugin handles the weapon EQUIP sync and reward system.
//
// Systems used:
//   app.ItemManager        — item queries, weapon detection
//   app.InventoryManager   — inventory state
//   app.RewardManager      — reward unlock sync
//   app.EquipManager       — weapon equip state

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_InventorySync
{
    static ManagedObject _itemMgr = null;
    static ManagedObject _rewardMgr = null;
    static ManagedObject _equipMgr = null;
    static bool _singletonsReady = false;

    // Pending weapon shares/rewards from network
    static string _pendingWeapon = null;
    static string _pendingReward = null;
    static readonly object _pendingLock = new object();

    // Track which rewards we've already sent to avoid duplicates
    static HashSet<string> _sentRewards = new HashSet<string>();

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupRewardHooks();
            SetupEquipHooks();
            API.LogInfo("[COOP-InvSync] Loaded. SHARED inventory mode.");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-InvSync] Init: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP-InvSync] Unloaded.");
    }

    static void CacheSingletons()
    {
        if (_singletonsReady) return;
        _singletonsReady = true;

        _itemMgr = GetSingleton("app.ItemManager");
        _rewardMgr = GetSingleton("app.RewardManager");
        _equipMgr = GetSingleton("app.EquipManager");
    }

    // =====================================================================
    //  EQUIP SYNC — Track weapon equip changes to show on ghost player
    //  The PlayerStateData.WeaponID is set here based on current equip.
    // =====================================================================

    static void SetupEquipHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition eqType = tdb.FindType("app.EquipManager");
        if (eqType == null)
        {
            API.LogWarning("[COOP-InvSync] app.EquipManager not found");
            return;
        }

        // Hook equip changes to track what weapon the player has
        Method eqWeapon = eqType.GetMethod("requestEquipWeapon");
        if (eqWeapon != null)
        {
            MethodHook.Create(eqWeapon, false).AddPre(new MethodHook.PreHookDelegate(OnPreEquipWeapon));
            API.LogInfo("[COOP-InvSync] Hooked EquipManager.requestEquipWeapon");
        }
    }

    static PreHookResult OnPreEquipWeapon(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            // Track the weapon ID for the ghost player visual
            int weaponID = (int)(args[2] & 0xFFFFFFFF);
            CoopState.LocalPlayer.WeaponID = weaponID;
            API.LogInfo("[COOP-InvSync] Equipped weapon: " + weaponID);
        }
        catch { }

        return PreHookResult.Continue;
    }

    // =====================================================================
    //  REWARD SYNC — Unlock rewards on both sides
    // =====================================================================

    static void SetupRewardHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition rmType = tdb.FindType("app.RewardManager");
        if (rmType == null)
        {
            API.LogWarning("[COOP-InvSync] app.RewardManager not found");
            return;
        }

        Method unlock = rmType.GetMethod("unlockReward");
        if (unlock != null)
        {
            MethodHook.Create(unlock, false).AddPre(new MethodHook.PreHookDelegate(OnPreUnlockReward));
            API.LogInfo("[COOP-InvSync] Hooked RewardManager.unlockReward");
        }
    }

    static PreHookResult OnPreUnlockReward(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            string rewardID = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(rewardID) && !_sentRewards.Contains(rewardID))
            {
                _sentRewards.Add(rewardID);
                API.LogInfo("[COOP-InvSync] Reward unlocked: " + rewardID + " -> syncing");
                CallCoreSendRaw(NetProtocol.WriteRewardUnlock(rewardID));
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    // =====================================================================
    //  PER-FRAME: Process incoming weapon shares and rewards
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        if (!CoopState.IsCoopActive) return;

        try
        {
            CacheSingletons();

            // Read current equipped weapon for partner display
            ReadCurrentWeapon();

            // Process pending rewards from network
            ProcessPendingReward();
        }
        catch { }
    }

    /// <summary>
    /// Read current equipped weapon from EquipManager for partner display.
    /// Updates CoopState.LocalPlayer.WeaponID.
    /// </summary>
    static void ReadCurrentWeapon()
    {
        if (_equipMgr == null) return;
        if (CoopState.FrameCount % 30 != 0) return; // every ~0.5s

        try
        {
            dynamic weapon = _equipMgr.Call("get_equipWeaponRight");
            if (weapon != null)
            {
                // Try to get weapon type ID
                try
                {
                    int weaponType = (int)(weapon as ManagedObject).Call("get_WeaponType");
                    CoopState.LocalPlayer.WeaponID = weaponType;
                }
                catch
                {
                    // Fallback: just mark as having a weapon (non-zero)
                    CoopState.LocalPlayer.WeaponID = 1;
                }
            }
            else
            {
                CoopState.LocalPlayer.WeaponID = 0; // no weapon
            }
        }
        catch { }
    }

    static void ProcessPendingReward()
    {
        string rewardID = null;
        lock (_pendingLock)
        {
            if (_pendingReward != null)
            {
                rewardID = _pendingReward;
                _pendingReward = null;
            }
        }

        if (string.IsNullOrEmpty(rewardID)) return;

        API.LogInfo("[COOP-InvSync] Received reward from partner: " + rewardID);

        if (_rewardMgr != null)
        {
            try
            {
                var vmStr = REFrameworkNET.VM.CreateString(rewardID);
                _rewardMgr.Call("unlockReward", vmStr, false);
                API.LogInfo("[COOP-InvSync] Reward unlocked locally: " + rewardID);
            }
            catch (Exception e)
            {
                API.LogError("[COOP-InvSync] unlockReward failed: " + e.Message);
            }
        }
    }

    // =====================================================================
    //  PUBLIC: Called by Core when receiving network messages
    // =====================================================================

    /// <summary>
    /// Queue a reward unlock from the network.
    /// </summary>
    public static void QueueRewardUnlock(string rewardID)
    {
        lock (_pendingLock)
        {
            _pendingReward = rewardID;
        }
    }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    static ManagedObject GetSingleton(string name)
    {
        try
        {
            dynamic s = API.GetManagedSingleton(name);
            if (s != null)
            {
                API.LogInfo("[COOP-InvSync] Found: " + name);
                return s as ManagedObject;
            }
        }
        catch { }
        return null;
    }

    static string ReadStringArg(Span<ulong> args, int index)
    {
        try
        {
            if (args[index] == 0) return null;
            ManagedObject strObj = ManagedObject.ToManagedObject(args[index]);
            return strObj != null ? strObj.ToString() : null;
        }
        catch { return null; }
    }

    // =====================================================================
    //  REFLECTION — Call Core plugin methods without direct reference
    // =====================================================================

    static Type _coreType = null;
    static System.Reflection.MethodInfo _coreSendRaw = null;

    static void CallCoreSendRaw(byte[] data)
    {
        try
        {
            if (_coreType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _coreType = asm.GetType("ResidentCOOP_Core"); if (_coreType != null) break; } catch { }
                }
            }
            if (_coreType == null) return;

            if (_coreSendRaw == null)
                _coreSendRaw = _coreType.GetMethod("SendRaw",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (_coreSendRaw != null)
                _coreSendRaw.Invoke(null, new object[] { data });
        }
        catch { }
    }
}
