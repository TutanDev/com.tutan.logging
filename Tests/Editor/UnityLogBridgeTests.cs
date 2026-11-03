using System;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tutan.Logging.Tests
{
    public class UnityLogBridgeTests
    {
        [Test]
        public void What_unity_logs_is_brought_into_the_store_at_the_level_of_its_type()
        {
            using var captured = new Captured();
            LogAssert.Expect(LogType.Error, "an error from Unity");

            using (UnityLogBridge.Attach(captured.Log))
            {
                Debug.Log("a log from Unity");
                Debug.LogWarning("a warning from Unity");
                Debug.LogError("an error from Unity");
            }

            var logged = captured.Of(UnityLog.Events.Logged);
            Assert.That(logged.Select(r => r.Message), Is.EqualTo(new[] { "a log from Unity", "a warning from Unity", "an error from Unity" }));
            Assert.That(logged.Select(r => r.Level), Is.EqualTo(new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Error }));
            Assert.That(logged.Select(r => r.Field("Type")), Is.EqualTo(new object[] { (long)LogType.Log, (long)LogType.Warning, (long)LogType.Error }));
            Assert.That(logged[2].Field("StackTrace"), Is.Not.Null);
            Assert.That(logged[0].CallSite.IsKnown, Is.False, "Where Unity's log was called from is in its stack trace, not a call site.");
        }

        [Test]
        public void Once_detached_the_bridge_brings_nothing()
        {
            using var captured = new Captured();
            IDisposable bridge = UnityLogBridge.Attach(captured.Log);

            bridge.Dispose();
            bridge.Dispose();
            Debug.Log("after the bridge was detached");

            Assert.That(captured.Of(UnityLog.Events.Logged), Is.Empty);
        }

        [Test]
        public void Rules_hold_unitys_log_to_its_levels()
        {
            using var captured = new Captured("trace; unity=warning");

            using (UnityLogBridge.Attach(captured.Log))
            {
                Debug.Log("too quiet");
                Debug.LogWarning("loud enough");
            }

            Assert.That(captured.Of(UnityLog.Events.Logged).Select(r => r.Message), Is.EqualTo(new[] { "loud enough" }));
        }

        [Test]
        public void What_the_console_sink_writes_is_not_brought_back()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            var memory = new MemorySink();
            LogAssert.Expect(LogType.Log, new Regex("^\\[tests\\.save\\] Saved slot 1 as a\\.sav"));

            using (var drain = new LogDrain(store, (new UnityConsoleSink(openOnDoubleClick: false), new LogSinkOptions(LogLevel.Trace)), (memory, new LogSinkOptions(LogLevel.Trace))))
            using (UnityLogBridge.Attach(store.Log))
            {
                store.Log.Saved(1, "a.sav");
                drain.Pump();
                drain.Pump();
            }

            Assert.That(memory.Records.Of(SaveLog.Events.Saved), Has.Count.EqualTo(1));
            Assert.That(memory.Records.Of(UnityLog.Events.Logged), Is.Empty);
        }

        [Test]
        public void A_default_log_is_refused()
        {
            Assert.That(() => UnityLogBridge.Attach(default), Throws.ArgumentException.With.Message.Contains("never handed a store"));
        }
    }
}
