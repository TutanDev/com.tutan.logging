using System;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogScopesTests
    {
        [Test]
        public void Scopes_are_learned_from_their_openings_and_closings()
        {
            var clock = new TestClock();
            LogStore store = Stores.Everything(clock: clock);
            LogScope outer = store.Log.Saving(1);
            long inner;
            using (LogScope saving = outer.Log.Saving(2))
            {
                inner = saving.Id;
                saving.Log.Step(5);
                clock.Advance(TimeSpan.FromMilliseconds(40));
            }

            LogScopes scopes = Learn(store);

            Assert.That(scopes.Count, Is.EqualTo(2));
            Assert.That(scopes.Parent(inner), Is.EqualTo(outer.Id));
            Assert.That(scopes.Parent(outer.Id), Is.EqualTo(0));
            Assert.That(scopes.IsUnder(inner, outer.Id), Is.True);
            Assert.That(scopes.IsUnder(outer.Id, outer.Id), Is.True);
            Assert.That(scopes.IsUnder(outer.Id, inner), Is.False);
            Assert.That(scopes.IsUnder(inner, 0), Is.False);
            Assert.That(scopes.IsOpen(outer.Id), Is.True);
            Assert.That(scopes.IsOpen(inner), Is.False);
            Assert.That(scopes.Elapsed(inner).Or(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromMilliseconds(40)));
            Assert.That(scopes.Elapsed(outer.Id).IsSome, Is.False);
            outer.Dispose();
        }

        [Test]
        public void A_scope_whose_opening_was_not_read_is_looked_up_in_the_records()
        {
            LogStore store = Stores.Everything();
            long outer;
            long inner;
            long step;
            using (LogScope saving = store.Log.Saving(1))
            using (LogScope nested = saving.Log.Saving(2))
            {
                outer = saving.Id;
                inner = nested.Id;
                step = store.Next;
                nested.Log.Step(5);
            }

            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(step, batch);

            scopes.Learn(batch[0]);

            Assert.That(scopes.Count, Is.EqualTo(2));
            Assert.That(scopes.Parent(inner), Is.EqualTo(outer));
            Assert.That(scopes.IsUnder(inner, outer), Is.True);
            Assert.That(scopes.IsOpen(inner), Is.True);
        }

        [Test]
        public void Asking_about_a_scope_teaches_it_nothing()
        {
            LogStore store = Stores.Everything();
            long outer;
            long inner;
            using (LogScope saving = store.Log.Saving(1))
            using (LogScope nested = saving.Log.Saving(2))
            {
                outer = saving.Id;
                inner = nested.Id;
                nested.Log.Step(5);
            }

            var scopes = new LogScopes(store);

            Assert.That(scopes.Parent(inner), Is.EqualTo(0));
            Assert.That(scopes.IsUnder(inner, outer), Is.False);
            Assert.That(scopes.IsUnder(inner, inner), Is.True);
            Assert.That(scopes.IsOpen(inner), Is.False);
            Assert.That(scopes.Elapsed(inner).IsSome, Is.False);
            Assert.That(scopes.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_closing_says_where_an_opening_not_read_belongs()
        {
            var clock = new TestClock();
            LogStore store = Stores.Everything(Stores.Tiny, clock);
            long outer;
            long inner;
            using (LogScope saving = store.Log.Saving(1))
            {
                outer = saving.Id;
                using LogScope nested = saving.Log.Saving(2);
                inner = nested.Id;
                for (int i = 0; i < 20; i++)
                    nested.Log.Step(i);
                clock.Advance(TimeSpan.FromMilliseconds(3));
            }

            LogScopes scopes = Learn(store);

            Assert.That(store.First, Is.GreaterThan(inner), "The store no longer holds either opening.");
            Assert.That(scopes.Parent(inner), Is.EqualTo(outer));
            Assert.That(scopes.Elapsed(inner).Or(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromMilliseconds(3)));
            Assert.That(scopes.IsUnder(inner, outer), Is.True);
            Assert.That(scopes.Elapsed(outer).Or(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromMilliseconds(3)));
        }

        [Test]
        public void A_scope_the_records_do_not_hold_is_under_nothing()
        {
            var scopes = new LogScopes(Stores.Everything());

            Assert.That(scopes.Parent(99), Is.EqualTo(0));
            Assert.That(scopes.IsOpen(99), Is.False);
            Assert.That(scopes.Elapsed(99).IsSome, Is.False);
            Assert.That(scopes.IsUnder(99, 1), Is.False);
            Assert.That(scopes.IsUnder(99, 99), Is.True);
        }

        [Test]
        public void Scopes_the_records_no_longer_hold_are_forgotten()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            long next = store.First;
            LogScope open = store.Log.Saving(0);
            for (int i = 0; i < 200; i++)
            {
                using (LogScope saving = open.Log.Saving(i))
                    saving.Log.Step(i);

                LogRead read = store.Read(next, batch);
                for (int r = 0; r < batch.Count; r++)
                    scopes.Learn(batch[r]);
                next = read.Next;
                scopes.Prune();
            }

            Assert.That(scopes.Count, Is.InRange(2, 16));
            Assert.That(scopes.IsOpen(open.Id), Is.True);
            open.Dispose();
        }

        [Test]
        public void Scopes_are_learned_alike_from_an_archive()
        {
            var clock = new TestClock();
            var archive = new LogArchive("Tests", default, cap: 100);
            LogStore store = Stores.Everything(clock: clock);
            LogScope outer = store.Log.Saving(1);
            long inner;
            using (LogScope saving = outer.Log.Saving(2))
            {
                inner = saving.Id;
                clock.Advance(TimeSpan.FromMilliseconds(7));
            }

            var batch = new LogBatch();
            store.Read(store.First, batch);
            for (int i = 0; i < batch.Count; i++)
                archive.Keep(batch[i], backlog: false);

            var scopes = new LogScopes(archive);
            archive.Read(archive.First, batch);
            for (int i = 0; i < batch.Count; i++)
                scopes.Learn(batch[i]);

            Assert.That(scopes.Parent(inner), Is.EqualTo(outer.Id));
            Assert.That(scopes.IsOpen(outer.Id), Is.True);
            Assert.That(scopes.Elapsed(inner).Or(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromMilliseconds(7)));
            outer.Dispose();
        }

        /// <summary>Scopes that have learned every record the store holds.</summary>
        private static LogScopes Learn(LogStore store)
        {
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);
            for (int i = 0; i < batch.Count; i++)
                scopes.Learn(batch[i]);

            return scopes;
        }
    }
}
