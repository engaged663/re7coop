// ResidentCOOP_Cutscene.cs
// SKIP ALL cinematics, found footage, and movies during co-op.
// ZERO game pauses during co-op.
//
// Uses app.GameManager as the central control:
//   - exitFoundFootage() to exit found footage
//   - isFoundFootage() / isOldChapterFoundFootage() to detect FF
//   - loadingMovieEnd() to force-end loading movies
//   - setMoviePause(false) to unfreeze movie pauses
//   - requestEvent() intercepted to block scripted events
//   - addTitleMovieControl / removeTitleMovieControl for title movies
//   - applyPause() intercepted to block all pauses
//   - setCantOpenInventoryFlag - ensure inventory always works
//   - setPlayerMove(true) / setPlayerCameraOperatable(true) - ensure player can always move

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_Cutscene
{
    static ManagedObject _gameManager = null;
    static bool _gmSearched = false;
    static int _numActiveTasks = 0;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupGameManagerHooks();
            SetupEventHooks();
            SetupMovieHooks();
            SetupAntiPause();
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

    static ManagedObject GM()
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
    //  GAME MANAGER HOOKS - The main control center
    // =====================================================================

    static void SetupGameManagerHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition gmType = tdb.FindType("app.GameManager");
        if (gmType == null)
        {
            API.LogWarning("[COOP-Cutscene] app.GameManager not found!");
            return;
        }

        // Block isFoundFootage -> always false during co-op
        HookPost(gmType, "isFoundFootage", OnPostIsFoundFootage, "GM.isFoundFootage");
        HookPost(gmType, "isOldChapterFoundFootage", OnPostIsFoundFootage, "GM.isOldChapterFoundFootage");

        // Block requestEvent -> skip scripted events/cutscenes
        HookPre(gmType, "requestEvent", OnPreRequestEvent, "GM.requestEvent");

        // Block applyPause -> no game freeze
        HookPre(gmType, "applyPause", OnPreApplyPause, "GM.applyPause");

        // Block requestPause -> no pause requests
        HookPre(gmType, "requestPause", OnPreBlockPause, "GM.requestPause");

        // Block setMoviePause(true) -> don't freeze on movies
        HookPre(gmType, "setMoviePause", OnPreSetMoviePause, "GM.setMoviePause");

        // Block setCantOpenInventoryFlag(true) -> inventory always accessible
        HookPre(gmType, "setCantOpenInventoryFlag", OnPreSetCantInventory, "GM.setCantOpenInventoryFlag");

        // Block setPlayerMove(false) -> player can always move
        HookPre(gmType, "setPlayerMove", OnPreSetPlayerMove, "GM.setPlayerMove");

        // Block setPlayerCameraOperatable(false) -> camera always works
        HookPre(gmType, "setPlayerCameraOperatable", OnPreSetCameraOp, "GM.setPlayerCameraOperatable");

        // Block setPlayerMoveMotion(false) -> motion always active
        HookPre(gmType, "setPlayerMoveMotion", OnPreSetPlayerMoveMotion, "GM.setPlayerMoveMotion");

        // Intercept addTitleMovieControl to know when title movies play
        HookPre(gmType, "addTitleMovieControl", OnPreAddTitleMovie, "GM.addTitleMovieControl");

        // Intercept gameOverUpdate to prevent game over from stalling
        HookPre(gmType, "gameOverCaptureStart", OnPreBlockPause, "GM.gameOverCaptureStart");
    }

    // -- Found Footage: force return false and exit --
    static void OnPostIsFoundFootage(ref ulong retval)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes) return;

        if ((retval & 1) != 0)
        {
            retval = 0; // force false
            ManagedObject gm = GM();
            if (gm != null)
            {
                try { gm.Call("exitFoundFootage"); } catch { }
            }
            API.LogInfo("[COOP-Cutscene] Found Footage -> forced exit");
        }
    }

    // -- Scripted events: skip entirely --
    static PreHookResult OnPreRequestEvent(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes)
            return PreHookResult.Continue;
        API.LogInfo("[COOP-Cutscene] SKIP GM.requestEvent");
        return PreHookResult.Skip;
    }

    // -- Pause: block all forms --
    static PreHookResult OnPreApplyPause(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Skip;
    }

    static PreHookResult OnPreBlockPause(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        return PreHookResult.Skip;
    }

    // -- Movie pause: block setting movie pause to true --
    static PreHookResult OnPreSetMoviePause(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes)
            return PreHookResult.Continue;

        // args[2] = bool pause. Block if trying to pause.
        bool wantsPause = (args[2] & 1) != 0;
        if (wantsPause)
        {
            API.LogInfo("[COOP-Cutscene] Blocked setMoviePause(true)");
            return PreHookResult.Skip;
        }
        return PreHookResult.Continue;
    }

    // -- Inventory lock: never lock during co-op --
    static PreHookResult OnPreSetCantInventory(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        bool cantOpen = (args[2] & 1) != 0;
        if (cantOpen)
        {
            // Force it to false instead of blocking entirely
            args[2] = 0;
        }
        return PreHookResult.Continue;
    }

    // -- Player movement: never disable during co-op --
    static PreHookResult OnPreSetPlayerMove(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        bool enable = (args[2] & 1) != 0;
        if (!enable)
        {
            // Force enable = true
            args[2] = 1;
            API.LogInfo("[COOP-Cutscene] Blocked setPlayerMove(false) -> forced true");
        }
        return PreHookResult.Continue;
    }

    // -- Camera control: never disable during co-op --
    static PreHookResult OnPreSetCameraOp(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        bool enable = (args[2] & 1) != 0;
        if (!enable)
        {
            args[2] = 1;
            API.LogInfo("[COOP-Cutscene] Blocked setPlayerCameraOperatable(false) -> forced true");
        }
        return PreHookResult.Continue;
    }

    // -- Movement motion: never disable --
    static PreHookResult OnPreSetPlayerMoveMotion(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        bool enable = (args[2] & 1) != 0;
        if (!enable)
        {
            args[2] = 1;
        }
        return PreHookResult.Continue;
    }

    // -- Title movie: skip it --
    static PreHookResult OnPreAddTitleMovie(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes)
            return PreHookResult.Continue;
        API.LogInfo("[COOP-Cutscene] SKIP title movie");
        return PreHookResult.Skip;
    }

    // =====================================================================
    //  EVENT ACTION CONTROLLER - scripted in-game events
    // =====================================================================

    static void SetupEventHooks()
    {
        TDB tdb = API.GetTDB();

        TypeDefinition eacType = tdb.FindType("app.EventActionController");
        if (eacType != null)
        {
            HookPre(eacType, "requestTask", OnPreRequestTask, "EventAction.requestTask");
        }

        TypeDefinition eatType = tdb.FindType("app.EventActionTask");
        if (eatType != null)
        {
            HookPre(eatType, "terminate", OnPreTaskTerminate, "EventAction.terminate");
        }
    }

    static PreHookResult OnPreRequestTask(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes)
            return PreHookResult.Continue;
        _numActiveTasks++;
        API.LogInfo("[COOP-Cutscene] SKIP EventAction task #" + _numActiveTasks);
        return PreHookResult.Skip;
    }

    static PreHookResult OnPreTaskTerminate(Span<ulong> args)
    {
        _numActiveTasks--;
        if (_numActiveTasks < 0) _numActiveTasks = 0;
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  MOVIE SYSTEM - Use UpdateMovie callback to force-end active movies.
    //  DO NOT hook play() - that causes crashes.
    //  Instead let movies start, then immediately end them.
    // =====================================================================

    static void SetupMovieHooks()
    {
        TDB tdb = API.GetTDB();

        // Dump movie manager methods for reference
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
    }

    [Callback(typeof(UpdateMovie), CallbackType.Post)]
    public static void OnUpdateMoviePost()
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes) return;

        try
        {
            // Force-end movies via GameManager
            ManagedObject gm = GM();
            if (gm != null)
            {
                try { gm.Call("loadingMovieEnd"); } catch { }
                try { gm.Call("setMoviePause", false); } catch { }
            }

            // Also try MovieManager singleton
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

                    // Check if playing
                    bool isPlaying = false;
                    try { isPlaying = ((int)(long)mo.Call("get_PlayingMovie") == 0); } catch { }
                    if (!isPlaying) continue;

                    // Try all stop/skip methods
                    TryCall(mo, "skipMovie");
                    TryCall(mo, "skip");
                    TryCall(mo, "requestSkip");
                    TryCall(mo, "endMovie");
                    TryCall(mo, "stopMovie");
                    TryCall(mo, "stop");

                    API.LogInfo("[COOP-Cutscene] Movie force-ended");
                    return;
                }
                catch { }
            }
        }
        catch { }
    }

    // =====================================================================
    //  ANTI-PAUSE: Additional engine-level pause blockers
    // =====================================================================

    static void SetupAntiPause()
    {
        TDB tdb = API.GetTDB();

        // via.Scene.set_Pause
        TypeDefinition sceneType = tdb.FindType("via.Scene");
        if (sceneType != null)
        {
            HookPre(sceneType, "set_Pause", OnPreScenePause, "Scene.set_Pause");
            HookPre(sceneType, "set_TimeScale", OnPreSceneTimeScale, "Scene.set_TimeScale");
        }
    }

    static PreHookResult OnPreScenePause(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        bool wantsPause = (args[2] & 1) != 0;
        if (wantsPause) return PreHookResult.Skip;
        return PreHookResult.Continue;
    }

    static PreHookResult OnPreSceneTimeScale(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;
        try
        {
            uint bits = (uint)(args[2] & 0xFFFFFFFF);
            float scale = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            if (scale < 0.01f) return PreHookResult.Skip;
        }
        catch { }
        return PreHookResult.Continue;
    }

    // =====================================================================
    //  PER-FRAME: Force-fix any stuck state
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        if (!CoopState.IsCoopActive) return;

        try
        {
            // Every 30 frames: ensure player is never locked
            if (CoopState.FrameCount % 30 == 0)
            {
                ManagedObject gm = GM();
                if (gm == null) return;

                // Force release all pause types
                for (int i = 0; i < 10; i++)
                {
                    try { gm.Call("requestReleasePause", i); } catch { }
                    try { gm.Call("requestReleasePause", (long)i); } catch { }
                }

                // Ensure player can move and use camera
                try { gm.Call("setPlayerMove", true, 0); } catch { }
                try { gm.Call("setPlayerMove", true, (long)0); } catch { }
                try { gm.Call("setPlayerCameraOperatable", true, 0); } catch { }
                try { gm.Call("setPlayerCameraOperatable", true, (long)0); } catch { }
                try { gm.Call("setCantOpenInventoryFlag", false); } catch { }

                // Force-exit found footage
                if (CoopState.SkipCutscenes)
                {
                    try
                    {
                        bool isFF = (bool)gm.Call("isFoundFootage");
                        if (isFF) gm.Call("exitFoundFootage");
                    }
                    catch { }
                }
            }

            // Every 60 frames: ensure scene timescale is 1.0
            if (CoopState.FrameCount % 60 == 0)
            {
                try
                {
                    var scene = via.SceneManager.CurrentScene;
                    if (scene != null)
                    {
                        ManagedObject sMO = (scene as IObject) as ManagedObject;
                        if (sMO != null)
                        {
                            float ts = (float)sMO.Call("get_TimeScale");
                            if (ts < 0.01f) sMO.Call("set_TimeScale", 1.0f);
                        }
                    }
                }
                catch { }
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
            MethodHook.Create(m, false).AddPost(new MethodHook.PostHookDelegate(OnPostPosturalCam));
            API.LogInfo("[COOP-Cutscene] Hooked " + types[i]);
        }
    }

    static void OnPostPosturalCam(ref ulong retval) { }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    static void HookPre(TypeDefinition type, string method,
        MethodHook.PreHookDelegate cb, string label)
    {
        Method m = type.GetMethod(method);
        if (m != null)
        {
            MethodHook.Create(m, false).AddPre(cb);
            API.LogInfo("[COOP-Cutscene] Hooked " + label);
        }
    }

    static void HookPost(TypeDefinition type, string method,
        MethodHook.PostHookDelegate cb, string label)
    {
        Method m = type.GetMethod(method);
        if (m != null)
        {
            MethodHook.Create(m, false).AddPost(cb);
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
}
