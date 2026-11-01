using NUnit.Framework;

namespace Tutan.Logging.Tests
{
    public class UnityPathsTests
    {
        private static readonly UnityPaths s_paths = new UnityPaths("D:/Game", new[]
        {
            ("D:/Game/Packages/com.tutan.logging", "com.tutan.logging"),
            ("E:/Elsewhere/pkg/", "com.other.pkg"),
            ("D:/Game/Packages/outer", "com.outer"),
            ("D:/Game/Packages/outer/inner", "com.inner"),
        });

        [TestCase("D:/Game/Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("D:\\Game\\Assets\\Scripts\\Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("D:/Game/Packages/com.tutan.logging/Runtime/Log.cs", "Packages/com.tutan.logging/Runtime/Log.cs")]
        [TestCase("E:/Elsewhere/pkg/Runtime/A.cs", "Packages/com.other.pkg/Runtime/A.cs")]
        [TestCase("D:/Game/Packages/outer/inner/B.cs", "Packages/com.inner/B.cs")]
        [TestCase("D:/Game/Packages/outer/B.cs", "Packages/com.outer/B.cs")]
        [TestCase("D:/Game/Library/PackageCache/com.unity.foo@1.2.3/Runtime/C.cs", "Packages/com.unity.foo/Runtime/C.cs")]
        [TestCase("D:/Game/Library/PackageCache/com.unity.bar/C.cs", "Packages/com.unity.bar/C.cs")]
        [TestCase(".\\Assets\\Scripts\\Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("./Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase(".\\Packages\\com.tutan.logging\\Runtime\\Log.cs", "Packages/com.tutan.logging/Runtime/Log.cs")]
        [TestCase(".\\Packages\\outer\\inner\\B.cs", "Packages/com.inner/B.cs")]
        [TestCase(".\\Library\\PackageCache\\com.unity.foo@1.2.3\\Runtime\\C.cs", "Packages/com.unity.foo/Runtime/C.cs")]
        [TestCase("Assets/Relative.cs", "Assets/Relative.cs")]
        [TestCase("F:/Other/D.cs", "F:/Other/D.cs")]
        [TestCase("D:/GameTwo/Assets/E.cs", "D:/GameTwo/Assets/E.cs")]
        public void A_file_is_mapped_to_the_path_unity_opens_it_by(string file, string mapped)
        {
            Assert.That(s_paths.Map(file), Is.EqualTo(mapped));
            Assert.That(s_paths.Map(file), Is.EqualTo(mapped), "A path mapped once is mapped the same again.");
        }

        [Test]
        public void With_no_project_folder_a_path_is_kept_as_it_is_but_with_forward_slashes()
        {
            var paths = new UnityPaths("", new (string, string)[0]);

            Assert.That(paths.Map("C:\\Game\\Assets\\A.cs"), Is.EqualTo("C:/Game/Assets/A.cs"));
        }

        [TestCase("Assets/Scripts/Player.cs", "Player.cs")]
        [TestCase("Player.cs", "Player.cs")]
        public void A_files_name_is_what_follows_its_last_slash(string path, string name)
        {
            Assert.That(UnityPaths.FileName(path), Is.EqualTo(name));
        }

        [Test]
        public void This_projects_paths_map_this_file_into_its_package()
        {
            UnityPaths paths = UnityPaths.ForThisProject();

            Assert.That(paths.Map(Here()), Is.EqualTo("Packages/com.tutan.logging/Tests/Editor/UnityPathsTests.cs"));
        }

        private static string Here([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;
    }
}
