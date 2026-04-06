// Zero Unity dependencies. Pure .NET.
using System;
using BurstStrike.Net.Shared.Protocol;

namespace BurstStrike.Net.Server.Tick
{
    /// <summary>
    /// Ring buffer of TickCommands for historical tick storage.
    /// Used for:
    /// 1) Reconnection — client requests missed ticks, server replays from cache.
    /// 2) Replay recording — full game can be reconstructed from command history.
    /// 
    /// Thread-safe for single-writer (tick thread) + single-reader (reconnect handler).
    /// </summary>
    public sealed class CommandHistory
    {
        private readonly Messages.TickCommands[] _buffer;
        private readonly int _capacity;
        private int _head;   // next write position
        private int _count;  // number of valid entries
        private int _minTick; // lowest tick still in buffer

        /// <summary>
        /// Create a history buffer.
        /// capacity = max ticks to retain. At 30 tick/s, 18000 = 10 minutes.
        /// </summary>
        public CommandHistory(int capacity = 18000)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _buffer = new Messages.TickCommands[capacity];
        }

        /// <summary>Number of ticks currently stored.</summary>
        public int Count => _count;

        /// <summary>Lowest tick number still available.</summary>
        public int MinTick => _minTick;

        /// <summary>Highest tick number stored (inclusive).</summary>
        public int MaxTick => _count == 0 ? -1 : _minTick + _count - 1;

        /// <summary>Store a tick's merged commands.</summary>
        public void Store(in Messages.TickCommands tc)
        {
            _buffer[_head] = tc;
            _head = (_head + 1) % _capacity;

            if (_count < _capacity)
            {
                if (_count == 0)
                    _minTick = tc.Tick;
                _count++;
            }
            else
            {
                // Buffer full — oldest entry overwritten, minTick advances.
                _minTick++;
            }
        }

        /// <summary>
        /// Retrieve a range of ticks [fromTick, toTick] (inclusive).
        /// Returns null if any tick in range is not available.
        /// </summary>
        public Messages.TickCommands[] GetRange(int fromTick, int toTick)
        {
            if (_count == 0) return null;
            if (fromTick < _minTick || toTick > MaxTick) return null;
            if (fromTick > toTick) return null;

            int rangeCount = toTick - fromTick + 1;
            var result = new Messages.TickCommands[rangeCount];

            for (int i = 0; i < rangeCount; i++)
            {
                int tick = fromTick + i;
                int offset = tick - _minTick;
                int idx = (_head - _count + offset + _capacity) % _capacity;
                result[i] = _buffer[idx];
            }

            return result;
        }

        /// <summary>Try to get a single tick's commands.</summary>
        public bool TryGet(int tick, out Messages.TickCommands tc)
        {
            tc = default;
            if (_count == 0 || tick < _minTick || tick > MaxTick)
                return false;

            int offset = tick - _minTick;
            int idx = (_head - _count + offset + _capacity) % _capacity;
            tc = _buffer[idx];
            return true;
        }

        /// <summary>Clear all history.</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}
