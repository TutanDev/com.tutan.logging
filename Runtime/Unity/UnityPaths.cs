using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tutan.Logging
{
    /// <summary>
    /// Maps the path the compiler wrote for a call site to the path Unity opens: relative to the
    /// project, as <c>Assets/Game/SaveManager.cs</c>, and for a package, under the name Unity mounts it
    /// at, as <c>Packages/com.tutan.messages/Runtime/MessageBus.cs</c>, wherever its folder is on disk.
    /// Unity compiles the assemblies of packages with <c>/pathmap:&lt;project&gt;=.</c>, so a package's
    /// file comes relative to the project, as <c>.\Packages\com.tutan.messages\Runtime\MessageBus.cs</c>,
    /// and a file under <c>Assets/</c> or outside the project comes absolute. A path it cannot map is
    /// kept as it is, with forward slashes.
    /// </summary>
    /// <remarks>
    /// Each path is mapped once and then read from a table, since a program has a few hundred call
    /// sites and writes them over and over. It maps on one thread, as a sink writes and a window draws on one.
    /// </remarks>
    public sealed class UnityPaths
    {
        private const string PackageCache = "Library/PackageCache/";

        private readonly string _root;
        private readonly Dictionary<string, string> _mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly StringComparison _comparison;
        private readonly (string Folder, string Name)[] _packages;

        /// <summary>
        /// Paths mapped against <paramref name="root"/>, the project's folder, or empty when it is not
        /// known, and <paramref name="packages"/>, each package's folder on disk and its name.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="packages"/> is null, or a package's folder or name is.</exception>
        /// <exception cref="ArgumentException">A package's folder or name is empty or blank.</exception>
        /// <remarks>A path is matched ignoring case on Windows and macOS, whose file systems ignore it by default, and by its case elsewhere.</remarks>
        public UnityPaths(string root, IReadOnlyList<(string Folder, string Name)> packages)
            : this(root, packages, ignoreCase: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
        }

        /// <summary>Paths mapped as the public constructor maps them, ignoring case when <paramref name="ignoreCase"/>, so a test chooses the file system's rule.</summary>
        internal UnityPaths(string root, IReadOnlyList<(string Folder, string Name)> packages, bool ignoreCase)
        {
            if (root is null)
                throw new ArgumentNullException(nameof(root), "The project's folder is a path, or empty when it is not known.");
            if (packages is null)
                throw new ArgumentNullException(nameof(packages), "The packages are a list, empty for none.");

            _root = Folder(root);
            _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // Longest first, so a package nested in another's folder is found before the outer one; of
            // two of one folder, the first listed, as the order is stable.
            _packages = packages.Select(Checked).OrderByDescending(static package => package.Folder.Length).ToArray();
        }

        /// <summary>The paths of the project Unity is running: its folder, and in the editor the folder of every package it has registered.</summary>
        public static UnityPaths ForThisProject() =>
            new UnityPaths(Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "", RegisteredPackages());

        /// <summary><paramref name="file"/> as Unity opens it.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
        public string Map(string file)
        {
            if (file is null)
                throw new ArgumentNullException(nameof(file));
            if (_mapped.TryGetValue(file, out string? mapped))
                return mapped;

            mapped = Mapped(file.Replace('\\', '/'));
            _mapped.Add(file, mapped);
            return mapped;
        }

        /// <summary>The file's name, without its folder: what a console shows of a call site.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static string FileName(string path)
        {
            if (path is null)
                throw new ArgumentNullException(nameof(path));

            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private string Mapped(string path)
        {
            // Relative to the project, as the path map wrote it: made whole again, so a package in the project is found by its folder.
            if (path.StartsWith("./", StringComparison.Ordinal))
                path = _root + path.Substring(2);

            foreach ((string folder, string name) in _packages)
            {
                if (StartsWith(path, folder))
                    return "Packages/" + name + "/" + path.Substring(folder.Length);
            }

            if (_root.Length == 0 || !StartsWith(path, _root))
                return path;

            string relative = path.Substring(_root.Length);

            // A package Unity downloaded, which the registry did not list: Library/PackageCache/name@version/ is mounted as Packages/name/.
            if (StartsWith(relative, PackageCache))
            {
                int end = relative.IndexOf('/', PackageCache.Length);
                if (end > 0)
                {
                    string folder = relative.Substring(PackageCache.Length, end - PackageCache.Length);
                    int at = folder.IndexOf('@');
                    string name = at > 0 ? folder.Substring(0, at) : folder;
                    return "Packages/" + name + relative.Substring(end);
                }
            }

            return relative;
        }

        private bool StartsWith(string path, string folder) => path.StartsWith(folder, _comparison);

        /// <summary>A package given, its folder with forward slashes and one at its end.</summary>
        /// <exception cref="ArgumentNullException">Its folder or name is null.</exception>
        /// <exception cref="ArgumentException">Its folder or name is empty or blank.</exception>
        private static (string Folder, string Name) Checked((string Folder, string Name) package)
        {
            (string folder, string name) = package;
            if (folder is null || name is null)
                throw new ArgumentNullException("packages", $"A package has a folder and a name, and ({folder ?? "null"}, {name ?? "null"}) was given.");
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    $"A package has a folder and a name, and (\"{folder}\", \"{name}\") was given: a package of no folder would hold every path.", "packages");

            return (Folder(folder), name);
        }

        /// <summary>A folder with forward slashes and one at its end, or empty.</summary>
        private static string Folder(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            string slashed = path.Replace('\\', '/');
            return slashed.EndsWith("/", StringComparison.Ordinal) ? slashed : slashed + "/";
        }

        private static IReadOnlyList<(string Folder, string Name)> RegisteredPackages()
        {
            var packages = new List<(string Folder, string Name)>();
#if UNITY_EDITOR
            foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.IsNullOrWhiteSpace(package.resolvedPath) && !string.IsNullOrWhiteSpace(package.name))
                    packages.Add((package.resolvedPath, package.name));
            }
#endif
            return packages;
        }
    }
}
