using System.Text.RegularExpressions;

namespace QueryTranslation.Infrastructure;

// Collects whatever LogTo hands us so a demo can run a query and then assert on the SQL that
// actually went to the server, rather than on what we assume EF produced.
public class SqlCapture
{
    private readonly List<string> _messages = new();

    public void Log(string message) => _messages.Add(message);

    public void Clear() => _messages.Clear();

    public IReadOnlyList<string> Messages => _messages;

    // LogTo gives the whole formatted event ("Executed DbCommand (4ms) [Parameters=...]" and then
    // the statement). For display we usually want just the statement.
    public string LastStatement
    {
        get
        {
            var message = _messages.LastOrDefault();
            if (message is null)
            {
                return "(nothing was logged)";
            }

            var selectIndex = message.IndexOf("SELECT", StringComparison.Ordinal);
            return selectIndex >= 0 ? message[selectIndex..].Trim() : message.Trim();
        }
    }

    public string LastParameterHeader
    {
        get
        {
            var message = _messages.LastOrDefault() ?? string.Empty;
            var match = Regex.Match(message, @"\[Parameters=\[(?<body>.*?)\]", RegexOptions.Singleline);
            return match.Success ? match.Groups["body"].Value : "(no parameters)";
        }
    }

    public int StatementCount => _messages.Count;

    // Counts the entries in the outermost SELECT list. Good enough to show "11 columns versus 4"
    // without pulling in a real SQL parser: it stops at the FROM that closes the outer select and
    // only counts commas at bracket depth zero.
    public static int CountSelectedColumns(string sql)
    {
        var selectIndex = sql.IndexOf("SELECT", StringComparison.Ordinal);
        if (selectIndex < 0)
        {
            return 0;
        }

        var cursor = selectIndex + "SELECT".Length;
        var depth = 0;
        var columns = 1;

        while (cursor < sql.Length)
        {
            var c = sql[cursor];

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                columns++;
            }
            else if (depth == 0 && IsKeywordAt(sql, cursor, "FROM"))
            {
                break;
            }

            cursor++;
        }

        return columns;
    }

    private static bool IsKeywordAt(string sql, int index, string keyword)
    {
        if (index + keyword.Length > sql.Length)
        {
            return false;
        }

        if (string.Compare(sql, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var before = index == 0 || char.IsWhiteSpace(sql[index - 1]);
        var afterIndex = index + keyword.Length;
        var after = afterIndex >= sql.Length || char.IsWhiteSpace(sql[afterIndex]);
        return before && after;
    }

    // Trims a statement down to something that fits on a console line or two.
    public static string Shorten(string sql, int maxLength = 300)
    {
        var flattened = Regex.Replace(sql, @"\s+", " ").Trim();
        return flattened.Length <= maxLength ? flattened : flattened[..maxLength] + " ...";
    }
}
