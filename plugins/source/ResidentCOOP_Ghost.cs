// ResidentCOOP_Ghost.cs
// Remote player representation in the game world.
//
// NEW DESIGN ("spawn-once + remote-only movement"):
//   - As soon as co-op is active we try to spawn a 3D ghost GameObject by
//     cloning the local player.
//   - At spawn time we POSITION the ghost ONCE at the local player's current
//     position and rotation (so it materializes exactly where the host is).
//   - Per-frame movement:
//       * If a partner is connected AND we have remote data → move the ghost
//         to remote pos/rot.
//       * Otherwise → DO NOTHING. The ghost stays wherever it was last put
//         (either the spawn position, or the last remote state received).
//     This means: before a partner joins, the ghost sits still at the
//     host's spawn position. When the partner connects, the ghost starts
//     following the partner's sync updates.

//
// Spawn strategy (tries, in order):
//   1) via.GameObject.Instantiate(player) — clean clone if the game supports it.
//   2) via.GameObject static Instantiate via TDB.
// Both paths are best-effort. If none work, we keep the ImGui indicator as
// the only visualization (still useful for locating the partner at distance).
//
// Safety on clone:
//   - Disable physics/collision/audio/AI components on the ghost so it doesn't
//     push the local player, make noise, or try to drive itself.
//   - (Best-effort) disable the ghost's head mesh to prefer the in-engine
//     "headless Ethan" look.

