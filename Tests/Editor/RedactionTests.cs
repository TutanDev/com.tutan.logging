using System;
using System.Linq;
using NUnit.Framework;

namespace Tutan.Logging.Tests
{
    public class RedactionTests
    {
        private static readonly string s_home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');

        [Test]
        public void A_sink_that_redacts_personal_fields_writes_them_as_personal()
        {
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal));

            captured.Log.SignedIn("ann@example.com", "C:/Saves");
            captured.Log.Lost("ann@example.com", new InvalidOperationException("ann@example.com is gone"));

            Assert.That(captured.Of(PeopleLog.Events.SignedIn).Single().Message, Is.EqualTo("Signed in as [personal] from C:/Saves"));
            Assert.That(captured.Of(PeopleLog.Events.Lost).Single().Message, Is.EqualTo("Lost [personal]"));
        }

        [Test]
        public void A_sink_that_does_not_redact_writes_personal_fields_as_they_are()
        {
            using var captured = new Captured();

            captured.Log.SignedIn("ann@example.com", "C:/Saves");

            Assert.That(captured.Of(PeopleLog.Events.SignedIn).Single().Message, Is.EqualTo("Signed in as ann@example.com from C:/Saves"));
        }

        [Test]
        public void A_sink_that_redacts_the_home_folder_writes_it_as_a_tilde()
        {
            Assume.That(s_home, Is.Not.Empty, "This platform has no home folder.");
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.HomeFolder));

            captured.Log.SignedIn("ann", s_home + "/Saves");
            captured.Log.SignedIn("ann", s_home);
            captured.Log.SignedIn("ann", s_home + "x/Saves");

            Assert.That(captured.Of(PeopleLog.Events.SignedIn).Select(r => r.Message), Is.EqualTo(new[]
            {
                "Signed in as ann from ~/Saves",
                "Signed in as ann from ~",
                $"Signed in as ann from {s_home}x/Saves",
            }), "A folder whose name only starts with the home folder's is not the home folder.");
        }

        [Test]
        public void The_home_folder_is_found_anywhere_in_text_and_on_windows_with_either_slash_and_any_case()
        {
            Assume.That(s_home, Is.Not.Empty, "This platform has no home folder.");
            bool windows = System.IO.Path.DirectorySeparatorChar == '\\';
            string other = windows ? s_home.Replace('\\', '/').ToUpperInvariant() : s_home;
            char[] text = $"from {s_home}\\a and {other}/b".ToCharArray();

            int length = LogText.WithoutHome(text);

            Assert.That(new string(text, 0, length), Is.EqualTo("from ~\\a and ~/b"));
        }

        [Test]
        public void A_context_gives_a_values_text_as_its_sink_leaves_it_out_and_an_exception_whole()
        {
            LogStore store = Stores.Everything();
            store.Log.Lost("ann@example.com", new InvalidOperationException("Ann is gone."));
            store.Log.Failed(1, new System.IO.IOException($"{s_home}/Saves/A.sav is locked."));
            var batch = new LogBatch();
            store.Read(store.First, batch);
            LogRecord lost = batch[0];
            LogRecord failed = batch[1];
            LogContext everything = Context(store, LogRedaction.None);
            LogContext personal = Context(store, LogRedaction.Personal);
            LogContext home = Context(store, LogRedaction.HomeFolder);

            Assert.That(everything.Text(lost.Field(0)), Is.EqualTo("ann@example.com"));
            Assert.That(everything.Text(failed.Field(1)), Is.EqualTo(failed.Field(1).AsRef()!.ToString()), "An exception's whole text: its type, its message and any stack.");
            Assert.That(everything.Text(failed.Field(1)), Does.StartWith("System.IO.IOException: "));
            Assert.That(new[] { personal.Text(lost.Field(0)), personal.Text(lost.Field(1)), personal.Text(failed.Field(0)) },
                Is.EqualTo(new[] { "[personal]", "[personal]", "1" }));
            if (s_home.Length > 0)
            {
                Assert.That(home.Text(failed.Field(1)), Does.Contain("~/Saves/A.sav is locked."));
                Assert.That(home.Text(failed.Field(1)), Does.Not.Contain(s_home));
                Assert.That(home.Text(failed.Field(0)), Is.EqualTo("1"));
            }
        }

        private static LogContext Context(LogStore store, LogRedaction redact) =>
            new LogContext(store, new LogSinkOptions(LogLevel.Trace, Redact: redact), new ScopeTable());
    }
}
