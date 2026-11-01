using System;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Tutan.Logging.Tests
{
    public class LogPumpTests
    {
        private PlayerLoopSystem _before;

        [SetUp]
        public void KeepPlayerLoop() => _before = PlayerLoop.GetCurrentPlayerLoop();

        [TearDown]
        public void RestorePlayerLoop() => PlayerLoop.SetPlayerLoop(_before);

        [Test]
        public void Installing_stamps_the_frame_first_in_early_update_and_drains_last_in_post_late_update()
        {
            using LogDrain drain = Drain(new LogStore(LogRules.Default, new LogStoreOptions(Program: "Tests")), new MemorySink());

            using (LogPump.Install(drain))
            {
                PlayerLoopSystem[] early = Phase<EarlyUpdate>();
                PlayerLoopSystem[] late = Phase<PostLateUpdate>();

                Assert.That(early[0].type.Name, Is.EqualTo("LogFrameStamp"));
                Assert.That(late[late.Length - 1].type.Name, Is.EqualTo("LogDrainPump"));
            }
        }

        [Test]
        public void The_systems_stamp_the_frame_and_drain_the_store()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            var memory = new MemorySink();
            using LogDrain drain = Drain(store, memory);

            using (LogPump.Install(drain))
            {
                Phase<EarlyUpdate>()[0].updateDelegate();
                store.Log.Saved(1, "a.sav");
                PlayerLoopSystem[] late = Phase<PostLateUpdate>();
                late[late.Length - 1].updateDelegate();
            }

            MemoryRecord saved = memory.Records.Of(SaveLog.Events.Saved).Single();
            Assert.That(saved.Frame, Is.EqualTo(Time.frameCount));
            Assert.That(store.Frame, Is.EqualTo(Time.frameCount));
        }

        [Test]
        public void Disposing_takes_out_only_its_own_systems()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Tests"));
            int earlyBefore = Phase<EarlyUpdate>().Length;
            int lateBefore = Phase<PostLateUpdate>().Length;

            IDisposable first = LogPump.Install(Drain(store, new MemorySink("first")));
            IDisposable second = LogPump.Install(Drain(store, new MemorySink("second")));
            first.Dispose();
            first.Dispose();

            Assert.That(Phase<EarlyUpdate>().Length, Is.EqualTo(earlyBefore + 1));
            Assert.That(Phase<PostLateUpdate>().Length, Is.EqualTo(lateBefore + 1));

            second.Dispose();

            Assert.That(Phase<EarlyUpdate>().Length, Is.EqualTo(earlyBefore));
            Assert.That(Phase<PostLateUpdate>().Length, Is.EqualTo(lateBefore));
            Assert.That(Phase<EarlyUpdate>().Any(s => s.type?.Name == "LogFrameStamp"), Is.False);
        }

        [Test]
        public void A_pump_whose_drain_was_disposed_does_nothing()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Tests"));
            LogDrain drain = Drain(store, new MemorySink());

            using (LogPump.Install(drain))
            {
                drain.Dispose();
                PlayerLoopSystem[] late = Phase<PostLateUpdate>();

                Assert.That(() => late[late.Length - 1].updateDelegate(), Throws.Nothing);
            }
        }

        [Test]
        public void A_null_drain_is_refused()
        {
            Assert.That(() => LogPump.Install(null!), Throws.ArgumentNullException);
        }

        private static LogDrain Drain(LogStore store, MemorySink sink) => new LogDrain(store, (sink, new LogSinkOptions(LogLevel.Trace)));

        private static PlayerLoopSystem[] Phase<T>()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            return loop.subSystemList.Single(s => s.type == typeof(T)).subSystemList;
        }
    }
}
