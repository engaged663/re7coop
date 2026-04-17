// ResidentCOOP.Shared - NetProtocol.cs
// Binary network protocol: message types, serialization, framing.

using System;
using System.IO;

namespace ResidentCOOP.Shared
{
    public enum MessageType : byte
    {
        Handshake    = 0x01,
        HandshakeAck = 0x02,
        PlayerState  = 0x03,
        EnemyStates  = 0x04,
        EnemyDamage  = 0x05,
        PlayerDamage = 0x06,
        Ping         = 0x07,
        Pong         = 0x08,
        Disconnect   = 0x09,
        GameEvent    = 0x0A,
        SceneLoad    = 0x0B,  // Host tells client to load a scene
        SceneUnload  = 0x0C,  // Host tells client to unload a scene
        DamageEvent  = 0x0D,  // Damage dealt to an entity (bidirectional)
        WeaponShare  = 0x0E,  // Weapon acquired - share to partner if space
        RewardUnlock = 0x0F,  // Reward unlocked - sync to partner
        TeleportSync = 0x10,  // Post-cutscene: teleport to this position
        GameOver     = 0x11   // Shared game over: one dies, all die
    }

    public static class NetProtocol
    {
        public const ushort PROTOCOL_VERSION = 1;
        public const int HEADER_SIZE = 5;
        public const int MAX_MESSAGE_SIZE = 65536;

        // --- Framing ---

        public static byte[] Frame(MessageType type, byte[] payload)
        {
            int totalLen = 1 + payload.Length;
            byte[] frame = new byte[4 + totalLen];
            frame[0] = (byte)(totalLen & 0xFF);
            frame[1] = (byte)((totalLen >> 8) & 0xFF);
            frame[2] = (byte)((totalLen >> 16) & 0xFF);
            frame[3] = (byte)((totalLen >> 24) & 0xFF);
            frame[4] = (byte)type;
            Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
            return frame;
        }

        // --- Handshake (Client -> Host) ---

        public static byte[] WriteHandshake(string playerName)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(PROTOCOL_VERSION);
            bw.Write(playerName ?? "Player");
            byte[] result = Frame(MessageType.Handshake, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadHandshake(byte[] payload, out ushort version, out string playerName)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            version = br.ReadUInt16();
            playerName = br.ReadString();
            br.Close(); ms.Close();
        }

        // --- HandshakeAck (Host -> Client) ---
        // Payload layout (v2, backward compatible via length check):
        //   accepted : bool
        //   hostName : string
        //   hostChapter : int32   (NEW - host's ChapterNo enum value; -1 if unknown)

        public static byte[] WriteHandshakeAck(bool accepted, string hostName, int hostChapter = -1)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(accepted);
            bw.Write(hostName ?? "Host");
            bw.Write(hostChapter);
            byte[] result = Frame(MessageType.HandshakeAck, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadHandshakeAck(byte[] payload, out bool accepted, out string hostName, out int hostChapter)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            accepted = br.ReadBoolean();
            hostName = br.ReadString();
            // Older peers won't include the chapter field; keep reading defensively.
            hostChapter = -1;
            try { if (ms.Position < ms.Length) hostChapter = br.ReadInt32(); } catch { }
            br.Close(); ms.Close();
        }

        // Legacy 2-out overload, preserved so other call-sites keep compiling.
        public static void ReadHandshakeAck(byte[] payload, out bool accepted, out string hostName)
        {
            int _dummyChapter;
            ReadHandshakeAck(payload, out accepted, out hostName, out _dummyChapter);
        }


        // --- PlayerState (Bidirectional) ---

