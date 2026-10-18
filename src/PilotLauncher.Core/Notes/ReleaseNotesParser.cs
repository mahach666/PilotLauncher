using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PilotLauncher.Core.Notes;

/// <summary>
/// Parses <c>https://help.pilotems.com/ru/Content/RelNotes.htm</c> (MadCap Flare): an <c>&lt;h2&gt;Версия X.Y.Z.B от ДД.ММ.ГГГГ&lt;/h2&gt;</c>
/// per release followed by tables; a row is a product name cell and a cell with a list, a one-cell row is a warning.
/// </summary>
public static class ReleaseNotesParser
{
    public const string FixesRowName = "Исправлены ошибки";

    private const RegexOptions Flags = RegexOptions.CultureInvariant | RegexOptions.Singleline;

    private static readonly Regex Comment = new(@"<!--.*?-->", Flags);
    private static readonly Regex Heading = new(@"<h2\b[^>]*>(?<text>.*?)</h2>", Flags);
    private static readonly Regex HeadingText = new(@"^Версия\s+(?<version>\d+\.\d+\.\d+\.\d+)\s+от\s+(?<date>\d{2}\.\d{2}\.\d{4})$", Flags);
    private static readonly Regex Table = new(@"<table\b[^>]*>(?<body>.*?)</table>", Flags);
    private static readonly Regex Row = new(@"<tr\b[^>]*>(?<body>.*?)</tr>", Flags);
    private static readonly Regex Cell = new(@"<td\b[^>]*>(?<body>.*?)</td>", Flags);
    private static readonly Regex Paragraph = new(@"<p\b[^>]*(?<!/)>(?<body>.*?)</p>", Flags);
    private static readonly Regex ListTag = new(@"<(?<end>/)?(?<tag>ul|li)\b[^>]*>", Flags);
    private static readonly Regex BlockTag = new(@"</?(?:p|li|ul|br|div)\b[^>]*>", Flags);
    private static readonly Regex AnyTag = new(@"<[^>]*>", Flags);
    private static readonly Regex Spaces = new(@"\s+", Flags);

    // "Pilot-ICE Web: text". The names are typed by hand on the page ("Pilot_ICE Desktop", "Pilto-ICE Web"), so only the start is fixed.
    private static readonly Regex ProductPrefix = new(@"^(?<name>(?:Pilot|Pilto|Ascon\.Pilot|3D-Storage)[^:]{0,40}):\s*(?<rest>.*)$", Flags);

