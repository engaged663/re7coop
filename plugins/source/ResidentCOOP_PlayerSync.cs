// ResidentCOOP_PlayerSync.cs
// Reads local player state (position, rotation, health) from RE7 game objects
// and writes it into CoopState.LocalPlayer every frame.
//
// Uses app.ObjectManager.getPlayer() (confirmed from singleton dump) instead
// of fragile PlayerObj field access.
//
// Also monitors player health for shared game over:
// if health drops to 0 → broadcast GameOver to partner.

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_PlayerSync
{
    // Cached type lookups (done once)
    static bool _typesResolved = false;
    static TypeDefinition _objectManagerType;
    static TypeDefinition _hpControllerType;
    static TypeDefinition _playerOrderType;
    static object _hpControllerRuntimeType;
    static object _playerOrderRuntimeType;

    // Death tracking
    static bool _wasAlive = true;
    static int _deathCooldownFrames = 0;

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[COOP] ResidentCOOP_PlayerSync loaded.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP] ResidentCOOP_PlayerSync unloaded.");
    }

    /// <summary>
    /// Called every frame before behavior update.
    /// Reads RE7's local player and populates CoopState.LocalPlayer.
    /// </summary>
    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        try
        {
            if (!CoopState.IsCoopActive) return;

            ResolveTypes();
            ReadLocalPlayerState();
        }
        catch (Exception e)
        {
            // Silently handle - player may not be loaded yet
            if (CoopState.FrameCount % 300 == 0)
            {
                API.LogError("[COOP-PlayerSync] " + e.Message);
            }
        }
    }

    static int _typeRetryFrames = 0;

    static void ResolveTypes()
    {
        // If fully resolved (ObjectManager + HitPointController), skip
        if (_typesResolved && _hpControllerRuntimeType != null) return;

        // Throttle retry to every ~2 seconds
        if (_typesResolved && _typeRetryFrames > 0) { _typeRetryFrames--; return; }
        _typeRetryFrames = 120;

        TDB tdb = API.GetTDB();
        if (tdb == null) return;

        if (_objectManagerType == null)
            _objectManagerType = tdb.FindType("app.ObjectManager");
        if (_hpControllerType == null)
            _hpControllerType = tdb.FindType("app.HitPointController");
        if (_playerOrderType == null)
            _playerOrderType = tdb.FindType("app.PlayerOrder");

        if (_hpControllerType != null && _hpControllerRuntimeType == null)
            _hpControllerRuntimeType = _hpControllerType.GetRuntimeType();
        if (_playerOrderType != null && _playerOrderRuntimeType == null)
            _playerOrderRuntimeType = _playerOrderType.GetRuntimeType();

        bool wasResolved = _typesResolved;
        _typesResolved = (_objectManagerType != null);

        if (_typesResolved && !wasResolved)
            API.LogInfo("[COOP-PlayerSync] Types resolved. HpCtrl=" +
                (_hpControllerType != null) + " PlayerOrder=" + (_playerOrderType != null));

        if (_hpControllerRuntimeType != null && wasResolved)
            API.LogInfo("[COOP-PlayerSync] HitPointController now available!");
    }

    static void ReadLocalPlayerState()
    {
        // Get player via app.ObjectManager.getPlayer() — confirmed from singleton dump
        dynamic objectManager = API.GetManagedSingleton("app.ObjectManager");
        if (objectManager == null) return;

        dynamic playerObj = null;
        try
        {
            // Use getPlayer() method (from dump: "via.GameObject getPlayer()")
            playerObj = (objectManager as ManagedObject).Call("getPlayer");
        }
        catch
        {
            // Fallback: try findActivePlayer()
            try
            {
                playerObj = (objectManager as ManagedObject).Call("findActivePlayer");
            }
            catch { return; }
        }

        if (playerObj == null) return;

        ManagedObject playerMO = playerObj as ManagedObject;
        if (playerMO == null) return;

        // ==== TRANSFORM (position + rotation) ====
        ReadTransform(playerMO);

        // ==== HEALTH ====
        ReadPlayerHealth(playerMO);

        // ==== FLAGS (aiming, crouching, running, flashlight) ====
        ReadPlayerFlags(playerMO);

        // ==== SHARED GAME OVER CHECK ====
        CheckDeath();
    }

    // =====================================================================
    //  TRANSFORM — Position and Rotation via typed proxy
    // =====================================================================

    static void ReadTransform(ManagedObject playerMO)
    {
        try
        {
            dynamic transform = playerMO.Call("get_Transform");
            if (transform == null) return;

            ManagedObject transformMO = transform as ManagedObject;
            if (transformMO == null) return;

            // Preferred: use typed proxy for direct struct access
            try
            {
                var typed = (transformMO as IObject).As<via.Transform>();
                if (typed != null)
                {
                    var pos = typed.Position;
                    CoopState.LocalPlayer.PosX = pos.x;
                    CoopState.LocalPlayer.PosY = pos.y;
                    CoopState.LocalPlayer.PosZ = pos.z;

                    var rot = typed.Rotation;
                    CoopState.LocalPlayer.RotX = rot.x;
                    CoopState.LocalPlayer.RotY = rot.y;
                    CoopState.LocalPlayer.RotZ = rot.z;
                    CoopState.LocalPlayer.RotW = rot.w;
                    return;
                }
            }
            catch { }

            // Fallback: dynamic call
            try
            {
                dynamic pos = transformMO.Call("get_Position");
                if (pos != null)
                {
                    CoopState.LocalPlayer.PosX = (float)pos.x;
                    CoopState.LocalPlayer.PosY = (float)pos.y;
                    CoopState.LocalPlayer.PosZ = (float)pos.z;
                }

                dynamic rot = transformMO.Call("get_Rotation");
                if (rot != null)
                {
                    CoopState.LocalPlayer.RotX = (float)rot.x;
                    CoopState.LocalPlayer.RotY = (float)rot.y;
                    CoopState.LocalPlayer.RotZ = (float)rot.z;
                    CoopState.LocalPlayer.RotW = (float)rot.w;
                }
            }
            catch { }
        }
        catch { }
    }

    // =====================================================================
    //  HEALTH — via HitPointController component on the player GO
    // =====================================================================

    /// <summary>
    /// Read player health. Primary: HitPointController. No fallback to PlayerStatus
    /// since get_HitPoint doesn't exist in RE7.
    /// </summary>
    static void ReadPlayerHealth(ManagedObject playerObj)
    {
        try
        {
            // Approach 1: HitPointController (confirmed in RE7)
            if (_hpControllerRuntimeType != null)
            {
                dynamic hitPointCtrl = playerObj.Call("getComponent(System.Type)", _hpControllerRuntimeType);
                if (hitPointCtrl != null)
                {
                    try
                    {
                        CoopState.LocalPlayer.Health = (float)(hitPointCtrl as ManagedObject).Call("get_CurrentHitPoint");
                        CoopState.LocalPlayer.MaxHealth = (float)(hitPointCtrl as ManagedObject).Call("get_DefaultHitPoint");
                        return;
                    }
                    catch { }
                }
            }

            // Fallback: set reasonable defaults if we haven't read health yet
            if (CoopState.LocalPlayer.MaxHealth <= 0)
            {
                CoopState.LocalPlayer.MaxHealth = 1000f;
                CoopState.LocalPlayer.Health = 1000f;
            }
        }
        catch { }
    }

    // =====================================================================
    //  FLAGS — Disabled for now (RE7 doesn't expose these as simple getters)
    //  get_IsCrouched, get_IsRunning, get_IsGrappleAimEnable do NOT exist
    //  in RE7's app.PlayerOrder. Calling them causes REFramework internal
    //  "Method not found" spam even inside try-catch.
    // =====================================================================

    static void ReadPlayerFlags(ManagedObject playerObj)
    {
        // RE7 doesn't have simple bool getters for player states.
        // Leave flags at 0 for now until we find the correct methods.
        CoopState.LocalPlayer.Flags = 0;
    }

    // =====================================================================
    //  SHARED GAME OVER — Detect player death and notify partner
    // =====================================================================

    static void CheckDeath()
    {
        if (_deathCooldownFrames > 0)
        {
            _deathCooldownFrames--;
            return;
        }

        // Reset shared game over flag if we're alive
        float hp = CoopState.LocalPlayer.Health;
        float maxHp = CoopState.LocalPlayer.MaxHealth;

        bool isAlive = (hp > 0f && maxHp > 0f);

        if (_wasAlive && !isAlive)
        {
            // Player just died — broadcast game over to partner
            API.LogInfo("[COOP-PlayerSync] Local player died! Broadcasting shared game over.");
            CallCoreBroadcastGameOver();
            _deathCooldownFrames = 120; // Don't spam for 2 seconds
        }

        _wasAlive = isAlive;

        // Also reset shared game over state if we're alive
        if (isAlive && CoopState.SharedGameOverTriggered)
        {
            CoopState.SharedGameOverTriggered = false;
        }
    }

    // =====================================================================
    //  REFLECTION — Call Core plugin methods without direct reference
    // =====================================================================

    static Type _coreType = null;

    static void CallCoreBroadcastGameOver()
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

            var method = _coreType.GetMethod("BroadcastGameOver",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method != null) method.Invoke(null, null);
        }
        catch { }
    }
}
