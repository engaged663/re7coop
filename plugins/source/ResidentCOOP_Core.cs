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

            // Check teleport-to-host request (post-cutscene)
            if (CoopState.IsHost && CoopState.TeleportToHostRequested)
            {
                CoopState.TeleportToHostRequested = false;
                SendRaw(NetProtocol.WriteTeleportSync(
                    CoopState.LocalPlayer.PosX,
                    CoopState.LocalPlayer.PosY,
                    CoopState.LocalPlayer.PosZ));
                API.LogInfo("[COOP] Sent teleport sync to client");
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

    public static bool SendRaw(byte[] data)
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
            case MessageType.TeleportSync:
                HandleTeleportSync(msg.Payload);
                break;
            case MessageType.GameOver:
                HandleGameOver(msg.Payload);
                break;
            case MessageType.GameEvent:
                HandleGameEventMsg(msg.Payload);
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
            _server.Send(NetProtocol.WriteHandshakeAck(false, "Version mismatch", -1));
            return;
        }

        CoopState.PartnerName = clientName;
        CoopState.Status = ConnectionStatus.Connected;
        CoopState.CurrentScreen = UIScreen.Connected;
        CoopState.StatusMessage = clientName + " joined the game!";
        CoopState.LastPongReceivedTicks = DateTime.UtcNow.Ticks;

        // Read the host's current ChapterNo enum value so the client can detect a mismatch.
        // NOTE: REFramework compiles each plugin .cs individually — we CANNOT reference
        // ResidentCOOP_GameSession directly here. Use CallPluginGet (reflection) instead.
        int hostChapter = -1;
        try
        {
            object r = CallPluginGet("ResidentCOOP_GameSession", "GetCurrentChapter", new object[0]);
            if (r != null) hostChapter = (int)r;
        }
        catch { }
        CoopState.HostCurrentChapter = hostChapter;


        _server.Send(NetProtocol.WriteHandshakeAck(true, CoopState.PlayerName, hostChapter));
        API.LogInfo("[COOP] Client joined: " + clientName + " (reporting hostChapter=" + hostChapter + ")");

        // Send current position immediately so client can teleport to host
        // The host's position is already in CoopState.LocalPlayer
        SendPlayerState();
        API.LogInfo("[COOP] Sent initial position for late-join teleport");
    }

    static void HandleHandshakeAck(byte[] payload)
    {
        if (!CoopState.IsClient) return;

        bool accepted;
        string hostName;
        int hostChapter;
        NetProtocol.ReadHandshakeAck(payload, out accepted, out hostName, out hostChapter);

        if (accepted)
        {
            CoopState.PartnerName = hostName;
            CoopState.Status = ConnectionStatus.Connected;
            CoopState.CurrentScreen = UIScreen.Connected;
            CoopState.StatusMessage = "Connected to " + hostName + "!";
            CoopState.LastPongReceivedTicks = DateTime.UtcNow.Ticks;
            CoopState.TeleportToHostRequested = true; // Late-join: teleport to host position

            // --- Late-join session sync ---
            // The host is already in-game (otherwise it wouldn't have accepted us). Mark our
            // local session as started and force the starting-items flow to run so the client
            // ALSO receives knife + handgun + ammo. GameSession.IsInventoryReady() will gate
            // actual creation until our local inventory is safe to touch.
            CoopState.SessionStarted = true;
            CoopState.StartingItemsGiven = false;
            CoopState.InventoryStableFrames = 0;

            // --- Chapter mismatch detection ---
            // Compare the host's reported chapter with our own. If they differ, flag it so
            // the UI can offer a "Force Sync to Host" button.
            CoopState.HostCurrentChapter = hostChapter;
            int myChapter = -1;
            try
            {
                object r = CallPluginGet("ResidentCOOP_GameSession", "GetCurrentChapter", new object[0]);
                if (r != null) myChapter = (int)r;
            }
            catch { }
            CoopState.LocalCurrentChapter = myChapter;


            if (hostChapter >= 0 && myChapter >= 0 && hostChapter != myChapter)
            {
                CoopState.ChapterMismatch = true;
                API.LogWarning("[COOP] Chapter mismatch: host=" + hostChapter + " local=" + myChapter);
                CoopState.StatusMessage = "WARNING: Host is on a different chapter!";
            }
            else
            {
                CoopState.ChapterMismatch = false;
            }

            API.LogInfo("[COOP] Connected to host: " + hostName
                + " (hostChapter=" + hostChapter + " localChapter=" + myChapter + ")");
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

        // Delegate to InventorySync via reflection
        try { CallPlugin("ResidentCOOP_InventorySync", "QueueRewardUnlock", new object[] { rewardID }); } catch { }
    }

    /// <summary>
    /// Handle incoming GameEvent messages — delegate to WorldSync.
    /// These carry item pickups, door changes, objectives, map flags.
    /// </summary>
    static void HandleGameEventMsg(byte[] payload)
    {
        try { CallPlugin("ResidentCOOP_WorldSync", "HandleIncomingGameEvent", new object[] { payload }); }
        catch (Exception e)
        {
            API.LogError("[COOP] GameEvent handler error: " + e.Message);
        }
    }

    /// <summary>
    /// Reflection helper: call a static method on another plugin class.
    /// REFramework compiles each .cs individually, so no direct cross-refs.
    /// </summary>
    static System.Collections.Generic.Dictionary<string, Type> _pluginTypeCache =
        new System.Collections.Generic.Dictionary<string, Type>();

    static void CallPlugin(string typeName, string methodName, object[] args)
    {
        Type t;
        if (!_pluginTypeCache.TryGetValue(typeName, out t) || t == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(typeName); if (t != null) break; } catch { }
            }
            _pluginTypeCache[typeName] = t;
        }
        if (t == null) return;

        var method = t.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (method != null) method.Invoke(null, args);
    }

    /// <summary>
    /// Reflection helper: call a static method on another plugin class and return its result.
    /// Returns null if the type/method is not found.
    /// </summary>
    static object CallPluginGet(string typeName, string methodName, object[] args)
    {
        Type t;
        if (!_pluginTypeCache.TryGetValue(typeName, out t) || t == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(typeName); if (t != null) break; } catch { }
            }
            _pluginTypeCache[typeName] = t;
        }
        if (t == null) return null;

        var method = t.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (method == null) return null;
        return method.Invoke(null, args);
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

        // Rotation: quaternion SLERP for smooth interpolation
        Slerp(a.RotX, a.RotY, a.RotZ, a.RotW,
              b.RotX, b.RotY, b.RotZ, b.RotW,
              it,
              out CoopState.RemotePlayer.RotX, out CoopState.RemotePlayer.RotY,
              out CoopState.RemotePlayer.RotZ, out CoopState.RemotePlayer.RotW);

        // Other fields use latest
        CoopState.RemotePlayer.Health = b.Health;
        CoopState.RemotePlayer.MaxHealth = b.MaxHealth;
        CoopState.RemotePlayer.AnimState = b.AnimState;
        CoopState.RemotePlayer.WeaponID = b.WeaponID;
        CoopState.RemotePlayer.Flags = b.Flags;
    }

    /// <summary>
    /// Quaternion SLERP for smooth rotation interpolation.
    /// Manual implementation since System.Numerics.Quaternion may not be available.
    /// </summary>
    static void Slerp(float ax, float ay, float az, float aw,
                       float bx, float by, float bz, float bw,
                       float t,
                       out float rx, out float ry, out float rz, out float rw)
    {
        // Compute dot product
        float dot = ax * bx + ay * by + az * bz + aw * bw;

        // If negative dot, negate one quaternion to take shorter path
        if (dot < 0f)
        {
            bx = -bx; by = -by; bz = -bz; bw = -bw;
            dot = -dot;
        }

        float s0, s1;
        if (dot > 0.9995f)
        {
            // Very close — use linear interpolation to avoid division by zero
            s0 = 1f - t;
            s1 = t;
        }
        else
        {
            float theta = (float)Math.Acos(dot);
            float sinTheta = (float)Math.Sin(theta);
            s0 = (float)Math.Sin((1f - t) * theta) / sinTheta;
            s1 = (float)Math.Sin(t * theta) / sinTheta;
        }

        rx = s0 * ax + s1 * bx;
        ry = s0 * ay + s1 * by;
        rz = s0 * az + s1 * bz;
        rw = s0 * aw + s1 * bw;
    }

    // --- Teleport sync handling ---

    static void HandleTeleportSync(byte[] payload)
    {
        if (!CoopState.IsClient) return;

        float x, y, z;
        NetProtocol.ReadTeleportSync(payload, out x, out y, out z);
        API.LogInfo("[COOP] Teleport sync received: " + x + ", " + y + ", " + z);

        // Store for the Cutscene plugin to apply
        CoopState.RemotePlayer.PosX = x;
        CoopState.RemotePlayer.PosY = y;
        CoopState.RemotePlayer.PosZ = z;

        // Apply teleport via ObjectManager
        try
        {
            dynamic objectManager = API.GetManagedSingleton("app.ObjectManager");
            if (objectManager == null) return;

            dynamic player = (objectManager as ManagedObject).Call("getPlayer");
            if (player == null) return;

            dynamic transform = (player as ManagedObject).Call("get_Transform");
            if (transform == null) return;

            ManagedObject transformMO = transform as ManagedObject;
            if (transformMO == null) return;

            var typed = (transformMO as IObject).As<via.Transform>();
            if (typed != null)
            {
                var pos = typed.Position;
                pos.x = x; pos.y = y; pos.z = z;
                typed.Position = pos;
                API.LogInfo("[COOP] Client teleported to host position");
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP] Teleport apply failed: " + e.Message);
        }
    }

    static void HandleGameOver(byte[] payload)
    {
        API.LogInfo("[COOP] Shared Game Over received from partner!");
        CoopState.SharedGameOverTriggered = true;

        // Trigger game over on the local player
        try
        {
            dynamic gameManager = API.GetManagedSingleton("app.GameManager");
            if (gameManager != null)
            {
                // Try to trigger game over via GameManager
                try { (gameManager as ManagedObject).Call("gameOverCaptureStart"); }
                catch { }
            }
        }
        catch (Exception e)
        {
            API.LogError("[COOP] GameOver trigger failed: " + e.Message);
        }
    }

    /// <summary>
    /// Public: send game over notification to partner.
    /// Called by PlayerSync when local player dies.
    /// </summary>
    public static void BroadcastGameOver()
    {
        if (!CoopState.IsCoopActive) return;
        if (CoopState.SharedGameOverTriggered) return; // Don't echo back
        SendRaw(NetProtocol.WriteGameOver());
        API.LogInfo("[COOP] Sent shared game over to partner");
    }
}
