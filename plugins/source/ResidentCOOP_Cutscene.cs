// ResidentCOOP_Cutscene.cs
// Surgical cutscene skipper for co-op mode.
// Ported from RE7CutsceneSkipper.lua by alphaZomega — adapted for C# plugin.
//
// Strategy (from the working Lua mod):
//   1. Each frame: find active app.GameEventController in the scene
//   2. Check the controller's GameObject name against a whitelist
//   3. If whitelisted: accelerate TimeScale to 100.1x to fast-forward
//   4. When cutscene ends: restore TimeScale to 1.0
//   5. Stop Wwise audio to prevent compressed sounds
//   6. Pause playtime counter via SaveDataManager
//   7. After cutscene ends: teleport all players to host position
//
// Also handles in-game TV/Video playback (movieBox_640_480).
// Found footage is force-exited immediately.
//
// IMPORTANT: This does NOT block requestEvent, applyPause, or setPlayerMove.
// Those aggressive hooks broke game progression. This approach lets the game
// run normally and just fast-forwards through cinematic sequences.

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_Cutscene
{
    // === State ===
    static ManagedObject _gameManager = null;
    static ManagedObject _saveDataManager = null;
    static ManagedObject _scene = null;
    static bool _singletonsSearched = false;

    static ManagedObject _cutsceneObj = null;   // Active GameEventController or VideoControl
    static string _currentCsName = null;
    static string _lastCsName = null;
    static bool _isSkipping = false;
    static bool _pausedPlaytime = false;
    static bool _isTvVideo = false;
    static long _csStartTick = 0;               // For delay support
    static bool _wasInCutscene = false;          // Track cutscene transitions for teleport

    // Cached types
    static TypeDefinition _gameEventCtrlType = null;
    static TypeDefinition _videoControlType = null;
    static object _gameEventCtrlRuntimeType = null;
    static object _videoControlRuntimeType = null;
    static bool _typesResolved = false;

    // === Cutscene Whitelist ===
    // Ported directly from RE7CutsceneSkipper.lua
    // active=true means it will be auto-skipped
    // delay=seconds to wait before skipping (some need a moment to set up state)

    struct CutsceneEntry
    {
        public bool Active;
        public float Delay;
        public string Name;

        public CutsceneEntry(bool active, float delay = 0f, string name = null)
        {
            Active = active;
            Delay = delay;
            Name = name;
        }
    }

    static readonly Dictionary<string, CutsceneEntry> WHITELIST = new Dictionary<string, CutsceneEntry>
    {
        // Chapter 1 - Guest House
        {"c00e00_00", new CutsceneEntry(true)},
        {"c01e00_00", new CutsceneEntry(true)},
        {"c01e05_00", new CutsceneEntry(true)},
        {"c01e05_02", new CutsceneEntry(true)},
        {"c01e07_00", new CutsceneEntry(true)},
        {"c01e07_01", new CutsceneEntry(true)},
        {"c01e08_00", new CutsceneEntry(true)},

        // Chapter 3 - Main House
        {"c03e00_00", new CutsceneEntry(true)},
        {"c03e01_00", new CutsceneEntry(true)},
        {"c03e04_00", new CutsceneEntry(true)},
        {"c03e41_00", new CutsceneEntry(true)},
        {"c03e55_00", new CutsceneEntry(true)},
        {"c03e51_10", new CutsceneEntry(true, 0f, "Mia Bound talk")},
        {"c03e50_00", new CutsceneEntry(false, 0f, "Zoe Bound Intro")},
        {"c03e51_00", new CutsceneEntry(false, 0f, "Zoe Unbinding")},
        {"c03e51_20", new CutsceneEntry(false, 0f, "Zoe Receive Serum")},
        {"c03e63_00", new CutsceneEntry(false, 1.0f, "Final Jack Intro")},
        {"c03e64_00", new CutsceneEntry(true)},
        {"c03e64_02", new CutsceneEntry(true)},
        {"c03e53_00", new CutsceneEntry(true)},
        {"c03e54_00", new CutsceneEntry(true)},
        {"c03e52_00", new CutsceneEntry(true, 1.0f, "Walk to Mia + Zoe on dock")},

        // Chapter 4
        {"c04e00_00", new CutsceneEntry(true)},
        {"c04e10_00", new CutsceneEntry(true)},
        {"c04e12_00", new CutsceneEntry(true)},
        {"c04e12_01", new CutsceneEntry(true)},
        {"c04e20_00", new CutsceneEntry(true)},
        {"c04e25_00", new CutsceneEntry(true)},
        {"c04e74_00", new CutsceneEntry(true)},
        {"c04e80_00", new CutsceneEntry(true)},
        {"c04e80_01", new CutsceneEntry(true)},
        {"c04e82_00", new CutsceneEntry(true)},
        {"c04f00_00", new CutsceneEntry(true)},
        {"c04f01_00", new CutsceneEntry(true)},
        {"c04e38_00", new CutsceneEntry(false, 1.0f, "Mia chainsaw hand flashback")},
        {"c04e81_00", new CutsceneEntry(true)},
        {"c04e65_00", new CutsceneEntry(false)},
        {"c04e01_00", new CutsceneEntry(true)},
    };

    // =====================================================================
    //  ENTRY / EXIT
    // =====================================================================

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            SetupFoundFootageHooks();
            SetupSceneTimeScaleGuard();
            API.LogInfo("[COOP-Cutscene] Surgical cutscene skipper loaded (" + WHITELIST.Count + " cutscenes in whitelist).");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-Cutscene] Init: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        // Restore timescale if we were skipping
        if (_isSkipping)
        {
            try { RestoreTimeScale(); } catch { }
        }
        API.LogInfo("[COOP-Cutscene] Unloaded.");
    }

    // =====================================================================
    //  MAIN FRAME LOOP — Detect and skip cutscenes
    //  Runs every frame on UpdateBehavior (same timing as the Lua's re.on_frame)
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        // Skip cutscenes as long as the setting is on — works even before a partner connects
        if (!CoopState.SkipCutscenes) return;

        try
        {
            CacheSingletons();
            if (_scene == null && _sceneNative == null) return;

            // Read current timescale to know if we're in skip mode
            float currentSpeed = GetTimeScale();
            _isSkipping = (currentSpeed > 50.0f);

            // Detect active cutscene controller
            DetectCutsceneObject();

            // Detect TV/video playback
            DetectTvVideo();

            if (_cutsceneObj != null)
            {
                // Get the cutscene name
                _lastCsName = _currentCsName;
                _currentCsName = GetCutsceneName(_cutsceneObj);
                if (_lastCsName == null) _lastCsName = _currentCsName;

                // Track if cutscene just started (for delay)
                if (_lastCsName != _currentCsName)
                {
                    _csStartTick = DateTime.UtcNow.Ticks;
                }

                // Pause playtime
                PausePlaytime(true);

                // Check whitelist
                CutsceneEntry entry;
                bool inWhitelist = (_currentCsName != null && WHITELIST.TryGetValue(_currentCsName, out entry));
                bool isActive = inWhitelist && entry.Active;
                bool isTv = _isTvVideo;

                if (isActive || isTv)
                {
                    bool autoSkip = CoopState.SkipCutscenes;

                    // Check delay
                    bool canContinue = true;
                    if (inWhitelist && entry.Delay > 0f)
                    {
                        float elapsed = (float)(DateTime.UtcNow.Ticks - _csStartTick) / TimeSpan.TicksPerSecond;
                        canContinue = (elapsed > entry.Delay);
                    }

                    if (!canContinue)
                    {
                        // Waiting for delay — restore normal speed if we were skipping
                        if (_isSkipping) RestoreTimeScale();
                    }
                    else if (autoSkip)
                    {
                        if (!_isSkipping)
                        {
                            // Start skipping: accelerate timescale
                            StartSkip();
                        }
                    }
                }
                else
                {
                    // Cutscene not in whitelist or not active — don't skip
                    if (_isSkipping)
                    {
                        RestoreTimeScale();
                    }
                    // Unpause playtime for non-skippable cutscenes
                    PausePlaytime(false);
                }

                _wasInCutscene = true;
            }
            else
            {
                // No cutscene active
                _currentCsName = null;

                if (_isSkipping)
                {
                    RestoreTimeScale();
                }

                if (_pausedPlaytime)
                {
                    PausePlaytime(false);
                }

                // Cutscene just ended → teleport client to host
                if (_wasInCutscene)
                {
                    _wasInCutscene = false;
                    OnCutsceneEnded();
                }
            }
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-Cutscene] Frame error: " + e.Message);
        }
    }

    // =====================================================================
    //  CUTSCENE DETECTION — app.GameEventController via scene.findComponents
    //  (Exact same approach as the Lua mod)
    // =====================================================================

    static void DetectCutsceneObject()
    {
        if (!_typesResolved) ResolveTypes();
        if (_gameEventCtrlRuntimeType == null) return;

        try
        {
            dynamic components = CallScene(
                "findComponents(System.Type)", _gameEventCtrlRuntimeType);

            if (components != null)
            {
                // components is a managed array — get first element
                ManagedObject arr = components as ManagedObject;
                if (arr != null)
                {
                    try
                    {
                        // Try indexer access [0]
                        dynamic first = arr.Call("Get", 0);
                        if (first != null)
                        {
                            _cutsceneObj = first as ManagedObject;
                            _isTvVideo = false;
                            return;
                        }
                    }
                    catch { }

                    // Try get_Item
                    try
                    {
                        dynamic first = arr.Call("get_Item", 0);
                        if (first != null)
                        {
                            _cutsceneObj = first as ManagedObject;
                            _isTvVideo = false;
                            return;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // No GameEventController found — will check TV below
        if (!_isTvVideo)
        {
            _cutsceneObj = null;
        }
    }

    // =====================================================================
    //  TV / VIDEO DETECTION — in-game TV playback
    //  The Lua mod checks for "movieBox_640_480" GameObjects with VideoControl
    // =====================================================================

    static void DetectTvVideo()
    {
        if (_cutsceneObj != null && !_isTvVideo) return; // Already have a regular cutscene

        try
        {
            dynamic tv = CallScene(
                "findGameObject(System.String)", "movieBox_640_480");

            if (tv != null)
            {
                if (_videoControlRuntimeType == null) ResolveTypes();
                if (_videoControlRuntimeType == null) return;

                dynamic videoCtrl = (tv as ManagedObject).Call(
                    "getComponent(System.Type)", _videoControlRuntimeType);

                if (videoCtrl != null)
                {
                    // Check if movie is playing (state == 2)
                    try
                    {
                        int movieState = (int)(videoCtrl as ManagedObject).Call("getMovieState");
                        if (movieState == 2)
                        {
                            _cutsceneObj = videoCtrl as ManagedObject;
                            _isTvVideo = true;

                            // For TV videos: force IsMovieCancel
                            try
                            {
                                (_cutsceneObj as ManagedObject).Call("set_field", "IsMovieCancel", true);
                            }
                            catch
                            {
                                // Try direct field set — the Lua does cutscene_obj:set_field("IsMovieCancel", true)
                                try
                                {
                                    var td = (_cutsceneObj as IObject).GetTypeDefinition();
                                    var field = td?.GetField("IsMovieCancel");
                                    // Field access will be handled by dynamic dispatch
                                }
                                catch { }
                            }
                            return;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // No TV video found
        if (_isTvVideo)
        {
            _cutsceneObj = null;
            _isTvVideo = false;
        }
    }

    // =====================================================================
    //  SKIP CONTROL — TimeScale manipulation
    // =====================================================================

    static void StartSkip()
    {
        try
        {
            SetTimeScale(100.1f);
            _isSkipping = true;
            API.LogInfo("[COOP-Cutscene] Skipping: " + (_currentCsName ?? "TV/Video"));
        }
        catch (Exception e)
        {
            API.LogError("[COOP-Cutscene] StartSkip error: " + e.Message);
        }
    }

    static void RestoreTimeScale()
    {
        try
        {
            SetTimeScale(1.0f);
            _isSkipping = false;
            // Stop Wwise audio to prevent compressed sounds
            StopWwiseAudio();
        }
        catch (Exception e)
        {
            API.LogError("[COOP-Cutscene] RestoreTimeScale error: " + e.Message);
        }
    }

    static float GetTimeScale()
    {
        try
        {
            return (float)CallScene("get_TimeScale");
        }
        catch { return 1.0f; }
    }

    static void SetTimeScale(float scale)
    {
        try
        {
            CallScene("set_TimeScale(System.Single)", scale);
        }
        catch
        {
            try { CallScene("set_TimeScale", scale); } catch { }
        }
    }

    // =====================================================================
    //  FOUND FOOTAGE — Hook to force-exit immediately
    //  (This is safe — found footage is a distinct game mode, not a cutscene)
    // =====================================================================

    static void SetupFoundFootageHooks()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition gmType = tdb.FindType("app.GameManager");
        if (gmType == null) return;

        // Post-hook isFoundFootage to force return false and trigger exit
        Method isFF = gmType.GetMethod("isFoundFootage");
        if (isFF != null)
        {
            MethodHook.Create(isFF, false).AddPost(new MethodHook.PostHookDelegate(OnPostIsFoundFootage));
            API.LogInfo("[COOP-Cutscene] Hooked GM.isFoundFootage");
        }

        Method isOldFF = gmType.GetMethod("isOldChapterFoundFootage");
        if (isOldFF != null)
        {
            MethodHook.Create(isOldFF, false).AddPost(new MethodHook.PostHookDelegate(OnPostIsFoundFootage));
            API.LogInfo("[COOP-Cutscene] Hooked GM.isOldChapterFoundFootage");
        }
    }

    static void OnPostIsFoundFootage(ref ulong retval)
    {
        if (!CoopState.IsCoopActive || !CoopState.SkipCutscenes) return;

        if ((retval & 1) != 0)
        {
            retval = 0; // Force return false
            try
            {
                ManagedObject gm = GetGameManager();
                if (gm != null) gm.Call("exitFoundFootage");
            }
            catch { }
            API.LogInfo("[COOP-Cutscene] Found Footage -> forced exit");
        }
    }

    // =====================================================================
    //  SCENE TIMESCALE GUARD — Prevent external code from freezing the game
    //  (Only block timescale being set to near-zero, not normal changes)
    // =====================================================================

    static void SetupSceneTimeScaleGuard()
    {
        TDB tdb = API.GetTDB();
        TypeDefinition sceneType = tdb.FindType("via.Scene");
        if (sceneType == null) return;

        Method setTs = sceneType.GetMethod("set_TimeScale");
        if (setTs != null)
        {
            MethodHook.Create(setTs, false).AddPre(
                new MethodHook.PreHookDelegate(OnPreSceneTimeScale));
            API.LogInfo("[COOP-Cutscene] Hooked via.Scene.set_TimeScale guard");
        }
    }

    static PreHookResult OnPreSceneTimeScale(Span<ulong> args)
    {
        if (!CoopState.IsCoopActive) return PreHookResult.Continue;

        try
        {
            // args[1] = this (scene), args[2] = float timescale as uint bits
            uint bits = (uint)(args[2] & 0xFFFFFFFF);
            float scale = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

            // Block near-zero timescale (game trying to freeze)
            // But allow our own 100.1x skip and normal 1.0
            if (scale < 0.01f && _isSkipping)
            {
                return PreHookResult.Skip;
            }
        }
        catch { }

        return PreHookResult.Continue;
    }

    // =====================================================================
    //  WWISE AUDIO STOP — Prevent horrible compressed audio during skip
    //  (Direct port of the Lua's stop_wwise function)
    // =====================================================================

    static void StopWwiseAudio()
    {
        if (_scene == null && _sceneNative == null) return;

        try
        {
            TDB tdb = API.GetTDB();
            TypeDefinition wwiseType = tdb.FindType("via.wwise.WwiseContainer");
            if (wwiseType == null) return;

            object wwiseRt = wwiseType.GetRuntimeType();
            if (wwiseRt == null) return;

            dynamic wwises = CallScene(
                "findComponents(System.Type)", wwiseRt);

            if (wwises == null) return;

            ManagedObject arr = wwises as ManagedObject;
            if (arr == null) return;

            // Iterate and stop audio
            int count = 0;
            try { count = (int)arr.Call("get_Count"); } catch { }
            if (count == 0)
            {
                try { count = (int)arr.Call("get_Length"); } catch { }
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic wwise = arr.Call("Get", i);
                    if (wwise == null) continue;

                    ManagedObject wwMO = wwise as ManagedObject;
                    if (wwMO == null) continue;

                    // Check if it has event assets and stop them
                    try
                    {
                        int assetCount = (int)wwMO.Call("getContainableAssetCount");
                        for (int c = 0; c < assetCount; c++)
                        {
                            try
                            {
                                dynamic asset = wwMO.Call("getContainableAsset", c);
                                if (asset == null) continue;
                                string path = (string)(asset as ManagedObject).Call("get_ResourcePath");
                                if (path != null && path.ToLower().Contains("event"))
                                {
                                    wwMO.Call("stopAll");
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch
                    {
                        // Fallback: just stop all
                        try { wwMO.Call("stopAll"); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // =====================================================================
    //  PLAYTIME PAUSE — Don't count skipped time as play time
    //  Uses app.SaveDataManager._IsPlayTimePause (confirmed from Lua mod)
    // =====================================================================

    static void PausePlaytime(bool pause)
    {
        if (_pausedPlaytime == pause) return;

        try
        {
            ManagedObject sdm = GetSaveDataManager();
            if (sdm == null) return;

            // Try setting the field via dynamic dispatch
            try
            {
                dynamic dynSdm = sdm;
                dynSdm._IsPlayTimePause = pause;
                _pausedPlaytime = pause;
            }
            catch
            {
                // Fallback: try finding SystemObject -> SaveDataManager component
                try
                {
                    dynamic sysObj = (_scene as ManagedObject).Call(
                        "findGameObject(System.String)", "SystemObject");
                    if (sysObj != null)
                    {
                        TDB tdb = API.GetTDB();
                        var sdmType = tdb.FindType("app.SaveDataManager");
                        object sdmRt = sdmType?.GetRuntimeType();
                        if (sdmRt != null)
                        {
                            dynamic comp = (sysObj as ManagedObject).Call(
                                "getComponent(System.Type)", sdmRt);
                            if (comp != null)
                            {
                                dynamic dynComp = comp;
                                dynComp._IsPlayTimePause = pause;
                                _pausedPlaytime = pause;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // =====================================================================
    //  TELEPORT POST-CUTSCENE — Move all players to host position
    //  This prevents getting stuck behind cutscene-gated areas.
    // =====================================================================

    static void OnCutsceneEnded()
    {
        if (!CoopState.IsCoopActive) return;

        API.LogInfo("[COOP-Cutscene] Cutscene ended — syncing player positions");

        if (CoopState.IsHost)
        {
            // Host: broadcast our position so client teleports to us
            // The client will read CoopState.RemotePlayer and teleport
            CoopState.TeleportToHostRequested = true;
        }
        else
        {
            // Client: teleport to the host's last known position
            TeleportLocalPlayerTo(
                CoopState.RemotePlayer.PosX,
                CoopState.RemotePlayer.PosY,
                CoopState.RemotePlayer.PosZ);
        }
    }

    static void TeleportLocalPlayerTo(float x, float y, float z)
    {
        try
        {
            dynamic objectManager = API.GetManagedSingleton("app.ObjectManager");
            if (objectManager == null) return;

            dynamic player = (objectManager as ManagedObject).Call("getPlayer");
            if (player == null) return;

            dynamic transform = (player as ManagedObject).Call("get_Transform");
            if (transform == null) return;

            // Use typed proxy for position set
            ManagedObject transformMO = transform as ManagedObject;
            if (transformMO == null) return;

            try
            {
                var typed = (transformMO as IObject).As<via.Transform>();
                if (typed != null)
                {
                    var pos = typed.Position;
                    pos.x = x;
                    pos.y = y;
                    pos.z = z;
                    typed.Position = pos;
                    API.LogInfo("[COOP-Cutscene] Teleported to host: " + x + ", " + y + ", " + z);
                }
            }
            catch
            {
                // Fallback: try via ValueType
                try
                {
                    var posType = API.GetTDB().FindType("via.vec3");
                    var posVal = posType?.CreateValueType();
                    if (posVal != null)
                    {
                        dynamic dynPos = posVal;
                        dynPos.x = x;
                        dynPos.y = y;
                        dynPos.z = z;
                        transformMO.Call("set_Position", posVal);
                    }
                }
                catch (Exception e)
                {
                    API.LogError("[COOP-Cutscene] Teleport fallback failed: " + e.Message);
                }
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-Cutscene] Teleport failed: " + e.Message);
        }
    }

    // =====================================================================
    //  CUTSCENE NAME EXTRACTION
    //  The Lua does: cs_gameobj:call("get_Name"):gsub("_Controller", "")
    // =====================================================================

    static string GetCutsceneName(ManagedObject cutObj)
    {
        try
        {
            dynamic go = cutObj.Call("get_GameObject");
            if (go == null) return null;

            string name = (string)(go as ManagedObject).Call("get_Name");
            if (name == null) return null;

            // Strip "_Controller" suffix like the Lua does
            int idx = name.IndexOf("_Controller");
            if (idx >= 0) name = name.Substring(0, idx);

            return name;
        }
        catch { return null; }
    }

    // =====================================================================
    //  SINGLETON CACHE
    // =====================================================================

    static void CacheSingletons()
    {
        // Get scene using the NATIVE singleton approach (like the Lua mod)
        // Lua: scene = sdk.call_native_func(sdk.get_native_singleton("via.SceneManager"), ..., "get_CurrentScene()")
        try
        {
            if (_getCurrentSceneMethod == null)
            {
                TDB tdb = API.GetTDB();
                var smType = tdb?.FindType("via.SceneManager");
                if (smType != null)
                    _getCurrentSceneMethod = smType.GetMethod("get_CurrentScene");
            }

            if (_getCurrentSceneMethod != null)
            {
                var nativeSM = API.GetNativeSingleton("via.SceneManager");
                if (nativeSM != null)
                {
                    // Invoke get_CurrentScene on the native SceneManager
                    dynamic sceneResult = _getCurrentSceneMethod.Invoke(nativeSM, new object[] { });
                    if (sceneResult != null)
                    {
                        // Store as ManagedObject for Call() dispatch
                        _scene = sceneResult as ManagedObject;
                        // If it comes back as NativeObject, store that separately
                        if (_scene == null)
                        {
                            _sceneNative = sceneResult;
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-Cutscene] Scene cache error: " + e.Message);
        }

        if (_singletonsSearched) return;
        _singletonsSearched = true;

        _gameManager = GetGameManager();
        _saveDataManager = GetSaveDataManager();

        API.LogInfo("[COOP-Cutscene] Singletons cached. Scene=" + (_scene != null || _sceneNative != null) +
            " GM=" + (_gameManager != null) + " SaveData=" + (_saveDataManager != null));
    }

    static Method _getCurrentSceneMethod = null;
    static dynamic _sceneNative = null;  // Fallback if scene comes as NativeObject

    /// <summary>
    /// Call a method on the scene object, handling both ManagedObject and NativeObject cases.
    /// </summary>
    static dynamic CallScene(string methodName, params object[] args)
    {
        if (_scene != null)
        {
            return _scene.Call(methodName, args.Length > 0 ? args : null);
        }
        if (_sceneNative != null)
        {
            // For NativeObject, use dynamic dispatch
            ManagedObject mo = _sceneNative as ManagedObject;
            if (mo != null)
                return mo.Call(methodName, args.Length > 0 ? args : null);
            // Last resort: try direct dynamic call
            return ((dynamic)_sceneNative).Call(methodName, args.Length > 0 ? args : null);
        }
        return null;
    }

    static ManagedObject GetGameManager()
    {
        try
        {
            dynamic gm = API.GetManagedSingleton("app.GameManager");
            return gm as ManagedObject;
        }
        catch { return null; }
    }

    static ManagedObject GetSaveDataManager()
    {
        try
        {
            dynamic sdm = API.GetManagedSingleton("app.SaveDataManager");
            return sdm as ManagedObject;
        }
        catch { return null; }
    }

    static void ResolveTypes()
    {
        if (_typesResolved) return;

        TDB tdb = API.GetTDB();
        if (tdb == null) return;

        _gameEventCtrlType = tdb.FindType("app.GameEventController");
        _videoControlType = tdb.FindType("app.VideoControl");

        if (_gameEventCtrlType != null)
            _gameEventCtrlRuntimeType = _gameEventCtrlType.GetRuntimeType();
        if (_videoControlType != null)
            _videoControlRuntimeType = _videoControlType.GetRuntimeType();

        _typesResolved = (_gameEventCtrlType != null);

        if (_typesResolved)
            API.LogInfo("[COOP-Cutscene] Types resolved: GameEventController=" +
                (_gameEventCtrlType != null) + " VideoControl=" + (_videoControlType != null));
    }
}
