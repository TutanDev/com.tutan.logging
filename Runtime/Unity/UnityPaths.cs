using System;
using System.Collections.Generic;
using System.IO;

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
    /// <remarks>Each path is mapped once and then read from a table, since a program has a few hundred call sites and writes them over and over.</remarks>
    internal sealed class UnityPaths
    {
        private const string PackageCache = "Library/PackageCache/";

        private readonly string _root;
        private readonly Dictionary<string, string> _mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly bool _ignoreCase = Path.DirectorySeparatorChar == '\\';
        private readonly IReadOnlyList<(string Folder, string Name)> _packages;

        /// <summary>Paths mapped against <paramref name="root"/>, the project's folder, and <paramref name="packages"/>, each package's folder on disk and its name.</summary>
        public UnityPaths(string root, IReadOnlyList<(string Folder, string Name)> packages)
        {
            _root = Folder(root);

            // Longest first, so a package nested in another's folder is found before the outer one.
            var sorted = new List<(string Folder, string Name)>(packages.Count);
            foreach ((string folder, string name) in packages)
                sorted.Add((Folder(folder), name));
            sorted.Sort((a, b) => b.Folder.Length.CompareTo(a.Folder.Length));
            _packages = sorted;
        }

        /// <summary>The paths of the project Unity is running: its folder, and in the editor the folder of every package it has registered.</summary>
        public static UnityPaths ForThisProject() =>
            new UnityPaths(Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "", RegisteredPackages());

        /// <summary><paramref name="file"/> as Unity opens it.</summary>
        public string Map(string file)
        {
            if (_mapped.TryGetValue(file, out string? mapped))
                return mapped;

            mapped = Mapped(file.Replace('\\', '/'));
            _mapped.Add(file, mapped);
            return mapped;
        }

        /// <summary>The file's name, without its folder: what a console shows of a call site.</summary>
        public static string FileName(string path)
        {
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
            if (relative.StartsWith(PackageCache, StringComparison.Ordinal))
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

        private bool StartsWith(string path, string folder) =>
            path.StartsWith(folder, _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
                if (!string.IsNullOrEmpty(package.resolvedPath))
                    packages.Add((package.resolvedPath, package.name));
            }
#endif
            return packages;
        }
    }
}
