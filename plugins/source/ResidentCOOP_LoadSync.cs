// ResidentCOOP_LoadSync.cs
// Synchronizes scene loading between host and client using app.AsyncLoadManager.
// Host hooks AsyncLoadManager.requestLoad/requestUnLoad/requestActivate to broadcast
// scene operations to the client. Client receives and replicates them locally.
//
// Also hooks DamageManager to detect and sync damage events between players.
//
// Key singletons:
//   app.AsyncLoadManager - scene loading
//   app.Collision.DamageManager - damage detection
//   app.Collision.AttackManager - attack registration
//   app.Collision.CollisionSystem - raycasting

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_LoadSync
{
    static ManagedObject _asyncLoadMgr = null;
    static ManagedObject _damageMgr = null;
    static ManagedObject _attackMgr = null;
    static ManagedObject _collisionSystem = null;
    static bool _singletonsSearched = false;
    static bool _hooksSetup = false;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupLoadHooks();
            SetupDamageHooks();
            API.LogInfo("[COOP-LoadSync] Loaded.");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-LoadSync] Init: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP-LoadSync] Unloaded.");
    }

    // =====================================================================
    //  Cache singletons
    // =====================================================================

    static void CacheSingletons()
    {
        if (_singletonsSearched) return;
        _singletonsSearched = true;

        try
        {
            dynamic alm = API.GetManagedSingleton("app.AsyncLoadManager");
            if (alm != null) { _asyncLoadMgr = alm as ManagedObject; API.LogInfo("[COOP-LoadSync] AsyncLoadManager found"); }
        }
        catch { }

        try
        {
            dynamic dm = API.GetManagedSingleton("app.Collision.DamageManager");
            if (dm != null) { _damageMgr = dm as ManagedObject; API.LogInfo("[COOP-LoadSync] DamageManager found"); }
        }
        catch { }

        try
        {
            dynamic am = API.GetManagedSingleton("app.Collision.AttackManager");
            if (am != null) { _attackMgr = am as ManagedObject; API.LogInfo("[COOP-LoadSync] AttackManager found"); }
        }
        catch { }

        try
        {
            dynamic cs = API.GetManagedSingleton("app.Collision.CollisionSystem");
            if (cs != null) { _collisionSystem = cs as ManagedObject; API.LogInfo("[COOP-LoadSync] CollisionSystem found"); }
        }
        catch { }
    }

    // =====================================================================
    //  SCENE LOAD HOOKS - Host broadcasts scene changes to client
    // =====================================================================

    static void SetupLoadHooks()
    {
        TDB tdb = API.GetTDB();

        TypeDefinition almType = tdb.FindType("app.AsyncLoadManager");
        if (almType == null)
        {
            API.LogWarning("[COOP-LoadSync] app.AsyncLoadManager type not found");
            return;
        }

        // Hook requestLoad(string) - when host loads a new scene
        Method reqLoad = almType.GetMethod("requestLoad");
        if (reqLoad != null)
        {
            MethodHook.Create(reqLoad, false).AddPre(new MethodHook.PreHookDelegate(OnPreRequestLoad));
            API.LogInfo("[COOP-LoadSync] Hooked AsyncLoadManager.requestLoad");
        }

        // Hook requestUnLoad(string) - when host unloads a scene
        Method reqUnload = almType.GetMethod("requestUnLoad");
        if (reqUnload != null)
        {
            MethodHook.Create(reqUnload, false).AddPre(new MethodHook.PreHookDelegate(OnPreRequestUnload));
            API.LogInfo("[COOP-LoadSync] Hooked AsyncLoadManager.requestUnLoad");
        }

        // Hook requestActivate(string, bool) - when host activates a scene
        Method reqActivate = almType.GetMethod("requestActivate");
        if (reqActivate != null)
        {
            MethodHook.Create(reqActivate, false).AddPre(new MethodHook.PreHookDelegate(OnPreRequestActivate));
            API.LogInfo("[COOP-LoadSync] Hooked AsyncLoadManager.requestActivate");
        }

        // Hook requestGameFlowCtrlActivefromLoad - for save loading sync
        Method reqFlowLoad = almType.GetMethod("requestGameFlowCtrlActivefromLoad");
        if (reqFlowLoad != null)
        {
            MethodHook.Create(reqFlowLoad, false).AddPre(new MethodHook.PreHookDelegate(OnPreRequestFlowFromLoad));
            API.LogInfo("[COOP-LoadSync] Hooked AsyncLoadManager.requestGameFlowCtrlActivefromLoad");
        }
    }

    static PreHookResult OnPreRequestLoad(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;

        try
        {
            // args[1] = this, args[2] = string sceneName
            string sceneName = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(sceneName))
            {
                QueueSceneLoad(sceneName, false);
                API.LogInfo("[COOP-LoadSync] Host requestLoad: " + sceneName);
            }
        }
        catch { }

        return PreHookResult.Continue; // Let the original call proceed
    }

    static PreHookResult OnPreRequestUnload(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;

        try
        {
            string sceneName = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(sceneName))
            {
                QueueSceneUnload(sceneName);
                API.LogInfo("[COOP-LoadSync] Host requestUnLoad: " + sceneName);
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreRequestActivate(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.IsHost) return PreHookResult.Continue;

        try
        {
            string sceneName = ReadStringArg(args, 2);
            if (!string.IsNullOrEmpty(sceneName))
            {
                QueueSceneLoad(sceneName, true);
                API.LogInfo("[COOP-LoadSync] Host requestActivate: " + sceneName);
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    static PreHookResult OnPreRequestFlowFromLoad(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        API.LogInfo("[COOP-LoadSync] requestGameFlowCtrlActivefromLoad called");
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  DAMAGE HOOKS - Detect damage events for sync
    // =====================================================================

    static void SetupDamageHooks()
    {
        TDB tdb = API.GetTDB();

        // Hook DamageManager.systemUpdate to track damage contacts
        TypeDefinition dmType = tdb.FindType("app.Collision.DamageManager");
        if (dmType != null)
        {
            Method sysUpdate = dmType.GetMethod("systemUpdate");
            if (sysUpdate != null)
            {
                MethodHook.Create(sysUpdate, false).AddPre(new MethodHook.PreHookDelegate(OnPreDamageSystemUpdate));
                API.LogInfo("[COOP-LoadSync] Hooked DamageManager.systemUpdate");
            }

            // Dump all methods for future reference
            var methods = dmType.GetMethods();
            if (methods != null)
            {
                for (int i = 0; i < methods.Count; i++)
                    API.LogInfo("[COOP-LoadSync]   DamageManager: " + methods[i].GetName());
            }
        }

        // Hook AttackManager.systemUpdate
        TypeDefinition amType = tdb.FindType("app.Collision.AttackManager");
        if (amType != null)
        {
            Method sysUpdate = amType.GetMethod("systemUpdate");
            if (sysUpdate != null)
            {
                MethodHook.Create(sysUpdate, false).AddPre(new MethodHook.PreHookDelegate(OnPreAttackSystemUpdate));
                API.LogInfo("[COOP-LoadSync] Hooked AttackManager.systemUpdate");
            }
        }
    }

    static PreHookResult OnPreDamageSystemUpdate(Span<ulong> args)
    {
        // Let damage process normally - we track it for sync purposes
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreAttackSystemUpdate(Span<ulong> args)
    {
        // Let attacks process normally
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  PER-FRAME: Process queued scene operations on client side
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        try
        {
            if (!CoopState.IsCoopActive) return;

            CacheSingletons();

            // Client: process pending scene loads/unloads from host
            if (CoopState.IsClient)
            {
                ProcessPendingSceneOps();
                ProcessPendingDamage();
            }
        }
        catch { }
    }

    static void ProcessPendingSceneOps()
    {
        if (_asyncLoadMgr == null) return;

        // Process scene loads
        int loadCount = CoopState.PendingSceneLoadCount;
        if (loadCount > 0)
        {
            for (int i = 0; i < loadCount; i++)
            {
                string scene = CoopState.PendingSceneLoads[i];
                bool activate = CoopState.PendingSceneLoadActivate[i];

                if (string.IsNullOrEmpty(scene)) continue;

                try
                {
                    if (activate)
                    {
                        _asyncLoadMgr.Call("requestActivate", VM.CreateString(scene), true);
                        API.LogInfo("[COOP-LoadSync] Client requestActivate: " + scene);
                    }
                    else
                    {
                        _asyncLoadMgr.Call("requestLoad", VM.CreateString(scene));
                        API.LogInfo("[COOP-LoadSync] Client requestLoad: " + scene);
                    }
                }
                catch (Exception e)
                {
                    API.LogError("[COOP-LoadSync] Client scene load error: " + e.Message);
                }
            }
            CoopState.PendingSceneLoadCount = 0;
        }

        // Process scene unloads
        int unloadCount = CoopState.PendingSceneUnloadCount;
        if (unloadCount > 0)
        {
            for (int i = 0; i < unloadCount; i++)
            {
                string scene = CoopState.PendingSceneUnloads[i];
                if (string.IsNullOrEmpty(scene)) continue;

                try
                {
                    _asyncLoadMgr.Call("requestUnLoad", VM.CreateString(scene));
                    API.LogInfo("[COOP-LoadSync] Client requestUnLoad: " + scene);
                }
                catch (Exception e)
                {
                    API.LogError("[COOP-LoadSync] Client scene unload error: " + e.Message);
                }
            }
            CoopState.PendingSceneUnloadCount = 0;
        }
    }

    static void ProcessPendingDamage()
    {
        // Process received damage events from host
        int count = CoopState.DmgCount;
        if (count <= 0) return;

        // For now, log damage events. Full damage application requires
        // knowing the target GameObject which we'll match via enemy sync.
        for (int i = 0; i < count; i++)
        {
            if (CoopState.FrameCount % 60 == 0)
            {
                API.LogInfo("[COOP-LoadSync] Damage event: target=" + CoopState.DmgTargetIDs[i] +
                    " dmg=" + CoopState.DmgAmounts[i]);
            }
        }
        CoopState.DmgCount = 0;
    }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    static void QueueSceneLoad(string sceneName, bool activate)
    {
        int idx = CoopState.PendingSceneLoadCount;
        if (idx >= CoopState.PendingSceneLoads.Length) return;
        CoopState.PendingSceneLoads[idx] = sceneName;
        CoopState.PendingSceneLoadActivate[idx] = activate;
        CoopState.PendingSceneLoadCount = idx + 1;
    }

    static void QueueSceneUnload(string sceneName)
    {
        int idx = CoopState.PendingSceneUnloadCount;
        if (idx >= CoopState.PendingSceneUnloads.Length) return;
        CoopState.PendingSceneUnloads[idx] = sceneName;
        CoopState.PendingSceneUnloadCount = idx + 1;
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
