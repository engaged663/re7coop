// ResidentCOOP_PlayerSync.cs
// Reads local player state (position, rotation, health) from RE7 game objects
// and writes it into CoopState.LocalPlayer every frame.

using System;
using REFrameworkNET;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_PlayerSync
{
    // Cached type lookups (done once)
    static bool _typesResolved = false;
    static TypeDefinition _objectManagerType;
    static TypeDefinition _inventoryTypeDef;
    static TypeDefinition _playerOrderTypeDef;
    static TypeDefinition _eventActionControllerTypeDef;
    static object _inventoryRuntimeType;
    static object _playerOrderRuntimeType;

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

    static void ResolveTypes()
    {
        if (_typesResolved) return;

        TDB tdb = API.GetTDB();
        if (tdb == null) return;

        _objectManagerType = tdb.FindType("app.ObjectManager");
        _inventoryTypeDef = tdb.FindType("app.Inventory");
        _playerOrderTypeDef = tdb.FindType("app.PlayerOrder");
        _eventActionControllerTypeDef = tdb.FindType("app.EventActionController");

        if (_inventoryTypeDef != null)
            _inventoryRuntimeType = _inventoryTypeDef.GetRuntimeType();
        if (_playerOrderTypeDef != null)
            _playerOrderRuntimeType = _playerOrderTypeDef.GetRuntimeType();

        _typesResolved = (_objectManagerType != null);

        if (_typesResolved)
            API.LogInfo("[COOP-PlayerSync] Types resolved successfully.");
    }

    static void ReadLocalPlayerState()
    {
        // Get player via app.ObjectManager singleton (confirmed from RE7.lua)
        dynamic objectManager = API.GetManagedSingleton("app.ObjectManager");
        if (objectManager == null) return;

        dynamic playerObj = null;
        try
        {
            playerObj = objectManager.PlayerObj;
        }
        catch
        {
            // Try field access pattern like the Lua does
            try
            {
                playerObj = (objectManager as ManagedObject).GetField("PlayerObj");
            }
            catch { return; }
        }

        if (playerObj == null) return;

        // Get transform for position/rotation
        ManagedObject playerMO = playerObj as ManagedObject;
        if (playerMO == null) return;

        dynamic transform = null;
        try
        {
            transform = playerMO.Call("get_Transform");
        }
        catch { return; }

        if (transform == null) return;

        // Read position
        try
        {
            ManagedObject transformMO = transform as ManagedObject;
            if (transformMO == null) return;

            // Use typed proxy if available, otherwise dynamic
            try
            {
                var typedTransform = (transformMO as IObject).As<via.Transform>();
                if (typedTransform != null)
                {
                    var pos = typedTransform.Position;
                    CoopState.LocalPlayer.PosX = pos.x;
                    CoopState.LocalPlayer.PosY = pos.y;
                    CoopState.LocalPlayer.PosZ = pos.z;

                    // Rotation
                    try
                    {
                        var rot = typedTransform.Rotation;
                        CoopState.LocalPlayer.RotX = rot.x;
                        CoopState.LocalPlayer.RotY = rot.y;
                        CoopState.LocalPlayer.RotZ = rot.z;
                        CoopState.LocalPlayer.RotW = rot.w;
                    }
                    catch { }
                }
            }
            catch
            {
                // Fallback to dynamic calls
                try
                {
                    dynamic pos = transformMO.Call("get_Position");
                    if (pos != null)
                    {
                        CoopState.LocalPlayer.PosX = (float)pos.x;
                        CoopState.LocalPlayer.PosY = (float)pos.y;
                        CoopState.LocalPlayer.PosZ = (float)pos.z;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Read health from the player condition
        // RE7 uses RopewaySurvivorPlayerCondition but the exact field depends on the build
        // Try multiple approaches
        ReadPlayerHealth(playerMO);

        // Read equipped weapon and flags
        ReadPlayerFlags(playerMO);
    }

    static void ReadPlayerHealth(ManagedObject playerObj)
    {
        try
        {
            // Try getting the HitPointVital component or field
            // In RE7, health can be accessed through various paths
            // Approach 1: Look for a health-related component
            dynamic hitPointCtrl = null;
            try
            {
                var hpType = API.GetTDB().FindType("app.HitPointController");
                if (hpType != null)
                {
                    var rtType = hpType.GetRuntimeType();
                    if (rtType != null)
                    {
                        hitPointCtrl = playerObj.Call("getComponent(System.Type)", rtType);
                    }
                }
            }
            catch { }

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

            // Approach 2: Try through CharacterManager
            try
            {
                dynamic charMgr = API.GetManagedSingleton("app.CharacterManager");
                if (charMgr != null)
                {
                    dynamic manualPlayer = null;
                    try { manualPlayer = charMgr.ManualPlayer; } catch { }
                    if (manualPlayer != null)
                    {
                        try
                        {
                            dynamic hitCtrl = (manualPlayer as ManagedObject).Call("get_HitController");
                            if (hitCtrl != null)
                            {
                                CoopState.LocalPlayer.Health = (float)(hitCtrl as ManagedObject).Call("get_CurrentHitPoint");
                                CoopState.LocalPlayer.MaxHealth = (float)(hitCtrl as ManagedObject).Call("get_DefaultHitPoint");
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Fallback: set defaults
            if (CoopState.LocalPlayer.MaxHealth <= 0)
            {
                CoopState.LocalPlayer.MaxHealth = 1000f;
                CoopState.LocalPlayer.Health = 1000f;
            }
        }
        catch { }
    }

    static void ReadPlayerFlags(ManagedObject playerObj)
    {
        byte flags = 0;

        try
        {
            // Check PlayerOrder for aiming state
            if (_playerOrderRuntimeType != null)
            {
                dynamic order = playerObj.Call("getComponent(System.Type)", _playerOrderRuntimeType);
                if (order != null)
                {
                    try
                    {
                        bool isGrappleAim = (bool)(order as ManagedObject).Call("get_IsGrappleAimEnable");
                        if (isGrappleAim) flags |= 1; // isAiming
                    }
                    catch { }
                }
            }
        }
        catch { }

        CoopState.LocalPlayer.Flags = flags;
    }
}
