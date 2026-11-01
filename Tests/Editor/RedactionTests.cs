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
    }
}
