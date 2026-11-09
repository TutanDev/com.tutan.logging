#if UNITY_6000_3_OR_NEWER
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The badge on the editor's main toolbar: the count of errors no console window has shown, not
    /// displayed while there are none, and a press shows the console at the first. Unity 6.3 and
    /// later; below, the window's title says the count.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleToolbar
    {
        private const string Path = "Log Console/Unseen Errors";

        // Made again only when the count changed, since a refresh calls the method again.
        static ConsoleToolbar() => ConsoleChrome.UnseenChanged += static () => MainToolbar.Refresh(Path);

        [MainToolbarElement(Path, defaultDockPosition = MainToolbarDockPosition.Right)]
        private static MainToolbarElement Badge()
        {
            long count = ConsoleChrome.Unseen.Count;
            var icon = EditorGUIUtility.IconContent("console.erroricon.sml").image as Texture2D;
            var content = new MainToolbarContent(UnseenText.Badge(count), icon, UnseenText.BadgeTip(count));
            return new MainToolbarButton(content, ConsoleChrome.ShowFirstUnseen) { displayed = count > 0 };
        }
    }
}
#endif
