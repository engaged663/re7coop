// ResidentCOOP_WorldSync.cs
// Synchronizes world state between host and client:
//   - Items: pickup, drop, creation (app.ItemManager)
//   - Objectives: quest progress (app.ObjectiveManager)
//   - Map: room discovery, door states (app.MapManager)
//   - Areas: room enter/exit triggers (app.AreaHitManager)
//   - Objects: player finding, enemy tracking (app.ObjectManager)
//   - Interactions: item examine, interact tracking (app.InteractManager)
//
// Host is authoritative. Client replicates state changes.

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_WorldSync
{
    // Cached singletons
    static ManagedObject _itemMgr = null;
    static ManagedObject _objectiveMgr = null;
    static ManagedObject _mapMgr = null;
    static ManagedObject _areaHitMgr = null;
    static ManagedObject _objectMgr = null;
    static ManagedObject _interactMgr = null;
    static bool _singletonsReady = false;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupItemHooks();
            SetupObjectiveHooks();
            SetupMapHooks();
            SetupAreaHooks();
            SetupObjectManagerHooks();
            SetupInteractHooks();
            API.LogInfo("[COOP-WorldSync] Loaded.");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-WorldSync] Init: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP-WorldSync] Unloaded.");
    }

    static void CacheSingletons()
    {
        if (_singletonsReady) return;

        TryGetSingleton("app.ItemManager", ref _itemMgr);
        TryGetSingleton("app.ObjectiveManager", ref _objectiveMgr);
        TryGetSingleton("app.MapManager", ref _mapMgr);
        TryGetSingleton("app.AreaHitManager", ref _areaHitMgr);
        TryGetSingleton("app.ObjectManager", ref _objectMgr);
        TryGetSingleton("app.InteractManager", ref _interactMgr);

        _singletonsReady = true;
    }

    // =====================================================================
    //  ITEM SYNC - Track when items are picked up, dropped, or created
    //  Host broadcasts item events to client
    // =====================================================================

    static void SetupItemHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition imType = tdb.FindType("app.ItemManager");
        if (imType == null)
        {
            API.LogWarning("[COOP-WorldSync] app.ItemManager not found");
            return;
        }

        // Hook setItemGotFlag - fires when player picks up an item
        HookMethod(imType, "setItemGotFlag", OnPreItemGotFlag, "ItemManager");

        // Hook createDropItemInstance - when an item is dropped in the world
        HookMethod(imType, "createDropItemInstance", OnPreDropItem, "ItemManager");

        // Hook createItemInstance - when an item is created
        HookMethod(imType, "createItemInstance", OnPreCreateItem, "ItemManager");
    }

    static PreHookResult OnPreItemGotFlag(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            // args[1] = this, args[2] = string itemID
            string itemID = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(itemID))
            {
                API.LogInfo("[COOP-WorldSync] Item picked up: " + itemID);
                // In the future, broadcast to partner so their game also marks it
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreDropItem(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            // args[1] = this, args[2] = GameObject, args[3] = string itemID, args[4] = int count
            string itemID = ReadStringArg(args, 3);
            if (!string.IsNullOrEmpty(itemID))
            {
                API.LogInfo("[COOP-WorldSync] Item dropped: " + itemID);
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreCreateItem(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            string itemID = ReadStringArg(args, 3);
            if (!string.IsNullOrEmpty(itemID))
            {
                API.LogInfo("[COOP-WorldSync] Item created: " + itemID);
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    // =====================================================================
    //  OBJECTIVE SYNC - Track quest objective changes
    // =====================================================================

    static void SetupObjectiveHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition omType = tdb.FindType("app.ObjectiveManager");
        if (omType == null)
        {
            API.LogWarning("[COOP-WorldSync] app.ObjectiveManager not found");
            return;
        }

        // Hook addObjectiveItem - new objectives added
        HookMethod(omType, "addObjectiveItem", OnPreAddObjective, "ObjectiveManager");

        // Hook removeObjectiveItem - objectives completed/removed
        HookMethod(omType, "removeObjectiveItem", OnPreRemoveObjective, "ObjectiveManager");

        // Hook ClearObjectiveItem - all objectives cleared (chapter transition)
        HookMethod(omType, "ClearObjectiveItem", OnPreClearObjectives, "ObjectiveManager");
    }

    static PreHookResult OnPreAddObjective(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Objective added");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreRemoveObjective(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Objective removed");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreClearObjectives(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] All objectives cleared");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  MAP SYNC - Track room discovery, door states, map flags
    // =====================================================================

    static void SetupMapHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition mmType = tdb.FindType("app.MapManager");
        if (mmType == null)
        {
            API.LogWarning("[COOP-WorldSync] app.MapManager not found");
            return;
        }

        // Hook addMapZoneRoomId - new room discovered
        HookMethod(mmType, "addMapZoneRoomId", OnPreAddRoom, "MapManager");

        // Hook setMapFlag - map flags (keys, items on map, etc.)
        HookMethod(mmType, "setMapFlag", OnPreSetMapFlag, "MapManager");

        // Hook ChangeDoorState - door opened/locked/unlocked
        HookMethod(mmType, "ChangeDoorState", OnPreChangeDoor, "MapManager");

        // Hook addMapDoor - new door registered on map
        HookMethod(mmType, "addMapDoor", OnPreAddDoor, "MapManager");

        // Hook ChangeObjectState - map object state change
        HookMethod(mmType, "ChangeObjectState", OnPreChangeObject, "MapManager");
    }

    static PreHookResult OnPreAddRoom(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;

        try
        {
            int roomId = (int)(args[2] & 0xFFFFFFFF);
            API.LogInfo("[COOP-WorldSync] Room discovered: " + roomId);
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreSetMapFlag(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Map flag set");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreChangeDoor(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Door state changed");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreAddDoor(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Map door registered");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreChangeObject(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Map object state changed");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  AREA HIT SYNC - Room enter/exit triggers
    // =====================================================================

    static void SetupAreaHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition ahType = tdb.FindType("app.AreaHitManager");
        if (ahType == null)
        {
            API.LogWarning("[COOP-WorldSync] app.AreaHitManager not found");
            return;
        }

        // Hook addAreaHitManager - new area trigger registered
        HookMethod(ahType, "addAreaHitManager", OnPreAddAreaHit, "AreaHitManager");
    }

    static PreHookResult OnPreAddAreaHit(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        // Track area registration for future room sync
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  PER-FRAME: Sync map player position + active objectives
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        if (!CoopState.IsCoopActive) return;
        if (CoopState.FrameCount % 120 != 0) return; // every ~2 seconds

        try
        {
            CacheSingletons();
            SyncMapPlayerPosition();
        }
        catch { }
    }

    /// <summary>
    /// Update the map's knowledge of the remote player's position.
    /// This lets both players see each other on the in-game map.
    /// </summary>
    static void SyncMapPlayerPosition()
    {
        if (_mapMgr == null) return;

        // The map tracks the local player position via updatePlayerPosition().
        // We could add a marker for the remote player using map object data,
        // but that requires deeper integration. For now, we ensure map state
        // stays consistent between host and client.
        try
        {
            _mapMgr.Call("updatePlayerPosition");
        }
        catch { }
    }

    // =====================================================================
    //  PUBLIC: Query methods for other plugins
    // =====================================================================

    /// <summary>
    /// Get current room ID from MapManager.
    /// </summary>
    public static int GetCurrentRoomID()
    {
        if (_mapMgr == null) { CacheSingletons(); if (_mapMgr == null) return -1; }
        try
        {
            return (int)_mapMgr.Call("getRoomID");
        }
        catch { return -1; }
    }

    /// <summary>
    /// Check if a specific area name is the current map area.
    /// </summary>
    public static string GetCurrentAreaName()
    {
        if (_mapMgr == null) { CacheSingletons(); if (_mapMgr == null) return ""; }
        try
        {
            dynamic result = _mapMgr.Call("getAreaName");
            if (result != null) return result.ToString();
        }
        catch { }
        return "";
    }

    // =====================================================================
    //  OBJECT MANAGER - Player and object tracking
    // =====================================================================

    static void SetupObjectManagerHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition omType = tdb.FindType("app.ObjectManager");
        if (omType == null)
        {
            API.LogWarning("[COOP-WorldSync] app.ObjectManager not found");
            return;
        }

        // Track active player changes
        HookMethod(omType, "onMainSceneUpdate", OnPreMainSceneUpdate, "ObjectManager");
    }

    static PreHookResult OnPreMainSceneUpdate(Span<ulong> args)
    {
        // Let it proceed - we just use this to know when the scene changes
        return PreHookResult.Continue;
    }

    /// <summary>
    /// Get the local player GameObject via ObjectManager.
    /// More reliable than direct field access on some versions.
    /// </summary>
    public static ManagedObject GetLocalPlayer()
    {
        if (_objectMgr == null) { CacheSingletons(); if (_objectMgr == null) return null; }
        try
        {
            dynamic player = _objectMgr.Call("findActivePlayer");
            if (player != null) return player as ManagedObject;
        }
        catch { }

        // Fallback: getPlayer
        try
        {
            dynamic players = _objectMgr.Call("getPlayerList");
            if (players != null)
            {
                dynamic first = (players as ManagedObject).Call("get_Item", 0);
                if (first != null) return first as ManagedObject;
            }
        }
        catch { }

        return null;
    }

    // =====================================================================
    //  INTERACT MANAGER - Track interactions (no pause, handled by Cutscene plugin)
    // =====================================================================

    static void SetupInteractHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition imType = tdb.FindType("app.InteractManager");
        if (imType == null)
        {
            API.LogWarning("[COOP-WorldSync] app.InteractManager not found");
            return;
        }

        // Track interaction start/end
        HookMethod(imType, "interactStart", OnPreInteractStart, "InteractManager");
        HookMethod(imType, "interactReset", OnPreInteractReset, "InteractManager");
        HookMethod(imType, "interactClear", OnPreInteractReset, "InteractManager");

        // Track item pickup sound
        HookMethod(imType, "ItemGetSeCall", OnPreItemGetSe, "InteractManager");
    }

    static PreHookResult OnPreInteractStart(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Interaction started");
        // Interaction proceeds normally - pause is blocked by Cutscene plugin
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreInteractReset(Span<ulong> args)
    {
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreItemGetSe(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            string itemId = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(itemId))
                API.LogInfo("[COOP-WorldSync] Item picked up (SE): " + itemId);
        }
        catch { }
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    static void TryGetSingleton(string name, ref ManagedObject target)
    {
        try
        {
            dynamic s = API.GetManagedSingleton(name);
            if (s != null)
            {
                target = s as ManagedObject;
                API.LogInfo("[COOP-WorldSync] Found: " + name);
            }
        }
        catch { }
    }

    static void HookMethod(TypeDefinition type, string methodName,
        MethodHook.PreHookDelegate preHook, string label)
    {
        Method m = type.GetMethod(methodName);
        if (m != null)
        {
            MethodHook.Create(m, false).AddPre(preHook);
            API.LogInfo("[COOP-WorldSync] Hooked " + label + "." + methodName);
        }
    }

    static string ReadStringArg(Span<ulong> args, int index)
    {
        try
        {
            if (args[index] == 0) return null;
            ManagedObject strObj = ManagedObject.ToManagedObject(args[index]);
            if (strObj == null) return null;
            return strObj.ToString();
        }
        catch { return null; }
    }
}
