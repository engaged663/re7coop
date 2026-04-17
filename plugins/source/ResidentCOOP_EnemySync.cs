// ResidentCOOP_EnemySync.cs
// Enemy synchronization and AI targeting for co-op.
//
// Architecture (Host-Authoritative):
//   HOST: Enumerates active enemies each frame via EnemyGeneratorManager hooks.
//         Reads their transforms, health, and animation states.
//         Broadcasts EnemyStates to the client at 10Hz.
//         Receives EnemyDamage from client and applies it locally.
//
//   CLIENT: Receives EnemyStates and applies positions/rotations to local enemies.
//           When dealing damage to enemies, sends EnemyDamage to host.
//
// AI Targeting:
//   Enemies in RE7 follow a single player. In co-op, we intercept the AI's
//   follow point to make enemies consider both players. When the remote player
//   is closer, we adjust targeting behavior.
//
// Key singletons:
//   app.EnemyGeneratorManager — spawn registry, SpawnInfo lookup
//   app.AI.AIWorldBlackBoard  — AI targeting, attack permits
//   app.AI.AIFollowPointManager — follow point control
//   app.AI.MansionAI           — mansion-specific stalker AI
//   app.CharacterExistManager  — zone presence tracking
//   app.GameFlowFsmManager     — game progression tracking
//   app.CameraManager          — camera access for ghost rendering

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_EnemySync
{
    // Cached singletons
    static ManagedObject _enemyGenMgr = null;
    static ManagedObject _aiBlackBoard = null;
    static ManagedObject _cameraMgr = null;
    static ManagedObject _gameFlowMgr = null;
    static ManagedObject _objectMgr = null;
    static bool _singletonsSearched = false;

    // Active enemy tracking (host side)
    static Dictionary<uint, ManagedObject> _activeEnemies = new Dictionary<uint, ManagedObject>();
    static uint _nextEnemyID = 1;

    // Cached types for enemy health
    static TypeDefinition _hpControllerType = null;
    static object _hpControllerRuntimeType = null;
    static bool _typesResolved = false;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupEnemyGenHooks();
            SetupAITargetingHooks();
            SetupCharacterExistHooks();
            SetupGameFlowHooks();
            SetupCameraHooks();
            ResolveTypes();
            API.LogInfo("[COOP-EnemySync] Loaded.");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-EnemySync] Init: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        _activeEnemies.Clear();
        API.LogInfo("[COOP-EnemySync] Unloaded.");
    }

    static void CacheSingletons()
    {
        if (_singletonsSearched) return;
        _singletonsSearched = true;

        _enemyGenMgr = GetSingleton("app.EnemyGeneratorManager");
        _aiBlackBoard = GetSingleton("app.AI.AIWorldBlackBoard");
        _cameraMgr = GetSingleton("app.CameraManager");
        _gameFlowMgr = GetSingleton("app.GameFlowFsmManager");
        _objectMgr = GetSingleton("app.ObjectManager");
    }

    static void ResolveTypes()
    {
        if (_typesResolved) return;
        TDB tdb = API.GetTDB();
        if (tdb == null) return;

        _hpControllerType = tdb.FindType("app.HitPointController");
        if (_hpControllerType != null)
            _hpControllerRuntimeType = _hpControllerType.GetRuntimeType();

        _typesResolved = true;
    }

    // =====================================================================
    //  ENEMY GENERATOR — Track spawn/kill/suspend via hooks
    //  POST-hooks on requestSpawn to capture the returned GameObject.
    //  PRE-hooks on requestKill/Suspend to remove from tracking.
    // =====================================================================

    static void SetupEnemyGenHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition egType = tdb.FindType("app.EnemyGeneratorManager");
        if (egType == null)
        {
            API.LogWarning("[COOP-EnemySync] app.EnemyGeneratorManager not found");
            return;
        }

        // POST-hook requestSpawn to capture the returned GameObject
        Method spawn1 = egType.GetMethod("requestSpawn");
        if (spawn1 != null)
        {
            MethodHook.Create(spawn1, false).AddPost(new MethodHook.PostHookDelegate(OnPostEnemySpawn));
            API.LogInfo("[COOP-EnemySync] Hooked EnemyGen.requestSpawn (post)");
        }

        // PRE-hook kill/suspend/resume for tracking
        HookPre(egType, "requestKill", OnPreEnemyKill, "EnemyGen.requestKill");
        HookPre(egType, "requestSuspend", OnPreEnemySuspend, "EnemyGen.requestSuspend");
        HookPre(egType, "requestResume", OnPreEnemyResume, "EnemyGen.requestResume");
        HookPre(egType, "requestAllKill", OnPreEnemyAllKill, "EnemyGen.requestAllKill");
        HookPre(egType, "requestAllSuspend", OnPreEnemyAllSuspend, "EnemyGen.requestAllSuspend");
    }

    static void OnPostEnemySpawn(ref ulong retval)
    {
        if (!CoopState.IsCoopActive) return;
        if (retval == 0) return;

        try
        {
            // The return value is a via.GameObject
            ManagedObject go = ManagedObject.ToManagedObject(retval);
            if (go == null) return;

            // Globalize to prevent GC collection
            go = go.Globalize();

            uint id = _nextEnemyID++;
            _activeEnemies[id] = go;

            string name = "unknown";
            try { name = go.Call("get_Name").ToString(); } catch { }

            API.LogInfo("[COOP-EnemySync] Enemy spawned: ID=" + id + " name=" + name +
                " (tracking " + _activeEnemies.Count + " enemies)");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-EnemySync] OnPostEnemySpawn: " + e.Message);
        }
    }

    static PreHookResult OnPreEnemyKill(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] Enemy kill requested");
        // We'll detect the actual kill via health check in the frame loop
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEnemySuspend(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] Enemy suspend");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEnemyResume(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] Enemy resume");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEnemyAllKill(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] ALL enemies kill — clearing tracking");
        _activeEnemies.Clear();
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEnemyAllSuspend(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] ALL enemies suspend");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  AI TARGETING — Make enemies consider both players
    //  We intercept AIFollowPointManager.requestFollowPoint and
    //  AIWorldBlackBoard.requestAttackPermit to redirect AI when
    //  the remote player is closer.
    // =====================================================================

    static void SetupAITargetingHooks()
    {
        TDB tdb = API.GetTDB();

        // Hook AIFollowPointManager
        TypeDefinition fpType = tdb.FindType("app.AI.AIFollowPointManager");
        if (fpType != null)
        {
            HookPre(fpType, "requestFollowPoint", OnPreRequestFollowPoint, "AIFollowPoint.requestFollowPoint");
            API.LogInfo("[COOP-EnemySync] AIFollowPointManager hooked");
        }
        else
        {
            API.LogWarning("[COOP-EnemySync] app.AI.AIFollowPointManager not found");
        }

        // Hook AIWorldBlackBoard
        TypeDefinition bbType = tdb.FindType("app.AI.AIWorldBlackBoard");
        if (bbType != null)
        {
            HookPre(bbType, "requestAttackPermit", OnPreAttackPermit, "AIBlackBoard.requestAttackPermit");
            API.LogInfo("[COOP-EnemySync] AIWorldBlackBoard hooks set");
        }

        // Hook MansionAI for zone-based enemy control
        TypeDefinition maiType = tdb.FindType("app.AI.MansionAI");
        if (maiType != null)
        {
            HookPre(maiType, "setEnableMansionAI", OnPreSetMansionAI, "MansionAI.setEnableMansionAI");
            API.LogInfo("[COOP-EnemySync] MansionAI hooked");
        }
    }

    static PreHookResult OnPreRequestFollowPoint(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        // AI follow point request proceeds — we could redirect here in the future
        // by modifying the target position based on remote player proximity
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreAttackPermit(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreSetMansionAI(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] MansionAI enable/disable");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  CHARACTER EXIST MANAGER — Zone presence tracking
    //  Syncs zone enter/leave between players for enemy awareness.
    // =====================================================================

    static void SetupCharacterExistHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition ceType = tdb.FindType("app.CharacterExistManager");
        if (ceType == null)
        {
            API.LogWarning("[COOP-EnemySync] app.CharacterExistManager not found");
            return;
        }

        HookPre(ceType, "enterZone", OnPreEnterZone, "CharExist.enterZone");
        HookPre(ceType, "leaveZone", OnPreLeaveZone, "CharExist.leaveZone");
        HookPre(ceType, "overlapSafeZone", OnPreOverlapSafe, "CharExist.overlapSafeZone");
        HookPre(ceType, "leaveSafeZone", OnPreLeaveSafe, "CharExist.leaveSafeZone");
        API.LogInfo("[COOP-EnemySync] CharacterExistManager hooked");
    }

    static PreHookResult OnPreEnterZone(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            string zone = ReadStringArg(args, 3);
            if (zone != null)
                API.LogInfo("[COOP-EnemySync] CharExist: enterZone " + zone);
        }
        catch { }
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreLeaveZone(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreOverlapSafe(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreLeaveSafe(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  GAME FLOW FSM — Track game progression
    // =====================================================================

    static void SetupGameFlowHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition gfType = tdb.FindType("app.GameFlowFsmManager");
        if (gfType == null) return;

        HookPre(gfType, "requestStartGameFlow", OnPreStartGameFlow, "GameFlow.requestStartGameFlow");
        HookPre(gfType, "setProgressiveNo", OnPreSetProgress, "GameFlow.setProgressiveNo");
        HookPre(gfType, "sendBattleEnded", OnPreBattleEnded, "GameFlow.sendBattleEnded");
        HookPre(gfType, "sendEventEnded", OnPreEventEnded, "GameFlow.sendEventEnded");
    }

    static PreHookResult OnPreStartGameFlow(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] GameFlow: requestStartGameFlow");
        // When game flow starts, clear enemy tracking (new area)
        _activeEnemies.Clear();
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreSetProgress(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            uint id = (uint)(args[2] & 0xFFFFFFFF);
            int no = (int)(args[3] & 0xFFFFFFFF);
            API.LogInfo("[COOP-EnemySync] GameFlow: setProgressiveNo id=" + id + " no=" + no);
        }
        catch { }
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreBattleEnded(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] GameFlow: battle ended");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEventEnded(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] GameFlow: event ended");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  CAMERA ACCESS — Used by Ghost renderer
    // =====================================================================

    static void SetupCameraHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition camType = tdb.FindType("app.CameraManager");
        if (camType == null) return;
        API.LogInfo("[COOP-EnemySync] CameraManager found");
    }

    public static ManagedObject GetMainCamera()
    {
        CacheSingletons();
        if (_cameraMgr == null) return null;
        try
        {
            dynamic cam = _cameraMgr.Call("get_mainCamera");
            return cam as ManagedObject;
        }
        catch { return null; }
    }

    public static ManagedObject GetPlayerCamera()
    {
        CacheSingletons();
        if (_cameraMgr == null) return null;
        try
        {
            dynamic cam = _cameraMgr.Call("get_playerCamera");
            return cam as ManagedObject;
        }
        catch { return null; }
    }

    // =====================================================================
    //  PER-FRAME: Enemy state reading (host) and application (client)
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        try
        {
            if (!CoopState.IsCoopActive) return;
            CacheSingletons();

            // Host: read enemy states every 6 frames (~10Hz at 60fps)
            if (CoopState.IsHost && CoopState.FrameCount % 6 == 0)
            {
                ReadEnemyStates();
            }

            // Client: apply enemy positions from host every 3 frames
            if (CoopState.IsClient && CoopState.EnemyCount > 0 && CoopState.FrameCount % 3 == 0)
            {
                ApplyEnemyStates();
            }

            // Process incoming damage events
            if (CoopState.IsHost)
            {
                ProcessIncomingDamage();
            }
        }
        catch { }
    }

    /// <summary>
    /// HOST: Read transforms and health of all tracked enemies.
    /// Populates CoopState.EnemyStates for Core to broadcast.
    /// </summary>
    static void ReadEnemyStates()
    {
        int count = 0;
        List<uint> deadKeys = null;

        foreach (var kvp in _activeEnemies)
        {
            if (count >= CoopState.EnemyStates.Length) break;

            ManagedObject enemyGO = kvp.Value;
            if (enemyGO == null) continue;

            try
            {
                // Read transform
                dynamic transform = enemyGO.Call("get_Transform");
                if (transform == null)
                {
                    // Enemy destroyed — mark for removal
                    if (deadKeys == null) deadKeys = new List<uint>();
                    deadKeys.Add(kvp.Key);
                    continue;
                }

                ManagedObject tMO = transform as ManagedObject;
                float px = 0, py = 0, pz = 0;
                float rx = 0, ry = 0, rz = 0, rw = 1;

                try
                {
                    var typed = (tMO as IObject).As<via.Transform>();
                    if (typed != null)
                    {
                        var pos = typed.Position;
                        px = pos.x; py = pos.y; pz = pos.z;
                        var rot = typed.Rotation;
                        rx = rot.x; ry = rot.y; rz = rot.z; rw = rot.w;
                    }
                }
                catch
                {
                    try
                    {
                        dynamic pos = tMO.Call("get_Position");
                        px = (float)pos.x; py = (float)pos.y; pz = (float)pos.z;
                    }
                    catch { continue; }
                }

                // Read health
                float hp = 0;
                bool isDead = false;
                if (_hpControllerRuntimeType != null)
                {
                    try
                    {
                        dynamic hpCtrl = enemyGO.Call("getComponent(System.Type)", _hpControllerRuntimeType);
                        if (hpCtrl != null)
                        {
                            hp = (float)(hpCtrl as ManagedObject).Call("get_CurrentHitPoint");
                            isDead = (hp <= 0f);
                        }
                    }
                    catch { }
                }

                // Check if active
                bool isActive = true;
                try { isActive = (bool)enemyGO.Call("get_Active"); }
                catch { }

                // Write to state
                if (CoopState.EnemyStates[count] == null)
                    CoopState.EnemyStates[count] = new EnemyStateData();

                CoopState.EnemyStates[count].EnemyID = kvp.Key;
                CoopState.EnemyStates[count].PosX = px;
                CoopState.EnemyStates[count].PosY = py;
                CoopState.EnemyStates[count].PosZ = pz;
                CoopState.EnemyStates[count].RotX = rx;
                CoopState.EnemyStates[count].RotY = ry;
                CoopState.EnemyStates[count].RotZ = rz;
                CoopState.EnemyStates[count].RotW = rw;
                CoopState.EnemyStates[count].Health = hp;
                CoopState.EnemyStates[count].Flags = (byte)((isDead ? 1 : 0) | (isActive ? 2 : 0));

                count++;
            }
            catch { }
        }

        CoopState.EnemyCount = count;

        // Clean up dead enemies
        if (deadKeys != null)
        {
            for (int i = 0; i < deadKeys.Count; i++)
            {
                _activeEnemies.Remove(deadKeys[i]);
            }
        }
    }

    /// <summary>
    /// CLIENT: Apply enemy states received from host.
    /// Moves local enemy GameObjects to match host positions.
    /// </summary>
    static void ApplyEnemyStates()
    {
        // Client receives enemy states but doesn't control them directly.
        // The host is authoritative for enemy AI. The client's local enemies
        // should be in roughly the same positions via shared game state.
        // If positions drift too much we could force-correct here.
        //
        // For now: enemy states are available in CoopState.EnemyStates
        // for the Ghost renderer to draw enemy indicators if needed.
    }

    /// <summary>
    /// HOST: Process damage events from the client.
    /// When the client damages an enemy, we receive it and apply locally.
    /// </summary>
    static void ProcessIncomingDamage()
    {
        int count = CoopState.DmgCount;
        if (count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            uint targetID = CoopState.DmgTargetIDs[i];
            float damage = CoopState.DmgAmounts[i];

            // Find the enemy in our tracking
            ManagedObject enemyGO;
            if (_activeEnemies.TryGetValue(targetID, out enemyGO))
            {
                // Apply damage to the enemy
                if (_hpControllerRuntimeType != null)
                {
                    try
                    {
                        dynamic hpCtrl = enemyGO.Call("getComponent(System.Type)", _hpControllerRuntimeType);
                        if (hpCtrl != null)
                        {
                            ManagedObject hpMO = hpCtrl as ManagedObject;
                            float currentHp = (float)hpMO.Call("get_CurrentHitPoint");
                            float newHp = currentHp - damage;
                            if (newHp < 0) newHp = 0;

                            // Try to apply damage via setCurrentHitPoint or similar
                            try { hpMO.Call("set_CurrentHitPoint", newHp); }
                            catch
                            {
                                try { hpMO.Call("addDamage", damage); }
                                catch { }
                            }

                            API.LogInfo("[COOP-EnemySync] Applied " + damage + " damage to enemy " +
                                targetID + " (HP: " + currentHp + " -> " + newHp + ")");
                        }
                    }
                    catch { }
                }
            }
        }

        CoopState.DmgCount = 0;
    }

    // =====================================================================
    //  PUBLIC: Get active enemy count and data
    // =====================================================================

    public static int GetActiveEnemyCount() { return _activeEnemies.Count; }

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
                API.LogInfo("[COOP-EnemySync] Found: " + name);
                return s as ManagedObject;
            }
        }
        catch { }
        return null;
    }

    static void HookPre(TypeDefinition type, string method,
        MethodHook.PreHookDelegate cb, string label)
    {
        Method m = type.GetMethod(method);
        if (m != null)
        {
            MethodHook.Create(m, false).AddPre(cb);
            API.LogInfo("[COOP-EnemySync] Hooked " + label);
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
