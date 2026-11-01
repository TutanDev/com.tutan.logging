namespace Tutan.Logging
{
    /// <summary>
    /// How much a record matters, from the detail only someone chasing a problem wants to a failure the
    /// program does not survive. The names and their order are Microsoft.Extensions.Logging's, so a
    /// bridge to it would map one to one.
    /// </summary>
    public enum LogLevel : byte
    {
        /// <summary>Every step, for following one thing through in detail.</summary>
        Trace,

        /// <summary>What a developer wants when something went wrong: captured by default, and written by a sink only as an error's context.</summary>
        Debug,

        /// <summary>What the program did: a level loaded, a save written, a connection opened.</summary>
        Information,

        /// <summary>Something that worked, but not as asked: an asset missing, a fallback taken.</summary>
        Warning,

        /// <summary>Something that failed, which the program goes on without.</summary>
        Error,

        /// <summary>A failure the program does not go on from.</summary>
        Critical,

        /// <summary>Not a level any record has. As a threshold, nothing passes it: what a rule's <c>off</c> means.</summary>
        None,
    }
}
