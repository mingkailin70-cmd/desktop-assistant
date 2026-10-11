using System.Text;

namespace XiaoK.Core;

/// <summary>Creates a bounded CSV table from explicitly supplied cells and rejects spreadsheet formulas.</summary>
public static class LocalCsvCreatePolicy
{
    public const string ToolId = "file.create.csv.v1";
    public const int MaximumColumns = 32;
    public const int MaximumRows = 500;
    public const int MaximumContentCharacters = 16_000;
    public const int MaximumContentBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParseRequest(string? request, out string fileName, out string csvContent)
    {
        fileName = string.Empty;
        csvContent = string.Empty;
        const string prefix = "创建CSV文件：";
        if (string.IsNullOrWhiteSpace(request) || !request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var lines = request[prefix.Length..].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || lines.Any(line => line.Contains('\r')) || !lines[1].StartsWith("表头：", StringComparison.Ordinal))
            return false;

        fileName = TrimOptionalQuotes(lines[0].Trim());
        if (!LocalTextFileCreatePolicy.IsValidCsvFileName(fileName)) return false;

        var firstDataLine = lines[2] == "数据：" ? 3 : 2;
        var lastLine = lines.Length;
        while (lastLine > firstDataLine && lines[lastLine - 1].Length == 0) lastLine--;
        var dataRowCount = lastLine - firstDataLine;
        if (dataRowCount is < 1 or > MaximumRows) return false;

        var header = lines[1]["表头：".Length..].Split('|');
        if (!IsValidHeader(header)) return false;

        var rows = new List<IReadOnlyList<string>>(dataRowCount + 1) { header };
        for (var index = firstDataLine; index < lastLine; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) return false;
            var cells = lines[index].Split('|');
            if (cells.Length != header.Length || cells.Any(cell => !IsValidCell(cell))) return false;
            rows.Add(cells);
        }

        csvContent = string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(EscapeCsvCell)))) + "\r\n";
        return IsValidCsvContent(csvContent);
    }

    public static bool IsValidCsvContent(string? csv)
    {
        if (string.IsNullOrEmpty(csv) || csv.Length > MaximumContentCharacters) return false;
        try
        {
            if (StrictUtf8.GetByteCount(csv) + 3 > MaximumContentBytes) return false;
        }
        catch (EncoderFallbackException) { return false; }

        if (!TryParseCsv(csv, out var rows) || rows.Count is < 2 or > MaximumRows + 1) return false;
        var width = rows[0].Count;
        if (width is < 1 or > MaximumColumns || rows[0].Any(string.IsNullOrWhiteSpace)) return false;
        return rows.All(row => row.Count == width && row.All(IsValidCell));
    }

    private static bool IsValidHeader(IReadOnlyList<string> cells) =>
        cells.Count is >= 1 and <= MaximumColumns && cells.All(cell => !string.IsNullOrWhiteSpace(cell) && IsValidCell(cell));

    private static bool IsValidCell(string cell)
    {
        if (cell.Length > 2_048 || cell.Any(character => character == '\0' || (char.IsControl(character) && character is not '\t')))
            return false;
        var trimmed = cell.TrimStart();
        if (trimmed.Length == 0) return true;
        if (trimmed[0] is '=' or '+' or '@') return false;
        if (trimmed[0] == '-') return IsNegativeNumericLiteral(trimmed);
        return true;
    }

    private static bool IsNegativeNumericLiteral(string value)
    {
        if (value.Length < 2) return false;
        var index = 1;
        var digitsBeforeDecimal = 0;
        while (index < value.Length && char.IsAsciiDigit(value[index])) { digitsBeforeDecimal++; index++; }
        var digitsAfterDecimal = 0;
        if (index < value.Length && value[index] == '.')
        {
            index++;
            while (index < value.Length && char.IsAsciiDigit(value[index])) { digitsAfterDecimal++; index++; }
        }
        return index == value.Length && digitsBeforeDecimal + digitsAfterDecimal > 0;
    }

    private static string EscapeCsvCell(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private static bool TryParseCsv(string csv, out List<List<string>> rows)
    {
        rows = [];
        var row = new List<string>();
        var cell = new StringBuilder();
        var index = 0;
        var quoted = false;
        var quotedField = false;
        var closedQuote = false;
        var fieldStarted = false;

        while (index < csv.Length)
        {
            var character = csv[index++];
            if (quoted)
            {
                if (character != '"') { cell.Append(character); continue; }
                if (index < csv.Length && csv[index] == '"') { cell.Append('"'); index++; continue; }
                quoted = false;
                closedQuote = true;
                continue;
            }

            if (!fieldStarted && character == '"') { quoted = true; quotedField = true; fieldStarted = true; continue; }
            if (character == ',' || character == '\r')
            {
                row.Add(cell.ToString());
                cell.Clear();
                fieldStarted = false;
                quotedField = false;
                closedQuote = false;
                if (character == ',') continue;

                if (character == '\r')
                {
                    if (index >= csv.Length || csv[index] != '\n') return false;
                    index++;
                }
                rows.Add(row);
                if (rows.Count > MaximumRows + 1) return false;
                row = [];
                continue;
            }

            if (closedQuote) return false;
            if (character == '"' && !quotedField) return false;
            cell.Append(character);
            fieldStarted = true;
        }

        if (quoted) return false;
        if (cell.Length > 0 || fieldStarted || row.Count > 0 || (csv.Length > 0 && csv[^1] == ','))
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows.Count is >= 2 and <= MaximumRows + 1;
    }

    private static string TrimOptionalQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '“' && value[^1] == '”')))
            return value[1..^1];
        return value;
    }
}
