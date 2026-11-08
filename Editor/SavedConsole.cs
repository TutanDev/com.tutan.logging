using System;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// What the console window saves of its state, as Unity serializes an editor window's fields: so
    /// it survives a domain reload, as when Play Mode starts, and the editor's layout keeps it after a
    /// restart. The query line, the filter the rows were of, which a query that does not read keeps,
    /// and whether it follows; the session shown, its folds and its selection are the process's, and
    /// are not saved.
    /// </summary>
    /// <remarks>
    /// A data transfer object: its fields are what Unity writes, and <see cref="Version"/> says which
    /// shape they have. It reads every version it ever wrote, each as that version wrote it, and a
    /// version it never wrote, as a newer package's, as nothing saved.
    /// </remarks>
    [Serializable]
    internal sealed class SavedConsole
    {
        /// <summary>The version this writes.</summary>
        public const int Current = 1;

        /// <summary>The shape of the fields: 0 for nothing saved, as a window's first opening has.</summary>
        public int Version;

        /// <summary>The text in the query line.</summary>
        public string Query = "";

        /// <summary>The filter the rows were of, as <see cref="LogFilter.ToString"/> writes it.</summary>
        public string Filter = "";

        /// <summary>Whether the window followed the newest row.</summary>
        public bool Following = true;

        /// <summary>What is saved of <paramref name="state"/>.</summary>
        public static SavedConsole FromDomain(ConsoleState state)
        {
            if (state is null)
                throw new ArgumentNullException(nameof(state));

            return new SavedConsole { Version = Current, Query = state.Query.Text, Filter = state.Filter.ToString(), Following = state.Following };
        }

        /// <summary>
        /// The state saved, read as the version that saved it wrote it: none when nothing was, or when
        /// a version this never wrote saved it, so the window starts afresh. A version that changes
        /// the fields gets an arm of its own here, and every earlier arm stays.
        /// </summary>
        public Optional<ConsoleState> ToDomain() => Version switch
        {
            1 => ConsoleState.Restored(Query ?? "", Filter ?? "", Following),
            _ => F.None,
        };
    }
}