    /// <summary>Releases found on the page, newest first. Text that is not a release heading is ignored; an empty list means a foreign page.</summary>
    public static IReadOnlyList<ReleaseNotes> Parse(string html)
    {
        html = Comment.Replace(html, "");
        var headings = Heading.Matches(html);
        var result = new List<ReleaseNotes>();
        for (var i = 0; i < headings.Count; i++)
        {
            var m = HeadingText.Match(Clean(headings[i].Groups["text"].Value));
            if (!m.Success || !Version.TryParse(m.Groups["version"].Value, out var version))
                continue;
            var start = headings[i].Index + headings[i].Length;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : html.Length;
            DateTime? date = DateTime.TryParseExact(m.Groups["date"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null;
            result.Add(ParseRelease(version, date, html[start..end]));
        }

        return result.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>"Pilot_ICE Desktop", "Pilot-ICE  Desktop" and "pilot-ice-desktop" are the same product.</summary>
    public static string ProductKey(string name) => Spaces.Replace(name.Trim(), "-").Replace('_', '-').ToLowerInvariant();

    private static ReleaseNotes ParseRelease(Version version, DateTime? date, string html)
    {
        var notices = new List<string>();
        var products = new List<ProductNotes>();
        var fixes = new List<NoteItem>();
        foreach (Match table in Table.Matches(html))
        foreach (Match row in Row.Matches(table.Groups["body"].Value))
        {
            var cells = Cell.Matches(row.Groups["body"].Value).Select(c => c.Groups["body"].Value).ToList();
            if (cells.Count == 1)
            {
                var notice = Clean(cells[0]);
                if (notice.Length > 0)
                    notices.Add(notice);
            }
            else if (cells.Count == 2)
            {
                // the name cell may start with an empty paragraph (&#160;) or end with a remark ("alpha version")
                var name = Paragraph.Matches(cells[0]).Select(p => Clean(p.Groups["body"].Value)).FirstOrDefault(t => t.Length > 0);
                if (name is null)
                    continue;
                if (name == FixesRowName)
                    fixes.AddRange(ParseItems(cells[1]).Select(SplitPrefix));
                else
                    products.Add(new ProductNotes(name, ParseItems(cells[1])));
            }
        }

        return new ReleaseNotes(version, date, notices, products, fixes);
    }

    private static NoteItem SplitPrefix(NoteItem item)
    {
        if (item.Product is not null)
            return item;
        var m = ProductPrefix.Match(item.Text);
        return m.Success && m.Groups["rest"].Length > 0 ? new NoteItem(m.Groups["rest"].Value, m.Groups["name"].Value.Trim()) : item;
    }

    private sealed class ListNode(int depth)
    {
        public int Depth { get; } = depth;
        public StringBuilder Text { get; } = new();
        public List<ListNode> Children { get; } = [];
    }

    /// <summary>
    /// Reads the list of a cell. Lists nest either properly (<c>&lt;li&gt;text&lt;ul&gt;...&lt;/ul&gt;&lt;/li&gt;</c>) or the 2024 way,
    /// with the sub list placed next to the heading item (<c>&lt;li&gt;Pilot-BIM Desktop:&lt;/li&gt;&lt;ul&gt;...&lt;/ul&gt;</c>).
    /// </summary>
    private static List<NoteItem> ParseItems(string html)
    {
        var root = new List<ListNode>();
        var lists = new Stack<List<ListNode>>();
        lists.Push(root);
        var open = new Stack<ListNode>();
        ListNode? last = null;
        var pos = 0;
        foreach (Match m in ListTag.Matches(html))
        {
            if (open.TryPeek(out var current) && current.Depth == lists.Count)
                current.Text.Append(html, pos, m.Index - pos);
            pos = m.Index + m.Length;
            var closing = m.Groups["end"].Success;
            if (m.Groups["tag"].Value == "li")
            {
                if (closing)
                {
                    if (open.TryPeek(out var top) && top.Depth == lists.Count)
                        last = open.Pop();
                    continue;
                }

                // an <li> without </li> ends at the next one
                if (open.TryPeek(out var unclosed) && unclosed.Depth == lists.Count)
                    open.Pop();
                var node = new ListNode(lists.Count);
                lists.Peek().Add(node);
                open.Push(node);
                last = null;
            }
            else if (closing)
            {
                while (open.TryPeek(out var unclosed) && unclosed.Depth == lists.Count)
                    open.Pop();
                if (lists.Count > 1)
                    lists.Pop();
                last = null;
            }
            else
            {
                var target = open.TryPeek(out var parent) && parent.Depth == lists.Count ? parent.Children : last?.Children ?? lists.Peek();
                lists.Push(target);
                last = null;
            }
        }

        var items = new List<NoteItem>();
        Flatten(root, null, items);
        return items;
    }

    private static void Flatten(List<ListNode> nodes, string? group, List<NoteItem> items)
    {
        foreach (var node in nodes)
        {
            var text = Clean(node.Text.ToString());
            var heading = ProductPrefix.Match(text);
            if (node.Children.Count > 0 && heading.Success && heading.Groups["rest"].Length == 0)
                Flatten(node.Children, heading.Groups["name"].Value.Trim(), items);
            else if (text.Length > 0 || node.Children.Count > 0)
                items.Add(new NoteItem(Describe(node, text).Trim(), group));
        }
    }

    private static string Describe(ListNode node, string text) =>
        node.Children.Aggregate(text, (acc, child) => acc + "\n- " + Describe(child, Clean(child.Text.ToString())));

    /// <summary>Visible text of a fragment on one line: inline markup dropped, blocks and entities resolved.</summary>
    private static string Clean(string html)
    {
        var text = AnyTag.Replace(BlockTag.Replace(html, " "), "");
        return Spaces.Replace(WebUtility.HtmlDecode(text).Replace('\u00A0', ' '), " ").Trim();
    }
}
