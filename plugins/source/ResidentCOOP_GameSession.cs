// ResidentCOOP_GameSession.cs
// Game session management using app.GameManager singleton.
//
// NEW DESIGN (simplified):
//   - The mod NEVER tries to auto-start a new game or auto-load a save.
//     Trying to call newGameRequest directly while the title screen is
//     showing does NOT close the title UI — it only broke the title menu.
//   - Instead: when the user clicks "Start Hosting" we just fire up the
//     network server and go straight to the Hosting screen. The user then
//     clicks "New Game" or "Continue" on RE7's native title menu as usual.
//     Co-op detects the player spawning and activates automatically.
//   - The requestPause block is only active when we have a real partner
//     connected AND we are actually in-game (player GameObject exists).
//     In the title screen, pause is left alone so the title menu works.
//
// Key methods on app.GameManager:
//   chapterJumpRequest(string,bool,str) - Jump to a specific chapter
//   getChapterJumpDataKey(ChapterNo)   - Map enum value -> key string
//   get_IsSceneLoading()               - Check if a scene is loading
//   get_CurrentChapter()               - Read current ChapterNo
//   exitFoundFootage()                 - Exit found footage mode
//   isFoundFootage()                   - Check if in found footage

using System;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_GameSession
{
    static ManagedObject _gameManager = null;

    [PluginEntryPoint]
    public static void Main()
    {
        // Refined pause-prevention hook: only block pauses while a partner is
        // actually connected and we are actually in-game (player GO exists).
        // This keeps the RE7 title menu fully functional.
        try
        {
            TDB tdb = API.GetTDB();
            TypeDefinition gmType = tdb?.FindType("app.GameManager");
            if (gmType != null)
            {
                Method reqPause = gmType.GetMethod("requestPause");
                if (reqPause != null)
                {
                    MethodHook.Create(reqPause, false).AddPre(
                        new MethodHook.PreHookDelegate(OnPreRequestPause));
                    API.LogInfo("[COOP-GameSession] Hooked GM.requestPause (contextual pause prevention)");
                }
            }
        }
        catch (Exception e)
        {
            API.LogWarning("[COOP-GameSession] Could not hook requestPause: " + e.Message);
        }

        API.LogInfo("[COOP-GameSession] Loaded.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP-GameSession] Unloaded.");
    }

    /// <summary>
    /// Block pause requests only when both sides are connected AND we are actually
    /// in-game. This allows the title menu to function normally while we're "hosting"
    /// but the partner hasn't joined and/or we're not yet in-game.
    /// </summary>
    static PreHookResult OnPreRequestPause(Span<ulong> args)
    {
        // Not actively co-op playing → let the game pause however it wants.
        if (CoopState.Status != ConnectionStatus.Connected) return PreHookResult.Continue;
        if (!IsPlayerInGame()) return PreHookResult.Continue;

        // Real co-op play + in-game → block the pause so the partner doesn't freeze.
        return PreHookResult.Skip;
    }

    /// <summary>
    /// Returns true when the player GameObject exists in the world (i.e. not in
    /// a menu/title). Used for pause gating and starting-items gating.
    /// </summary>
    public static bool IsPlayerInGame()
    {
        try
        {
            dynamic om = API.GetManagedSingleton("app.ObjectManager");
            if (om == null) return false;
            dynamic player = (om as ManagedObject).GetField("PlayerObj");
            return player != null;
        }
        catch { return false; }
    }

    // Pending chapter jump (after new game loads / Force Sync to Host)
    static bool _chapterJumpPending = false;
    static int _chapterJumpTarget = 0;
    static int _chapterJumpDelayFrames = 0;

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

            // Process session start (user clicked "Start Hosting").
            if (CoopState.SessionStartRequested && !CoopState.SessionStarted)
            {
                CoopState.SessionStartRequested = false;
                MarkSessionStarted();
            }

            // Process a pending chapter jump (force sync to host).
            if (_chapterJumpPending && _gameManager != null)
            {
                if (_chapterJumpDelayFrames > 0)
                {
                    _chapterJumpDelayFrames--;
                }
                else
                {
                    PerformChapterJump(_chapterJumpTarget);
                    _chapterJumpPending = false;
                }
            }

            // Force-sync to host (triggered from the UI)
            if (CoopState.ForceSyncToHostRequested)
            {
                CoopState.ForceSyncToHostRequested = false;
                if (CoopState.HostCurrentChapter >= 0)
                {
                    _chapterJumpPending = true;
                    _chapterJumpTarget = CoopState.HostCurrentChapter;
                    _chapterJumpDelayFrames = 10;
                    CoopState.StatusMessage = "Syncing to host's chapter...";
                    API.LogInfo("[COOP-GameSession] ForceSyncToHost -> ChapterNo " + CoopState.HostCurrentChapter);
                }
            }

            // Continuously update LocalCurrentChapter so other plugins / UI can read it.
            UpdateLocalChapterCache();

            // Give starting items once the local player spawns (only the first time).
            if (CoopState.IsCoopActive && !CoopState.StartingItemsGiven && _gameManager != null)
            {
                TryGiveStartingItems();
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

    /// <summary>
    /// Reads the current ChapterNo from the GameManager and caches it into CoopState.
    /// Called every update tick (cheap call).
    /// </summary>
    static void UpdateLocalChapterCache()
    {
        if (_gameManager == null) return;
        try
        {
            dynamic ch = _gameManager.Call("get_CurrentChapter");
            if (ch != null)
            {
                try { CoopState.LocalCurrentChapter = (int)ch; }
                catch { try { CoopState.LocalCurrentChapter = (int)(long)ch; } catch { } }
            }
        }
        catch { }
    }

    /// <summary>
    /// Public read of the local player's current chapter enum value. Used by Core.cs
    /// when sending the HandshakeAck to the client.
    /// </summary>
    public static int GetCurrentChapter()
    {
        UpdateLocalChapterCache();
        return CoopState.LocalCurrentChapter;
    }

    // =====================================================================
    //  SESSION START (no auto new-game!)
    // =====================================================================

    /// <summary>
    /// Called when the user clicked "Start Hosting" or a client successfully
    /// connected. We do NOT try to change game state — we just flip our session
    /// flag. The player's own interaction with RE7's title screen (New Game / Continue)
    /// drives the real game-start flow.
    /// </summary>
    static void MarkSessionStarted()
    {
        CoopState.SessionStarted = true;

        // Always kick off the starting-items flow. TryGiveStartingItems is
        // idempotent (it flips StartingItemsGiven = true on success) and is
        // gated by IsInventoryReady() so it won't crash if the scene is still
        // loading. This covers BOTH cases:
        //   - Hosting from the title screen: waits for Ethan to spawn.
        //   - Hosting mid-game with Ethan already alive: injects knife/gun/ammo
        //     once the inventory is stable.
        CoopState.StartingItemsGiven = false;
        CoopState.InventoryStableFrames = 0;

        bool inGame = IsPlayerInGame();
        CoopState.StatusMessage = inGame
            ? "Co-op active in-game. Preparing starting items..."
            : "Hosting! On the RE7 title screen, click 'New Game' or 'Continue'. Co-op activates automatically.";
        API.LogInfo("[COOP-GameSession] MarkSessionStarted: starting-items flow enabled (in-game=" + inGame + ")");
    }

    // =====================================================================
    //  PUBLIC: GameManager access for other plugins
    // =====================================================================

    public static bool IsFoundFootage()
    {
        if (_gameManager == null) return false;
        try { return (bool)_gameManager.Call("isFoundFootage"); }
        catch { return false; }
    }

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

    public static bool IsSceneLoading()
    {
        if (_gameManager == null) return false;
        try { return (bool)_gameManager.Call("get_IsSceneLoading"); }
        catch { return false; }
    }

    // =====================================================================
    //  CHAPTER JUMP — Jump to a specific chapter via GameManager
    //  (Used by the UI "Jump to Selected Chapter" button and by Force Sync to Host.)
    // =====================================================================

    // Real ChapterNo enum values from il2cpp dump (NOT sequential!)
    // BootLogo=0, Chapter0=2, Chapter1=4, Chapter3=5, Chapter4=6,
    // FF000-FF040=7-11, Chapter123=13, Chapter324=14, EndingMovie=17
    static readonly int[] ALL_CHAPTER_ENUM_VALUES = { 0, 2, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 17 };

    public static void PerformChapterJump(int chapterEnumValue)
    {
        if (_gameManager == null) return;

        try
        {
            API.LogInfo("[COOP-GameSession] Performing chapter jump, ChapterNo enum = " + chapterEnumValue);

            EnumerateChapterJumpKeys();

            string jumpKey = null;
            try
            {
                dynamic key = _gameManager.Call("getChapterJumpDataKey", chapterEnumValue);
                if (key != null) jumpKey = key.ToString();
            }
            catch
            {
                try
                {
                    dynamic key = _gameManager.Call("getChapterJumpDataKey", (long)chapterEnumValue);
                    if (key != null) jumpKey = key.ToString();
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(jumpKey))
            {
                API.LogInfo("[COOP-GameSession] Got jump key: '" + jumpKey + "' for enum value " + chapterEnumValue);

                var jumpKeyStr = REFrameworkNET.VM.CreateString(jumpKey);
                var emptyStr = REFrameworkNET.VM.CreateString("");
                _gameManager.Call("chapterJumpRequest", jumpKeyStr, false, emptyStr);
                CoopState.StatusMessage = "Jumping to chapter (key=" + jumpKey + ")...";
                CoopState.StartingItemsGiven = false;
                API.LogInfo("[COOP-GameSession] chapterJumpRequest called successfully!");
                return;
            }

            API.LogWarning("[COOP-GameSession] No jump key for enum " + chapterEnumValue);
            CoopState.StatusMessage = "Chapter jump: key lookup failed for " + chapterEnumValue;
        }
        catch (Exception e)
        {
            API.LogError("[COOP-GameSession] ChapterJump failed: " + e.Message);
            CoopState.StatusMessage = "Chapter jump failed: " + e.Message;
        }
    }

    static bool _keysEnumerated = false;

    static void EnumerateChapterJumpKeys()
    {
        if (_keysEnumerated) return;
        _keysEnumerated = true;

        try
        {
            API.LogInfo("[COOP-GameSession] === Enumerating chapter jump keys (using real enum values) ===");

            foreach (int enumVal in ALL_CHAPTER_ENUM_VALUES)
            {
                try
                {
                    dynamic key = _gameManager.Call("getChapterJumpDataKey", enumVal);
                    if (key != null)
                    {
                        string keyStr = key.ToString();
                        if (!string.IsNullOrEmpty(keyStr))
                            API.LogInfo("[COOP-GameSession]   ChapterNo(" + enumVal + ") -> key: '" + keyStr + "'");
                    }
                }
                catch { }
            }

            int[] dlcValues = { 20, 21, 22, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37 };
            foreach (int v in dlcValues)
            {
                try
                {
                    dynamic key = _gameManager.Call("getChapterJumpDataKey", v);
                    if (key != null)
                    {
                        string keyStr = key.ToString();
                        if (!string.IsNullOrEmpty(keyStr))
                            API.LogInfo("[COOP-GameSession]   ChapterNo(" + v + ") -> key: '" + keyStr + "'");
                    }
                }
                catch { }
            }

            API.LogInfo("[COOP-GameSession] === End chapter jump key enumeration ===");
        }
        catch (Exception e)
        {
            API.LogError("[COOP-GameSession] EnumerateKeys error: " + e.Message);
        }
    }

    // =====================================================================
    //  STARTING ITEMS — Give knife + pistol + 15 bullets when first spawning.
    //  From il2cpp dump:
    //    app.ItemManager.createItemInstance(via.GameObject owner, string itemDataID) -> via.GameObject
    //    app.ItemManager.createDropItemInstance(via.GameObject owner, string itemDataID, int setStackNum) -> via.GameObject
    //    app.Inventory.AddList(via.GameObject obj, bool isDrawOff) -> void
    //    app.Inventory is a COMPONENT on the player GameObject
    // =====================================================================

    // REAL item IDs confirmed from in-game get_ItemDataIDNames dump (see items.txt).
    static readonly string[] HANDGUN_IDS = { "Handgun_G17", "Handgun_M19", "Handgun_MPM" };
    static readonly string[] BULLET_IDS  = { "HandgunBullet", "HandgunBulletL" };
    static readonly string[] KNIFE_IDS   = { "Knife", "KitchenKnife", "CKnife" };

    static int _itemGiveRetryFrames = 0;
    static bool _itemIDsDumped = false;

    // Cache of discovered 'app.Item' stack-count setter (name + kind)
    static string _appItemStackMember = null;   // property/method/field name
    static int _appItemStackKind = 0;           // 0=none, 1=method(set_StackNum style), 2=field
    static bool _appItemIntrospected = false;

    /// <summary>
    /// Guard: only return true when the inventory is safe to poke. The crash stack we saw
    /// (`app.CH9ItemFlashLight.doAwake -> EnvActivateManager.folderActivate -> createDropItemInstance`)
    /// means we were calling item-creation while the scene was still being activated.
    /// </summary>
    static bool IsInventoryReady(ManagedObject playerGO, ManagedObject invMO)
    {
        try
        {
            // 1) Scene must not be loading
            if (_gameManager != null)
            {
                try
                {
                    bool loading = (bool)_gameManager.Call("get_IsSceneLoading");
                    if (loading) { CoopState.InventoryStableFrames = 0; return false; }
                }
                catch { }
            }

            // 2) Player transform must have a real position (not origin 0,0,0)
            try
            {
                dynamic tf = playerGO.Call("get_Transform");
                if (tf == null) { CoopState.InventoryStableFrames = 0; return false; }

                var typed = ((tf as ManagedObject) as IObject).As<via.Transform>();
                if (typed != null)
                {
                    var p = typed.Position;
                    float magSq = p.x * p.x + p.y * p.y + p.z * p.z;
                    if (magSq < 0.0001f) { CoopState.InventoryStableFrames = 0; return false; }
                }
            }
            catch { /* tolerate */ }

            // 3) Inventory component is present
            if (invMO == null) { CoopState.InventoryStableFrames = 0; return false; }

            // 4) Cooldown: require N consecutive good frames so folderActivate can finish.
            CoopState.InventoryStableFrames++;
            if (CoopState.InventoryStableFrames < CoopState.RequiredStableFrames)
            {
                if (CoopState.InventoryStableFrames % 60 == 0)
                    API.LogInfo("[COOP-Items] Waiting for inventory to stabilize... "
                        + CoopState.InventoryStableFrames + "/" + CoopState.RequiredStableFrames);
                return false;
            }

            return true;
        }
        catch
        {
            CoopState.InventoryStableFrames = 0;
            return false;
        }
    }

    static void IntrospectAppItemOnce(TDB tdb)
    {
        if (_appItemIntrospected) return;
        _appItemIntrospected = true;

        try
        {
            TypeDefinition appItemType = tdb.FindType("app.Item");
            if (appItemType == null) return;

            var methods = appItemType.GetMethods();
            if (methods != null)
            {
                for (int i = 0; i < methods.Count; i++)
                {
                    string n = methods[i].GetName();
                    string l = n.ToLower();
                    if (l == "set_stacknum" || l == "setstacknum" || l == "set_count"
                        || l == "setstackcount" || l == "set_itemnum" || l == "setitemnum")
                    {
                        _appItemStackMember = n;
                        _appItemStackKind = 1;
                        API.LogInfo("[COOP-Items] app.Item stack setter found (method): " + n);
                        return;
                    }
                }
            }

            var fields = appItemType.GetFields();
            if (fields != null)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    string n = fields[i].GetName();
                    string l = n.ToLower();
                    if (l == "_stacknum" || l == "stacknum" || l == "_count"
                        || l == "_itemnum" || l == "itemnum")
                    {
                        _appItemStackMember = n;
                        _appItemStackKind = 2;
                        API.LogInfo("[COOP-Items] app.Item stack setter found (field): " + n);
                        return;
                    }
                }
            }

            API.LogInfo("[COOP-Items] app.Item: no stack setter discovered — will rely on createDropItemInstance fallback for counts > 1");
        }
        catch (Exception e)
        {
            API.LogWarning("[COOP-Items] IntrospectAppItem error: " + e.Message);
        }
    }

    static bool TryCreateAndAddItem(string[] ids, int count, string label,
        ManagedObject playerGO, ManagedObject invMO, ManagedObject imMO, TypeDefinition invType, TDB tdb)
    {
        foreach (string id in ids)
        {
            try
            {
                var idStr = REFrameworkNET.VM.CreateString(id);
                ManagedObject itemGOMO = null;

                // Attempt 1: createItemInstance (safer — no shell/DoomsBehavior)
                try
                {
                    dynamic itemGO = imMO.Call("createItemInstance", playerGO, idStr);
                    if (itemGO != null)
                    {
                        itemGOMO = itemGO as ManagedObject;

                        // Apply stack count if needed
                        if (count > 1 && _appItemStackMember != null)
                        {
                            try
                            {
                                TypeDefinition appItemType = tdb.FindType("app.Item");
                                object appItemRt = appItemType.GetRuntimeType();
                                dynamic itemComp = itemGOMO.Call("getComponent(System.Type)", appItemRt);
                                if (itemComp != null)
                                {
                                    ManagedObject ic = itemComp as ManagedObject;
                                    if (_appItemStackKind == 1)
                                    {
                                        try { ic.Call(_appItemStackMember, count); }
                                        catch { try { ic.Call(_appItemStackMember, (long)count); } catch { } }
                                    }
                                    else if (_appItemStackKind == 2)
                                    {
                                        try { ic.SetField(_appItemStackMember, count); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ci)
                {
                    if (CoopState.FrameCount % 300 == 0)
                        API.LogInfo("[COOP-Items] " + label + " '" + id + "' createItemInstance: " + ci.Message);
                }

                // Attempt 2 (fallback): createDropItemInstance for counts > 1 if no stack setter
                if (itemGOMO == null && count > 1 && _appItemStackMember == null)
                {
                    try
                    {
                        dynamic dropGO = imMO.Call("createDropItemInstance", playerGO, idStr, count);
                        if (dropGO != null) itemGOMO = dropGO as ManagedObject;
                    }
                    catch (Exception di)
                    {
                        if (CoopState.FrameCount % 300 == 0)
                            API.LogInfo("[COOP-Items] " + label + " '" + id + "' createDropItemInstance: " + di.Message);
                    }
                }

                if (itemGOMO == null) continue;

                // Add to inventory
                try
                {
                    invMO.Call("AddList", itemGOMO, false);
                    API.LogInfo("[COOP-Items] " + label + " '" + id + "' x" + count + " added via AddList!");
                    return true;
                }
                catch (Exception addEx)
                {
                    API.LogWarning("[COOP-Items] AddList failed for " + label + " '" + id + "': " + addEx.Message);
                    try
                    {
                        TypeDefinition appItemType = tdb.FindType("app.Item");
                        object appItemRt = appItemType.GetRuntimeType();
                        dynamic itemComp = itemGOMO.Call("getComponent(System.Type)", appItemRt);
                        if (itemComp != null)
                        {
                            Method addItemMethod = invType.GetMethod("addItem");
                            if (addItemMethod != null)
                            {
                                addItemMethod.Invoke(invMO, new object[] { itemComp, null });
                                API.LogInfo("[COOP-Items] " + label + " '" + id + "' added via addItem fallback!");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex2)
                    {
                        API.LogWarning("[COOP-Items] addItem fallback for " + label + ": " + ex2.Message);
                    }
                }
            }
            catch (Exception outer)
            {
                if (CoopState.FrameCount % 300 == 0)
                    API.LogInfo("[COOP-Items] " + label + " '" + id + "' failed: " + outer.Message);
            }
        }

        return false;
    }

    static void TryGiveStartingItems()
    {
        if (_itemGiveRetryFrames > 0) { _itemGiveRetryFrames--; return; }
        _itemGiveRetryFrames = 120;

        try
        {
            // Get the player GameObject
            dynamic objMgr = API.GetManagedSingleton("app.ObjectManager");
            if (objMgr == null) { return; }

            dynamic playerObj = (objMgr as ManagedObject).GetField("PlayerObj");
            if (playerObj == null)
            {
                // Player not spawned yet — reset stability counter but don't spam logs.
                CoopState.InventoryStableFrames = 0;
                return;
            }

            ManagedObject playerGO = playerObj as ManagedObject;

            TDB tdb = API.GetTDB();
            TypeDefinition invType = tdb.FindType("app.Inventory");
            if (invType == null) { API.LogWarning("[COOP-Items] app.Inventory not in TDB"); return; }

            object invRt = invType.GetRuntimeType();
            if (invRt == null) return;

            dynamic inventoryComp = playerGO.Call("getComponent(System.Type)", invRt);
            if (inventoryComp == null)
            {
                if (CoopState.FrameCount % 300 == 0)
                    API.LogInfo("[COOP-Items] Player has no Inventory yet, retrying...");
                CoopState.InventoryStableFrames = 0;
                return;
            }
            ManagedObject invMO = inventoryComp as ManagedObject;

            dynamic itemMgr = API.GetManagedSingleton("app.ItemManager");
            if (itemMgr == null) { return; }
            ManagedObject imMO = itemMgr as ManagedObject;

            if (!_itemIDsDumped)
            {
                _itemIDsDumped = true;
                DumpItemDataIDs(imMO);
            }

            IntrospectAppItemOnce(tdb);

            // GUARD: bail out until the inventory is really ready — avoids the
            // access-violation crash inside createDropItemInstance during scene activation.
            if (!IsInventoryReady(playerGO, invMO))
            {
                _itemGiveRetryFrames = 30;
                return;
            }

            API.LogInfo("[COOP-Items] Inventory ready. Creating starting items (Knife + Handgun + Ammo)...");

            bool knifeOk = TryCreateAndAddItem(KNIFE_IDS,   1,  "Knife",   playerGO, invMO, imMO, invType, tdb);
            bool gunOk   = TryCreateAndAddItem(HANDGUN_IDS, 1,  "Handgun", playerGO, invMO, imMO, invType, tdb);
            bool ammoOk  = TryCreateAndAddItem(BULLET_IDS,  15, "Bullets", playerGO, invMO, imMO, invType, tdb);

            if (knifeOk || gunOk || ammoOk)
            {
                CoopState.StartingItemsGiven = true;
                CoopState.StatusMessage = "Starting items: Knife=" + knifeOk + " Gun=" + gunOk + " Ammo=" + ammoOk;
                API.LogInfo("[COOP-Items] Items given! Knife=" + knifeOk + " Gun=" + gunOk + " Ammo=" + ammoOk);
            }
            else
            {
                API.LogInfo("[COOP-Items] No items could be created/added yet, will retry...");
            }
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-Items] TryGiveStartingItems: " + e.Message);
        }
    }

    static void DumpItemDataIDs(ManagedObject itemMgrMO)
    {
        try
        {
            dynamic itemNames = itemMgrMO.Call("get_ItemDataIDNames");
            if (itemNames == null) return;

            ManagedObject namesList = itemNames as ManagedObject;
            int count = 0;
            try { count = (int)namesList.Call("get_Count"); } catch { }

            API.LogInfo("[COOP-Items] === ItemDataIDs (" + count + " total) ===");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic name = namesList.Call("get_Item", i);
                    if (name != null)
                    {
                        string n = name.ToString();
                        if (n.ToLower().Contains("gun") || n.ToLower().Contains("bullet") ||
                            n.ToLower().Contains("weapon") || n.ToLower().Contains("ammo") ||
                            n.ToLower().Contains("handgun") || n.ToLower().Contains("pistol") ||
                            n.ToLower().Contains("wp") || n.ToLower().Contains("knife"))
                        {
                            API.LogInfo("[COOP-Items]   >>> [" + i + "] " + n + " <<<");
                        }
                        else
                        {
                            API.LogInfo("[COOP-Items]   [" + i + "] " + n);
                        }
                    }
                }
                catch { }
            }
            API.LogInfo("[COOP-Items] === End ItemDataIDs ===");
        }
        catch (Exception e)
        {
            API.LogWarning("[COOP-Items] DumpItemDataIDs error: " + e.Message);
        }
    }
}
