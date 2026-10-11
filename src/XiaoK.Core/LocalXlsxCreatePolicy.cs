namespace XiaoK.Core;

/// <summary>解析有界的表格命令；内容暂以经过严格校验的CSV记录承载，不接受公式。</summary>
public static class LocalXlsxCreatePolicy
{
    public const string ToolId = "file.create.xlsx.v1";

    public static bool TryParseRequest(string? request, out string fileName, out string serializedRows) =>
        LocalCsvCreatePolicy.TryParseTabularRequest(request, "创建Excel文件：",
            LocalTextFileCreatePolicy.IsValidXlsxFileName, out fileName, out _, out serializedRows);

    public static bool IsValidContent(string? serializedRows) =>
        LocalCsvCreatePolicy.IsValidCsvContent(serializedRows);

    public static bool TryGetRows(string? serializedRows, out IReadOnlyList<IReadOnlyList<string>> rows) =>
        LocalCsvCreatePolicy.TryParseCsvRows(serializedRows, out rows);
}
