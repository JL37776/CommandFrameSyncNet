// Zero Unity dependencies. Pure .NET.
using System;
using System.Text;

namespace BurstStrike.Net.Shared.Protocol
{
    /// <summary>
    /// All network message structs and their binary encode/decode logic.
    /// Wire format: [4-byte LE payload length][1-byte MessageType][payload bytes...]
    ///
    /// Strings are length-prefixed UTF-8 (2-byte LE length + bytes).
    /// Byte arrays are length-prefixed (4-byte LE length + bytes).
    /// </summary>
    public static class Messages
    {
        // ══════════════════════════════════════════════════════════════════
        //  C→S: JoinRequest
        // ══════════════════════════════════════════════════════════════════

        public struct JoinRequest
        {
            /// <summary>Auth token provided by the web/lobby server.</summary>
            public string AuthToken;
            /// <summary>Desired room id (empty = create/auto-match).</summary>
            public string RoomId;
            /// <summary>Client protocol version for compatibility check.</summary>
            public int ProtocolVersion;

            public byte[] Encode()
            {
                var w = new NetWriter(256);
                w.WriteByte((byte)MessageType.JoinRequest);
                w.WriteInt32(ProtocolVersion);
                w.WriteString(AuthToken);
                w.WriteString(RoomId);
                return w.ToArray();
            }

            public static JoinRequest Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new JoinRequest();
                msg.ProtocolVersion = r.ReadInt32();
                msg.AuthToken = r.ReadString();
                msg.RoomId = r.ReadString();
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  S→C: JoinResult
        // ══════════════════════════════════════════════════════════════════

        public struct JoinResult
        {
            public bool Success;
            /// <summary>Assigned player slot index (0-based).</summary>
            public byte PlayerId;
            public string RoomId;
            /// <summary>If rejected, the reason string.</summary>
            public string Reason;

            public byte[] Encode()
            {
                var w = new NetWriter(128);
                w.WriteByte((byte)MessageType.JoinResult);
                w.WriteByte(Success ? (byte)1 : (byte)0);
                w.WriteByte(PlayerId);
                w.WriteString(RoomId);
                w.WriteString(Reason);
                return w.ToArray();
            }

            public static JoinResult Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new JoinResult();
                msg.Success = r.ReadByte() != 0;
                msg.PlayerId = r.ReadByte();
                msg.RoomId = r.ReadString();
                msg.Reason = r.ReadString();
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  S→C: GameStart
        // ══════════════════════════════════════════════════════════════════

        public struct GameStart
        {
            public byte PlayerCount;
            public int TickRate;
            /// <summary>Countdown ticks before simulation starts (0 = immediate).</summary>
            public int CountdownTicks;
            /// <summary>Random seed for deterministic init.</summary>
            public int RandomSeed;

            public byte[] Encode()
            {
                var w = new NetWriter(32);
                w.WriteByte((byte)MessageType.GameStart);
                w.WriteByte(PlayerCount);
                w.WriteInt32(TickRate);
                w.WriteInt32(CountdownTicks);
                w.WriteInt32(RandomSeed);
                return w.ToArray();
            }

            public static GameStart Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new GameStart();
                msg.PlayerCount = r.ReadByte();
                msg.TickRate = r.ReadInt32();
                msg.CountdownTicks = r.ReadInt32();
                msg.RandomSeed = r.ReadInt32();
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  C→S: ClientReady
        // ══════════════════════════════════════════════════════════════════

