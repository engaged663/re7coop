// ResidentCOOP_UI.cs
// ImGui-based UI: main menu (host/join), settings, and in-game HUD overlay.
// Uses Hexa.NET.ImGui which is available in REFramework's C# environment.
//
// NEW simplified flow:
//   - "Host Game" -> simple setup screen with ONE button: "Start Hosting".
//     No new-game/load-save branches here. The player uses RE7's own title
//     menu to click New Game / Continue after hosting starts.
//   - "Join Game" -> IP + port + connect.
//   - Once connected, if the host reports a different chapter than us, we
//     show a warning and a "Force Sync to Host" button that performs a
//     chapter jump locally.

using System;
using System.Numerics;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

using ResidentCOOP.Shared;

public class ResidentCOOP_UI
{
    // Input buffers for ImGui text fields
    static string _ipBuffer = "127.0.0.1";
    static string _portBuffer = "27015";
    static string _nameBuffer = "Player";

    // Chapter selection - values from app.GameManager.ChapterNo enum (il2cpp dump)
    static int _selectedChapterIdx = 0;
    static readonly string[] ChapterNames = new string[]
    {
        "Ch.0 - Guest House",         // ChapterNo.Chapter0   = 2
        "Ch.1 - Main House 1F",       // ChapterNo.Chapter1   = 4
        "Ch.3 - Old House",           // ChapterNo.Chapter3   = 5
        "Ch.4 - Main House 2F",       // ChapterNo.Chapter4   = 6
        "Ch.1-2-3 - Revisit",         // ChapterNo.Chapter123 = 13
        "Ch.3-2-4 - Testing Area",    // ChapterNo.Chapter324 = 14
    };
    static readonly int[] ChapterValues = new int[]
    {
        2,   // Chapter0
        4,   // Chapter1
        5,   // Chapter3
        6,   // Chapter4
        13,  // Chapter123
        14,  // Chapter324
    };