        public static byte[] WritePlayerState(PlayerStateData state)
        {
            MemoryStream ms = new MemoryStream(64);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(state.PosX); bw.Write(state.PosY); bw.Write(state.PosZ);
            bw.Write(state.RotX); bw.Write(state.RotY); bw.Write(state.RotZ); bw.Write(state.RotW);
            bw.Write(state.Health); bw.Write(state.MaxHealth);
            bw.Write(state.AnimState); bw.Write(state.WeaponID); bw.Write(state.Flags);
            byte[] result = Frame(MessageType.PlayerState, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static PlayerStateData ReadPlayerState(byte[] payload)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            PlayerStateData s = new PlayerStateData();
            s.PosX = br.ReadSingle(); s.PosY = br.ReadSingle(); s.PosZ = br.ReadSingle();
            s.RotX = br.ReadSingle(); s.RotY = br.ReadSingle(); s.RotZ = br.ReadSingle(); s.RotW = br.ReadSingle();
            s.Health = br.ReadSingle(); s.MaxHealth = br.ReadSingle();
            s.AnimState = br.ReadUInt16(); s.WeaponID = br.ReadInt32(); s.Flags = br.ReadByte();
            br.Close(); ms.Close();
            return s;
        }

        // --- EnemyStates (Host -> Client) ---

        public static byte[] WriteEnemyStates(EnemyStateData[] enemies, int count)
        {
            MemoryStream ms = new MemoryStream(4 + count * 36);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                bw.Write(enemies[i].EnemyID);
                bw.Write(enemies[i].PosX); bw.Write(enemies[i].PosY); bw.Write(enemies[i].PosZ);
                bw.Write(enemies[i].RotX); bw.Write(enemies[i].RotY);
                bw.Write(enemies[i].RotZ); bw.Write(enemies[i].RotW);
                bw.Write(enemies[i].Health); bw.Write(enemies[i].AnimState); bw.Write(enemies[i].Flags);
            }
            byte[] result = Frame(MessageType.EnemyStates, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadEnemyStates(byte[] payload, out EnemyStateData[] enemies, out int count)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            count = br.ReadUInt16();
            enemies = new EnemyStateData[Math.Max(count, 1)];
            for (int i = 0; i < count; i++)
            {
                enemies[i] = new EnemyStateData();
                enemies[i].EnemyID = br.ReadUInt32();
                enemies[i].PosX = br.ReadSingle(); enemies[i].PosY = br.ReadSingle(); enemies[i].PosZ = br.ReadSingle();
                enemies[i].RotX = br.ReadSingle(); enemies[i].RotY = br.ReadSingle();
                enemies[i].RotZ = br.ReadSingle(); enemies[i].RotW = br.ReadSingle();
                enemies[i].Health = br.ReadSingle(); enemies[i].AnimState = br.ReadUInt16(); enemies[i].Flags = br.ReadByte();
            }
            br.Close(); ms.Close();
        }

        // --- EnemyDamage (Client -> Host) ---

        public static byte[] WriteEnemyDamage(uint enemyID, float damage, float hitX, float hitY, float hitZ)
        {
            MemoryStream ms = new MemoryStream(20);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(enemyID); bw.Write(damage);
            bw.Write(hitX); bw.Write(hitY); bw.Write(hitZ);
            byte[] result = Frame(MessageType.EnemyDamage, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadEnemyDamage(byte[] payload, out uint enemyID, out float damage,
            out float hitX, out float hitY, out float hitZ)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            enemyID = br.ReadUInt32(); damage = br.ReadSingle();
            hitX = br.ReadSingle(); hitY = br.ReadSingle(); hitZ = br.ReadSingle();
            br.Close(); ms.Close();
        }

        // --- Ping / Pong ---

        public static byte[] WritePing(long timestamp)
        {
            MemoryStream ms = new MemoryStream(8);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(timestamp);
            byte[] result = Frame(MessageType.Ping, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static long ReadTimestamp(byte[] payload)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            long val = br.ReadInt64();
            br.Close(); ms.Close();
            return val;
        }

        public static byte[] WritePong(long echoTimestamp)
        {
            MemoryStream ms = new MemoryStream(8);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(echoTimestamp);
            byte[] result = Frame(MessageType.Pong, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        // --- Disconnect ---

        public static byte[] WriteDisconnect(byte reason)
        {
            return Frame(MessageType.Disconnect, new byte[] { reason });
        }

        public static byte ReadDisconnect(byte[] payload)
        {
            return payload.Length > 0 ? payload[0] : (byte)0;
        }

        // --- SceneLoad (Host -> Client) ---

        public static byte[] WriteSceneLoad(string sceneName, bool activate)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(sceneName ?? "");
            bw.Write(activate);
            byte[] result = Frame(MessageType.SceneLoad, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadSceneLoad(byte[] payload, out string sceneName, out bool activate)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            sceneName = br.ReadString();
            activate = br.ReadBoolean();
            br.Close(); ms.Close();
        }

        // --- SceneUnload (Host -> Client) ---

        public static byte[] WriteSceneUnload(string sceneName)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(sceneName ?? "");
            byte[] result = Frame(MessageType.SceneUnload, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static string ReadSceneUnload(byte[] payload)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            string name = br.ReadString();
            br.Close(); ms.Close();
            return name;
        }

        // --- DamageEvent (Bidirectional) ---
        // targetID: enemyID or 0 for player
        // damageAmount: HP damage
        // hitPos: world position of hit
        // attackerIsHost: true if host dealt damage

        public static byte[] WriteDamageEvent(uint targetID, float damage,
            float hitX, float hitY, float hitZ, bool attackerIsHost)
        {
            MemoryStream ms = new MemoryStream(24);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(targetID);
            bw.Write(damage);
            bw.Write(hitX); bw.Write(hitY); bw.Write(hitZ);
            bw.Write(attackerIsHost);
            byte[] result = Frame(MessageType.DamageEvent, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadDamageEvent(byte[] payload, out uint targetID, out float damage,
            out float hitX, out float hitY, out float hitZ, out bool attackerIsHost)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            targetID = br.ReadUInt32();
            damage = br.ReadSingle();
            hitX = br.ReadSingle(); hitY = br.ReadSingle(); hitZ = br.ReadSingle();
            attackerIsHost = br.ReadBoolean();
            br.Close(); ms.Close();
        }

        // --- WeaponShare (Bidirectional) ---
        // itemID: the weapon's item data ID string

        public static byte[] WriteWeaponShare(string itemID)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(itemID ?? "");
            byte[] result = Frame(MessageType.WeaponShare, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static string ReadWeaponShare(byte[] payload)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            string id = br.ReadString();
            br.Close(); ms.Close();
            return id;
        }

        // --- RewardUnlock (Bidirectional) ---

        public static byte[] WriteRewardUnlock(string rewardID)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(rewardID ?? "");
            byte[] result = Frame(MessageType.RewardUnlock, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static string ReadRewardUnlock(byte[] payload)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            string id = br.ReadString();
            br.Close(); ms.Close();
            return id;
        }

        // --- TeleportSync (Host -> Client) ---
        // Host sends their position after a cutscene ends so client teleports to them.

        public static byte[] WriteTeleportSync(float x, float y, float z)
        {
            MemoryStream ms = new MemoryStream(12);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(x); bw.Write(y); bw.Write(z);
            byte[] result = Frame(MessageType.TeleportSync, ms.ToArray());
            bw.Close(); ms.Close();
            return result;
        }

        public static void ReadTeleportSync(byte[] payload, out float x, out float y, out float z)
        {
            MemoryStream ms = new MemoryStream(payload);
            BinaryReader br = new BinaryReader(ms);
            x = br.ReadSingle(); y = br.ReadSingle(); z = br.ReadSingle();
            br.Close(); ms.Close();
        }

        // --- GameOver (Bidirectional) ---
        // When one player dies, notify the other to trigger game over.

        public static byte[] WriteGameOver()
        {
            return Frame(MessageType.GameOver, new byte[0]);
        }
    }
}
