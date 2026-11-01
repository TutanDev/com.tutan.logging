using System;

namespace Tutan.Logging
{
    /// <summary>
    /// Names the category of every event a class declares: one or more lowercase words of letters and
    /// digits, joined by dots, as <c>game.save</c>. An event's key is its category and its name,
    /// <c>game.save/Saved</c>, which is what rules, filters and files name it by.
    /// </summary>
    /// <remarks>
    /// The class is a top-level <c>static partial</c> class, since the methods the generator writes
    /// beside its declarations are extension methods of <see cref="Log"/>.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class LogCategoryAttribute : Attribute
    {
        /// <summary>Names the category.</summary>
        /// <param name="name">The category.</param>
        public LogCategoryAttribute(string name) => Name = name;

        /// <summary>The category.</summary>
        public string Name { get; }
    }

    /// <summary>
    /// Declares an event: a <c>static partial void</c> method with no body and no access modifier,
    /// whose parameters are its fields. Tutan.Logging's generator writes, beside it, the method a
    /// program calls: an extension of <see cref="Log"/> with the same fields and the call site the
    /// compiler fills in.
    /// <code>
    /// [LogEvent(LogLevel.Information, "Saved slot {Slot} in {Elapsed}")]
    /// static partial void Saved(int slot, TimeSpan elapsed);
    ///
    /// log.Saved(3, elapsed);
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class LogEventAttribute : Attribute
    {
        /// <summary>An event at a level of its own.</summary>
        /// <param name="level">The level every record of it has.</param>
        /// <param name="template">The message, with a hole naming a parameter for each field it says: <c>Saved slot {Slot} in {Elapsed}</c>.</param>
        public LogEventAttribute(LogLevel level, string template)
        {
            Level = level;
            Template = template;
        }

        /// <summary>
        /// An event whose level is chosen at each call, by a <see cref="LogLevel"/> parameter: for what
        /// the program hears from elsewhere at a level it did not choose, as Unity's own log messages.
        /// </summary>
        /// <param name="template">The message, as for an event at a level of its own.</param>
        public LogEventAttribute(string template) => Template = template;

        /// <summary>The level, or null when a parameter chooses it.</summary>
        public LogLevel? Level { get; }

        /// <summary>The message, with its holes.</summary>
        public string Template { get; }

        /// <summary>The name in the event's key, when it is not the method's: what keeps the key when the method is renamed.</summary>
        public string? Name { get; init; }

        /// <summary>
        /// How many records of the event a store keeps a second, at most, before it counts the rest
        /// instead of keeping them; 0 for no limit. A rule overrides it. What answers a warning that
        /// fires every frame.
        /// </summary>
        public int PerSecond { get; init; }
    }

    /// <summary>
    /// Declares a scope: a <c>static partial void</c> method with no body and no access modifier, as
    /// an event is. The method the generator writes beside it returns a <see cref="LogScope"/>:
    /// opening it writes a record with its fields, every record written through its
    /// <see cref="LogScope.Log"/> is under it, and disposing it writes the record that closes it.
    /// <code>
    /// [LogScope(LogLevel.Information, "Saving slot {Slot}")]
    /// static partial void Saving(int slot);
    ///
    /// using LogScope saving = log.Saving(slot);
    /// saving.Log.Saved(slot, elapsed);
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class LogScopeAttribute : Attribute
    {
        /// <summary>A scope at a level of its own.</summary>
        /// <param name="level">The level of the records that open and close it.</param>
        /// <param name="template">The message the opening record says, with its holes.</param>
        public LogScopeAttribute(LogLevel level, string template)
        {
            Level = level;
            Template = template;
        }

        /// <summary>The level.</summary>
        public LogLevel Level { get; }

        /// <summary>The message, with its holes.</summary>
        public string Template { get; }

        /// <summary>The name in the scope's key, when it is not the method's.</summary>
        public string? Name { get; init; }
    }

    /// <summary>
    /// Marks a field that can name a person, as a player's name or a path that starts with the user's
    /// home folder. The store keeps it; a sink whose options redact personal fields writes
    /// <c>[personal]</c> for it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class PersonalAttribute : Attribute
    {
    }

    /// <summary>
    /// Says what a field is, beyond its type, for a reader that offers to act on it: a console that
    /// turns a field tagged <c>asset</c> into a button that pings the asset.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class LogTagAttribute : Attribute
    {
        /// <summary>Tags the field.</summary>
        /// <param name="tag">The tag.</param>
        public LogTagAttribute(string tag) => Tag = tag;

        /// <summary>The tag.</summary>
        public string Tag { get; }
    }
}
