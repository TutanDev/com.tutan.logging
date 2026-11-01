using System;
using System.ComponentModel;

namespace Tutan.Logging
{
    /// <summary>
    /// An open scope, which closes when it is disposed. It is a value: what is written through its
    /// <see cref="Log"/> is under it, and nothing else is, on any thread, so there is no ambient
    /// state to carry across an <c>await</c>.
    /// <code>
    /// using LogScope saving = log.Saving(slot);
    /// saving.Log.Saved(slot, elapsed);   // under "Saving slot 3"
    /// </code>
    /// </summary>
    /// <remarks>
    /// Work that outlives the call that opened the scope is handed <see cref="Log"/>, and whoever
    /// finishes the work disposes the scope, so its closing says how long the whole took. A scope that
    /// is off, because its event is off or a limit dropped it, hands back the <see cref="Tutan.Logging.Log"/>
    /// it was opened from, and disposing it does nothing. Disposing a scope twice, or a copy of it,
    /// closes it once.
    /// </remarks>
    public readonly struct LogScope : IDisposable
    {
        private readonly Log _from;
        private readonly long _id;

        internal LogScope(Log from, long id)
        {
            _from = from;
            _id = id;
        }

        /// <summary>The scope's id, the sequence of the record that opened it; 0 for a scope that is off.</summary>
        public long Id => _id;

        /// <summary>Whether the scope was opened, so that its records were written.</summary>
        public bool IsOn => _id != 0;

        /// <summary>
        /// The log the scope's work writes through: what is written through it is under the scope. For
        /// a scope that is off, the log it was opened from.
        /// </summary>
        public Log Log => _id != 0 ? new Log(_from.Store, _id) : _from;

        /// <summary>A scope that is off, which hands back <paramref name="from"/>: what the generated code returns when the scope's event is off.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogScope Off(Log from) => new LogScope(from, 0);

        /// <summary>Closes the scope: writes the record that ends it, the first time any copy of it is disposed.</summary>
        public void Dispose()
        {
            if (_id != 0)
                _from.Store.Close(_id);
        }

        /// <summary>The scope's id, for a debugger.</summary>
        public override string ToString() => _id != 0 ? $"LogScope #{_id}" : "LogScope (off)";
    }
}
