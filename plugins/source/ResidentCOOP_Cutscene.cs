// ResidentCOOP_Cutscene.cs
// ZERO PAUSES during co-op. Hooks EVERY pause mechanism in RE7.
// Also skips cinematics, found footage, and movies.
//
// Pause sources in RE7:
//   1. app.GameManager.requestPause(PauseRequestType)
//   2. via.Scene.set_Pause(bool) / timescale set to 0
//   3. Item pickup/examine (InteractManager triggers pause internally)
//   4. Inventory open
//   5. Map open
//   6. Message system
//   7. Found footage mode
//   8. Movie playback
//   9. EventActionController tasks

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_Cutscene
{
    static int _numActiveTasks = 0;
    static ManagedObject _gameManager = null;
    static bool _gmSearched = false;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupAntiPause();
            SetupEventHooks();
            SetupMovieSkipHooks();
            SetupFoundFootageHooks();
            SetupMotionControllerHooks();
            API.LogInfo("[COOP-Cutscene] All hooks loaded.");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-Cutscene] Init: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP-Cutscene] Unloaded.");
    }

    static ManagedObject GetGameManager()
    {
        if (_gameManager != null) return _gameManager;
        if (_gmSearched) return null;
        try
        {
            dynamic gm = API.GetManagedSingleton("app.GameManager");
            if (gm != null) _gameManager = gm as ManagedObject;
        }
        catch { }
        _gmSearched = true;
        return _gameManager;
    }

    // =====================================================================
    //  ANTI-PAUSE: Hook EVERY pause mechanism
    // =====================================================================

    static void SetupAntiPause()
    {
        TDB tdb = API.GetTDB();

        // 1. GameManager.requestPause - main game pause
        TypeDefinition gmType = tdb.FindType("app.GameManager");
        if (gmType != null)
        {
            HookPre(gmType, "requestPause", OnPreBlockPause, "GameManager.requestPause");
        }

        // 2. via.Scene.set_Pause - engine-level scene pause
        TypeDefinition sceneType = tdb.FindType("via.Scene");
        if (sceneType != null)
        {
            HookPre(sceneType, "set_Pause", OnPreBlockScenePause, "via.Scene.set_Pause");
            HookPre(sceneType, "set_TimeScale", OnPreBlockTimeScale, "via.Scene.set_TimeScale");
        }

        // 3. InteractManager - item pickup/examine pause
        TypeDefinition imType = tdb.FindType("app.InteractManager");
        if (imType != null)
        {
            // The interact system likely pauses via GameManager internally.
            // But we also hook interactStart to log and ensure no pause slips through.
            // We DON'T skip interactStart - we want interactions to work,
            // just without pausing the game.
            API.LogInfo("[COOP-Cutscene] InteractManager found - pause will be caught by GameManager hook");
        }

        // 4. MapManager.set_IsOpening - map open pauses
        TypeDefinition mapType = tdb.FindType("app.MapManager");
        if (mapType != null)
        {
            HookPre(mapType, "lockPlayerPos", OnPreBlockPause, "MapManager.lockPlayerPos");
        }

        // 5. MessageSystem - message display may pause
        TypeDefinition msgType = tdb.FindType("app.MessageSystem");
        if (msgType != null)
        {
            HookPre(msgType, "pauseGUIGameObjectList", OnPreBlockPause, "MessageSystem.pauseGUIGameObjectList");
        }

        // 6. PauseFlow - the ropeway pause flow system
        TypeDefinition pfType = tdb.FindType("app.ropeway.gamemastering.PauseFlow");
        if (pfType != null)
        {
            API.LogInfo("[COOP-Cutscene] PauseFlow type found");
            // Try hooking its update or state changes
            HookPre(pfType, "update", OnPreBlockPause, "PauseFlow.update");
            HookPre(pfType, "doUpdate", OnPreBlockPause, "PauseFlow.doUpdate");
        }

        // 7. PauseBehavior (GUI pause screen)
        TypeDefinition pbType = tdb.FindType("app.ropeway.gui.PauseBehavior");
        if (pbType == null) pbType = tdb.FindType("app.PauseBehavior");
        if (pbType != null)
        {
            API.LogInfo("[COOP-Cutscene] PauseBehavior found");
        }
    }

    static PreHookResult OnPreBlockPause(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Skip;
    }

    static PreHookResult OnPreBlockScenePause(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        // args[2] = bool value. If trying to pause (true), block it.
        bool wantsPause = (args[2] & 1) != 0;
        if (wantsPause)
        {
            API.LogInfo("[COOP-Cutscene] Blocked Scene.set_Pause(true)");
            return PreHookResult.Skip;
        }
        return PreHookResult.Continue; // Allow unpause
    }

    static PreHookResult OnPreBlockTimeScale(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        // Prevent timescale from being set to 0 (pause).
        // We read the float from args[2] by reinterpreting bits.
        try
        {
            uint bits = (uint)(args[2] & 0xFFFFFFFF);
            float scale = BitToFloat(bits);

            if (scale < 0.01f)
            {
                API.LogInfo("[COOP-Cutscene] Blocked Scene.set_TimeScale(" + scale + ")");
                return PreHookResult.Skip;
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    // Per-frame: force unpause if somehow paused
    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        if (!CoopState.IsCoopActive) return;

        try
        {
            // Every 30 frames, force-release any pause state
            if (CoopState.FrameCount % 30 == 0)
            {
                ForceUnpause();
                CheckFoundFootage();
            }
        }
        catch { }
    }

    static void ForceUnpause()
    {
        ManagedObject gm = GetGameManager();
        if (gm == null) return;

        // Force release all pause types (try 0-10 as common PauseRequestType values)
        for (int i = 0; i < 10; i++)
        {
            try { gm.Call("requestReleasePause", i); } catch { }
            try { gm.Call("requestReleasePause", (long)i); } catch { }
        }

        // Also ensure scene timescale is 1.0
        try
        {
            var scene = via.SceneManager.CurrentScene;
            if (scene != null)
            {
                // Check if timescale is 0 and fix it
                try
                {
                    ManagedObject sceneMO = (scene as IObject) as ManagedObject;
                    if (sceneMO != null)
                    {
                        dynamic ts = sceneMO.Call("get_TimeScale");
                        float timeScale = (float)ts;
                        if (timeScale < 0.01f)
                        {
                            sceneMO.Call("set_TimeScale", 1.0f);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // =====================================================================
    //  EVENT ACTION - scripted events
    // =====================================================================

    static void SetupEventHooks()
    {
        TDB tdb = API.GetTDB();

        TypeDefinition eacType = tdb.FindType("app.EventActionController");
        if (eacType != null)
        {
            HookPre(eacType, "requestTask", OnPreRequestTask, "EventActionController.requestTask");
        }

        TypeDefinition eatType = tdb.FindType("app.EventActionTask");
        if (eatType != null)
        {
            HookPre(eatType, "terminate", OnPreTaskTerminate, "EventActionTask.terminate");
        }
    }

    static PreHookResult OnPreRequestTask(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes)
            return PreHookResult.Continue;
        _numActiveTasks++;
        API.LogInfo("[COOP-Cutscene] SKIP EventAction #" + _numActiveTasks);
        return PreHookResult.Skip;
    }

    static PreHookResult OnPreTaskTerminate(Span<ulong> args)
    {
        _numActiveTasks--;
        if (_numActiveTasks < 0) _numActiveTasks = 0;
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  MOVIE - pre-rendered videos. Don't skip play(), force-finish instead.
    // =====================================================================

    static void SetupMovieSkipHooks()
    {
        TDB tdb = API.GetTDB();

        // Dump methods for MovieManager
        string[] mmNames = new string[] {
            "app.ropeway.gamemastering.MovieManager", "app.MovieManager"
        };
        for (int i = 0; i < mmNames.Length; i++)
        {
            TypeDefinition t = tdb.FindType(mmNames[i]);
            if (t != null) DumpMethods(t, mmNames[i]);
        }

        TypeDefinition mpType = tdb.FindType("app.ropeway.movie.MoviePlayer");
        if (mpType != null) DumpMethods(mpType, "MoviePlayer");

        TypeDefinition vmType = tdb.FindType("via.movie.Movie");
        if (vmType != null) DumpMethods(vmType, "via.movie.Movie");
    }

    [Callback(typeof(UpdateMovie), CallbackType.Post)]
    public static void OnUpdateMoviePost()
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes) return;

        try
        {
            string[] mgrs = new string[] {
                "app.ropeway.gamemastering.MovieManager", "app.MovieManager"
            };

            for (int i = 0; i < mgrs.Length; i++)
            {
                try
                {
                    dynamic mgr = API.GetManagedSingleton(mgrs[i]);
                    if (mgr == null) continue;
                    ManagedObject mo = mgr as ManagedObject;

                    bool isPlaying = false;
                    try { isPlaying = ((int)(long)mo.Call("get_PlayingMovie") == 0); } catch { }

                    if (!isPlaying) continue;

                    TryCall(mo, "skipMovie");
                    TryCall(mo, "skip");
                    TryCall(mo, "requestSkip");
                    TryCall(mo, "endMovie");
                    TryCall(mo, "stopMovie");
                    TryCall(mo, "stop");
                    return;
                }
                catch { }
            }
        }
        catch { }
    }

    // =====================================================================
    //  FOUND FOOTAGE - hook isFoundFootage to force-exit
    // =====================================================================

    static void SetupFoundFootageHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition gmType = tdb.FindType("app.GameManager");
        if (gmType == null) return;

        Method isFF = gmType.GetMethod("isFoundFootage");
        if (isFF != null)
        {
            MethodHook.Create(isFF, false).AddPost(new MethodHook.PostHookDelegate(OnPostIsFoundFootage));
            API.LogInfo("[COOP-Cutscene] Hooked isFoundFootage");
        }

        Method isOldFF = gmType.GetMethod("isOldChapterFoundFootage");
        if (isOldFF != null)
        {
            MethodHook.Create(isOldFF, false).AddPost(new MethodHook.PostHookDelegate(OnPostIsFoundFootage));
            API.LogInfo("[COOP-Cutscene] Hooked isOldChapterFoundFootage");
        }
    }

    static void OnPostIsFoundFootage(ref ulong retval)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes) return;

        if ((retval & 1) != 0)
        {
            API.LogInfo("[COOP-Cutscene] Found Footage -> forcing exit");
            retval = 0;
            ManagedObject gm = GetGameManager();
            if (gm != null)
            {
                try { gm.Call("exitFoundFootage"); } catch { }
            }
        }
    }

    static void CheckFoundFootage()
    {
        if (!CoopState.SkipCutscenes) return;
        ManagedObject gm = GetGameManager();
        if (gm == null) return;

        try
        {
            bool isFF = (bool)gm.Call("isFoundFootage");
            if (isFF)
            {
                API.LogInfo("[COOP-Cutscene] Still in FoundFootage - exiting");
                gm.Call("exitFoundFootage");
            }
        }
        catch { }
    }

    // =====================================================================
    //  MOTION CONTROLLERS
    // =====================================================================

    static void SetupMotionControllerHooks()
    {
        TDB tdb = API.GetTDB();
        string[] types = new string[] {
            "app.PlayerMotionController",
            "app.CH8PlayerMotionController",
            "app.CH9PlayerMotionController"
        };

        for (int i = 0; i < types.Length; i++)
        {
            TypeDefinition t = tdb.FindType(types[i]);
            if (t == null) continue;
            Method m = t.GetMethod("updatePosturalCameraMotion");
            if (m == null) continue;
            MethodHook.Create(m, false).AddPost(new MethodHook.PostHookDelegate(OnPostPosturalCamera));
            API.LogInfo("[COOP-Cutscene] Hooked " + types[i]);
        }
    }

    static void OnPostPosturalCamera(ref ulong retval) { }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    static void HookPre(TypeDefinition type, string methodName,
        MethodHook.PreHookDelegate cb, string label)
    {
        Method m = type.GetMethod(methodName);
        if (m != null)
        {
            MethodHook.Create(m, false).AddPre(cb);
            API.LogInfo("[COOP-Cutscene] Hooked " + label);
        }
    }

    static bool TryCall(ManagedObject obj, string method)
    {
        try { obj.Call(method); return true; } catch { return false; }
    }

    static void DumpMethods(TypeDefinition type, string label)
    {
        try
        {
            var methods = type.GetMethods();
            if (methods == null) return;
            for (int i = 0; i < methods.Count; i++)
                API.LogInfo("[COOP-Cutscene]   " + label + ": " + methods[i].GetName());
        }
        catch { }
    }

    static float BitToFloat(uint bits)
    {
        byte[] b = BitConverter.GetBytes(bits);
        return BitConverter.ToSingle(b, 0);
    }
}
