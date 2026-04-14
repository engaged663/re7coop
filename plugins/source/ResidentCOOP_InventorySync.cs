// ResidentCOOP_InventorySync.cs
// Inventory synchronization with weapon sharing rules:
//
// RULES:
//   - Normal items (herbs, ammo, keys, etc.): NOT shared
//   - Weapons: SHARED to partner IF partner has inventory space
//   - If partner inventory is full: weapon is NOT shared (no forced drop)
//   - Rewards: Unlock synced to both players
//
// Systems used:
//   app.ItemManager        - item creation, weapon detection
//   app.InventorySystem    - get active player inventory
//   app.InventoryManager   - inventory state, slot checks
//   app.RewardManager      - reward unlock sync
//   app.Inventory          - component on player for direct inventory access

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_InventorySync
{
    static ManagedObject _itemMgr = null;
    static ManagedObject _inventorySys = null;
    static ManagedObject _inventoryMgr = null;
    static ManagedObject _rewardMgr = null;
    static bool _singletonsReady = false;
    static bool _hooksDone = false;

    // Track which weapons we've already shared (avoid duplicate sends)
    static System.Collections.Generic.HashSet<string> _sharedWeapons =
        new System.Collections.Generic.HashSet<string>();

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupWeaponDetectionHooks();
            SetupRewardHooks();
            API.LogInfo("[COOP-InvSync] Loaded. Weapons shared, items private.");
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
        _inventorySys = GetSingleton("app.InventorySystem");
        _inventoryMgr = GetSingleton("app.InventoryManager");
        _rewardMgr = GetSingleton("app.RewardManager");
    }

    // =====================================================================
    //  WEAPON DETECTION - Hook item pickup to detect weapons
    //
    //  When a player picks up a weapon (via ItemManager.setItemGotFlag or
    //  ItemManager.createItemInstance), we check if it's a weapon type.
    //  RE7 weapon IDs typically contain "wp" or "Weapon" patterns.
    //  We also check via ItemManager.getBulletNum - if an item has bullet
    //  data, it's likely a weapon.
    // =====================================================================

    static void SetupWeaponDetectionHooks()
    {
        TDB tdb = API.GetTDB();

        TypeDefinition imType = tdb.FindType("app.ItemManager");
        if (imType == null)
        {
            API.LogWarning("[COOP-InvSync] app.ItemManager not found");
            return;
        }

        // Hook setItemGotFlag - fires when player acquires any item
        Method gotFlag = imType.GetMethod("setItemGotFlag");
        if (gotFlag != null)
        {
            MethodHook.Create(gotFlag, false).AddPre(new MethodHook.PreHookDelegate(OnPreItemGot));
            API.LogInfo("[COOP-InvSync] Hooked ItemManager.setItemGotFlag");
        }

        // Hook createItemInstance - item creation in world
        Method createItem = imType.GetMethod("createItemInstance");
        if (createItem != null)
        {
            MethodHook.Create(createItem, false).AddPost(new MethodHook.PostHookDelegate(OnPostCreateItem));
            API.LogInfo("[COOP-InvSync] Hooked ItemManager.createItemInstance (post)");
        }

        // Dump ItemManager methods for reference
        DumpMethods(imType, "ItemManager");
    }

    static PreHookResult OnPreItemGot(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            // args[1] = this, args[2] = string itemID
            string itemID = ReadStringArg(args, 2);
            if (string.IsNullOrEmpty(itemID)) return PreHookResult.Continue;

            if (IsWeapon(itemID))
            {
                // Check if we already shared this weapon
                if (!_sharedWeapons.Contains(itemID))
                {
                    _sharedWeapons.Add(itemID);
                    API.LogInfo("[COOP-InvSync] WEAPON acquired: " + itemID + " -> sharing with partner");
                    BroadcastWeapon(itemID);
                }
            }
            else
            {
                // Normal item - don't share, just log occasionally
                if (CoopState.FrameCount % 60 == 0)
                    API.LogInfo("[COOP-InvSync] Item acquired (private): " + itemID);
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    static void OnPostCreateItem(ref ulong retval)
    {
        // Track item creation - primarily for logging
    }

    // =====================================================================
    //  REWARD SYNC - Unlock rewards on both sides
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

        // Hook unlockReward - when a reward is unlocked locally
        Method unlock = rmType.GetMethod("unlockReward");
        if (unlock != null)
        {
            MethodHook.Create(unlock, false).AddPre(new MethodHook.PreHookDelegate(OnPreUnlockReward));
            API.LogInfo("[COOP-InvSync] Hooked RewardManager.unlockReward");
        }

        DumpMethods(rmType, "RewardManager");
    }

    static PreHookResult OnPreUnlockReward(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            string rewardID = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(rewardID))
            {
                API.LogInfo("[COOP-InvSync] Reward unlocked: " + rewardID + " -> syncing");
                BroadcastReward(rewardID);
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
            ProcessIncomingWeapon();
            ProcessIncomingReward();
        }
        catch { }
    }

    static void ProcessIncomingWeapon()
    {
        // Get pending weapon from Core via reflection
        string weaponID = CallCoreConsume("ConsumePendingWeapon");
        if (string.IsNullOrEmpty(weaponID)) return;

        API.LogInfo("[COOP-InvSync] Received weapon share: " + weaponID);

        // Check if we have inventory space
        bool hasSpace = CheckInventorySpace();
        if (!hasSpace)
        {
            API.LogInfo("[COOP-InvSync] Inventory full - cannot receive weapon: " + weaponID);
            return;
        }

        // Give the weapon to the local player
        GiveWeaponToPlayer(weaponID);
    }

    static void ProcessIncomingReward()
    {
        string rewardID = CallCoreConsume("ConsumePendingReward");
        if (string.IsNullOrEmpty(rewardID)) return;

        API.LogInfo("[COOP-InvSync] Received reward unlock: " + rewardID);

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
    //  WEAPON IDENTIFICATION
    //  RE7 weapon item IDs typically follow patterns like:
    //    wp0000 (handgun), wp1000 (shotgun), wp2000 (burner), etc.
    //  We check prefixes and also query ItemManager for bullet data.
    // =====================================================================

    static bool IsWeapon(string itemID)
    {
        if (string.IsNullOrEmpty(itemID)) return false;

        string lower = itemID.ToLower();

        // Direct weapon ID patterns
        if (lower.StartsWith("wp")) return true;
        if (lower.Contains("weapon")) return true;
        if (lower.Contains("gun")) return true;
        if (lower.Contains("shotgun")) return true;
        if (lower.Contains("pistol")) return true;
        if (lower.Contains("knife")) return true;
        if (lower.Contains("grenade")) return true;
        if (lower.Contains("burner")) return true;
        if (lower.Contains("flamethrower")) return true;
        if (lower.Contains("machinegun")) return true;
        if (lower.Contains("magnum")) return true;
        if (lower.Contains("circular_saw")) return true;

        // Check via ItemManager if this item has bullet data (= weapon)
        if (_itemMgr != null)
        {
            try
            {
                var vmStr = REFrameworkNET.VM.CreateString(itemID);
                dynamic bulletNum = _itemMgr.Call("getBulletNum", vmStr);
                int num = (int)bulletNum;
                if (num > 0) return true;
            }
            catch { }
        }

        return false;
    }

    // =====================================================================
    //  INVENTORY SPACE CHECK
    // =====================================================================

    static bool CheckInventorySpace()
    {
        // Try via InventorySystem.getActivePlayerInventory
        if (_inventorySys != null)
        {
            try
            {
                dynamic inv = _inventorySys.Call("getActivePlayerInventory");
                if (inv != null)
                {
                    ManagedObject invMO = inv as ManagedObject;
                    // Try to check if inventory is full
                    try
                    {
                        dynamic isFull = invMO.Call("isFull");
                        if ((bool)isFull) return false;
                        return true;
                    }
                    catch { }

                    // Try checking count vs capacity
                    try
                    {
                        dynamic count = invMO.Call("get_Count");
                        dynamic capacity = invMO.Call("get_Capacity");
                        return (int)count < (int)capacity;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // If we can't determine, assume there's space
        return true;
    }

    // =====================================================================
    //  GIVE WEAPON TO PLAYER
    // =====================================================================

    static void GiveWeaponToPlayer(string weaponID)
    {
        if (_itemMgr == null) return;

        try
        {
            // Get player GameObject
            dynamic objectMgr = API.GetManagedSingleton("app.ObjectManager");
            if (objectMgr == null) return;

            dynamic playerObj = null;
            try { playerObj = objectMgr.PlayerObj; }
            catch { try { playerObj = (objectMgr as ManagedObject).Call("findActivePlayer"); } catch { } }

            if (playerObj == null) return;

            // Create the weapon item instance on the player
            var vmWeaponID = REFrameworkNET.VM.CreateString(weaponID);
            try
            {
                _itemMgr.Call("createItemInstance", playerObj, vmWeaponID);
                API.LogInfo("[COOP-InvSync] Weapon given to player: " + weaponID);
            }
            catch
            {
                // Fallback: just set the got flag
                try
                {
                    _itemMgr.Call("setItemGotFlag", vmWeaponID);
                    API.LogInfo("[COOP-InvSync] Weapon flag set: " + weaponID);
                }
                catch (Exception e)
                {
                    API.LogError("[COOP-InvSync] Failed to give weapon: " + e.Message);
                }
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-InvSync] GiveWeapon error: " + e.Message);
        }
    }

    // =====================================================================
    //  BROADCAST - Send weapon/reward to partner via Core
    // =====================================================================

    static void BroadcastWeapon(string weaponID)
    {
        try
        {
            Type coreType = FindType("ResidentCOOP_Core");
            if (coreType != null)
            {
                coreType.GetMethod("BroadcastWeaponShare").Invoke(null, new object[] { weaponID });
            }
        }
        catch { }
    }

    static void BroadcastReward(string rewardID)
    {
        try
        {
            Type coreType = FindType("ResidentCOOP_Core");
            if (coreType != null)
            {
                coreType.GetMethod("BroadcastRewardUnlock").Invoke(null, new object[] { rewardID });
            }
        }
        catch { }
    }

    static string CallCoreConsume(string methodName)
    {
        try
        {
            Type coreType = FindType("ResidentCOOP_Core");
            if (coreType != null)
            {
                object result = coreType.GetMethod(methodName).Invoke(null, null);
                return result as string;
            }
        }
        catch { }
        return null;
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

    static void DumpMethods(TypeDefinition type, string label)
    {
        try
        {
            var methods = type.GetMethods();
            if (methods == null) return;
            for (int i = 0; i < methods.Count; i++)
                API.LogInfo("[COOP-InvSync]   " + label + ": " + methods[i].GetName());
        }
        catch { }
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

    static Type FindType(string name)
    {
        System.Reflection.Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < asms.Length; i++)
        {
            try { Type t = asms[i].GetType(name); if (t != null) return t; } catch { }
        }
        return null;
    }
}
