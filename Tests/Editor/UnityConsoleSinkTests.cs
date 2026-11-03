using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tutan.Logging.Tests
{
    public class UnityConsoleSinkTests
    {
        // The second line of every row: a link to the call site in this file.
        private const string Link = @"\n<a href=""Packages/com\.tutan\.logging/Tests/Editor/UnityConsoleSinkTests\.cs"" line=""\d+"">UnityConsoleSinkTests\.cs:\d+</a>";

        [Test]
        public void A_row_is_the_category_in_brackets_the_message_and_a_link_to_the_call_site()
        {
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.save\\] Saved slot 3 as slot3\\.sav{Link}$"));

            Write(log => log.Saved(3, "slot3.sav"));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void A_warning_is_a_warning_row_and_an_error_an_error_row_with_its_exception_whole()
        {
            LogAssert.Expect(LogType.Warning, new Regex($"^\\[tests\\.heard\\] Heard careful{Link}$"));
            LogAssert.Expect(LogType.Error, new Regex($"^\\[tests\\.save\\] Saving slot 1 failed\\nSystem\\.InvalidOperationException: The disk is full\\.{Link}$"));
            LogAssert.Expect(LogType.Error, new Regex($"^\\[tests\\.heard\\] Heard worse{Link}$"));

            Write(log =>
            {
                log.Heard(LogLevel.Warning, "careful");
                log.Failed(1, new InvalidOperationException("The disk is full."));
                log.Heard(LogLevel.Critical, "worse");
            });

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void A_row_says_what_it_is_under_how_many_a_limit_dropped_and_its_frame()
        {
            var clock = new TestClock();
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.save\\] Saving slot 1{Link}  frame 7$"));
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.save\\] Saved slot 1 as a\\.sav  < Saving slot 1{Link}  frame 7$"));
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.save\\] Saving slot 1: done in 12\\.5 ms{Link}  frame 7$"));
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.limits\\] Hit 0{Link}  frame 7$"));
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.limits\\] Hit 1{Link}  frame 7$"));
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.limits\\] Hit 4  \\(\\+2\\){Link}  frame 7$"));

            Write(log =>
            {
                using (LogScope saving = log.Saving(1))
                {
                    saving.Log.Saved(1, "a.sav");
                    // 12.5 ms: on Mono, TimeSpan.FromMilliseconds rounds to a whole millisecond.
                    clock.Advance(TimeSpan.FromTicks(125_000));
                }

                for (int i = 0; i < 4; i++)
                    log.Hit(i);
                clock.Advance(TimeSpan.FromSeconds(1));
                log.Hit(4);
            }, clock: clock, frame: 7);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void An_errors_context_is_marked_as_context()
        {
            LogAssert.Expect(LogType.Log, new Regex($"^\\(context\\) \\[tests\\.save\\] Step 1{Link}$"));
            LogAssert.Expect(LogType.Error, new Regex("^\\[tests\\.save\\] Saving slot 1 failed"));

            Write(log =>
            {
                log.Step(1);
                log.Failed(1, new InvalidOperationException("The disk is full."));
            }, new LogSinkOptions(LogLevel.Error, Backlog: 2));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void A_sink_that_redacts_writes_personal_fields_and_exceptions_as_personal()
        {
            LogAssert.Expect(LogType.Error, new Regex($"^\\[tests\\.people\\] Lost \\[personal\\]\\n\\[personal\\]{Link}$"));

            Write(log => log.Lost("ann@example.com", new InvalidOperationException("ann@example.com is gone")),
                new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void What_the_bridge_brought_from_unitys_log_is_not_written_back_to_it()
        {
            LogAssert.Expect(LogType.Log, new Regex("^\\[tests\\.save\\] Saved slot 1"));

            Write(log =>
            {
                log.Logged(LogLevel.Error, "from Unity", "", LogType.Error);
                log.Saved(1, "a.sav");
            });

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Records_the_console_lost_are_a_warning_row()
        {
            LogAssert.Expect(LogType.Warning, "1,234 records were overwritten before the Console read them");

            new UnityConsoleSink(openOnDoubleClick: false).Lost(1234, null!);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void A_row_written_to_open_on_double_click_still_reaches_the_console()
        {
            LogAssert.Expect(LogType.Log, new Regex($"^\\[tests\\.save\\] Saved slot 5 as slot5\\.sav{Link}$"));

            Write(log => log.Saved(5, "slot5.sav"), openOnDoubleClick: true);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void A_sink_without_the_call_site_line_writes_the_first_line_alone()
        {
            LogAssert.Expect(LogType.Log, "[tests.save] Saved slot 3 as slot3.sav");
            LogAssert.Expect(LogType.Error, "[tests.save] Saving slot 1 failed\nSystem.InvalidOperationException: The disk is full.");

            Write(log =>
            {
                log.Saved(3, "slot3.sav");
                log.Failed(1, new InvalidOperationException("The disk is full."));
            }, frame: 7, callSiteLine: false);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void A_row_without_the_call_site_line_written_to_open_on_double_click_still_reaches_the_console()
        {
            LogAssert.Expect(LogType.Log, "[tests.save] Saved slot 5 as slot5.sav");

            Write(log => log.Saved(5, "slot5.sav"), openOnDoubleClick: true, callSiteLine: false);

            LogAssert.NoUnexpectedReceived();
        }

        [TestCase(LogLevel.Trace, LogType.Log)]
        [TestCase(LogLevel.Debug, LogType.Log)]
        [TestCase(LogLevel.Information, LogType.Log)]
        [TestCase(LogLevel.Warning, LogType.Warning)]
        [TestCase(LogLevel.Error, LogType.Error)]
        [TestCase(LogLevel.Critical, LogType.Error)]
        public void A_level_is_written_as_a_row_of_its_type(LogLevel level, LogType type)
        {
            Assert.That(UnityConsoleSink.TypeOf(level), Is.EqualTo(type));
        }

        private static void Write(Action<Log> write, LogSinkOptions? options = null, TestClock? clock = null, long frame = 0, bool openOnDoubleClick = false, bool callSiteLine = true)
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"), clock is null ? (Func<long>?)null : clock.Read);
            store.EnterFrame(frame);
            using var drain = new LogDrain(store, (new UnityConsoleSink(openOnDoubleClick, callSiteLine: callSiteLine), options ?? new LogSinkOptions(LogLevel.Trace)));
            write(store.Log);
        }
    }
}
