// Zero Unity dependencies. Pure .NET.
// This bridges the network layer to the game's logic world command pipeline.
// It references Game.Command types but NOT UnityEngine.
using System;
using System.Collections.Concurrent;
using BurstStrike.Net.Shared.Protocol;

namespace BurstStrike.Net.Client
{
    /// <summary>
    /// Bridges network TickCommands to the game's logic world.
    /// 
    /// Usage:
    ///   1. Each frame (main thread), call ProcessReceivedTicks().
    ///   2. This decodes commands from network format and enqueues them
    ///      into the LogicWorld via the command injection delegate.
    ///   3. The LogicWorld executes them at the correct tick.
    /// 
    /// This class does NOT depend on Unity. The Unity-side glue (World.cs)
    /// provides the injection delegate.
    /// 
    /// Data flow:
    ///   Server → NetSyncClient.ReceivedTicks → NetSyncBridge.ProcessReceivedTicks() →
    ///   DecodeAndInject delegate → World.ReceiveEncodedCommand() → LogicWorld
    /// </summary>
    public sealed class NetSyncBridge
    {
        private readonly NetSyncClient _client;

        /// <summary>
        /// Delegate to inject a single encoded command (byte[]) into the game world
        /// with a specific tick and sequence.
        /// Signature: (byte[] encodedCommand, int tick, int sequence) → void
        /// 
        /// The game layer (World.cs) provides this. Example:
        ///   bridge.InjectCommand = (bytes, tick, seq) => world.ReceiveEncodedCommand(bytes, tick, seq);
        /// </summary>
        public Action<byte[], int, int> InjectCommand;

        /// <summary>
        /// Delegate to inject a "tick barrier" — signals LogicWorld that tick N's
        /// commands are complete and it may advance. Used for strict lockstep.
        /// Signature: (int tick) → void
        /// </summary>
        public Action<int> OnTickReady;

        /// <summary>Latest tick that has been fully processed.</summary>
        public int LastProcessedTick { get; private set; } = -1;

        public NetSyncBridge(NetSyncClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Call from main thread each frame.
        /// Drains all received ticks and injects their commands into the game world.
        /// Returns the number of ticks processed.
        /// </summary>
        public int ProcessReceivedTicks()
        {
            int processed = 0;
            var inject = InjectCommand;
            if (inject == null) return 0;

            while (_client.ReceivedTicks.TryDequeue(out var tickCmd))
            {
                int tick = tickCmd.Tick;

                if (tickCmd.Inputs != null)
                {
                    int globalSeq = 0;
                    for (int i = 0; i < tickCmd.Inputs.Length; i++)
                    {
                        var playerInput = tickCmd.Inputs[i];
                        if (playerInput.Commands == null) continue;

                        for (int j = 0; j < playerInput.Commands.Length; j++)
                        {
                            var cmdBytes = playerInput.Commands[j];
                            if (cmdBytes == null || cmdBytes.Length == 0) continue;

                            // Inject with deterministic ordering:
                            // tick from server, sequence = player*1000 + command index
                            int seq = playerInput.PlayerId * 1000 + globalSeq++;
                            inject(cmdBytes, tick, seq);
                        }
                    }
                }

                LastProcessedTick = tick;
                OnTickReady?.Invoke(tick);
                processed++;
            }

            return processed;
        }

        /// <summary>
        /// Encode and send local commands for the current tick.
        /// Called by the game layer once per local tick.
        /// </summary>
        public void SendLocalCommands(int localTick, byte[][] encodedCommands)
        {
            _client.SendTickInput(localTick, encodedCommands);
        }
    }
}
