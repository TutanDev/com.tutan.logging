using System;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogCatalogTests
    {
        private static readonly LogSource s_here = new LogSource("Tests", "Tests.Here", "Here.cs", 1);
        private static readonly LogSource s_there = new LogSource("Other", "Other.There", "There.cs", 1);

        [Test]
        public void The_generated_classes_registered_their_events_when_the_domain_loaded()
        {
            Assert.That(LogCatalog.Events, Has.Member(SaveLog.Events.Saved));
            Assert.That(LogCatalog.Events, Has.Member(FieldLog.Events.Numbers));
            Assert.That(SaveLog.Events.Saved.Index, Is.GreaterThanOrEqualTo(0));
            Assert.That(LogCatalog.Events[SaveLog.Events.Saved.Index], Is.SameAs(SaveLog.Events.Saved));
            Assert.That(LogCatalog.Count, Is.EqualTo(LogCatalog.Events.Count));
        }

        [Test]
        public void The_packages_own_events_are_registered_first()
        {
            Assert.That(LogCatalog.Events[0], Is.SameAs(SelfLog.Events.DrainFellBehind));
            Assert.That(LogCatalog.Find("unity/Logged").ValueUnsafe(), Is.SameAs(UnityLog.Events.Logged));
        }

        [Test]
        public void A_spec_is_found_by_its_key()
        {
            Assert.That(LogCatalog.Find("tests.save/Saved").ValueUnsafe(), Is.SameAs(SaveLog.Events.Saved));
            Assert.That(LogCatalog.Find("tests.limits/Struck").ValueUnsafe(), Is.SameAs(LimitLog.Events.Struck));
            Assert.That(LogCatalog.Find("tests.save/Nothing").IsNone, Is.True);
        }

        [Test]
        public void A_spec_describes_its_event()
        {
            LogEventSpec saved = SaveLog.Events.Saved;

            Assert.That(saved.Category, Is.EqualTo("tests.save"));
            Assert.That(saved.Name, Is.EqualTo("Saved"));
            Assert.That(saved.Key, Is.EqualTo("tests.save/Saved"));
            Assert.That(saved.Kind, Is.EqualTo(LogEventKind.Event));
            Assert.That(saved.Level.ValueUnsafe(), Is.EqualTo(LogLevel.Information));
            Assert.That(saved.PerSecond, Is.EqualTo(0));
            Assert.That(saved.Fields.Select(f => f.ToString()), Is.EqualTo(new[] { "Slot: int", "File: string" }));
            Assert.That(saved.Source.Assembly, Is.EqualTo("Tutan.Logging.Editor.Tests"));
            Assert.That(saved.Source.Type, Is.EqualTo("Tutan.Logging.Tests.SaveLog"));
            Assert.That(saved.Source.File, Does.EndWith("TestEvents.cs"));
            Assert.That(SaveLog.Events.Saving.Kind, Is.EqualTo(LogEventKind.Scope));
            Assert.That(HeardLog.Events.Heard.Level.IsNone, Is.True);
            Assert.That(LimitLog.Events.Hit.PerSecond, Is.EqualTo(2));
            Assert.That(PeopleLog.Events.SignedIn.Fields[0].Personal, Is.True);
            Assert.That(PeopleLog.Events.SignedIn.Fields[1].Tag.ValueUnsafe(), Is.EqualTo("path"));
        }

        [Test]
        public void Registering_gives_the_next_index_and_passes_over_a_spec_already_registered()
        {
            var registry = new LogRegistry();
            LogEventSpec first = Spec("tests.registry", "First", s_here);
            LogEventSpec second = Spec("tests.registry", "Second", s_here);

            registry.Register(new[] { first });
            registry.Register(new[] { first, second });

            Assert.That(registry.EventArray, Is.EqualTo(new[] { first, second }));
            Assert.That(first.Index, Is.EqualTo(0));
            Assert.That(second.Index, Is.EqualTo(1));
            Assert.That(registry.Conflicts, Is.Empty);
        }

        [Test]
        public void A_key_another_class_has_is_a_conflict_and_the_first_is_still_the_one_found()
        {
            var registry = new LogRegistry();
            LogEventSpec mine = Spec("tests.keys", "Saved", s_here);
            LogEventSpec theirs = Spec("tests.keys", "Saved", s_there);

            registry.Register(new[] { mine });
            registry.Register(new[] { theirs });

            Assert.That(registry.Find("tests.keys/Saved").ValueUnsafe(), Is.SameAs(mine));
            Assert.That(theirs.Index, Is.EqualTo(1), "A spec in conflict still gets an index, so its records are written.");
            Assert.That(registry.Conflicts, Is.EqualTo(new LogCatalogConflict[]
            {
                new LogCatalogConflict.CategoryTwice("tests.keys", s_here, s_there),
                new LogCatalogConflict.KeyTwice("tests.keys/Saved", s_here, s_there),
            }));
            Assert.That(registry.Conflicts[1].ToString(), Is.EqualTo("tests.keys/Saved is declared by Tests.Here in Tests and again by Other.There in Other"));
        }

        [Test]
        public void A_category_another_class_declares_is_one_conflict_however_many_of_its_events_register()
        {
            var registry = new LogRegistry();

            registry.Register(new[] { Spec("tests.shared", "A", s_here) });
            registry.Register(new[] { Spec("tests.shared", "B", s_there), Spec("tests.shared", "C", s_there) });

            Assert.That(registry.Conflicts, Has.Count.EqualTo(1));
            Assert.That(registry.Conflicts[0], Is.EqualTo(new LogCatalogConflict.CategoryTwice("tests.shared", s_here, s_there)));
        }

        [Test]
        public void A_key_the_same_class_has_again_is_the_new_specs_as_a_spec_made_again_after_a_reload()
        {
            var registry = new LogRegistry();
            LogEventSpec before = Spec("tests.again", "Saved", s_here);
            LogEventSpec after = Spec("tests.again", "Saved", s_here);

            registry.Register(new[] { before });
            registry.Register(new[] { after });

            Assert.That(registry.Find("tests.again/Saved").ValueUnsafe(), Is.SameAs(after));
            Assert.That(registry.Conflicts, Is.Empty);
        }

        [Test]
        public void A_registration_with_a_null_spec_registers_none_of_them()
        {
            var registry = new LogRegistry();
            LogEventSpec first = Spec("tests.whole", "Saved", s_here);

            Assert.That(() => registry.Register(new[] { first, null! }), Throws.ArgumentNullException.With.Message.Contains("The spec at 1 is null"));
            Assert.That(first.Index, Is.EqualTo(-1));
            Assert.That(registry.EventArray, Is.Empty);
            Assert.That(registry.Find("tests.whole/Saved").IsNone, Is.True);
        }

        [Test]
        public void A_registration_with_an_event_a_file_declares_registers_none_of_them()
        {
            var registry = new LogRegistry();
            LogEventSpec first = Spec("tests.whole", "Saved", s_here);
            LogEventSpec read = LogEventSpec.OfFile("tests.whole", "Read", LogEventKind.Event, F.Some(LogLevel.Information), "Read",
                Array.Empty<LogField>(), 0, s_here).ValueUnsafe();

            Assert.That(() => registry.Register(new[] { first, read }),
                Throws.ArgumentException.With.Message.Contains("tests.whole/Read was read back from a file"));
            Assert.That((first.Index, read.Index), Is.EqualTo((-1, -1)));
            Assert.That(registry.EventArray, Is.Empty);
        }

        [Test]
        public void A_spec_given_twice_in_one_registration_is_registered_once()
        {
            var registry = new LogRegistry();
            LogEventSpec first = Spec("tests.twice", "Saved", s_here);
            LogEventSpec second = Spec("tests.twice", "Loaded", s_here);

            registry.Register(new[] { first, second, first });

            Assert.That(registry.EventArray, Is.EqualTo(new[] { first, second }));
            Assert.That((first.Index, second.Index), Is.EqualTo((0, 1)));
        }

        [TestCase("Tests.Bad", "Saved", "not a category")]
        [TestCase("tests.bad", "9Saved", "not an event's name")]
        [TestCase("tests.bad", "Saved", "has no field of that name", "{Missing}")]
        [TestCase("tests.bad", "Saved", "does not parse", "{Slot")]
        public void A_malformed_declaration_throws_saying_why(string category, string name, string why, string template = "Saved")
        {
            Assert.That(
                () => LogEventSpec.Declare(category, name, LogEventKind.Event, LogLevel.Information, template,
                    new[] { LogField.Value<int>("Slot", LogValueKind.Int32, "int") }, 0, s_here),
                Throws.ArgumentException.With.Message.Contains(why));
        }

        [Test]
        public void A_scope_must_have_a_level_and_nothing_is_declared_at_none()
        {
            Assert.That(() => LogEventSpec.Declare("tests.bad", "Saving", LogEventKind.Scope, null, "Saving", Array.Empty<LogField>(), 0, s_here),
                Throws.ArgumentException.With.Message.Contains("scope with no level"));
            Assert.That(() => LogEventSpec.Declare("tests.bad", "Saved", LogEventKind.Event, LogLevel.None, "Saved", Array.Empty<LogField>(), 0, s_here),
                Throws.ArgumentException.With.Message.Contains("None"));
        }

        [Test]
        public void Two_fields_of_one_name_are_refused()
        {
            Assert.That(
                () => LogEventSpec.Declare("tests.bad", "Saved", LogEventKind.Event, LogLevel.Information, "Saved",
                    new[] { LogField.Value<int>("Slot", LogValueKind.Int32, "int"), LogField.Value<int>("slot", LogValueKind.Int32, "int") }, 0, s_here),
                Throws.ArgumentException.With.Message.Contains("two fields named"));
        }

        [Test]
        public void A_field_held_as_another_type_than_its_kind_says_is_refused()
        {
            Assert.That(() => LogField.Value<long>("Slot", LogValueKind.Int32, "int"), Throws.ArgumentException);
            Assert.That(() => LogField.Ref("Slot", LogValueKind.Int32, "int"), Throws.ArgumentException);
        }

        private static LogEventSpec Spec(string category, string name, LogSource source) =>
            LogEventSpec.Declare(category, name, LogEventKind.Event, LogLevel.Information, name, Array.Empty<LogField>(), 0, source);
    }
}
