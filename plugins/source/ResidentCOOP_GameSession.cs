// ResidentCOOP_GameSession.cs
// Game session management using app.GameManager singleton.
//
// Key methods on app.GameManager:
//   newGameRequest()                    - Start a new game
//   newGameInitParam(bool)              - Init new game params
//   set_gameDifficulty(Difficulty)      - Set difficulty before new game
//   chapterJumpRequest(string,bool,str) - Jump to a specific chapter
//   loadInit()                          - Initialize loading
//   get_IsSceneLoading()               - Check if scene is loading
//   get_LoadingProgress()              - Loading progress
//   sceneLoadAllEnd()                  - Signal all scene loads done
//   exitFoundFootage()                 - Exit found footage mode
//   isFoundFootage()                   - Check if in found footage
//   getPlayer()                        - Get player object
//   requestPause(PauseRequestType)     - Pause (menu still works, game won't freeze)
//   requestReleasePause(PauseReqType)  - Release pause
//
// For save loading: app.SaveDataManager (methods discovered at runtime)

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_GameSession
{
    static bool _initialized = false;
    static ManagedObject _gameManager = null;

    // Discovered save methods
    static string _checkSaveExistsMethod = null;
    static string _loadSaveMethod = null;

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[COOP-GameSession] Loaded.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP-GameSession] Unloaded.");
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        try
        {
            // Cache GameManager singleton
            if (_gameManager == null)
            {
                InitGameManager();
            }

            // One-time: dump SaveDataManager methods
            if (!_initialized)
            {
                DumpSaveDataManagerMethods();
                _initialized = true;
            }

            // Scan save slots when needed
            if (!CoopState.SaveSlotsScanned &&
                (CoopState.CurrentScreen == UIScreen.HostNewOrLoad ||
                 CoopState.CurrentScreen == UIScreen.HostLoadGame))
            {
                ScanSaveSlots();
            }

            // Process session start
            if (CoopState.SessionStartRequested && !CoopState.SessionStarted)
            {
                CoopState.SessionStartRequested = false;
                ProcessSessionStart();
            }
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-GameSession] " + e.Message);
        }
    }

    // =====================================================================
    //  GAME MANAGER
    // =====================================================================

    static void InitGameManager()
    {
        try
        {
            dynamic gm = API.GetManagedSingleton("app.GameManager");
            if (gm != null)
            {
                _gameManager = gm as ManagedObject;
                API.LogInfo("[COOP-GameSession] app.GameManager found!");
            }
        }
        catch { }
    }

    // =====================================================================
    //  SAVE DATA MANAGER INTROSPECTION
    // =====================================================================

    static void DumpSaveDataManagerMethods()
    {
        try
        {
            dynamic saveMgr = API.GetManagedSingleton("app.SaveDataManager");
            if (saveMgr == null)
            {
                API.LogWarning("[COOP-GameSession] app.SaveDataManager not found");
                return;
            }

            ManagedObject smMO = saveMgr as ManagedObject;
            TypeDefinition smType = smMO.GetTypeDefinition();
            if (smType == null) return;

            API.LogInfo("[COOP-GameSession] === SaveDataManager: " + smType.GetFullName() + " ===");

            var methods = smType.GetMethods();
            if (methods != null)
            {
                for (int i = 0; i < methods.Count; i++)
                {
                    string name = methods[i].GetName();
                    string lower = name.ToLower();

                    // Detect check-exists method
                    if (lower.Contains("exist") || lower.Contains("issave") || lower.Contains("hassave") || lower.Contains("isvalid"))
                    {
                        if (_checkSaveExistsMethod == null)
                        {
                            _checkSaveExistsMethod = name;
                            API.LogInfo("[COOP-GameSession]   >> Check exists: " + name);
                        }
                    }

                    // Detect load method
                    if (lower.Contains("load") && !lower.Contains("unload") && !lower.Contains("reload") && !lower.Contains("isload") && !lower.Contains("getload"))
                    {
                        if (_loadSaveMethod == null)
                        {
                            _loadSaveMethod = name;
                            API.LogInfo("[COOP-GameSession]   >> Load method: " + name);
                        }
                    }
                }
            }

            API.LogInfo("[COOP-GameSession] === End SaveDataManager ===");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-GameSession] SaveDataManager dump error: " + e.Message);
        }
    }

    // =====================================================================
    //  SAVE SLOT SCANNING
    // =====================================================================

    static void ScanSaveSlots()
    {
        try
        {
            dynamic saveMgr = API.GetManagedSingleton("app.SaveDataManager");
            if (saveMgr != null && _checkSaveExistsMethod != null)
            {
                ScanSaveSlotsWithMethod(saveMgr as ManagedObject);
            }
            else
            {
                ScanSaveSlotsViaOptionManager();
            }
            CoopState.SaveSlotsScanned = true;
        }
        catch (Exception e)
        {
            API.LogError("[COOP-GameSession] ScanSaveSlots: " + e.Message);
            CoopState.SaveSlotsScanned = true;
        }
    }

    static void ScanSaveSlotsWithMethod(ManagedObject saveMgr)
    {
        int found = 0;
        for (int slot = 0; slot < 21; slot++)
        {
            bool exists = false;
            // Try with slot directly, and slot+1
            try { exists = (bool)saveMgr.Call(_checkSaveExistsMethod, slot); } catch { }
            if (!exists)
            {
                try { exists = (bool)saveMgr.Call(_checkSaveExistsMethod, slot + 1); } catch { }
            }

            CoopState.SaveSlotExists[slot] = exists;
            CoopState.SaveSlotNames[slot] = exists ? ("Save Slot " + (slot + 1)) : "(Empty)";
            if (exists) found++;
        }
        CoopState.SaveSlotCount = found;
        API.LogInfo("[COOP-GameSession] Scan: " + found + " saves found");
    }

    static void ScanSaveSlotsViaOptionManager()
    {
        try
        {
            dynamic optMgr = API.GetManagedSingleton("app.OptionManager");
            if (optMgr == null) { CoopState.SaveSlotCount = 0; return; }

            bool hasAny = false;
            try { hasAny = (bool)optMgr._HasAnySave; } catch { }

            if (hasAny)
            {
                CoopState.SaveSlotExists[0] = true;
                CoopState.SaveSlotNames[0] = "Continue (Latest Save)";
                CoopState.SaveSlotCount = 1;
            }
            else
            {
                CoopState.SaveSlotCount = 0;
            }
        }
        catch { CoopState.SaveSlotCount = 0; }
    }

    // =====================================================================
    //  SESSION START
    // =====================================================================

    static void ProcessSessionStart()
    {
        switch (CoopState.StartMode)
        {
            case SessionStartMode.NewGame:
                StartNewGame();
                break;
            case SessionStartMode.LoadSave:
                LoadSaveGame();
                break;
            case SessionStartMode.ContinueCurrentGame:
                ContinueCurrentGame();
                break;
        }
    }

    /// <summary>
    /// Start a new game using app.GameManager.
    /// Flow: set_gameDifficulty -> newGameInitParam(true) -> newGameRequest()
    /// </summary>
    static void StartNewGame()
    {
        API.LogInfo("[COOP-GameSession] NewGame requested, difficulty=" + CoopState.Difficulty);

        if (_gameManager == null)
        {
            InitGameManager();
        }

        if (_gameManager != null)
        {
            try
            {
                // Set difficulty first
                // app.GameManager.Difficulty enum: we pass it as int
                try { _gameManager.Call("set_gameDifficulty", (int)CoopState.Difficulty); }
                catch
                {
                    try { _gameManager.Call("set_gameDifficulty", (long)CoopState.Difficulty); }
                    catch (Exception de) { API.LogWarning("[COOP-GameSession] set_gameDifficulty: " + de.Message); }
                }

                // Init new game parameters
                try { _gameManager.Call("newGameInitParam", true); }
                catch (Exception pe) { API.LogWarning("[COOP-GameSession] newGameInitParam: " + pe.Message); }

                // Fire the new game request
                try
                {
                    _gameManager.Call("newGameRequest");
                    API.LogInfo("[COOP-GameSession] newGameRequest() called successfully!");
                    CoopState.StatusMessage = "New game starting...";
                    CoopState.SessionStarted = true;
                    return;
                }
                catch (Exception ne)
                {
                    API.LogError("[COOP-GameSession] newGameRequest failed: " + ne.Message);
                }
            }
            catch (Exception e)
            {
                API.LogError("[COOP-GameSession] NewGame error: " + e.Message);
            }
        }

        // Fallback: try GameFlowFsmManager.requestStartGameFlow
        try
        {
            dynamic gfm = API.GetManagedSingleton("app.GameFlowFsmManager");
            if (gfm != null)
            {
                ManagedObject gfmMO = gfm as ManagedObject;
                // GameFlowKindEnum values: try 0 (main story)
                try { gfmMO.Call("requestStartGameFlow", 0); }
                catch { try { gfmMO.Call("requestStartGameFlow", (long)0); } catch { } }

                try { gfmMO.Call("startGameFlow", true); }
                catch { try { gfmMO.Call("startGameFlow", false); } catch { } }

                API.LogInfo("[COOP-GameSession] Fallback: GameFlowFsmManager.requestStartGameFlow(0)");
                CoopState.StatusMessage = "New game starting via GameFlow...";
                CoopState.SessionStarted = true;
                return;
            }
        }
        catch { }

        CoopState.SessionStarted = true;
        CoopState.StatusMessage = "Could not auto-start. Please start New Game from title screen.";
    }

    /// <summary>
    /// Load a save game using app.SaveDataManager.
    /// The user confirmed a load method was found in the logs.
    /// </summary>
    static void LoadSaveGame()
    {
        int slot = CoopState.SaveSlotToLoad;
        API.LogInfo("[COOP-GameSession] LoadSave slot=" + slot);

        if (_loadSaveMethod == null)
        {
            CoopState.StatusMessage = "No load method found. Load save manually from title screen.";
            CoopState.SessionStarted = true;
            return;
        }

        try
        {
            dynamic saveMgr = API.GetManagedSingleton("app.SaveDataManager");
            if (saveMgr != null)
            {
                ManagedObject smMO = saveMgr as ManagedObject;

                // Try slot directly and slot+1
                bool loaded = false;
                try { smMO.Call(_loadSaveMethod, slot); loaded = true; }
                catch
                {
                    try { smMO.Call(_loadSaveMethod, slot + 1); loaded = true; }
                    catch
                    {
                        try { smMO.Call(_loadSaveMethod, (long)slot); loaded = true; }
                        catch
                        {
                            try { smMO.Call(_loadSaveMethod, (long)(slot + 1)); loaded = true; }
                            catch { }
                        }
                    }
                }

                if (loaded)
                {
                    API.LogInfo("[COOP-GameSession] " + _loadSaveMethod + " called for slot " + slot);

                    // After requesting load, tell GameManager + AsyncLoadManager to proceed
                    if (_gameManager != null)
                    {
                        try { _gameManager.Call("loadInit"); API.LogInfo("[COOP-GameSession] loadInit OK"); }
                        catch { }
                    }

                    // Use AsyncLoadManager to activate the game flow from load
                    try
                    {
                        dynamic alm = API.GetManagedSingleton("app.AsyncLoadManager");
                        if (alm != null)
                        {
                            ManagedObject almMO = alm as ManagedObject;
                            // Try requestGameFlowCtrlActivefromLoad with chapter param
                            try { almMO.Call("requestGameFlowCtrlActivefromLoad", 0); API.LogInfo("[COOP-GameSession] AsyncLoad.requestGameFlowCtrlActivefromLoad(0) OK"); }
                            catch
                            {
                                try { almMO.Call("requestGameFlowCtrlActivefromLoad", (long)0); API.LogInfo("[COOP-GameSession] AsyncLoad flow ctrl OK (long)"); }
                                catch { API.LogInfo("[COOP-GameSession] requestGameFlowCtrlActivefromLoad failed"); }
                            }
                        }
                    }
                    catch { }

                    CoopState.StatusMessage = "Loading save slot " + (slot + 1) + "...";
                    CoopState.SessionStarted = true;
                    return;
                }
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-GameSession] LoadSave error: " + e.Message);
        }

        CoopState.StatusMessage = "Load failed. Please load save manually from title screen.";
        CoopState.SessionStarted = true;
    }

    static void ContinueCurrentGame()
    {
        CoopState.SessionStarted = true;
        CoopState.StatusMessage = "Continuing current game.";
    }

    // =====================================================================
    //  PUBLIC: GameManager access for other plugins
    // =====================================================================

    /// <summary>
    /// Check if currently in found footage mode.
    /// Called by cutscene skipper.
    /// </summary>
    public static bool IsFoundFootage()
    {
        if (_gameManager == null) return false;
        try
        {
            return (bool)_gameManager.Call("isFoundFootage");
        }
        catch { return false; }
    }

    /// <summary>
    /// Exit found footage mode.
    /// Called by cutscene skipper.
    /// </summary>
    public static bool ExitFoundFootage()
    {
        if (_gameManager == null) return false;
        try
        {
            _gameManager.Call("exitFoundFootage");
            API.LogInfo("[COOP-GameSession] exitFoundFootage() called");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Check if scene is currently loading.
    /// </summary>
    public static bool IsSceneLoading()
    {
        if (_gameManager == null) return false;
        try
        {
            return (bool)_gameManager.Call("get_IsSceneLoading");
        }
        catch { return false; }
    }
}
