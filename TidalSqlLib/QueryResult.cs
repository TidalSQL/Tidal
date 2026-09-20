namespace TidalSqlLib
{
    using System.Linq;
    using System.Text;

    public class QueryResult
    {
        public List<string> Columns { get; set; } = new();
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
        public string Message { get; set; } = string.Empty;
        public int RowCount { get; set; }
        public bool Success { get; set; } = true;

        /// <summary>
        /// Renders the result as a text table. Presentation is kept out of the engine so the
        /// library/API surface stays free of console side effects.
        /// </summary>
        public string ToDisplayString()
        {
            if (this.Columns.Count == 0)
            {
                return this.Message;
            }

            var widths = new Dictionary<string, int>();
            foreach (var col in this.Columns)
            {
                var width = col.Length;
                foreach (var row in this.Rows)
                {
                    var value = row.TryGetValue(col, out var o) ? o : null;
                    var length = (value?.ToString() ?? "NULL").Length;
                    if (length > width)
                    {
                        width = length;
                    }
                }

                widths[col] = width;
            }

            var builder = new StringBuilder();
            var header = string.Join(" | ", this.Columns.Select(c => c.PadRight(widths[c])));
            builder.AppendLine(header);
            builder.AppendLine(new string('-', header.Length));

            foreach (var row in this.Rows)
            {
                builder.AppendLine(string.Join(" | ", this.Columns.Select(c =>
                {
                    var value = row.TryGetValue(c, out var o) ? o : null;
                    return (value?.ToString() ?? "NULL").PadRight(widths[c]);
                })));
            }

            builder.AppendLine();
            builder.Append($"({this.RowCount} row(s) returned)");
            return builder.ToString();
        }
    }
}
