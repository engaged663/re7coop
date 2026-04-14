// ResidentCOOP_UI.cs
// ImGui-based UI: main menu (host/join), settings, and in-game HUD overlay.
// Uses Hexa.NET.ImGui which is available in REFramework's C# environment.

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

    // Difficulty selection
    static int _selectedDifficulty = 1; // 0=Easy, 1=Normal, 2=Hard
    static string[] _difficultyNames = new string[] { "Easy", "Normal", "Hard" };

    // Save slot selection
    static int _selectedSaveSlot = 0;

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
        ImGui.SetNextWindowSize(new Vector2(380, 300), ImGuiCond.FirstUseEver);
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
            case UIScreen.HostNewOrLoad:
                RenderHostNewOrLoad();
                break;
            case UIScreen.HostNewGame:
                RenderHostNewGame();
                break;
            case UIScreen.HostLoadGame:
                RenderHostLoadGame();
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

        if (ImGui.Button("Host Game", new Vector2(160, 36)))
        {
            CoopState.CurrentScreen = UIScreen.HostSetup;
        }

        ImGui.SameLine();

        if (ImGui.Button("Join Game", new Vector2(160, 36)))
        {
            CoopState.CurrentScreen = UIScreen.JoinSetup;
        }

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

        ImGui.Text("Your partner needs your IP address to connect.");

        ImGui.Text("Port:");
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("##hostport", ref _portBuffer, 8);
        int port;
        if (int.TryParse(_portBuffer, out port) && port > 0 && port < 65536)
        {
            CoopState.Port = port;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Three options: New Game, Load Save, Continue Current
        if (ImGui.Button("New Game", new Vector2(330, 36)))
        {
            CoopState.PlayerName = _nameBuffer;
            CoopState.CurrentScreen = UIScreen.HostNewGame;
            CoopState.ErrorMessage = "";
        }

        ImGui.Spacing();

        if (ImGui.Button("Load Save", new Vector2(330, 36)))
        {
            CoopState.PlayerName = _nameBuffer;
            CoopState.SaveSlotsScanned = false; // force re-scan
            CoopState.CurrentScreen = UIScreen.HostLoadGame;
            CoopState.ErrorMessage = "";
        }

        ImGui.Spacing();

        if (ImGui.Button("Continue Current Game", new Vector2(330, 36)))
        {
            CoopState.PlayerName = _nameBuffer;
            CoopState.StartMode = SessionStartMode.ContinueCurrentGame;
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

    static void RenderHostNewOrLoad()
    {
        ImGui.Text("Choose Game Mode");
        ImGui.Separator();

        if (ImGui.Button("New Game", new Vector2(330, 40)))
        {
            CoopState.CurrentScreen = UIScreen.HostNewGame;
        }

        ImGui.Spacing();

        if (ImGui.Button("Load Save", new Vector2(330, 40)))
        {
            CoopState.SaveSlotsScanned = false;
            CoopState.CurrentScreen = UIScreen.HostLoadGame;
        }

        ImGui.Spacing();

        if (ImGui.Button("Back", new Vector2(100, 32)))
        {
            CoopState.CurrentScreen = UIScreen.HostSetup;
        }
    }

    static void RenderHostNewGame()
    {
        ImGui.Text("Start New Co-Op Game");
        ImGui.Separator();

        ImGui.Spacing();

        // Difficulty selection
        ImGui.Text("Difficulty:");
        for (int i = 0; i < _difficultyNames.Length; i++)
        {
            bool selected = (_selectedDifficulty == i);
            if (ImGui.RadioButton(_difficultyNames[i], selected))
            {
                _selectedDifficulty = i;
            }
            if (i < _difficultyNames.Length - 1)
                ImGui.SameLine();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
            "Both players should be at the Title Screen.");
        ImGui.TextWrapped("The host will start hosting and trigger a new game. " +
            "The client should join and also start a new game from their title screen. " +
            "Once both are in-game, synchronization begins automatically.");

        ImGui.Spacing();

        if (ImGui.Button("Start Hosting + New Game", new Vector2(330, 40)))
        {
            CoopState.StartMode = SessionStartMode.NewGame;
            CoopState.Difficulty = (GameDifficulty)_selectedDifficulty;
            StartHostAndSession();
        }

        ImGui.Spacing();

        if (ImGui.Button("Back", new Vector2(100, 32)))
        {
            CoopState.CurrentScreen = UIScreen.HostSetup;
        }
    }

    static void RenderHostLoadGame()
    {
        ImGui.Text("Load Save for Co-Op");
        ImGui.Separator();

        // Scanning state
        if (!CoopState.SaveSlotsScanned)
        {
            int dots = (int)((CoopState.FrameCount / 20) % 4);
            ImGui.Text("Scanning save slots" + new string('.', dots));
            return;
        }

        ImGui.Spacing();

        if (CoopState.SaveSlotCount == 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), "No save files found!");
            ImGui.TextWrapped("Start a New Game instead, or make sure you have save files from a previous playthrough.");

            ImGui.Spacing();

            if (ImGui.Button("Start New Game Instead", new Vector2(260, 36)))
            {
                CoopState.CurrentScreen = UIScreen.HostNewGame;
            }
        }
        else
        {
            ImGui.Text("Select a save slot to load:");
            ImGui.Spacing();

            // List available save slots
            for (int i = 0; i < 21; i++)
            {
                if (!CoopState.SaveSlotExists[i]) continue;

                string label = CoopState.SaveSlotNames[i] ?? ("Slot " + (i + 1));
                bool selected = (_selectedSaveSlot == i);

                if (ImGui.Selectable(label, selected))
                {
                    _selectedSaveSlot = i;
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                "Both players should load the same save point.");
            ImGui.TextWrapped("The host will start hosting and load this save. " +
                "The client should join and also load the same save from their title screen. " +
                "Synchronization begins once both are in-game.");

            ImGui.Spacing();

            if (CoopState.SaveSlotCount > 0)
            {
                if (ImGui.Button("Start Hosting + Load Save", new Vector2(330, 40)))
                {
                    CoopState.StartMode = SessionStartMode.LoadSave;
                    CoopState.SaveSlotToLoad = _selectedSaveSlot;
                    StartHostAndSession();
                }
            }
        }

        ImGui.Spacing();

        if (ImGui.Button("Back", new Vector2(100, 32)))
        {
            CoopState.CurrentScreen = UIScreen.HostSetup;
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
                // Signal the GameSession plugin to act
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
        ImGui.Text("Hosting - Waiting for player...");
        ImGui.Text("Port: " + CoopState.Port);

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

        // Session info
        if (CoopState.StartMode != SessionStartMode.None)
        {
            string modeStr = "Unknown";
            if (CoopState.StartMode == SessionStartMode.NewGame) modeStr = "New Game";
            else if (CoopState.StartMode == SessionStartMode.LoadSave) modeStr = "Loaded Save";
            else if (CoopState.StartMode == SessionStartMode.ContinueCurrentGame) modeStr = "Continue";
            ImGui.Text("Session: " + modeStr);
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
