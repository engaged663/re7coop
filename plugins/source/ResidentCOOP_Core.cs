// ResidentCOOP_Core.cs
// Main plugin entry point: lifecycle, network loop, message dispatch.
// Manages the NetServer/NetClient and routes messages to CoopState.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using ResidentCOOP.Shared;

public class ResidentCOOP_Core
{
    // Network instances (only one active at a time)
    static NetServer _server;
    static NetClient _client;

    // Rate limiting
    static Stopwatch _syncTimer = new Stopwatch();
    static Stopwatch _pingTimer = new Stopwatch();
    static Stopwatch _enemySyncTimer = new Stopwatch();

    const int PLAYER_SYNC_INTERVAL_MS = 50;   // 20 Hz
    const int PING_INTERVAL_MS = 2000;         // every 2s
    const int ENEMY_SYNC_INTERVAL_MS = 100;    // 10 Hz
    const int PING_TIMEOUT_MS = 8000;

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            API.LogInfo("[COOP] ResidentCOOP_Core loaded.");
            _syncTimer.Start();
            _pingTimer.Start();
            _enemySyncTimer.Start();
        }
        catch (Exception e)
        {
            API.LogError("[COOP] Init error: " + e.ToString());
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        try
        {
            Shutdown();
            API.LogInfo("[COOP] ResidentCOOP_Core unloaded.");
        }
        catch (Exception e)
        {
            API.LogError("[COOP] Unload error: " + e.ToString());
        }
    }

    /// <summary>
    /// Called every frame before behavior update. This is our main network tick.
    /// </summary>
    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        try
        {
            CoopState.FrameCount++;

            // Handle connecting state (client waiting for TCP to establish)
            if (CoopState.Status == ConnectionStatus.Connecting && _client != null)
            {
                if (_client.ConnectFailed)
                {
                    CoopState.ErrorMessage = "Connection failed!";
                    CoopState.CurrentScreen = UIScreen.JoinSetup;
                    CoopState.Status = ConnectionStatus.Disconnected;
                    _client.Dispose();
                    _client = null;
                    return;
                }

                if (_client.IsConnected)
                {
                    // TCP established - send handshake
                    _client.Send(NetProtocol.WriteHandshake(CoopState.PlayerName));
                    CoopState.StatusMessage = "Handshake sent...";
                }
                return;
            }

            // Host: check for incoming connection
            if (CoopState.Status == ConnectionStatus.Hosting && _server != null)
            {
                if (!_server.HasClient)
                {
                    if (_server.TryAcceptClient())
                    {
                        CoopState.StatusMessage = "Client connected! Waiting for handshake...";
                    }
                }
            }

            // Process incoming messages
            if (CoopState.IsCoopActive || CoopState.Status == ConnectionStatus.Hosting)
            {
                ProcessIncomingMessages();
            }

            // Send player state at 20Hz
            if (CoopState.IsCoopActive && _syncTimer.ElapsedMilliseconds >= PLAYER_SYNC_INTERVAL_MS)
            {
                _syncTimer.Restart();
                SendPlayerState();
            }

            // Send enemy states at 10Hz (host only)
            if (CoopState.IsHost && CoopState.IsCoopActive && _enemySyncTimer.ElapsedMilliseconds >= ENEMY_SYNC_INTERVAL_MS)
            {
                _enemySyncTimer.Restart();
                SendEnemyStates();
            }

            // Ping every 2s
            if (CoopState.IsCoopActive && _pingTimer.ElapsedMilliseconds >= PING_INTERVAL_MS)
            {
                _pingTimer.Restart();
                long now = DateTime.UtcNow.Ticks;
                CoopState.LastPingSentTicks = now;
                SendRaw(NetProtocol.WritePing(now));

                // Check for timeout
                if (CoopState.LastPongReceivedTicks > 0)
                {
                    long elapsed = (now - CoopState.LastPongReceivedTicks) / TimeSpan.TicksPerMillisecond;
                    if (elapsed > PING_TIMEOUT_MS)
                    {
                        CoopState.ErrorMessage = "Connection timed out!";
                        Disconnect();
                    }
                }
            }

            // Update interpolation for remote player
            UpdateInterpolation();
        }
        catch (Exception e)
        {
            API.LogError("[COOP] Update error: " + e.Message);
        }
    }

    // ----- PUBLIC API (called by UI plugin) -----

    /// <summary>
    /// Start hosting a co-op session.
    /// </summary>
    public static void StartHost()
    {
        try
        {
            Shutdown();
            _server = new NetServer();
            _server.Start(CoopState.Port);
            CoopState.Role = CoopRole.Host;
            CoopState.Status = ConnectionStatus.Hosting;
            CoopState.CurrentScreen = UIScreen.Hosting;
            CoopState.StatusMessage = "Listening on port " + CoopState.Port + "...";
            CoopState.ErrorMessage = "";
            API.LogInfo("[COOP] Hosting on port " + CoopState.Port);
        }
        catch (Exception e)
        {
            CoopState.ErrorMessage = "Failed to host: " + e.Message;
            API.LogError("[COOP] Host error: " + e.ToString());
        }
    }

    /// <summary>
    /// Connect to a host as client.
    /// </summary>
    public static void StartClient()
    {
        try
        {
            Shutdown();
            _client = new NetClient();
            _client.Connect(CoopState.HostIP, CoopState.Port);
            CoopState.Role = CoopRole.Client;
            CoopState.Status = ConnectionStatus.Connecting;
            CoopState.CurrentScreen = UIScreen.Connecting;
            CoopState.StatusMessage = "Connecting to " + CoopState.HostIP + ":" + CoopState.Port + "...";
            CoopState.ErrorMessage = "";
            API.LogInfo("[COOP] Connecting to " + CoopState.HostIP + ":" + CoopState.Port);
        }
        catch (Exception e)
        {
            CoopState.ErrorMessage = "Failed to connect: " + e.Message;
            CoopState.Status = ConnectionStatus.Disconnected;
            API.LogError("[COOP] Connect error: " + e.ToString());
        }
    }

    /// <summary>
    /// Disconnect from current session.
    /// </summary>
    public static void Disconnect()
    {
        try
        {
            SendRaw(NetProtocol.WriteDisconnect(0));
        }
        catch { }
        Shutdown();
        CoopState.Reset();
        API.LogInfo("[COOP] Disconnected.");
    }

    // ----- INTERNALS -----

    static void Shutdown()
    {
        if (_server != null) { _server.Dispose(); _server = null; }
        if (_client != null) { _client.Dispose(); _client = null; }
    }

    static bool SendRaw(byte[] data)
    {
        if (CoopState.IsHost && _server != null)
            return _server.Send(data);
        if (CoopState.IsClient && _client != null)
            return _client.Send(data);
        return false;
    }

    static void ProcessIncomingMessages()
    {
        List<NetMessage> messages;

        if (CoopState.IsHost && _server != null)
            messages = _server.ReceiveAll();
        else if (_client != null)
            messages = _client.ReceiveAll();
        else
            return;

        for (int i = 0; i < messages.Count; i++)
        {
            HandleMessage(messages[i]);
        }

        // Check if we lost connection
        if (CoopState.IsHost && _server != null && !_server.HasClient && CoopState.Status == ConnectionStatus.Connected)
        {
            CoopState.Status = ConnectionStatus.Hosting;
            CoopState.PartnerName = "";
            CoopState.StatusMessage = "Client disconnected. Waiting for reconnect...";
            CoopState.CurrentScreen = UIScreen.Hosting;
        }
        else if (CoopState.IsClient && _client != null && !_client.IsConnected && CoopState.Status == ConnectionStatus.Connected)
        {
            CoopState.ErrorMessage = "Lost connection to host!";
            CoopState.Status = ConnectionStatus.Disconnected;
            CoopState.CurrentScreen = UIScreen.MainMenu;
        }
    }

    static void HandleMessage(NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.Handshake:
                HandleHandshake(msg.Payload);
                break;
            case MessageType.HandshakeAck:
                HandleHandshakeAck(msg.Payload);
                break;
            case MessageType.PlayerState:
                HandleRemotePlayerState(msg.Payload);
                break;
            case MessageType.EnemyStates:
                HandleEnemyStates(msg.Payload);
                break;
            case MessageType.EnemyDamage:
                HandleEnemyDamage(msg.Payload);
                break;
            case MessageType.Ping:
                HandlePing(msg.Payload);
                break;
            case MessageType.Pong:
                HandlePong(msg.Payload);
                break;
            case MessageType.Disconnect:
                HandleDisconnectMsg(msg.Payload);
                break;
            case MessageType.SceneLoad:
                HandleSceneLoad(msg.Payload);
                break;
            case MessageType.SceneUnload:
                HandleSceneUnload(msg.Payload);
                break;
            case MessageType.DamageEvent:
                HandleDamageEvent(msg.Payload);
                break;
            case MessageType.WeaponShare:
                HandleWeaponShare(msg.Payload);
                break;
            case MessageType.RewardUnlock:
                HandleRewardUnlock(msg.Payload);
                break;
        }
    }

    static void HandleHandshake(byte[] payload)
    {
        if (!CoopState.IsHost) return;

        ushort version;
        string clientName;
        NetProtocol.ReadHandshake(payload, out version, out clientName);

        if (version != NetProtocol.PROTOCOL_VERSION)
        {
            _server.Send(NetProtocol.WriteHandshakeAck(false, "Version mismatch"));
            return;
        }

        CoopState.PartnerName = clientName;
        CoopState.Status = ConnectionStatus.Connected;
        CoopState.CurrentScreen = UIScreen.Connected;
        CoopState.StatusMessage = clientName + " joined the game!";
        CoopState.LastPongReceivedTicks = DateTime.UtcNow.Ticks;
        _server.Send(NetProtocol.WriteHandshakeAck(true, CoopState.PlayerName));
        API.LogInfo("[COOP] Client joined: " + clientName);
    }

    static void HandleHandshakeAck(byte[] payload)
    {
        if (!CoopState.IsClient) return;

        bool accepted;
        string hostName;
        NetProtocol.ReadHandshakeAck(payload, out accepted, out hostName);

        if (accepted)
        {
            CoopState.PartnerName = hostName;
            CoopState.Status = ConnectionStatus.Connected;
            CoopState.CurrentScreen = UIScreen.Connected;
            CoopState.StatusMessage = "Connected to " + hostName + "!";
            CoopState.LastPongReceivedTicks = DateTime.UtcNow.Ticks;
            API.LogInfo("[COOP] Connected to host: " + hostName);
        }
        else
        {
            CoopState.ErrorMessage = "Rejected: " + hostName;
            CoopState.CurrentScreen = UIScreen.JoinSetup;
            Disconnect();
        }
    }

    static void HandleRemotePlayerState(byte[] payload)
    {
        PlayerStateData state = NetProtocol.ReadPlayerState(payload);

        // Push into interpolation ring buffer
        int idx = CoopState.RemoteBufferIndex % CoopState.RemoteBuffer.Length;
        CoopState.RemoteBuffer[idx].CopyFrom(state);
        CoopState.RemoteBufferIndex++;
        CoopState.LastRemoteUpdateTicks = DateTime.UtcNow.Ticks;
        CoopState.InterpolationT = 0f;

        // Also update the current remote player state
        CoopState.RemotePlayer.CopyFrom(state);
    }

    static void HandleEnemyStates(byte[] payload)
    {
        if (!CoopState.IsClient) return;

        EnemyStateData[] enemies;
        int count;
        NetProtocol.ReadEnemyStates(payload, out enemies, out count);

        // Store for EnemySync plugin to apply
        if (count > CoopState.EnemyStates.Length)
        {
            CoopState.EnemyStates = new EnemyStateData[count];
        }
        for (int i = 0; i < count; i++)
        {
            CoopState.EnemyStates[i] = enemies[i];
        }
        CoopState.EnemyCount = count;
    }

    static void HandleEnemyDamage(byte[] payload)
    {
        if (!CoopState.IsHost) return;
        // EnemySync plugin reads this - for now just log
        // The actual damage application happens in EnemySync
        uint enemyID;
        float damage, hitX, hitY, hitZ;
        NetProtocol.ReadEnemyDamage(payload, out enemyID, out damage, out hitX, out hitY, out hitZ);
        API.LogInfo("[COOP] Enemy damage from client: ID=" + enemyID + " dmg=" + damage);
    }

    static void HandlePing(byte[] payload)
    {
        long timestamp = NetProtocol.ReadTimestamp(payload);
        SendRaw(NetProtocol.WritePong(timestamp));
    }

    static void HandlePong(byte[] payload)
    {
        long echoTimestamp = NetProtocol.ReadTimestamp(payload);
        long now = DateTime.UtcNow.Ticks;
        CoopState.PingMs = (now - echoTimestamp) / TimeSpan.TicksPerMillisecond;
        CoopState.LastPongReceivedTicks = now;
    }

    static void HandleDisconnectMsg(byte[] payload)
    {
        CoopState.ErrorMessage = "Partner disconnected.";
        if (CoopState.IsHost && _server != null)
        {
            _server.DisconnectClient();
            CoopState.Status = ConnectionStatus.Hosting;
            CoopState.CurrentScreen = UIScreen.Hosting;
            CoopState.PartnerName = "";
            CoopState.StatusMessage = "Waiting for new client...";
        }
        else
        {
            CoopState.Status = ConnectionStatus.Disconnected;
            CoopState.CurrentScreen = UIScreen.MainMenu;
        }
    }

    static void SendPlayerState()
    {
        SendRaw(NetProtocol.WritePlayerState(CoopState.LocalPlayer));
    }

    static void SendEnemyStates()
    {
        if (CoopState.EnemyCount > 0)
        {
            SendRaw(NetProtocol.WriteEnemyStates(CoopState.EnemyStates, CoopState.EnemyCount));
        }
    }

    // --- Scene sync handlers ---

    static void HandleSceneLoad(byte[] payload)
    {
        if (!CoopState.IsClient) return;
        string sceneName;
        bool activate;
        NetProtocol.ReadSceneLoad(payload, out sceneName, out activate);
        API.LogInfo("[COOP] Scene load from host: " + sceneName + " activate=" + activate);

        int idx = CoopState.PendingSceneLoadCount;
        if (idx < CoopState.PendingSceneLoads.Length)
        {
            CoopState.PendingSceneLoads[idx] = sceneName;
            CoopState.PendingSceneLoadActivate[idx] = activate;
            CoopState.PendingSceneLoadCount = idx + 1;
        }
    }

    static void HandleSceneUnload(byte[] payload)
    {
        if (!CoopState.IsClient) return;
        string sceneName = NetProtocol.ReadSceneUnload(payload);
        API.LogInfo("[COOP] Scene unload from host: " + sceneName);

        int idx = CoopState.PendingSceneUnloadCount;
        if (idx < CoopState.PendingSceneUnloads.Length)
        {
            CoopState.PendingSceneUnloads[idx] = sceneName;
            CoopState.PendingSceneUnloadCount = idx + 1;
        }
    }

    static void HandleDamageEvent(byte[] payload)
    {
        uint targetID;
        float damage, hitX, hitY, hitZ;
        bool fromHost;
        NetProtocol.ReadDamageEvent(payload, out targetID, out damage, out hitX, out hitY, out hitZ, out fromHost);

        int idx = CoopState.DmgCount;
        if (idx < CoopState.DmgTargetIDs.Length)
        {
            CoopState.DmgTargetIDs[idx] = targetID;
            CoopState.DmgAmounts[idx] = damage;
            CoopState.DmgHitX[idx] = hitX;
            CoopState.DmgHitY[idx] = hitY;
            CoopState.DmgHitZ[idx] = hitZ;
            CoopState.DmgFromHost[idx] = fromHost;
            CoopState.DmgCount = idx + 1;
        }
    }

    // Weapon share: partner picked up a weapon, give it to us if we have space.
    // Handled by the InventorySync plugin - we just store the pending weapon ID.
    static string _pendingWeaponShare = null;
    static string _pendingRewardUnlock = null;

    static void HandleWeaponShare(byte[] payload)
    {
        string weaponID = NetProtocol.ReadWeaponShare(payload);
        _pendingWeaponShare = weaponID;
        API.LogInfo("[COOP] Weapon share received: " + weaponID);
    }

    static void HandleRewardUnlock(byte[] payload)
    {
        string rewardID = NetProtocol.ReadRewardUnlock(payload);
        _pendingRewardUnlock = rewardID;
        API.LogInfo("[COOP] Reward unlock received: " + rewardID);
    }

    /// <summary>
    /// Public: get and clear pending weapon share.
    /// Called by InventorySync plugin each frame.
    /// </summary>
    public static string ConsumePendingWeapon()
    {
        string w = _pendingWeaponShare;
        _pendingWeaponShare = null;
        return w;
    }

    /// <summary>
    /// Public: get and clear pending reward unlock.
    /// </summary>
    public static string ConsumePendingReward()
    {
        string r = _pendingRewardUnlock;
        _pendingRewardUnlock = null;
        return r;
    }

    /// <summary>
    /// Public: send a weapon share message to partner.
    /// </summary>
    public static void BroadcastWeaponShare(string weaponID)
    {
        SendRaw(NetProtocol.WriteWeaponShare(weaponID));
    }

    /// <summary>
    /// Public: send a reward unlock message to partner.
    /// </summary>
    public static void BroadcastRewardUnlock(string rewardID)
    {
        SendRaw(NetProtocol.WriteRewardUnlock(rewardID));
    }

    static void UpdateInterpolation()
    {
        if (!CoopState.IsCoopActive) return;
        if (CoopState.RemoteBufferIndex < 2) return;

        long now = DateTime.UtcNow.Ticks;
        long elapsed = (now - CoopState.LastRemoteUpdateTicks) / TimeSpan.TicksPerMillisecond;
        float t = (float)elapsed / (float)PLAYER_SYNC_INTERVAL_MS;
        if (t > 1.5f) t = 1.5f;
        CoopState.InterpolationT = t;

        // Interpolate between last two buffered states
        int len = CoopState.RemoteBuffer.Length;
        int cur = (CoopState.RemoteBufferIndex - 1) % len;
        int prev = (CoopState.RemoteBufferIndex - 2) % len;
        if (cur < 0) cur += len;
        if (prev < 0) prev += len;

        PlayerStateData a = CoopState.RemoteBuffer[prev];
        PlayerStateData b = CoopState.RemoteBuffer[cur];

        float it = Math.Min(t, 1.0f);
        CoopState.RemotePlayer.PosX = a.PosX + (b.PosX - a.PosX) * it;
        CoopState.RemotePlayer.PosY = a.PosY + (b.PosY - a.PosY) * it;
        CoopState.RemotePlayer.PosZ = a.PosZ + (b.PosZ - a.PosZ) * it;

        // For rotation, use latest (slerp is complex for minimal benefit here)
        CoopState.RemotePlayer.RotX = b.RotX;
        CoopState.RemotePlayer.RotY = b.RotY;
        CoopState.RemotePlayer.RotZ = b.RotZ;
        CoopState.RemotePlayer.RotW = b.RotW;

        // Other fields use latest
        CoopState.RemotePlayer.Health = b.Health;
        CoopState.RemotePlayer.MaxHealth = b.MaxHealth;
        CoopState.RemotePlayer.AnimState = b.AnimState;
        CoopState.RemotePlayer.WeaponID = b.WeaponID;
        CoopState.RemotePlayer.Flags = b.Flags;
    }
}
