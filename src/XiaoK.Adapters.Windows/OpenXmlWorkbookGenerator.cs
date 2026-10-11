using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using System.Xml;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

internal static class OpenXmlWorkbookGenerator
{
    public static bool TryCreate(string serializedRows, CancellationToken cancellationToken, out byte[] workbookBytes)
    {
        workbookBytes = [];
        if (!LocalXlsxCreatePolicy.IsValidContent(serializedRows)
            || !LocalXlsxCreatePolicy.TryGetRows(serializedRows, out var rows)) return false;

        using var buffer = new MemoryStream();
        try
        {
            using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rowNumber = checked((uint)rowIndex + 1);
                    var row = new Row { RowIndex = rowNumber };
                    for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
                    {
                        var cell = new Cell
                        {
                            CellReference = ColumnName(columnIndex) + rowNumber,
                            DataType = CellValues.InlineString,
                            InlineString = new InlineString(new Text(rows[rowIndex][columnIndex])
                            {
                                Space = SpaceProcessingModeValues.Preserve
                            })
                        };
                        row.Append(cell);
                    }
                    sheetData.Append(row);
                }

                worksheetPart.Worksheet = new Worksheet(sheetData);
                var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1,
                    Name = "数据"
                });
                workbookPart.Workbook.Save();

                cancellationToken.ThrowIfCancellationRequested();
                if (new OpenXmlValidator().Validate(document).Any()) return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            workbookBytes = buffer.ToArray();
            return workbookBytes.Length > 0;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or ArgumentException or InvalidOperationException
            or IOException or XmlException)
        {
            workbookBytes = [];
            return false;
        }
    }

    private static string ColumnName(int zeroBasedIndex)
    {
        Span<char> buffer = stackalloc char[3];
        var index = buffer.Length;
        var value = zeroBasedIndex + 1;
        while (value > 0)
        {
            value--;
            buffer[--index] = (char)('A' + value % 26);
            value /= 26;
        }
        return new string(buffer[index..]);
    }
}
