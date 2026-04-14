// ResidentCOOP_EnemySync.cs
// Enemy synchronization and AI targeting for co-op.
//
// Key systems:
//   app.EnemyGeneratorManager - spawn/kill/suspend enemies (host authoritative)
//   app.AI.AIWorldBlackBoard  - AI targeting, attack permits, enemy queries
//   app.AI.AINavigationManager - pathfinding zones
//   app.AI.MansionAI          - mansion-specific AI controllers
//   app.CameraManager         - camera access for ghost rendering
//
// AI Targeting Strategy:
//   RE7 enemies target a single player. In co-op, we hook the AI's
//   target acquisition to make enemies chase the NEAREST player.
//   We do this by hooking requestAttackPermit and getAttackPermitRequestTarget
//   to consider both the local player and the remote player's position.

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_EnemySync
{
    static ManagedObject _enemyGenMgr = null;
    static ManagedObject _aiBlackBoard = null;
    static ManagedObject _cameraMgr = null;
    static ManagedObject _gameFlowMgr = null;
    static bool _singletonsSearched = false;
    static bool _methodsDumped = false;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupEnemyGenHooks();
            SetupAITargetingHooks();
            SetupGameFlowHooks();
            SetupCameraHooks();
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
    }

    // =====================================================================
    //  ENEMY GENERATOR - Sync spawn/kill/suspend across host and client
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

        // Hook spawn requests - host only, broadcast to client
        HookPre(egType, "requestSpawn", OnPreEnemySpawn, "EnemyGen.requestSpawn");

        // Hook kill requests
        HookPre(egType, "requestKill", OnPreEnemyKill, "EnemyGen.requestKill");

        // Hook suspend (enemy goes dormant)
        HookPre(egType, "requestSuspend", OnPreEnemySuspend, "EnemyGen.requestSuspend");

        // Hook resume (enemy wakes up)
        HookPre(egType, "requestResume", OnPreEnemyResume, "EnemyGen.requestResume");

        // Hook bulk operations
        HookPre(egType, "requestAllKill", OnPreEnemyAllKill, "EnemyGen.requestAllKill");
        HookPre(egType, "requestAllSuspend", OnPreEnemyAllSuspend, "EnemyGen.requestAllSuspend");
    }

    static PreHookResult OnPreEnemySpawn(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] Enemy spawn requested");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEnemyKill(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] Enemy kill requested");
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
        API.LogInfo("[COOP-EnemySync] ALL enemies kill");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreEnemyAllSuspend(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] ALL enemies suspend");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  AI TARGETING - Make enemies chase the nearest player
    //
    //  Strategy: Hook AIWorldBlackBoard.requestAttackPermit to check
    //  if the remote player is closer to the enemy than the local player.
    //  If so, redirect the AI's target toward the remote player position.
    //
    //  Since we can't actually create a second player GameObject, we instead
    //  manipulate the AI's perception of where "the player" is.
    // =====================================================================

    static void SetupAITargetingHooks()
    {
        TDB tdb = API.GetTDB();

        // Hook AIWorldBlackBoard for attack targeting
        TypeDefinition bbType = tdb.FindType("app.AI.AIWorldBlackBoard");
        if (bbType != null)
        {
            HookPre(bbType, "requestAttackPermit", OnPreAttackPermit, "AIBlackBoard.requestAttackPermit");
            HookPre(bbType, "doUpdate", OnPreAIUpdate, "AIBlackBoard.doUpdate");
            API.LogInfo("[COOP-EnemySync] AIWorldBlackBoard hooks set");

            // One-time dump
            if (!_methodsDumped)
            {
                DumpMethods(bbType, "AIWorldBlackBoard");
                _methodsDumped = true;
            }
        }

        // Hook MansionAI
        TypeDefinition maiType = tdb.FindType("app.AI.MansionAI");
        if (maiType != null)
        {
            API.LogInfo("[COOP-EnemySync] MansionAI found");
        }

        // Hook AINavigationManager
        TypeDefinition navType = tdb.FindType("app.AI.AINavigationManager");
        if (navType != null)
        {
            API.LogInfo("[COOP-EnemySync] AINavigationManager found");
        }
    }

    static PreHookResult OnPreAttackPermit(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        // Let attack permits proceed normally.
        // In the future, we can intercept the target parameter to redirect
        // attacks toward the closest player.
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreAIUpdate(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        // AI update proceeds normally
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  GAME FLOW FSM - Track and sync game progression
    //  This is the REAL game flow controller (not MainFlowManager)
    // =====================================================================

    static void SetupGameFlowHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition gfType = tdb.FindType("app.GameFlowFsmManager");
        if (gfType == null)
        {
            API.LogWarning("[COOP-EnemySync] app.GameFlowFsmManager not found");
            return;
        }

        API.LogInfo("[COOP-EnemySync] GameFlowFsmManager found! Dumping methods:");
        DumpMethods(gfType, "GameFlowFsm");

        // Hook requestStartGameFlow - game progression changes
        HookPre(gfType, "requestStartGameFlow", OnPreStartGameFlow, "GameFlow.requestStartGameFlow");

        // Hook startGameFlow
        HookPre(gfType, "startGameFlow", OnPreStartFlow, "GameFlow.startGameFlow");

        // Hook setProgressiveNo - progress counter changes
        HookPre(gfType, "setProgressiveNo", OnPreSetProgress, "GameFlow.setProgressiveNo");

        // Hook jumpProgressiveNo - progress jumps (chapter skip etc.)
        HookPre(gfType, "jumpProgressiveNo", OnPreJumpProgress, "GameFlow.jumpProgressiveNo");

        // Hook battle/event end signals
        HookPre(gfType, "sendBattleEnded", OnPreBattleEnded, "GameFlow.sendBattleEnded");
        HookPre(gfType, "sendEventEnded", OnPreEventEnded, "GameFlow.sendEventEnded");

        // Hook setCurrentFlow
        HookPre(gfType, "setCurrentFlow", OnPreSetCurrentFlow, "GameFlow.setCurrentFlow");

        // Hook requestUnfreezeMenuOpen - important for pause management
        HookPre(gfType, "requestUnfreezeMenuOpen", OnPreUnfreezeMenu, "GameFlow.requestUnfreezeMenuOpen");
    }

    static PreHookResult OnPreStartGameFlow(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] GameFlow: requestStartGameFlow");
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreStartFlow(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] GameFlow: startGameFlow");
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

    static PreHookResult OnPreJumpProgress(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            uint id = (uint)(args[2] & 0xFFFFFFFF);
            int no = (int)(args[3] & 0xFFFFFFFF);
            API.LogInfo("[COOP-EnemySync] GameFlow: jumpProgressiveNo id=" + id + " no=" + no);
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

    static PreHookResult OnPreSetCurrentFlow(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            string flowName = ReadStringArg(args, 2);
            API.LogInfo("[COOP-EnemySync] GameFlow: setCurrentFlow = " + (flowName ?? "null"));
        }
        catch { }
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreUnfreezeMenu(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-EnemySync] GameFlow: requestUnfreezeMenuOpen");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  CAMERA ACCESS - Used by Ghost renderer for projection
    // =====================================================================

    static void SetupCameraHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition camType = tdb.FindType("app.CameraManager");
        if (camType == null) return;
        API.LogInfo("[COOP-EnemySync] CameraManager found");
    }

    /// <summary>
    /// Public: Get main camera from CameraManager.
    /// Used by Ghost renderer for world-to-screen projection.
    /// </summary>
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

    /// <summary>
    /// Public: Get player camera.
    /// </summary>
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
    //  PER-FRAME: Enemy state reading for host
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        try
        {
            if (!CoopState.IsCoopActive) return;
            CacheSingletons();

            // Host: read enemy states every 6 frames (~10Hz)
            if (CoopState.IsHost && CoopState.FrameCount % 6 == 0)
            {
                ReadEnemyStatesFromGenerator();
            }

            // Client: apply enemy positions from host
            if (CoopState.IsClient && CoopState.EnemyCount > 0 && CoopState.FrameCount % 3 == 0)
            {
                ApplyEnemyStates();
            }
        }
        catch { }
    }

    static void ReadEnemyStatesFromGenerator()
    {
        if (_enemyGenMgr == null) return;

        // Use the original approach but via ObjectManager for active enemies
        try
        {
            dynamic objectMgr = API.GetManagedSingleton("app.ObjectManager");
            if (objectMgr == null) return;

            ManagedObject omMO = objectMgr as ManagedObject;
            // Try to get enemy list via ObjectManager
            // app.ObjectManager.ListType might have an enemy list type

            // For now, count stays at 0 until we identify the exact method
            // that returns active enemy GameObjects with transforms
        }
        catch { }
    }

    static void ApplyEnemyStates()
    {
        // Apply received enemy positions from host to local game
        // This will be fleshed out once we have enemy enumeration working
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

    static void DumpMethods(TypeDefinition type, string label)
    {
        try
        {
            var methods = type.GetMethods();
            if (methods == null) return;
            for (int i = 0; i < methods.Count; i++)
                API.LogInfo("[COOP-EnemySync]   " + label + ": " + methods[i].GetName());
        }
        catch { }
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
