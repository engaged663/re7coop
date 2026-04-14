// ResidentCOOP_Ghost.cs
// Renders a screen-space indicator for the remote player.
// Uses ImGui's background draw list to overlay a marker at the projected world position.
// This is the Phase 1 approach - simple but guaranteed to work without game object management.

using System;
using System.Numerics;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_Ghost
{
    // Camera matrices cached per frame
    static float[] _viewMatrix = new float[16];
    static float[] _projMatrix = new float[16];
    static bool _matricesValid = false;
    static float _screenWidth = 1920f;
    static float _screenHeight = 1080f;

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[COOP] ResidentCOOP_Ghost loaded.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP] ResidentCOOP_Ghost unloaded.");
    }

    /// <summary>
    /// Called every frame before behavior update - cache camera matrices.
    /// </summary>
    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnPostUpdate()
    {
        try
        {
            if (!CoopState.IsCoopActive) { _matricesValid = false; return; }
            CacheCameraData();
        }
        catch
        {
            _matricesValid = false;
        }
    }

    /// <summary>
    /// Render the ghost indicator using ImGui's draw list.
    /// </summary>
    [Callback(typeof(ImGuiRender), CallbackType.Pre)]
    public static void RenderGhost()
    {
        try
        {
            if (!CoopState.IsCoopActive) return;
            if (CoopState.RemoteBufferIndex < 1) return; // No data yet

            // Get screen size from ImGui viewport
            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            _screenWidth = viewport.Size.X;
            _screenHeight = viewport.Size.Y;

            // Project remote player world position to screen
            Vector2 screenPos;
            bool inFront;
            bool projected = WorldToScreen(
                CoopState.RemotePlayer.PosX,
                CoopState.RemotePlayer.PosY,
                CoopState.RemotePlayer.PosZ,
                out screenPos, out inFront);

            if (!projected || !inFront) return;

            // Clamp to screen with margin
            float margin = 30f;
            screenPos.X = Math.Max(margin, Math.Min(_screenWidth - margin, screenPos.X));
            screenPos.Y = Math.Max(margin, Math.Min(_screenHeight - margin, screenPos.Y));

            ImDrawListPtr drawList = ImGui.GetBackgroundDrawList();

            // Calculate distance for size scaling
            float dx = CoopState.RemotePlayer.PosX - CoopState.LocalPlayer.PosX;
            float dy = CoopState.RemotePlayer.PosY - CoopState.LocalPlayer.PosY;
            float dz = CoopState.RemotePlayer.PosZ - CoopState.LocalPlayer.PosZ;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Scale indicator size based on distance (bigger when close)
            float baseRadius = 20f;
            float radius = baseRadius * Math.Max(0.5f, Math.Min(2.0f, 10.0f / (dist + 1f)));

            // Color based on health
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

            // Draw outer circle (border)
            drawList.AddCircle(screenPos, radius + 2, ImGuiColor(0f, 0f, 0f, 0.6f), 24, 3.0f);

            // Draw filled circle
            drawList.AddCircleFilled(screenPos, radius, circleColor, 24);

            // Draw inner circle highlight
            drawList.AddCircleFilled(screenPos, radius * 0.4f, ImGuiColor(1f, 1f, 1f, 0.5f), 12);

            // Draw name above
            string nameText = CoopState.PartnerName;
            if (string.IsNullOrEmpty(nameText)) nameText = "Partner";
            Vector2 textPos = new Vector2(screenPos.X - nameText.Length * 3.5f, screenPos.Y - radius - 18);
            // Text shadow
            drawList.AddText(new Vector2(textPos.X + 1, textPos.Y + 1), ImGuiColor(0, 0, 0, 0.8f), nameText);
            // Text
            drawList.AddText(textPos, ImGuiColor(1f, 1f, 1f, 0.95f), nameText);

            // Draw mini health bar below
            float barW = radius * 3f;
            float barH = 4f;
            Vector2 barStart = new Vector2(screenPos.X - barW / 2, screenPos.Y + radius + 6);
            Vector2 barEnd = new Vector2(barStart.X + barW, barStart.Y + barH);
            Vector2 barFill = new Vector2(barStart.X + barW * hp, barStart.Y + barH);

            drawList.AddRectFilled(barStart, barEnd, ImGuiColor(0.1f, 0.1f, 0.1f, 0.7f), 2f, ImDrawFlags.None);
            drawList.AddRectFilled(barStart, barFill, circleColor, 2f, ImDrawFlags.None);
            drawList.AddRect(barStart, barEnd, ImGuiColor(0f, 0f, 0f, 0.5f), 2f, ImDrawFlags.None, 1f);

            // Draw distance text
            string distText = ((int)dist) + "m";
            Vector2 distPos = new Vector2(screenPos.X - distText.Length * 3f, screenPos.Y + radius + 14);
            drawList.AddText(new Vector2(distPos.X + 1, distPos.Y + 1), ImGuiColor(0, 0, 0, 0.6f), distText);
            drawList.AddText(distPos, ImGuiColor(0.7f, 0.7f, 0.7f, 0.8f), distText);

            // Draw direction arrow when partner is off-screen or far
            if (dist > 30f)
            {
                // Arrow pointing toward partner
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
            // Get the main camera through via.SceneManager
            dynamic mainView = null;
            try { mainView = via.SceneManager.MainView; } catch { return; }
            if (mainView == null) return;

            dynamic primaryCamera = null;
            try { primaryCamera = mainView.PrimaryCamera; } catch { return; }
            if (primaryCamera == null) return;

            // Get camera's GameObject and Transform for view matrix calculation
            dynamic camGO = null;
            try { camGO = primaryCamera.GameObject; } catch { return; }
            if (camGO == null) return;

            dynamic camTransform = null;
            try { camTransform = (camGO as ManagedObject).Call("get_Transform"); } catch { return; }
            if (camTransform == null) return;

            // Read camera position and build a simple view matrix
            try
            {
                var typedTransform = ((camTransform as ManagedObject) as IObject).As<via.Transform>();
                if (typedTransform == null) return;

                var camPos = typedTransform.Position;
                var camRot = typedTransform.Rotation;

                // Store camera data for world-to-screen projection
                // We'll use a simplified projection based on camera forward/right/up vectors
                // derived from the quaternion
                float qx = camRot.x, qy = camRot.y, qz = camRot.z, qw = camRot.w;

                // Build rotation matrix columns from quaternion
                // Forward (Z axis in RE Engine is typically forward)
                float fwdX = 2 * (qx * qz + qw * qy);
                float fwdY = 2 * (qy * qz - qw * qx);
                float fwdZ = 1 - 2 * (qx * qx + qy * qy);

                // Right (X axis)
                float rightX = 1 - 2 * (qy * qy + qz * qz);
                float rightY = 2 * (qx * qy + qw * qz);
                float rightZ = 2 * (qx * qz - qw * qy);

                // Up (Y axis)
                float upX = 2 * (qx * qy - qw * qz);
                float upY = 1 - 2 * (qx * qx + qz * qz);
                float upZ = 2 * (qy * qz + qw * qx);

                // Store as a view-like transform
                // _viewMatrix stores: [rightX, rightY, rightZ, upX, upY, upZ, fwdX, fwdY, fwdZ, camPosX, camPosY, camPosZ, fov]
                _viewMatrix[0] = rightX; _viewMatrix[1] = rightY; _viewMatrix[2] = rightZ;
                _viewMatrix[3] = upX;    _viewMatrix[4] = upY;    _viewMatrix[5] = upZ;
                _viewMatrix[6] = fwdX;   _viewMatrix[7] = fwdY;   _viewMatrix[8] = fwdZ;
                _viewMatrix[9] = camPos.x; _viewMatrix[10] = camPos.y; _viewMatrix[11] = camPos.z;

                // Get FOV
                float fov = 90f;
                try
                {
                    dynamic camMO = primaryCamera as ManagedObject;
                    if (camMO != null)
                    {
                        fov = (float)camMO.Call("get_FOV");
                    }
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

        // Camera parameters
        float camX = _viewMatrix[9], camY = _viewMatrix[10], camZ = _viewMatrix[11];
        float rightX = _viewMatrix[0], rightY = _viewMatrix[1], rightZ = _viewMatrix[2];
        float upX = _viewMatrix[3], upY = _viewMatrix[4], upZ = _viewMatrix[5];
        float fwdX = _viewMatrix[6], fwdY = _viewMatrix[7], fwdZ = _viewMatrix[8];
        float fov = _viewMatrix[12];

        // Direction from camera to point
        float dx = wx - camX;
        float dy = wy - camY;
        float dz = wz - camZ;

        // Project onto camera axes
        float dotFwd = dx * fwdX + dy * fwdY + dz * fwdZ;
        float dotRight = dx * rightX + dy * rightY + dz * rightZ;
        float dotUp = dx * upX + dy * upY + dz * upZ;

        inFront = dotFwd > 0.1f;
        if (!inFront) return false;

        // Perspective divide
        float fovRad = fov * 3.14159f / 180f;
        float tanHalfFov = (float)Math.Tan(fovRad * 0.5f);
        float aspect = _screenWidth / _screenHeight;

        float ndcX = dotRight / (dotFwd * tanHalfFov * aspect);
        float ndcY = -dotUp / (dotFwd * tanHalfFov);

        // NDC [-1, 1] to screen pixels
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
