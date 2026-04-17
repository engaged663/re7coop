// ResidentCOOP.Shared - CoopState.cs
// Global shared state accessible by all plugin source files.

using System;

namespace ResidentCOOP.Shared
{
    public enum CoopRole : byte
    {
        None = 0,
        Host = 1,
        Client = 2
    }

    public enum ConnectionStatus : byte
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Hosting = 3
    }

    public enum UIScreen : byte
    {
        MainMenu = 0,
        HostSetup = 1,
        HostNewOrLoad = 2,
        HostNewGame = 3,
        HostLoadGame = 4,
        Hosting = 5,
        JoinSetup = 6,
        Connecting = 7,
        Connected = 8
    }

    /// <summary>
    /// How the host wants to start the game session.
    /// </summary>
    public enum SessionStartMode : byte
    {
        None = 0,
        NewGame = 1,
        LoadSave = 2,
        ContinueCurrentGame = 3
    }

    /// <summary>
    /// Difficulty levels matching RE7's MainFlowManager.Difficulty enum.
    /// </summary>
    public enum GameDifficulty : byte
    {
        Easy = 0,
        Normal = 1,
        Hard = 2
    }

    public class PlayerStateData
    {
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float Health;
        public float MaxHealth;
        public ushort AnimState;
        public int WeaponID;
        public byte Flags;

        public bool IsAiming { get { return (Flags & 1) != 0; } }
        public bool IsCrouching { get { return (Flags & 2) != 0; } }
        public bool IsRunning { get { return (Flags & 4) != 0; } }
        public bool HasFlashlight { get { return (Flags & 8) != 0; } }

        public PlayerStateData() { RotW = 1f; MaxHealth = 100f; Health = 100f; }

        public void CopyFrom(PlayerStateData other)
        {
            PosX = other.PosX; PosY = other.PosY; PosZ = other.PosZ;
            RotX = other.RotX; RotY = other.RotY; RotZ = other.RotZ; RotW = other.RotW;
            Health = other.Health; MaxHealth = other.MaxHealth;
            AnimState = other.AnimState; WeaponID = other.WeaponID; Flags = other.Flags;
        }
    }

    public class EnemyStateData
    {
        public uint EnemyID;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float Health;
        public ushort AnimState;
        public byte Flags;

        public bool IsDead { get { return (Flags & 1) != 0; } }
        public bool IsActive { get { return (Flags & 2) != 0; } }
    }

    public static class CoopState
    {
        public static CoopRole Role = CoopRole.None;
        public static ConnectionStatus Status = ConnectionStatus.Disconnected;
        public static UIScreen CurrentScreen = UIScreen.MainMenu;

        public static string PlayerName = "Player";
        public static string HostIP = "127.0.0.1";
        public static int Port = 27015;
        public static bool SkipCutscenes = true;

        public static string PartnerName = "";
        public static PlayerStateData LocalPlayer = new PlayerStateData();
        public static PlayerStateData RemotePlayer = new PlayerStateData();

        // --- Session management (host decides) ---
        public static SessionStartMode StartMode = SessionStartMode.None;
        public static GameDifficulty Difficulty = GameDifficulty.Normal;
        public static int SaveSlotToLoad = -1;    // -1 = none selected
        public static bool SessionStartRequested = false; // flag for GameSession plugin to act on
        public static bool SessionStarted = false;
        public static int SelectedChapter = 2;            // ChapterNo enum value (2=Chapter0/GuestHouse, 4=Chapter1/MainHouse, etc.)
        public static bool StartingItemsGiven = false;    // flag to give pistol + ammo once per chapter

        // --- Stability window before creating inventory items (avoids crashes during scene activation) ---
        // We only create starting items once we've observed the inventory in a 'healthy' state for this many
        // consecutive frames. 180 frames ≈ 3 seconds at 60 FPS — enough for EnvActivateManager.folderActivate
        // to finish and for all item prefabs' doAwake callbacks to have their required context.
        public static int InventoryStableFrames = 0;
        public const int RequiredStableFrames = 180;

        // --- Chapter sync (host-authoritative) ---
        // When the client connects, the host reports its current ChapterNo enum value so we can warn
        // the client if they are on a different chapter (which would cause desync problems).
        public static int HostCurrentChapter = -1;    // -1 means "unknown"
        public static int LocalCurrentChapter = -1;
        public static bool ChapterMismatch = false;
        public static bool ForceSyncToHostRequested = false;  // UI sets this; GameSession consumes it



        // --- Post-cutscene teleport ---
        public static bool TeleportToHostRequested = false; // set by Cutscene plugin after cutscene ends

        // --- Shared Game Over ---
        public static bool SharedGameOverTriggered = false;  // one dies, all die

        // --- Save slot info (populated by GameSession plugin at runtime) ---
        public static string[] SaveSlotNames = new string[21]; // RE7 has up to 21 scenario slots
        public static bool[] SaveSlotExists = new bool[21];
        public static int SaveSlotCount = 0;
        public static bool SaveSlotsScanned = false;

        public static PlayerStateData[] RemoteBuffer = new PlayerStateData[]
        {
            new PlayerStateData(), new PlayerStateData(),
            new PlayerStateData(), new PlayerStateData()
        };
        public static int RemoteBufferIndex = 0;
        public static long LastRemoteUpdateTicks = 0;
        public static float InterpolationT = 0f;

        public static EnemyStateData[] EnemyStates = new EnemyStateData[64];
        public static int EnemyCount = 0;

        public static long PingMs = 0;
        public static long LastPingSentTicks = 0;
        public static long LastPongReceivedTicks = 0;

        public static bool ShowWindow = true;
        public static string StatusMessage = "";
        public static string ErrorMessage = "";

        public static bool IsCoopActive
        {
            get { return Status == ConnectionStatus.Connected || Status == ConnectionStatus.Hosting; }
        }
        public static bool IsHost { get { return Role == CoopRole.Host; } }
        public static bool IsClient { get { return Role == CoopRole.Client; } }

        public static long FrameCount = 0;

        // --- Scene load queue (host -> client) ---
        public static string[] PendingSceneLoads = new string[16];
        public static bool[] PendingSceneLoadActivate = new bool[16];
        public static int PendingSceneLoadCount = 0;
        public static string[] PendingSceneUnloads = new string[16];
        public static int PendingSceneUnloadCount = 0;

        // --- Damage event queue (bidirectional) ---
        public static uint[] DmgTargetIDs = new uint[32];
        public static float[] DmgAmounts = new float[32];
        public static float[] DmgHitX = new float[32];
        public static float[] DmgHitY = new float[32];
        public static float[] DmgHitZ = new float[32];
        public static bool[] DmgFromHost = new bool[32];
        public static int DmgCount = 0;

        public static void Reset()
        {
            Role = CoopRole.None;
            Status = ConnectionStatus.Disconnected;
            CurrentScreen = UIScreen.MainMenu;
            PartnerName = "";
            LocalPlayer = new PlayerStateData();
            RemotePlayer = new PlayerStateData();
            RemoteBuffer = new PlayerStateData[]
            {
                new PlayerStateData(), new PlayerStateData(),
                new PlayerStateData(), new PlayerStateData()
            };
            RemoteBufferIndex = 0;
            LastRemoteUpdateTicks = 0;
            InterpolationT = 0f;
            EnemyStates = new EnemyStateData[64];
            EnemyCount = 0;
            PingMs = 0;
            LastPingSentTicks = 0;
            LastPongReceivedTicks = 0;
            StatusMessage = "";
            ErrorMessage = "";
            StartMode = SessionStartMode.None;
            Difficulty = GameDifficulty.Normal;
            SaveSlotToLoad = -1;
            SessionStartRequested = false;
            SessionStarted = false;
            SaveSlotsScanned = false;
            TeleportToHostRequested = false;
            SharedGameOverTriggered = false;
            PendingSceneLoads = new string[16];
            PendingSceneLoadActivate = new bool[16];
            PendingSceneLoadCount = 0;
            PendingSceneUnloads = new string[16];
            PendingSceneUnloadCount = 0;
            DmgTargetIDs = new uint[32];
            DmgAmounts = new float[32];
            DmgHitX = new float[32];
            DmgHitY = new float[32];
            DmgHitZ = new float[32];
            DmgFromHost = new bool[32];
            DmgCount = 0;
        }
    }
}
