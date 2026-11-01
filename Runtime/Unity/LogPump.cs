using System;
using UnityEngine;
using UnityEngine.LowLevel;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Tutan.Logging
{
    /// <summary>
    /// Pumps a drain from Unity's PlayerLoop: at the start of every frame it stamps the store with
    /// <c>Time.frameCount</c>, so every record says the frame it was written in, and at the end of
    /// every frame, after rendering is set up, it pumps the drain, so the sinks see the frame's records
    /// once it is done.
    /// <code>
    /// IDisposable pump = LogPump.Install(drain);
    /// // ...
    /// pump.Dispose();
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installed in Play Mode in the editor, the pump removes itself when Play Mode ends, since Unity
    /// keeps a PlayerLoop that was changed into Edit Mode. That also covers entering Play Mode with
    /// domain reload off: each entry installs a pump of its own, and nothing of the last one is left.
    /// </para>
    /// <para>
    /// In Edit Mode the PlayerLoop does not run every editor frame, so an editor tool pumps its drain
    /// itself: <c>EditorApplication.update += drain.Pump;</c>.
    /// </para>
    /// </remarks>
    public static class LogPump
    {
        /// <summary>Installs the two systems into the PlayerLoop, until what it returns is disposed.</summary>
        public static IDisposable Install(LogDrain drain)
        {
            if (drain is null)
                throw new ArgumentNullException(nameof(drain));

            var pump = new Pump(drain);
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            Insert(ref loop, typeof(UnityEngine.PlayerLoop.EarlyUpdate), new PlayerLoopSystem
            {
                type = typeof(LogFrameStamp),
                updateDelegate = pump.Stamp,
            }, first: true);
            Insert(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate), new PlayerLoopSystem
            {
                type = typeof(LogDrainPump),
                updateDelegate = pump.Drain,
            }, first: false);
            PlayerLoop.SetPlayerLoop(loop);

#if UNITY_EDITOR
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.playModeStateChanged += pump.PlayModeChanged;
#endif
            return pump;
        }

        /// <summary>Adds <paramref name="system"/> to the phase of type <paramref name="phase"/>, first or last among its systems.</summary>
        private static void Insert(ref PlayerLoopSystem loop, Type phase, PlayerLoopSystem system, bool first)
        {
            PlayerLoopSystem[] phases = loop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            for (int i = 0; i < phases.Length; i++)
            {
                if (phases[i].type != phase)
                    continue;

                PlayerLoopSystem[] systems = phases[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var grown = new PlayerLoopSystem[systems.Length + 1];
                Array.Copy(systems, 0, grown, first ? 1 : 0, systems.Length);
                grown[first ? 0 : systems.Length] = system;
                phases[i].subSystemList = grown;
                return;
            }

            throw new InvalidOperationException($"The PlayerLoop has no {phase.Name} phase to pump the log from.");
        }

        /// <summary>Takes out of every phase the systems <paramref name="pump"/> added, by the object their delegates are bound to.</summary>
        private static void Remove(ref PlayerLoopSystem loop, Pump pump)
        {
            PlayerLoopSystem[] phases = loop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            for (int i = 0; i < phases.Length; i++)
            {
                PlayerLoopSystem[]? systems = phases[i].subSystemList;
                if (systems is null)
                    continue;

                int kept = 0;
                foreach (PlayerLoopSystem system in systems)
                {
                    if (!ReferenceEquals(system.updateDelegate?.Target, pump))
                        kept++;
                }

                if (kept == systems.Length)
                    continue;

                var fewer = new PlayerLoopSystem[kept];
                int at = 0;
                foreach (PlayerLoopSystem system in systems)
                {
                    if (!ReferenceEquals(system.updateDelegate?.Target, pump))
                        fewer[at++] = system;
                }

                phases[i].subSystemList = fewer;
            }
        }

        /// <summary>What the Profiler and the PlayerLoop call the system that stamps the frame.</summary>
        private struct LogFrameStamp
        {
        }

        /// <summary>What the Profiler and the PlayerLoop call the system that pumps the drain.</summary>
        private struct LogDrainPump
        {
        }

        private sealed class Pump : IDisposable
        {
            private readonly LogDrain _drain;
            private bool _disposed;

            public Pump(LogDrain drain) => _drain = drain;

            public void Stamp()
            {
                if (!_disposed)
                    _drain.Store.EnterFrame(Time.frameCount);
            }

            public void Drain()
            {
                // Flush, not Pump: a root that disposed its drain before the pump gets no exception every frame.
                if (!_disposed)
                    _drain.Flush();
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                Remove(ref loop, this);
                PlayerLoop.SetPlayerLoop(loop);
#if UNITY_EDITOR
                EditorApplication.playModeStateChanged -= PlayModeChanged;
#endif
            }

#if UNITY_EDITOR
            public void PlayModeChanged(PlayModeStateChange change)
            {
                if (change == PlayModeStateChange.ExitingPlayMode)
                    Dispose();
            }
#endif
        }
    }
}