using System;
using System.Numerics;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_Ghost
{
    // Camera data for ImGui indicator
    static float[] _viewMatrix = new float[16];
    static bool _matricesValid = false;
    static float _screenWidth = 1920f;
    static float _screenHeight = 1080f;

    // 3D ghost model tracking
    static ManagedObject _ghostGameObject = null;
    static ManagedObject _ghostTransform = null;
    static bool _ghost3DActive = false;
    static int _spawnRetryFrames = 0;
    static int _spawnAttempts = 0;
    const int MAX_SPAWN_ATTEMPTS = 20; // stop trying after N failed attempts

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[COOP-Ghost] Loaded (3D co-located ghost + ImGui indicator).");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        DestroyGhost3D();
        API.LogInfo("[COOP-Ghost] Unloaded.");
    }

    // =====================================================================
    //  PER-FRAME: Update camera + spawn/update 3D ghost
    // =====================================================================

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        try
        {
            // When co-op stops, tear down the ghost.
            if (!CoopState.IsCoopActive)
            {
                _matricesValid = false;
                if (_ghost3DActive) DestroyGhost3D();
                return;
            }

            CacheCameraData();

            // Try to spawn the ghost once we're in-game.
            if (!_ghost3DActive && _spawnRetryFrames <= 0 && _spawnAttempts < MAX_SPAWN_ATTEMPTS)
            {
                TrySpawnGhost3D();
                _spawnRetryFrames = 120; // retry every ~2s
            }
            else if (_spawnRetryFrames > 0)
            {
                _spawnRetryFrames--;
            }

            // Move ghost each frame.
            if (_ghost3DActive)
            {
                UpdateGhost3DTransform();
            }
        }
        catch (Exception e)
        {
            _matricesValid = false;
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-Ghost] OnPostUpdate error: " + e.Message);
        }
    }

    // =====================================================================
    //  3D GHOST — spawn by cloning the local player
    // =====================================================================

    static void TrySpawnGhost3D()
    {
        _spawnAttempts++;
        try
        {
            // Must have a local player to clone from.
            dynamic objMgr = API.GetManagedSingleton("app.ObjectManager");
            if (objMgr == null) return;

            dynamic playerObj = (objMgr as ManagedObject).GetField("PlayerObj");
            if (playerObj == null)
            {
                // No player yet (title screen). Don't waste attempts.
                _spawnAttempts--;
                return;
            }

            ManagedObject playerMO = playerObj as ManagedObject;
            TDB tdb = API.GetTDB();

            API.LogInfo("[COOP-Ghost] Spawn attempt " + _spawnAttempts + "/" + MAX_SPAWN_ATTEMPTS
                + " - cloning player GameObject...");

            ManagedObject cloneMO = null;

            // --- Strategy 1: instance method Instantiate(via.GameObject) ---
            try
            {
                dynamic clone = playerMO.Call("Instantiate(via.GameObject)", playerMO);
                if (clone != null) cloneMO = clone as ManagedObject;
                if (cloneMO != null)
                    API.LogInfo("[COOP-Ghost] Strategy 1 (instance Instantiate) succeeded.");
            }
            catch (Exception e1)
            {
                API.LogInfo("[COOP-Ghost] Strategy 1 failed: " + e1.Message);
            }

            // --- Strategy 2: static via.GameObject.Instantiate(via.GameObject) ---
            if (cloneMO == null)
            {
                try
                {
                    TypeDefinition goType = tdb.FindType("via.GameObject");
                    if (goType != null)
                    {
                        Method instM = goType.GetMethod("Instantiate(via.GameObject)");
                        if (instM != null)
                        {
                            object cloneObj = instM.Invoke(null, new object[] { playerMO });
                            if (cloneObj != null) cloneMO = cloneObj as ManagedObject;
                            if (cloneMO != null)
                                API.LogInfo("[COOP-Ghost] Strategy 2 (static Instantiate) succeeded.");
                        }
                    }
                }
                catch (Exception e2)
                {
                    API.LogInfo("[COOP-Ghost] Strategy 2 failed: " + e2.Message);
                }
            }

            if (cloneMO == null)
            {
                API.LogInfo("[COOP-Ghost] All spawn strategies failed this attempt.");
                return;
            }

            // Globalize so the reference survives garbage collection.
            try { _ghostGameObject = cloneMO.Globalize(); }
            catch { _ghostGameObject = cloneMO; }

            // Cache transform
            try
            {
                dynamic gtf = _ghostGameObject.Call("get_Transform");
                if (gtf != null)
                {
                    ManagedObject gtfMO = gtf as ManagedObject;
                    try { _ghostTransform = gtfMO.Globalize(); }
                    catch { _ghostTransform = gtfMO; }
                }
            }
            catch (Exception e)
            {
                API.LogWarning("[COOP-Ghost] get_Transform on clone failed: " + e.Message);
            }

            // Rename for debugging
            try
            {
                _ghostGameObject.Call("set_Name",
                    VM.CreateString("COOP_Ghost_" + (CoopState.PartnerName ?? "Partner")));
            }
            catch { }

            // Disable everything that would interfere with us.
            DisableGhostComponents();

            // Try to hide the head (best-effort, doesn't block success).
            DisableGhostHead();

            _ghost3DActive = true;

            // ONE-TIME spawn positioning: place the ghost exactly where the local
            // player (host) is right now. After this, the ghost will NOT follow us;
            // it will stay here until remote player data arrives (partner connected).
            PositionGhostAtLocalOnce();

            API.LogInfo(string.Format(
                "[COOP-Ghost] 3D ghost spawned at host position ({0:F2}, {1:F2}, {2:F2}).",
                CoopState.LocalPlayer.PosX, CoopState.LocalPlayer.PosY, CoopState.LocalPlayer.PosZ));

        }
        catch (Exception e)
        {
            API.LogError("[COOP-Ghost] TrySpawnGhost3D error: " + e.Message);
        }
    }

    /// <summary>
    /// Try to disable the ghost's head mesh. Best-effort only.
    /// </summary>
    static void DisableGhostHead()
    {
        if (_ghostGameObject == null) return;

        try
        {
            TDB tdb = API.GetTDB();
            TypeDefinition meshType = tdb.FindType("via.render.Mesh");
            if (meshType == null) return;

            object meshRt = meshType.GetRuntimeType();
            if (meshRt == null) return;

            dynamic meshes = null;
            try { meshes = _ghostGameObject.Call("getComponents(System.Type)", meshRt); }
            catch { return; }
            if (meshes == null) return;

            ManagedObject meshArr = meshes as ManagedObject;
            int count = 0;
            try { count = (int)meshArr.Call("get_Count"); } catch { }
            if (count == 0) try { count = (int)meshArr.Call("get_Length"); } catch { }

            API.LogInfo("[COOP-Ghost] Ghost has " + count + " render.Mesh components.");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic mesh = null;
                    try { mesh = meshArr.Call("Get", i); }
                    catch { try { mesh = meshArr.Call("get_Item", i); } catch { } }
                    if (mesh == null) continue;

                    ManagedObject meshMO = mesh as ManagedObject;

                    // Query the GameObject name to detect head-related meshes.
                    string meshName = "";
                    try
                    {
                        dynamic meshGO = meshMO.Call("get_GameObject");
                        if (meshGO != null)
                        {
                            dynamic n = (meshGO as ManagedObject).Call("get_Name");
                            if (n != null) meshName = n.ToString();
                        }
                    }
                    catch { }

                    string low = (meshName ?? "").ToLower();
                    if (low.Contains("head") || low.Contains("face") || low.Contains("hair"))
                    {
                        try { meshMO.Call("set_Enabled", false); } catch { }
                        API.LogInfo("[COOP-Ghost] Disabled head-like mesh: '" + meshName + "'");
                    }
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            API.LogInfo("[COOP-Ghost] DisableGhostHead: " + e.Message);
        }
    }

    /// <summary>
    /// Disable components that would cause the ghost to interfere with us
    /// (collide, make noise, react to input, duplicate damage, etc.).
    /// </summary>
    static void DisableGhostComponents()
    {
        if (_ghostGameObject == null) return;

        string[] disableTypes = new string[]
        {
            // Player-controller components
            "app.PlayerMotionController",
            "app.CH8PlayerMotionController",
            "app.CH9PlayerMotionController",
            "app.PlayerOrder",
            "app.PlayerStatus",
            "app.PlayerMeshController",

            // Damage / hit system
            "app.HitPointController",
            "app.Collision.HitController",
            "app.Collision.AttackController",

            // Physics
            "via.physics.Colliders",
            "via.physics.RigidBody",

            // Audio (prevents duplicated footsteps when ghost coincides with local)
            "via.audio.WwiseCharacterSoundContainer",
        };

        TDB tdb = API.GetTDB();
        int disabledCount = 0;

        for (int i = 0; i < disableTypes.Length; i++)
        {
            try
            {
                TypeDefinition t = tdb.FindType(disableTypes[i]);
                if (t == null) continue;

                object rt = t.GetRuntimeType();
                if (rt == null) continue;

                dynamic comp = null;
                try { comp = _ghostGameObject.Call("getComponent(System.Type)", rt); }
                catch { }
                if (comp == null) continue;

                try
                {
                    (comp as ManagedObject).Call("set_Enabled", false);
                    disabledCount++;
                }
                catch { }
            }
            catch { }
        }

        API.LogInfo("[COOP-Ghost] Disabled " + disabledCount + " interfering components on ghost.");
    }

    /// <summary>
    /// Called ONCE right after the ghost is spawned. Places the ghost exactly at
    /// the local player's (host's) current position and rotation so it
    /// materializes "on top of" where the host is.
    /// Per-frame movement is NOT done from local data — it will only move
    /// once the partner's remote state begins arriving.
    /// </summary>
    static void PositionGhostAtLocalOnce()
    {
        if (_ghostTransform == null) return;

        try
        {
            var typed = (_ghostTransform as IObject).As<via.Transform>();
            if (typed == null) return;

            var pos = typed.Position;
            var rot = typed.Rotation;

            pos.x = CoopState.LocalPlayer.PosX;
            pos.y = CoopState.LocalPlayer.PosY;
            pos.z = CoopState.LocalPlayer.PosZ;

            rot.x = CoopState.LocalPlayer.RotX;
            rot.y = CoopState.LocalPlayer.RotY;
            rot.z = CoopState.LocalPlayer.RotZ;
            rot.w = CoopState.LocalPlayer.RotW;

            typed.Position = pos;
            typed.Rotation = rot;
        }
        catch (Exception e)
        {
            API.LogError("[COOP-Ghost] PositionGhostAtLocalOnce: " + e.Message);
        }
    }

    /// <summary>
    /// Update ghost position/rotation ONLY from remote player data.
    /// If no partner is connected / no remote data has arrived yet, we leave the
    /// ghost exactly where PositionGhostAtLocalOnce put it (the host's spawn spot).
    /// </summary>
    static void UpdateGhost3DTransform()
    {
        if (_ghostTransform == null) return;

        bool hasRemote = (CoopState.Status == ConnectionStatus.Connected)
                         && CoopState.RemoteBufferIndex >= 1;

        // No remote data yet → leave the ghost frozen at its spawn position.
        if (!hasRemote) return;

        try
        {
            var typed = (_ghostTransform as IObject).As<via.Transform>();
            if (typed == null) return;

            var pos = typed.Position;
            var rot = typed.Rotation;

            pos.x = CoopState.RemotePlayer.PosX;
            pos.y = CoopState.RemotePlayer.PosY;
            pos.z = CoopState.RemotePlayer.PosZ;

            rot.x = CoopState.RemotePlayer.RotX;
            rot.y = CoopState.RemotePlayer.RotY;
            rot.z = CoopState.RemotePlayer.RotZ;
            rot.w = CoopState.RemotePlayer.RotW;

            typed.Position = pos;
            typed.Rotation = rot;
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-Ghost] 3D transform update: " + e.Message);
        }
    }


    static void DestroyGhost3D()
    {
        if (_ghostGameObject != null)
        {
            try { _ghostGameObject.Call("destroy", _ghostGameObject); }
            catch
            {
                // Fallback: just deactivate
                try { _ghostGameObject.Call("set_Active", false); } catch { }
            }
        }
        _ghostGameObject = null;
        _ghostTransform = null;
        _ghost3DActive = false;
        _spawnAttempts = 0;
        _spawnRetryFrames = 0;
    }

    // =====================================================================
    //  ImGui INDICATOR — always drawn (even if 3D ghost isn't available)
    // =====================================================================

    [Callback(typeof(ImGuiRender), CallbackType.Pre)]
    public static void RenderGhost()
    {
        try
        {
            if (!CoopState.IsCoopActive) return;
            if (CoopState.Status != ConnectionStatus.Connected) return;
            if (CoopState.RemoteBufferIndex < 1) return;

            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            _screenWidth = viewport.Size.X;
            _screenHeight = viewport.Size.Y;

            Vector2 screenPos;
            bool inFront;
            bool projected = WorldToScreen(
                CoopState.RemotePlayer.PosX,
                CoopState.RemotePlayer.PosY,
                CoopState.RemotePlayer.PosZ,
                out screenPos, out inFront);

            if (!projected || !inFront) return;

            float margin = 30f;
            screenPos.X = Math.Max(margin, Math.Min(_screenWidth - margin, screenPos.X));
            screenPos.Y = Math.Max(margin, Math.Min(_screenHeight - margin, screenPos.Y));

            ImDrawListPtr drawList = ImGui.GetBackgroundDrawList();

            float dx = CoopState.RemotePlayer.PosX - CoopState.LocalPlayer.PosX;
            float dy = CoopState.RemotePlayer.PosY - CoopState.LocalPlayer.PosY;
            float dz = CoopState.RemotePlayer.PosZ - CoopState.LocalPlayer.PosZ;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // If 3D ghost is active, draw a smaller indicator
            float baseRadius = _ghost3DActive ? 8f : 20f;
            float radius = baseRadius * Math.Max(0.5f, Math.Min(2.0f, 10.0f / (dist + 1f)));

            float hp = CoopState.RemotePlayer.MaxHealth > 0
                ? CoopState.RemotePlayer.Health / CoopState.RemotePlayer.MaxHealth
                : 1f;
            hp = Math.Max(0f, Math.Min(1f, hp));

            uint circleColor;
            if (hp > 0.5f)
                circleColor = ImGuiColor(0.2f, 0.9f, 0.3f, 0.8f);
            else if (hp > 0.25f)
                circleColor = ImGuiColor(0.9f, 0.9f, 0.2f, 0.8f);
            else
                circleColor = ImGuiColor(0.9f, 0.2f, 0.2f, 0.8f);

            drawList.AddCircle(screenPos, radius + 2, ImGuiColor(0f, 0f, 0f, 0.6f), 24, 3.0f);
            drawList.AddCircleFilled(screenPos, radius, circleColor, 24);
            drawList.AddCircleFilled(screenPos, radius * 0.4f, ImGuiColor(1f, 1f, 1f, 0.5f), 12);

            // Name and distance
            string nameText = CoopState.PartnerName;
            if (string.IsNullOrEmpty(nameText)) nameText = "Partner";
            Vector2 textPos = new Vector2(screenPos.X - nameText.Length * 3.5f, screenPos.Y - radius - 18);
            drawList.AddText(new Vector2(textPos.X + 1, textPos.Y + 1), ImGuiColor(0, 0, 0, 0.8f), nameText);
            drawList.AddText(textPos, ImGuiColor(1f, 1f, 1f, 0.95f), nameText);

            // Health bar
            float barW = radius * 3f;
            float barH = 4f;
            Vector2 barStart = new Vector2(screenPos.X - barW / 2, screenPos.Y + radius + 6);
            Vector2 barEnd = new Vector2(barStart.X + barW, barStart.Y + barH);
            Vector2 barFill = new Vector2(barStart.X + barW * hp, barStart.Y + barH);

            drawList.AddRectFilled(barStart, barEnd, ImGuiColor(0.1f, 0.1f, 0.1f, 0.7f), 2f, ImDrawFlags.None);
            drawList.AddRectFilled(barStart, barFill, circleColor, 2f, ImDrawFlags.None);
            drawList.AddRect(barStart, barEnd, ImGuiColor(0f, 0f, 0f, 0.5f), 2f, ImDrawFlags.None, 1f);

            // Distance
            string distText = ((int)dist) + "m";
            Vector2 distPos = new Vector2(screenPos.X - distText.Length * 3f, screenPos.Y + radius + 14);
            drawList.AddText(new Vector2(distPos.X + 1, distPos.Y + 1), ImGuiColor(0, 0, 0, 0.6f), distText);
            drawList.AddText(distPos, ImGuiColor(0.7f, 0.7f, 0.7f, 0.8f), distText);

            // Direction arrow when far
            if (dist > 30f)
            {
                float angle = (float)Math.Atan2(screenPos.Y - _screenHeight / 2, screenPos.X - _screenWidth / 2);
                float arrowLen = 12f;
                Vector2 arrowTip = new Vector2(
                    screenPos.X + (float)Math.Cos(angle) * (radius + 8),
                    screenPos.Y + (float)Math.Sin(angle) * (radius + 8));
                Vector2 arrowL = new Vector2(
                    arrowTip.X - (float)Math.Cos(angle + 2.5f) * arrowLen,
                    arrowTip.Y - (float)Math.Sin(angle + 2.5f) * arrowLen);
                Vector2 arrowR = new Vector2(
                    arrowTip.X - (float)Math.Cos(angle - 2.5f) * arrowLen,
                    arrowTip.Y - (float)Math.Sin(angle - 2.5f) * arrowLen);
                drawList.AddTriangleFilled(arrowTip, arrowL, arrowR, ImGuiColor(1f, 1f, 1f, 0.4f));
            }

            // Status indicator (if 3D ghost is active)
            if (_ghost3DActive)
            {
                drawList.AddText(
                    new Vector2(screenPos.X - 10, screenPos.Y + radius + 28),
                    ImGuiColor(0.3f, 0.7f, 1.0f, 0.5f), "3D");
            }
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-Ghost] Render error: " + e.Message);
        }
    }

    // =====================================================================
    //  CAMERA & PROJECTION
    // =====================================================================

    static void CacheCameraData()
    {
        _matricesValid = false;

        try
        {
            dynamic mainView = null;
            try { mainView = via.SceneManager.MainView; } catch { return; }
            if (mainView == null) return;

            dynamic primaryCamera = null;
            try { primaryCamera = mainView.PrimaryCamera; } catch { return; }
            if (primaryCamera == null) return;

            dynamic camGO = null;
            try { camGO = primaryCamera.GameObject; } catch { return; }
            if (camGO == null) return;

            dynamic camTransform = null;
            try { camTransform = (camGO as ManagedObject).Call("get_Transform"); } catch { return; }
            if (camTransform == null) return;

            try
            {
                var typedTransform = ((camTransform as ManagedObject) as IObject).As<via.Transform>();
                if (typedTransform == null) return;

                var camPos = typedTransform.Position;
                var camRot = typedTransform.Rotation;

                float qx = camRot.x, qy = camRot.y, qz = camRot.z, qw = camRot.w;

                // Forward
                float fwdX = 2 * (qx * qz + qw * qy);
                float fwdY = 2 * (qy * qz - qw * qx);
                float fwdZ = 1 - 2 * (qx * qx + qy * qy);

                // Right
                float rightX = 1 - 2 * (qy * qy + qz * qz);
                float rightY = 2 * (qx * qy + qw * qz);
                float rightZ = 2 * (qx * qz - qw * qy);

                // Up
                float upX = 2 * (qx * qy - qw * qz);
                float upY = 1 - 2 * (qx * qx + qz * qz);
                float upZ = 2 * (qy * qz + qw * qx);

                _viewMatrix[0] = rightX; _viewMatrix[1] = rightY; _viewMatrix[2] = rightZ;
                _viewMatrix[3] = upX;    _viewMatrix[4] = upY;    _viewMatrix[5] = upZ;
                _viewMatrix[6] = fwdX;   _viewMatrix[7] = fwdY;   _viewMatrix[8] = fwdZ;
                _viewMatrix[9] = camPos.x; _viewMatrix[10] = camPos.y; _viewMatrix[11] = camPos.z;

                float fov = 90f;
                try
                {
                    dynamic camMO = primaryCamera as ManagedObject;
                    if (camMO != null) fov = (float)camMO.Call("get_FOV");
                }
                catch { }
                _viewMatrix[12] = fov;

                _matricesValid = true;
            }
            catch { }
        }
        catch { }
    }

    static bool WorldToScreen(float wx, float wy, float wz, out Vector2 screen, out bool inFront)
    {
        screen = new Vector2(0, 0);
        inFront = false;
        if (!_matricesValid) return false;

        float camX = _viewMatrix[9], camY = _viewMatrix[10], camZ = _viewMatrix[11];
        float rightX = _viewMatrix[0], rightY = _viewMatrix[1], rightZ = _viewMatrix[2];
        float upX = _viewMatrix[3], upY = _viewMatrix[4], upZ = _viewMatrix[5];
        float fwdX = _viewMatrix[6], fwdY = _viewMatrix[7], fwdZ = _viewMatrix[8];
        float fov = _viewMatrix[12];

        float dx = wx - camX;
        float dy = wy - camY;
        float dz = wz - camZ;

        float dotFwd = dx * fwdX + dy * fwdY + dz * fwdZ;
        float dotRight = dx * rightX + dy * rightY + dz * rightZ;
        float dotUp = dx * upX + dy * upY + dz * upZ;

        inFront = dotFwd > 0.1f;
        if (!inFront) return false;

        float fovRad = fov * 3.14159f / 180f;
        float tanHalfFov = (float)Math.Tan(fovRad * 0.5f);
        float aspect = _screenWidth / _screenHeight;

        float ndcX = dotRight / (dotFwd * tanHalfFov * aspect);
        float ndcY = -dotUp / (dotFwd * tanHalfFov);

        screen.X = (ndcX * 0.5f + 0.5f) * _screenWidth;
        screen.Y = (ndcY * 0.5f + 0.5f) * _screenHeight;

        return true;
    }

    static uint ImGuiColor(float r, float g, float b, float a)
    {
        byte rb = (byte)(Math.Max(0f, Math.Min(1f, r)) * 255);
        byte gb = (byte)(Math.Max(0f, Math.Min(1f, g)) * 255);
        byte bb = (byte)(Math.Max(0f, Math.Min(1f, b)) * 255);
        byte ab = (byte)(Math.Max(0f, Math.Min(1f, a)) * 255);
        return (uint)((ab << 24) | (bb << 16) | (gb << 8) | rb);
    }
}
