// ResidentCOOP_WorldSync.cs
// World state synchronization: items, doors, objectives, map flags.
//
// Shared Inventory model:
//   When either player picks up an item, the item is broadcast to BOTH.
//   The partner's game also marks the item as obtained (setItemGotFlag).
//   This means both players effectively share the same inventory state.
//
// Door/Map sync:
//   Host is authoritative. When doors open, map flags change, or objectives
//   update on the host, these are broadcast to the client.
//
// Key singletons:
//   app.ItemManager           — item acquisition, creation
//   app.MapManager            — doors, rooms, map flags
//   app.ObjectiveManager      — quest objectives
//   app.InteractManager       — interactions
//   app.AreaHitManager        — zone triggers
//   app.ObjectManager         — player/object tracking

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_WorldSync
{
    static ManagedObject _itemMgr = null;
    static ManagedObject _mapMgr = null;
    static ManagedObject _objectiveMgr = null;
    static ManagedObject _interactMgr = null;
    static ManagedObject _areaHitMgr = null;
    static ManagedObject _objectMgr = null;
    static bool _singletonsSearched = false;

    // Pending world events from the network (to apply on game thread)
    static List<WorldEvent> _pendingEvents = new List<WorldEvent>();
    static readonly object _pendingLock = new object();

    struct WorldEvent
    {
        public byte Type;      // 0=item, 1=door, 2=objective, 3=mapFlag
        public string ID;
        public int IntParam;
        public bool BoolParam;
    }

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupItemHooks();
            SetupObjectiveHooks();
            SetupMapHooks();
            SetupInteractHooks();
            SetupAreaHooks();
            API.LogInfo("[COOP-WorldSync] Loaded. Shared inventory mode.");
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
        if (_singletonsSearched) return;
        _singletonsSearched = true;

        TryGetSingleton("app.ItemManager", ref _itemMgr);
        TryGetSingleton("app.MapManager", ref _mapMgr);
        TryGetSingleton("app.ObjectiveManager", ref _objectiveMgr);
        TryGetSingleton("app.InteractManager", ref _interactMgr);
        TryGetSingleton("app.AreaHitManager", ref _areaHitMgr);
        TryGetSingleton("app.ObjectManager", ref _objectMgr);
    }

    // =====================================================================
    //  ITEM SYNC — Shared inventory: any item pickup is shared
    //  Hook setItemGotFlag to detect and broadcast item acquisition.
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

        // Hook setItemGotFlag — fires when any item is acquired
        HookMethod(imType, "setItemGotFlag", OnPreItemGotFlag, "ItemManager");

        // Hook dropItemFromObserve — item dropped from observation
        HookMethod(imType, "dropItemFromObserve", OnPreDropItem, "ItemManager");
    }

    static PreHookResult OnPreItemGotFlag(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            string itemID = ReadStringArg(args, 2);
            if (string.IsNullOrEmpty(itemID)) return PreHookResult.Continue;

            API.LogInfo("[COOP-WorldSync] Item acquired: " + itemID + " -> broadcasting to partner");

            // Broadcast as a GameEvent so partner also gets the item
            BroadcastWorldEvent(0, itemID, 0, true);
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreDropItem(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  OBJECTIVE SYNC — Track quest objectives and broadcast changes
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

        HookMethod(omType, "addObjectiveItem", OnPreAddObjective, "ObjectiveManager");
        HookMethod(omType, "removeObjectiveItem", OnPreRemoveObjective, "ObjectiveManager");
        HookMethod(omType, "ClearObjectiveItem", OnPreClearObjectives, "ObjectiveManager");
    }

    static PreHookResult OnPreAddObjective(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            string objID = ReadStringArg(args, 2);
            API.LogInfo("[COOP-WorldSync] Objective added: " + (objID ?? "unknown"));
            BroadcastWorldEvent(2, objID ?? "", 1, true); // type=2, intParam=1 (add)
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreRemoveObjective(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            string objID = ReadStringArg(args, 2);
            API.LogInfo("[COOP-WorldSync] Objective completed: " + (objID ?? "unknown"));
            BroadcastWorldEvent(2, objID ?? "", 0, true); // type=2, intParam=0 (remove)
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreClearObjectives(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] All objectives cleared (chapter transition)");
        BroadcastWorldEvent(2, "CLEAR_ALL", -1, true);
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  MAP SYNC — Doors, rooms, map flags
    //  Host is authoritative for map state changes.
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

        HookMethod(mmType, "addMapZoneRoomId", OnPreAddRoom, "MapManager");
        HookMethod(mmType, "setMapFlag", OnPreSetMapFlag, "MapManager");
        HookMethod(mmType, "ChangeDoorState", OnPreChangeDoor, "MapManager");
        HookMethod(mmType, "addMapDoor", OnPreAddDoor, "MapManager");
        HookMethod(mmType, "ChangeObjectState", OnPreChangeObject, "MapManager");
    }

    static PreHookResult OnPreAddRoom(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;

        try
        {
            int roomId = (int)(args[2] & 0xFFFFFFFF);
            API.LogInfo("[COOP-WorldSync] Room discovered: " + roomId);
            BroadcastWorldEvent(3, "room", roomId, true); // type=3 mapFlag
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreSetMapFlag(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;

        try
        {
            int flagId = (int)(args[2] & 0xFFFFFFFF);
            bool value = (args[3] & 1) != 0;
            API.LogInfo("[COOP-WorldSync] Map flag set: " + flagId + " = " + value);
            BroadcastWorldEvent(3, "flag_" + flagId, flagId, value);
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreChangeDoor(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            int doorId = (int)(args[2] & 0xFFFFFFFF);
            int state = (int)(args[3] & 0xFFFFFFFF);
            API.LogInfo("[COOP-WorldSync] Door state change: door=" + doorId + " state=" + state);
            BroadcastWorldEvent(1, "door_" + doorId, state, true);
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreAddDoor(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreChangeObject(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Map object state changed");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  AREA HIT — Zone triggers
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

        HookMethod(ahType, "addAreaHitManager", OnPreAddAreaHit, "AreaHitManager");
    }

    static PreHookResult OnPreAddAreaHit(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  INTERACT MANAGER — Track interactions for sync
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

        HookMethod(imType, "interactStart", OnPreInteractStart, "InteractManager");
        HookMethod(imType, "ItemGetSeCall", OnPreItemGetSe, "InteractManager");
    }

    static PreHookResult OnPreInteractStart(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-WorldSync] Interaction started");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreItemGetSe(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            string itemId = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(itemId))
                API.LogInfo("[COOP-WorldSync] Item SE: " + itemId);
        }
        catch { }
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  PER-FRAME: Process incoming world events and sync map
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        if (!CoopState.IsCoopActive) return;

        try
        {
            CacheSingletons();

            // Process pending world events from network
            ProcessPendingEvents();

            // Sync map position every ~2 seconds
            if (CoopState.FrameCount % 120 == 0)
            {
                SyncMapPlayerPosition();
            }
        }
        catch { }
    }

    /// <summary>
    /// Process world events received from the partner.
    /// Called on the game thread.
    /// </summary>
    static void ProcessPendingEvents()
    {
        List<WorldEvent> events = null;
        lock (_pendingLock)
        {
            if (_pendingEvents.Count > 0)
            {
                events = new List<WorldEvent>(_pendingEvents);
                _pendingEvents.Clear();
            }
        }

        if (events == null) return;

        for (int i = 0; i < events.Count; i++)
        {
            WorldEvent evt = events[i];
            try
            {
                switch (evt.Type)
                {
                    case 0: // Item acquired
                        ApplyItemGot(evt.ID);
                        break;
                    case 1: // Door state change
                        ApplyDoorChange(evt.ID, evt.IntParam);
                        break;
                    case 2: // Objective change
                        ApplyObjectiveChange(evt.ID, evt.IntParam);
                        break;
                    case 3: // Map flag
                        ApplyMapFlag(evt.ID, evt.IntParam, evt.BoolParam);
                        break;
                }
            }
            catch (Exception e)
            {
                API.LogError("[COOP-WorldSync] Event apply error: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Apply an item acquisition on the local game.
    /// This is the shared inventory mechanism — when partner picks up an item,
    /// we also set the item got flag locally.
    /// </summary>
    static void ApplyItemGot(string itemID)
    {
        if (_itemMgr == null) return;

        try
        {
            var vmID = REFrameworkNET.VM.CreateString(itemID);
            _itemMgr.Call("setItemGotFlag", vmID);
            API.LogInfo("[COOP-WorldSync] Item synced from partner: " + itemID);
        }
        catch (Exception e)
        {
            API.LogError("[COOP-WorldSync] ApplyItemGot failed: " + e.Message);
        }
    }

    /// <summary>
    /// Apply a door state change on the local game.
    /// </summary>
    static void ApplyDoorChange(string doorKey, int state)
    {
        if (_mapMgr == null) return;

        try
        {
            // Extract door ID from key "door_123"
            string[] parts = doorKey.Split('_');
            if (parts.Length >= 2)
            {
                int doorId;
                if (int.TryParse(parts[1], out doorId))
                {
                    _mapMgr.Call("ChangeDoorState", doorId, state);
                    API.LogInfo("[COOP-WorldSync] Door " + doorId + " state -> " + state);
                }
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-WorldSync] ApplyDoorChange failed: " + e.Message);
        }
    }

    /// <summary>
    /// Apply an objective change on the local game.
    /// </summary>
    static void ApplyObjectiveChange(string objID, int action)
    {
        if (_objectiveMgr == null) return;

        try
        {
            if (objID == "CLEAR_ALL")
            {
                _objectiveMgr.Call("ClearObjectiveItem");
                API.LogInfo("[COOP-WorldSync] All objectives cleared (from partner)");
            }
            else if (action == 1) // add
            {
                var vmID = REFrameworkNET.VM.CreateString(objID);
                _objectiveMgr.Call("addObjectiveItem", vmID);
                API.LogInfo("[COOP-WorldSync] Objective added from partner: " + objID);
            }
            else if (action == 0) // remove
            {
                var vmID = REFrameworkNET.VM.CreateString(objID);
                _objectiveMgr.Call("removeObjectiveItem", vmID);
                API.LogInfo("[COOP-WorldSync] Objective completed from partner: " + objID);
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-WorldSync] ApplyObjective failed: " + e.Message);
        }
    }

    /// <summary>
    /// Apply a map flag change on the local game.
    /// </summary>
    static void ApplyMapFlag(string flagKey, int id, bool value)
    {
        if (_mapMgr == null) return;

        try
        {
            if (flagKey == "room")
            {
                _mapMgr.Call("addMapZoneRoomId", id);
                API.LogInfo("[COOP-WorldSync] Room discovered from partner: " + id);
            }
            else
            {
                _mapMgr.Call("setMapFlag", id, value);
                API.LogInfo("[COOP-WorldSync] Map flag from partner: " + id + " = " + value);
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-WorldSync] ApplyMapFlag failed: " + e.Message);
        }
    }

    /// <summary>
    /// Update map's knowledge of player positions.
    /// </summary>
    static void SyncMapPlayerPosition()
    {
        if (_mapMgr == null) return;
        try { _mapMgr.Call("updatePlayerPosition"); } catch { }
    }

    // =====================================================================
    //  PUBLIC: Called by Core when receiving world events from network
    // =====================================================================

    /// <summary>
    /// Queue a world event received from the network to be applied on the game thread.
    /// Called by ResidentCOOP_Core when it receives a GameEvent message.
    /// </summary>
    public static void QueueWorldEvent(byte type, string id, int intParam, bool boolParam)
    {
        lock (_pendingLock)
        {
            _pendingEvents.Add(new WorldEvent
            {
                Type = type,
                ID = id,
                IntParam = intParam,
                BoolParam = boolParam
            });
        }
    }

    // =====================================================================
    //  PUBLIC: Query methods for other plugins
    // =====================================================================

    public static int GetCurrentRoomID()
    {
        if (_mapMgr == null) { CacheSingletons(); if (_mapMgr == null) return -1; }
        try { return (int)_mapMgr.Call("getRoomID"); }
        catch { return -1; }
    }

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

    public static ManagedObject GetLocalPlayer()
    {
        if (_objectMgr == null) { CacheSingletons(); if (_objectMgr == null) return null; }
        try
        {
            dynamic player = (_objectMgr as ManagedObject).Call("getPlayer");
            if (player != null) return player as ManagedObject;
        }
        catch { }
        return null;
    }

    // =====================================================================
    //  BROADCAST — Send world events to partner
    // =====================================================================

    static void BroadcastWorldEvent(byte type, string id, int intParam, bool boolParam)
    {
        try
        {
            // Encode as GameEvent message: type|id|int|bool
            string payload = type + "|" + (id ?? "") + "|" + intParam + "|" + (boolParam ? "1" : "0");
            byte[] data = NetProtocol.Frame(MessageType.GameEvent,
                System.Text.Encoding.UTF8.GetBytes(payload));
            CallCoreSendRaw(data);
        }
        catch { }
    }

    /// <summary>
    /// Called by Core when receiving a GameEvent message.
    /// Parses the payload and queues the event for processing.
    /// </summary>
    public static void HandleIncomingGameEvent(byte[] payload)
    {
        try
        {
            string data = System.Text.Encoding.UTF8.GetString(payload);
            string[] parts = data.Split('|');
            if (parts.Length < 4) return;

            byte type = byte.Parse(parts[0]);
            string id = parts[1];
            int intParam = int.Parse(parts[2]);
            bool boolParam = parts[3] == "1";

            QueueWorldEvent(type, id, intParam, boolParam);
        }
        catch (Exception e)
        {
            API.LogError("[COOP-WorldSync] HandleIncomingGameEvent: " + e.Message);
        }
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

    // =====================================================================
    //  REFLECTION — Call Core plugin methods without direct reference
    //  REFramework compiles each .cs individually, so no direct cross-refs.
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
                _coreSendRaw = _coreType.GetMethod("SendRaw", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (_coreSendRaw != null)
                _coreSendRaw.Invoke(null, new object[] { data });
        }
        catch { }
    }
}
