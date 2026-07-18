using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using EcmisWitness.Api.Contracts;

namespace EcmisWitness.Api.Services;

public sealed class WitnessReportService
{
    private static readonly string[] Headers =
    [
        "เลขคำร้อง", "ประเภทแบบ", "ผู้ขอคุ้มครอง", "ผู้ยื่นคำร้อง", "ขั้นตอนปัจจุบัน",
        "สถานะ", "ความเร่งด่วน", "ระดับความเสี่ยง", "ผู้รับผิดชอบ", "บทบาทผู้รับผิดชอบ",
        "งานถัดไป", "วันที่รับคำร้อง", "แก้ไขล่าสุด"
    ];

    public byte[] GenerateRegistryXlsx(IReadOnlyList<WitnessCaseSummaryDto> cases)
    {
        using var output = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = CreateStylesheet();
            stylesPart.Stylesheet.Save();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(
                new Columns(
                    Column(1, 2, 20), Column(3, 4, 24), Column(5, 6, 28),
                    Column(7, 8, 16), Column(9, 11, 28), Column(12, 13, 22)),
                sheetData,
                new AutoFilter { Reference = $"A1:M{Math.Max(1, cases.Count + 1)}" });

            sheetData.Append(new Row(Headers.Select(value => TextCell(value, 1U))));
            foreach (var item in cases)
            {
                sheetData.Append(new Row(
                    TextCell(item.RequestNo), TextCell(item.IntakeFormCode),
                    TextCell(item.WitnessDisplayName), TextCell(item.PetitionerDisplayName),
                    TextCell(item.StatusLabel), TextCell(item.Status),
                    TextCell(item.IsUrgent ? "กรณีเร่งด่วน" : "กรณีปกติ"),
                    TextCell(item.RiskLevel), TextCell(item.CurrentOwnerName),
                    TextCell(item.CurrentOwnerRole), TextCell(item.NextAction),
                    TextCell(ToThaiDateTime(item.CreatedAt)), TextCell(ToThaiDateTime(item.UpdatedAt))));
            }

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1U,
                Name = "ทะเบียนคำร้อง"
            });
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }
        return output.ToArray();
    }

    private static Cell TextCell(string? value, uint styleIndex = 0U)
        => new()
        {
            DataType = CellValues.InlineString,
            StyleIndex = styleIndex,
            InlineString = new InlineString(new Text(value ?? string.Empty)
            {
                Space = SpaceProcessingModeValues.Preserve
            })
        };

    private static Column Column(uint min, uint max, double width)
        => new() { Min = min, Max = max, Width = width, CustomWidth = true };

    private static Stylesheet CreateStylesheet()
        => new(
            new Fonts(
                new Font(new FontName { Val = "TH Sarabun New" }, new FontSize { Val = 16D }),
                new Font(new Bold(), new Color { Rgb = "FFFFFFFF" },
                    new FontName { Val = "TH Sarabun New" }, new FontSize { Val = 16D })),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FF12345B" })
                { PatternType = PatternValues.Solid })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat
                {
                    FontId = 0U, FillId = 0U, BorderId = 0U,
                    Alignment = new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }
                },
                new CellFormat
                {
                    FontId = 1U, FillId = 2U, BorderId = 0U, ApplyFill = true, ApplyFont = true,
                    Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = true }
                }));

    private static string ToThaiDateTime(DateTimeOffset value)
    {
        var local = value.ToOffset(TimeSpan.FromHours(7));
        return $"{local:dd/MM}/{local.Year + 543:0000} {local:HH:mm}";
    }
}