    // Window toggle
    static bool _showMainWindow = true;

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[COOP] ResidentCOOP_UI loaded.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        API.LogInfo("[COOP] ResidentCOOP_UI unloaded.");
    }

    [Callback(typeof(ImGuiRender), CallbackType.Pre)]
    public static void RenderUI()
    {
        try
        {
            RenderMainWindow();

            if (CoopState.IsCoopActive)
            {
                RenderHUD();
            }
        }
        catch (Exception e)
        {
            if (CoopState.FrameCount % 300 == 0)
                API.LogError("[COOP-UI] Render error: " + e.Message);
        }
    }

    // =====================================================================
    //  MAIN WINDOW
    // =====================================================================

    static void RenderMainWindow()
    {
        // Toggle with a key or always show
        ImGui.SetNextWindowSize(new Vector2(400, 320), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(20, 20), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("RE7 Co-Op Mod", ref _showMainWindow, ImGuiWindowFlags.None))
        {
            ImGui.End();
            return;
        }

        // Header
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1.0f), "Resident Evil 7 - Co-Op");
        ImGui.Separator();

        // Error message display
        if (!string.IsNullOrEmpty(CoopState.ErrorMessage))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), CoopState.ErrorMessage);
            ImGui.Separator();
        }

        // Status message
        if (!string.IsNullOrEmpty(CoopState.StatusMessage))
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), CoopState.StatusMessage);
            ImGui.Separator();
        }

        switch (CoopState.CurrentScreen)
        {
            case UIScreen.MainMenu:
                RenderMainMenu();
                break;
            case UIScreen.HostSetup:
                RenderHostSetup();
                break;
            // Deprecated screens — if CurrentScreen ever lands here, fall back to Hosting or HostSetup.
            case UIScreen.HostNewOrLoad:
            case UIScreen.HostNewGame:
            case UIScreen.HostLoadGame:
                CoopState.CurrentScreen = UIScreen.HostSetup;
                RenderHostSetup();
                break;
            case UIScreen.Hosting:
                RenderHosting();
                break;
            case UIScreen.JoinSetup:
                RenderJoinSetup();
                break;
            case UIScreen.Connecting:
                RenderConnecting();
                break;
            case UIScreen.Connected:
                RenderConnected();
                break;
        }

        ImGui.End();
    }

    static void RenderMainMenu()
    {
        ImGui.Text("Player Name:");
        ImGui.InputText("##name", ref _nameBuffer, 32);
        CoopState.PlayerName = _nameBuffer;

        ImGui.Spacing();

        if (ImGui.Button("Host Game", new Vector2(170, 36)))
        {
            CoopState.CurrentScreen = UIScreen.HostSetup;
        }

        ImGui.SameLine();

        if (ImGui.Button("Join Game", new Vector2(170, 36)))
        {
            CoopState.CurrentScreen = UIScreen.JoinSetup;
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextWrapped(
            "How it works: both players should run their own RE7. One hosts, "
            + "the other joins. After hosting/connecting, click 'New Game' or "
            + "'Continue' on RE7's title screen as usual — co-op activates "
            + "automatically once you're in-game.");

        ImGui.Spacing();
        ImGui.Separator();

        // Settings
        if (ImGui.CollapsingHeader("Settings"))
        {
            ImGui.Checkbox("Skip Cutscenes", ref CoopState.SkipCutscenes);
            ImGui.Text("Port:");
            ImGui.InputText("##port", ref _portBuffer, 8);

            int port;
            if (int.TryParse(_portBuffer, out port) && port > 0 && port < 65536)
            {
                CoopState.Port = port;
            }
        }
    }

    static void RenderHostSetup()
    {
        ImGui.Text("Host a Co-Op Game");
        ImGui.Separator();

        ImGui.TextWrapped("Your partner will need your IP address and port to connect.");

        ImGui.Spacing();

        ImGui.Text("Port:");
        ImGui.SetNextItemWidth(140);
        ImGui.InputText("##hostport", ref _portBuffer, 8);
        int port;
        if (int.TryParse(_portBuffer, out port) && port > 0 && port < 65536)
        {
            CoopState.Port = port;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
            "After hosting, use RE7's own title screen to start the game.");
        ImGui.TextWrapped(
            "Click 'New Game' or 'Continue' on the RE7 title menu. Co-op will "
            + "activate as soon as Ethan spawns. If you are already in-game, "
            + "co-op activates immediately when the partner connects.");

        ImGui.Spacing();

        if (ImGui.Button("Start Hosting", new Vector2(350, 40)))
        {
            CoopState.PlayerName = _nameBuffer;
            CoopState.StartMode = SessionStartMode.None;
            StartHostAndSession();
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Back", new Vector2(100, 32)))
        {
            CoopState.CurrentScreen = UIScreen.MainMenu;
            CoopState.ErrorMessage = "";
        }
    }

    /// <summary>
    /// Common function: start the network host AND trigger the session start.
    /// </summary>
    static void StartHostAndSession()
    {
        try
        {
            Type coreType = FindCoreType();
            if (coreType != null)
            {
                coreType.GetMethod("StartHost").Invoke(null, null);
                // Signal the GameSession plugin to act (MarkSessionStarted -> no-op for in-game,
                // or sets up waiting for player-spawn at title).
                CoopState.SessionStartRequested = true;
            }
            else
            {
                CoopState.ErrorMessage = "Core plugin not loaded!";
            }
        }
        catch (Exception e)
        {
            CoopState.ErrorMessage = "Start host failed: " + e.Message;
        }
    }


    static void RenderHosting()
    {
        // Contextual header: are we in-game or waiting?
        bool inGame = IsPlayerInGameSafe();

        if (inGame)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
                "Hosting - Co-op active in-game.");
            ImGui.Text("Waiting for partner on port " + CoopState.Port + "...");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
                "Hosting on port " + CoopState.Port + ".");
            ImGui.TextWrapped(
                "Click 'New Game' or 'Continue' on the RE7 title screen. "
                + "Co-op will activate automatically when Ethan spawns.");
        }

        ImGui.Spacing();

        // Animated dots
        int dots = (int)((CoopState.FrameCount / 30) % 4);
        string dotStr = new string('.', dots);
        ImGui.Text("Listening" + dotStr);

        ImGui.Spacing();

        if (ImGui.Button("Cancel", new Vector2(120, 32)))
        {
            CallCoreDisconnect();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ===== CHAPTER SELECTOR (in-game utility) =====
        ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), "Skip to Chapter (in-game)");
        ImGui.TextWrapped("Only useful once you're in-game: jumps to a different chapter.");
        ImGui.Spacing();

        for (int i = 0; i < ChapterNames.Length; i++)
        {
            bool selected = (_selectedChapterIdx == i);
            if (ImGui.Selectable(ChapterNames[i], selected))
            {
                _selectedChapterIdx = i;
            }
        }

        ImGui.Spacing();

        if (ImGui.Button("Jump to Selected Chapter", new Vector2(-1, 35)))
        {
            JumpToChapter(ChapterValues[_selectedChapterIdx]);
        }
    }

    static void JumpToChapter(int chapterNo)
    {
        try
        {
            // Direct approach: call GameManager singleton
            dynamic gm = API.GetManagedSingleton("app.GameManager");
            if (gm != null)
            {
                ManagedObject gmMO = gm as ManagedObject;

                // Set the chapter
                try { gmMO.Call("set_CurrentChapter", chapterNo); }
                catch { try { gmMO.Call("set_CurrentChapter", (long)chapterNo); } catch { } }

                // Get jump key for this chapter
                string jumpKey = null;
                try
                {
                    dynamic key = gmMO.Call("getChapterJumpDataKey", chapterNo);
                    if (key != null) jumpKey = key.ToString();
                }
                catch
                {
                    try
                    {
                        dynamic key = gmMO.Call("getChapterJumpDataKey", (long)chapterNo);
                        if (key != null) jumpKey = key.ToString();
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(jumpKey))
                {
                    API.LogInfo("[COOP-UI] Jumping to chapter " + chapterNo + " with key: " + jumpKey);
                    var jumpKeyManaged = REFrameworkNET.VM.CreateString(jumpKey);
                    var emptyStr = REFrameworkNET.VM.CreateString("");
                    gmMO.Call("chapterJumpRequest", jumpKeyManaged, false, emptyStr);
                    CoopState.StatusMessage = "Jumping to " + ChapterNames[_selectedChapterIdx] + "...";
                    CoopState.StartingItemsGiven = false;
                }
                else
                {
                    // Fallback: use GameFlowFsmManager
                    API.LogInfo("[COOP-UI] No jump key, using requestStartGameFlow(" + chapterNo + ")");
                    dynamic gfm = API.GetManagedSingleton("app.GameFlowFsmManager");
                    if (gfm != null)
                    {
                        (gfm as ManagedObject).Call("requestStartGameFlow", chapterNo);
                    }
                    CoopState.StatusMessage = "Starting chapter " + chapterNo + "...";
                }
            }
            else
            {
                CoopState.StatusMessage = "GameManager not found!";
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP-UI] JumpToChapter failed: " + e.Message);
            CoopState.StatusMessage = "Jump failed: " + e.Message;
        }
    }

    static void RenderJoinSetup()
    {
        ImGui.Text("Join a Co-Op Game");
        ImGui.Separator();

        ImGui.Text("Host IP Address:");
        ImGui.InputText("##ip", ref _ipBuffer, 64);
        CoopState.HostIP = _ipBuffer;

        ImGui.Text("Port:");
        ImGui.InputText("##joinport", ref _portBuffer, 8);
        int port;
        if (int.TryParse(_portBuffer, out port) && port > 0 && port < 65536)
        {
            CoopState.Port = port;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
            "Tip: both players should be on the same chapter.");
        ImGui.TextWrapped(
            "After connecting, click 'New Game' or 'Continue' on the RE7 title "
            + "screen. If the host is already in-game on a different chapter, "
            + "you'll get a warning and a 'Force Sync to Host' button to jump "
            + "to the host's chapter.");

        ImGui.Spacing();

        if (ImGui.Button("Connect", new Vector2(160, 36)))
        {
            CoopState.PlayerName = _nameBuffer;
            try
            {
                Type coreType = FindCoreType();
                if (coreType != null)
                {
                    coreType.GetMethod("StartClient").Invoke(null, null);
                }
                else
                {
                    CoopState.ErrorMessage = "Core plugin not loaded!";
                }
            }
            catch (Exception e)
            {
                CoopState.ErrorMessage = "Connect failed: " + e.Message;
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Back", new Vector2(100, 36)))
        {
            CoopState.CurrentScreen = UIScreen.MainMenu;
            CoopState.ErrorMessage = "";
        }
    }

    static void RenderConnecting()
    {
        ImGui.Text("Connecting to " + CoopState.HostIP + "...");

        int dots = (int)((CoopState.FrameCount / 30) % 4);
        string dotStr = new string('.', dots);
        ImGui.Text("Please wait" + dotStr);

        ImGui.Spacing();

        if (ImGui.Button("Cancel", new Vector2(120, 32)))
        {
            CallCoreDisconnect();
        }
    }

    static void RenderConnected()
    {
        ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Connected!");
        ImGui.Separator();

        ImGui.Text("Partner: " + CoopState.PartnerName);
        ImGui.Text("Role: " + (CoopState.IsHost ? "Host" : "Client"));
        ImGui.Text("Ping: " + CoopState.PingMs + " ms");

        // --- Chapter mismatch warning (client-side) ---
        if (CoopState.IsClient && CoopState.ChapterMismatch)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f),
                "WARNING: chapter mismatch with host!");
            ImGui.TextWrapped(string.Format(
                "Host chapter enum: {0}   |   Your chapter enum: {1}",
                CoopState.HostCurrentChapter, CoopState.LocalCurrentChapter));
            ImGui.TextWrapped(
                "Gameplay will desync until you are on the same chapter. "
                + "Click the button below to jump to the host's chapter (you will "
                + "lose any unsaved progress in your current run).");

            ImGui.Spacing();
            if (ImGui.Button("Force Sync to Host", new Vector2(-1, 36)))
            {
                CoopState.ForceSyncToHostRequested = true;
                CoopState.ChapterMismatch = false; // hide warning until next check
                CoopState.StatusMessage = "Requesting chapter jump to match host...";
            }

            ImGui.Spacing();
            ImGui.Separator();
        }

        ImGui.Spacing();

        // Partner health bar
        float healthPct = CoopState.RemotePlayer.MaxHealth > 0
            ? CoopState.RemotePlayer.Health / CoopState.RemotePlayer.MaxHealth
            : 0f;
        healthPct = Math.Max(0f, Math.Min(1f, healthPct));

        ImGui.Text("Partner Health:");
        Vector4 healthColor;
        if (healthPct > 0.5f)
            healthColor = new Vector4(0.2f, 0.8f, 0.2f, 1.0f);
        else if (healthPct > 0.25f)
            healthColor = new Vector4(0.8f, 0.8f, 0.2f, 1.0f);
        else
            healthColor = new Vector4(0.8f, 0.2f, 0.2f, 1.0f);

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, healthColor);
        ImGui.ProgressBar(healthPct, new Vector2(-1, 20),
            ((int)CoopState.RemotePlayer.Health) + " / " + ((int)CoopState.RemotePlayer.MaxHealth));
        ImGui.PopStyleColor(1);

        ImGui.Spacing();

        // Partner position
        ImGui.Text(string.Format("Partner Pos: ({0:F1}, {1:F1}, {2:F1})",
            CoopState.RemotePlayer.PosX, CoopState.RemotePlayer.PosY, CoopState.RemotePlayer.PosZ));

        // Local position
        ImGui.Text(string.Format("Local Pos: ({0:F1}, {1:F1}, {2:F1})",
            CoopState.LocalPlayer.PosX, CoopState.LocalPlayer.PosY, CoopState.LocalPlayer.PosZ));

        if (CoopState.IsHost)
        {
            ImGui.Text("Enemies tracked: " + CoopState.EnemyCount);
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Disconnect", new Vector2(140, 32)))
        {
            CallCoreDisconnect();
        }
    }

    // =====================================================================
    //  HUD OVERLAY (always visible when connected)
    // =====================================================================

    static void RenderHUD()
    {
        // Transparent overlay window in top-right corner
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        Vector2 vpSize = viewport.Size;

        ImGui.SetNextWindowPos(new Vector2(vpSize.X - 260, 10), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(250, 80), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.4f);

        ImGuiWindowFlags hudFlags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoInputs;

        ImGui.Begin("##CoopHUD", hudFlags);

        // Connection indicator
        Vector4 statusColor;
        string statusText;
        if (CoopState.PingMs < 100)
        {
            statusColor = new Vector4(0.2f, 1.0f, 0.2f, 1.0f);
            statusText = "OK";
        }
        else if (CoopState.PingMs < 250)
        {
            statusColor = new Vector4(1.0f, 1.0f, 0.2f, 1.0f);
            statusText = "~";
        }
        else
        {
            statusColor = new Vector4(1.0f, 0.3f, 0.3f, 1.0f);
            statusText = "!!";
        }

        ImGui.TextColored(statusColor, "[" + statusText + "] " + CoopState.PartnerName);
        ImGui.SameLine();
        ImGui.Text(" " + CoopState.PingMs + "ms");

        // Mini health bar
        float hp = CoopState.RemotePlayer.MaxHealth > 0
            ? CoopState.RemotePlayer.Health / CoopState.RemotePlayer.MaxHealth
            : 0f;
        hp = Math.Max(0f, Math.Min(1f, hp));

        Vector4 hpCol = hp > 0.5f
            ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f)
            : hp > 0.25f
                ? new Vector4(0.8f, 0.8f, 0.2f, 1.0f)
                : new Vector4(0.8f, 0.2f, 0.2f, 1.0f);

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, hpCol);
        ImGui.ProgressBar(hp, new Vector2(-1, 14), "");
        ImGui.PopStyleColor(1);

        // Role indicator
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
            CoopState.IsHost ? "[HOST]" : "[CLIENT]");

        ImGui.End();
    }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    /// <summary>
    /// Safely check if the player is in-game (ObjectManager.PlayerObj != null).
    /// Uses reflection into GameSession for consistency.
    /// </summary>
    static bool IsPlayerInGameSafe()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType("ResidentCOOP_GameSession");
                    if (t != null)
                    {
                        var m = t.GetMethod("IsPlayerInGame",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (m != null)
                        {
                            return (bool)m.Invoke(null, null);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Find the ResidentCOOP_Core type across loaded assemblies via reflection.
    /// Each .cs file is its own assembly, so we need to search.
    /// </summary>
    static Type FindCoreType()
    {
        System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                Type t = assemblies[i].GetType("ResidentCOOP_Core");
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    static void CallCoreDisconnect()
    {
        try
        {
            Type coreType = FindCoreType();
            if (coreType != null)
            {
                coreType.GetMethod("Disconnect").Invoke(null, null);
            }
            else
            {
                CoopState.Reset();
            }
        }
        catch
        {
            CoopState.Reset();
        }
    }
}