        public struct ClientReady
        {
            public byte[] Encode()
            {
                var w = new NetWriter(4);
                w.WriteByte((byte)MessageType.ClientReady);
                return w.ToArray();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  C→S: TickInput  — player commands for one tick
        // ══════════════════════════════════════════════════════════════════

        public struct TickInput
        {
            public int Tick;
            /// <summary>
            /// Encoded commands (each element is one CommandCodec-encoded command).
            /// Empty array = no-op frame (player did nothing this tick).
            /// </summary>
            public byte[][] Commands;

            public byte[] Encode()
            {
                int cmdCount = Commands?.Length ?? 0;
                var w = new NetWriter(32 + cmdCount * 64);
                w.WriteByte((byte)MessageType.TickInput);
                w.WriteInt32(Tick);
                w.WriteInt32(cmdCount);
                for (int i = 0; i < cmdCount; i++)
                    w.WriteBytes(Commands[i]);
                return w.ToArray();
            }

            public static TickInput Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new TickInput();
                msg.Tick = r.ReadInt32();
                int count = r.ReadInt32();
                msg.Commands = new byte[count][];
                for (int i = 0; i < count; i++)
                    msg.Commands[i] = r.ReadBytes();
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  S→C: TickCommands  — merged commands for one tick from all players
        // ══════════════════════════════════════════════════════════════════

        /// <summary>One player's commands within a tick.</summary>
        public struct PlayerInput
        {
            public byte PlayerId;
            public byte[][] Commands;
        }

        public struct TickCommands
        {
            public int Tick;
            public PlayerInput[] Inputs;

            public byte[] Encode()
            {
                int inputCount = Inputs?.Length ?? 0;
                var w = new NetWriter(64 + inputCount * 128);
                w.WriteByte((byte)MessageType.TickCommands);
                w.WriteInt32(Tick);
                w.WriteInt32(inputCount);
                for (int i = 0; i < inputCount; i++)
                {
                    w.WriteByte(Inputs[i].PlayerId);
                    int cmdCount = Inputs[i].Commands?.Length ?? 0;
                    w.WriteInt32(cmdCount);
                    for (int j = 0; j < cmdCount; j++)
                        w.WriteBytes(Inputs[i].Commands[j]);
                }
                return w.ToArray();
            }

            public static TickCommands Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new TickCommands();
                msg.Tick = r.ReadInt32();
                int inputCount = r.ReadInt32();
                msg.Inputs = new PlayerInput[inputCount];
                for (int i = 0; i < inputCount; i++)
                {
                    msg.Inputs[i].PlayerId = r.ReadByte();
                    int cmdCount = r.ReadInt32();
                    msg.Inputs[i].Commands = new byte[cmdCount][];
                    for (int j = 0; j < cmdCount; j++)
                        msg.Inputs[i].Commands[j] = r.ReadBytes();
                }
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  C→S: SyncRequest  — request missed ticks for reconnection
        // ══════════════════════════════════════════════════════════════════

        public struct SyncRequest
        {
            public int FromTick;
            public int ToTick;

            public byte[] Encode()
            {
                var w = new NetWriter(16);
                w.WriteByte((byte)MessageType.SyncRequest);
                w.WriteInt32(FromTick);
                w.WriteInt32(ToTick);
                return w.ToArray();
            }

            public static SyncRequest Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new SyncRequest();
                msg.FromTick = r.ReadInt32();
                msg.ToTick = r.ReadInt32();
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  S→C: SyncResponse  — historical ticks for reconnection
        // ══════════════════════════════════════════════════════════════════

        public struct SyncResponse
        {
            public TickCommands[] Ticks;

            public byte[] Encode()
            {
                int count = Ticks?.Length ?? 0;
                var w = new NetWriter(128 + count * 256);
                w.WriteByte((byte)MessageType.SyncResponse);
                w.WriteInt32(count);
                for (int i = 0; i < count; i++)
                {
                    // Inline the TickCommands encoding (without the message type byte)
                    var tc = Ticks[i];
                    w.WriteInt32(tc.Tick);
                    int inputCount = tc.Inputs?.Length ?? 0;
                    w.WriteInt32(inputCount);
                    for (int j = 0; j < inputCount; j++)
                    {
                        w.WriteByte(tc.Inputs[j].PlayerId);
                        int cmdCount = tc.Inputs[j].Commands?.Length ?? 0;
                        w.WriteInt32(cmdCount);
                        for (int k = 0; k < cmdCount; k++)
                            w.WriteBytes(tc.Inputs[j].Commands[k]);
                    }
                }
                return w.ToArray();
            }

            public static SyncResponse Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                var msg = new SyncResponse();
                int count = r.ReadInt32();
                msg.Ticks = new TickCommands[count];
                for (int i = 0; i < count; i++)
                {
                    msg.Ticks[i].Tick = r.ReadInt32();
                    int inputCount = r.ReadInt32();
                    msg.Ticks[i].Inputs = new PlayerInput[inputCount];
                    for (int j = 0; j < inputCount; j++)
                    {
                        msg.Ticks[i].Inputs[j].PlayerId = r.ReadByte();
                        int cmdCount = r.ReadInt32();
                        msg.Ticks[i].Inputs[j].Commands = new byte[cmdCount][];
                        for (int k = 0; k < cmdCount; k++)
                            msg.Ticks[i].Inputs[j].Commands[k] = r.ReadBytes();
                    }
                }
                return msg;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Ping / Pong
        // ══════════════════════════════════════════════════════════════════

        public struct Ping
        {
            public long TimestampMs;

            public byte[] Encode()
            {
                var w = new NetWriter(16);
                w.WriteByte((byte)MessageType.Ping);
                w.WriteInt64(TimestampMs);
                return w.ToArray();
            }

            public static Ping Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                return new Ping { TimestampMs = r.ReadInt64() };
            }
        }

        public struct Pong
        {
            public long TimestampMs;

            public byte[] Encode()
            {
                var w = new NetWriter(16);
                w.WriteByte((byte)MessageType.Pong);
                w.WriteInt64(TimestampMs);
                return w.ToArray();
            }

            public static Pong Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                return new Pong { TimestampMs = r.ReadInt64() };
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  S→C: PlayerDisconnected / PlayerReconnected / GameEnd
        // ══════════════════════════════════════════════════════════════════

        public struct PlayerDisconnected
        {
            public byte PlayerId;

            public byte[] Encode()
            {
                var w = new NetWriter(4);
                w.WriteByte((byte)MessageType.PlayerDisconnected);
                w.WriteByte(PlayerId);
                return w.ToArray();
            }

            public static PlayerDisconnected Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                return new PlayerDisconnected { PlayerId = r.ReadByte() };
            }
        }

        public struct PlayerReconnected
        {
            public byte PlayerId;

            public byte[] Encode()
            {
                var w = new NetWriter(4);
                w.WriteByte((byte)MessageType.PlayerReconnected);
                w.WriteByte(PlayerId);
                return w.ToArray();
            }

            public static PlayerReconnected Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                return new PlayerReconnected { PlayerId = r.ReadByte() };
            }
        }

        public struct GameEnd
        {
            /// <summary>0 = normal, 1 = disconnect, 2 = timeout, 3 = error.</summary>
            public byte Reason;

            public byte[] Encode()
            {
                var w = new NetWriter(4);
                w.WriteByte((byte)MessageType.GameEnd);
                w.WriteByte(Reason);
                return w.ToArray();
            }

            public static GameEnd Decode(byte[] data, int offset)
            {
                var r = new NetReader(data, offset);
                return new GameEnd { Reason = r.ReadByte() };
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Dispatch helper
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Extract the MessageType from a received payload.</summary>
        public static MessageType GetType(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return MessageType.Error;
            return (MessageType)payload[0];
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Binary read/write helpers (no Unity, no BinaryReader GC overhead)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Simple binary writer into a growable byte buffer.</summary>
    public struct NetWriter
    {
        private byte[] _buf;
        private int _pos;

        public NetWriter(int capacity) { _buf = new byte[capacity]; _pos = 0; }

        private void Ensure(int bytes)
        {
            if (_pos + bytes <= _buf.Length) return;
            int newCap = Math.Max(_buf.Length * 2, _pos + bytes);
            var newBuf = new byte[newCap];
            Buffer.BlockCopy(_buf, 0, newBuf, 0, _pos);
            _buf = newBuf;
        }

        public void WriteByte(byte v) { Ensure(1); _buf[_pos++] = v; }

        public void WriteInt32(int v)
        {
            Ensure(4);
            _buf[_pos++] = (byte)(v);
            _buf[_pos++] = (byte)(v >> 8);
            _buf[_pos++] = (byte)(v >> 16);
            _buf[_pos++] = (byte)(v >> 24);
        }

        public void WriteInt64(long v)
        {
            Ensure(8);
            for (int i = 0; i < 8; i++)
                _buf[_pos++] = (byte)(v >> (i * 8));
        }

        public void WriteString(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                WriteInt32(0);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteInt32(bytes.Length);
            Ensure(bytes.Length);
            Buffer.BlockCopy(bytes, 0, _buf, _pos, bytes.Length);
            _pos += bytes.Length;
        }

        public void WriteBytes(byte[] data)
        {
            int len = data?.Length ?? 0;
            WriteInt32(len);
            if (len > 0)
            {
                Ensure(len);
                Buffer.BlockCopy(data, 0, _buf, _pos, len);
                _pos += len;
            }
        }

        public byte[] ToArray()
        {
            var result = new byte[_pos];
            Buffer.BlockCopy(_buf, 0, result, 0, _pos);
            return result;
        }
    }

    /// <summary>Simple binary reader over a byte buffer.</summary>
    public struct NetReader
    {
        private readonly byte[] _buf;
        private int _pos;

        public NetReader(byte[] buf, int offset) { _buf = buf; _pos = offset; }

        public byte ReadByte() => _buf[_pos++];

        public int ReadInt32()
        {
            int v = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24);
            _pos += 4;
            return v;
        }

        public long ReadInt64()
        {
            long v = 0;
            for (int i = 0; i < 8; i++)
                v |= (long)_buf[_pos++] << (i * 8);
            return v;
        }

        public string ReadString()
        {
            int len = ReadInt32();
            if (len <= 0) return string.Empty;
            var s = Encoding.UTF8.GetString(_buf, _pos, len);
            _pos += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            int len = ReadInt32();
            if (len <= 0) return Array.Empty<byte>();
            var data = new byte[len];
            Buffer.BlockCopy(_buf, _pos, data, 0, len);
            _pos += len;
            return data;
        }
    }
}
