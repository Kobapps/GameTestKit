using System;
using System.Collections.Generic;
using System.Text;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Categories: the folder a test lives in, treated as a first-class grouping.
    /// </summary>
    /// <remarks>
    /// A category is a <c>/</c>-separated path such as <c>Shop/Checkout</c>. It comes from the folder
    /// the script sits in — so organising a suite is a matter of dragging files into folders, with no
    /// second place to keep in sync — and a script can override it with an explicit <c>"category"</c>
    /// field when the file has to live somewhere the folders do not describe (a package sample, a
    /// flattened Resources mirror).
    /// <para>
    /// Categories are hierarchical and tags are not: a test belongs to exactly one category, and
    /// filtering on <c>Shop</c> includes everything under <c>Shop/Checkout</c>. That is what makes them
    /// the right tool for structure and tags the right tool for cross-cutting sets like <c>smoke</c>.
    /// </para>
    /// </remarks>
    public static class TestCategory
    {
        public const char Separator = '/';

        /// <summary>Shown in place of an empty category — tests sitting directly in a test folder.</summary>
        public const string UncategorizedLabel = "Uncategorized";

        /// <summary>
        /// Cleans a category written by a human or derived from a path: back-slashes become forward
        /// slashes, empty and whitespace-only segments are dropped, and each segment is trimmed.
        /// </summary>
        public static string Normalize(string category)
        {
            if (string.IsNullOrEmpty(category)) return "";

            var builder = new StringBuilder(category.Length);

            foreach (var segment in category.Split('/', '\\'))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0) continue;

                if (builder.Length > 0) builder.Append(Separator);
                builder.Append(trimmed);
            }

            return builder.ToString();
        }

        /// <summary>The segments of a category, outermost first. Empty for an uncategorized test.</summary>
        public static string[] Segments(string category)
        {
            var normalized = Normalize(category);
            return normalized.Length == 0
                ? Array.Empty<string>()
                : normalized.Split(Separator);
        }

        /// <summary>The last segment — what a folder-tree row shows.</summary>
        public static string Leaf(string category)
        {
            var normalized = Normalize(category);
            if (normalized.Length == 0) return "";

            int slash = normalized.LastIndexOf(Separator);
            return slash < 0 ? normalized : normalized.Substring(slash + 1);
        }

        /// <summary>The enclosing category, or "" for a top-level one.</summary>
        public static string Parent(string category)
        {
            var normalized = Normalize(category);
            int slash = normalized.LastIndexOf(Separator);
            return slash < 0 ? "" : normalized.Substring(0, slash);
        }

        /// <summary>A category and every category above it, outermost first: <c>Shop</c>, <c>Shop/Checkout</c>.</summary>
        public static IEnumerable<string> SelfAndAncestors(string category)
        {
            var segments = Segments(category);
            var path = new StringBuilder();

            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) path.Append(Separator);
                path.Append(segments[i]);
                yield return path.ToString();
            }
        }

        /// <summary>Human-readable form, for a window row or a report heading.</summary>
        public static string Display(string category)
        {
            var normalized = Normalize(category);
            return normalized.Length == 0 ? UncategorizedLabel : normalized;
        }

        /// <summary>
        /// True when <paramref name="category"/> is <paramref name="filter"/> or sits underneath it.
        /// An empty filter matches everything, which is what makes "All categories" the default.
        /// </summary>
        public static bool IsWithin(string category, string filter)
        {
            var wanted = Normalize(filter);
            if (wanted.Length == 0) return true;

            var actual = Normalize(category);
            if (actual.Length < wanted.Length) return false;

            if (!actual.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return false;

            // "Shop" must not match "Shopping" — only the whole category or a child of it.
            return actual.Length == wanted.Length || actual[wanted.Length] == Separator;
        }

        /// <summary>Orders categories the way a folder tree reads: parents before children, then A–Z.</summary>
        public static int Compare(string left, string right) =>
            string.Compare(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Derives a category from the folder a script lives in, relative to whichever discovery root
        /// contains it (a configured test folder, the Resources mirror, or StreamingAssets).
        /// </summary>
        /// <remarks>
        /// The roots are tried longest-first so that a file under <c>Assets/GameTests/Shop</c> is
        /// <c>Shop</c> rather than <c>GameTests/Shop</c>. A path under no known root — an absolute path
        /// handed to <c>-gtk-test</c>, say — yields "" rather than a category named after somebody's
        /// home directory.
        /// </remarks>
        public static string FromSourcePath(string sourcePath) =>
            FromSourcePath(sourcePath, DiscoveryRoots());

        /// <summary>
        /// Derives a category against an explicit set of roots rather than the configured ones.
        /// </summary>
        /// <param name="sourcePath">The script's path.</param>
        /// <param name="roots">Folders to measure against. Tried longest-first, whatever order they arrive in.</param>
        public static string FromSourcePath(string sourcePath, IEnumerable<string> roots)
        {
            if (string.IsNullOrEmpty(sourcePath)) return "";

            var path = sourcePath.Replace('\\', Separator);
            int slash = path.LastIndexOf(Separator);
            if (slash < 0) return "";

            var folder = path.Substring(0, slash);
            if (folder.Length == 0) return "";

            foreach (var root in Ordered(roots))
            {
                var relative = RelativeTo(folder, root);
                if (relative != null) return Normalize(relative);
            }

            // A relative path from somewhere else in the project still describes a hierarchy worth
            // keeping; an absolute one describes this machine, which nobody wants to filter on.
            return IsRooted(folder) ? "" : Normalize(folder);
        }

        /// <summary>The folders a category is measured against, taken from the project settings.</summary>
        /// <remarks>
        /// Falls back to the defaults when the settings cannot be read. Reaching them goes through
        /// <c>Resources.Load</c>, which is only legal on Unity's main thread — and a script being parsed
        /// on a worker thread, or by tooling outside the Editor, should come back with the category its
        /// folder implies rather than an exception from three layers down.
        /// </remarks>
        public static List<string> DiscoveryRoots()
        {
            var roots = new List<string>();
            var settings = GameTesterSettings.TryGetInstance();

            if (settings == null)
            {
                roots.Add("Assets/GameTests");
                roots.Add("Assets/Resources/GameTests");
                roots.Add("Resources/GameTests");
                roots.Add("StreamingAssets/GameTests");
                roots.Add("Assets/Resources");
                roots.Add("StreamingAssets");
                roots.Add("Assets");
                return roots;
            }

            foreach (var folder in settings.TestFolders) roots.Add(folder);

            if (!string.IsNullOrWhiteSpace(settings.RuntimeResourcesFolder))
            {
                roots.Add("Assets/Resources/" + settings.RuntimeResourcesFolder);
                roots.Add("Resources/" + settings.RuntimeResourcesFolder);
            }

            if (!string.IsNullOrWhiteSpace(settings.StreamingAssetsFolder))
                roots.Add("StreamingAssets/" + settings.StreamingAssetsFolder);

            // The last resorts, so a script kept beside the feature it tests still gets a category.
            roots.Add("Assets/Resources");
            roots.Add("StreamingAssets");
            roots.Add("Assets");

            return roots;
        }

        /// <summary>Normalised, de-duplicated and longest-first, so the most specific root wins.</summary>
        private static List<string> Ordered(IEnumerable<string> roots)
        {
            var ordered = new List<string>();

            if (roots != null)
            {
                foreach (var root in roots)
                {
                    var normalized = Normalize(root);
                    if (normalized.Length > 0 && !ordered.Contains(normalized)) ordered.Add(normalized);
                }
            }

            ordered.Sort((left, right) => right.Length.CompareTo(left.Length));
            return ordered;
        }

        /// <summary>
        /// The part of <paramref name="folder"/> below <paramref name="root"/>, or null when it is not
        /// under it. The root is matched anywhere in the path so absolute paths work too.
        /// </summary>
        private static string RelativeTo(string folder, string root)
        {
            if (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)) return "";

            if (folder.Length > root.Length + 1 &&
                folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                folder[root.Length] == Separator)
                return folder.Substring(root.Length + 1);

            var anchor = Separator + root + Separator;
            int index = folder.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return folder.Substring(index + anchor.Length);

            var trailing = Separator + root;
            if (folder.Length > trailing.Length &&
                folder.EndsWith(trailing, StringComparison.OrdinalIgnoreCase))
                return "";

            return null;
        }

        /// <summary>True for <c>/foo</c> and <c>C:/foo</c> — a path that names a place on this machine.</summary>
        private static bool IsRooted(string folder) =>
            folder.Length > 0 && (folder[0] == Separator || folder.IndexOf(':') >= 0);
    }
}
