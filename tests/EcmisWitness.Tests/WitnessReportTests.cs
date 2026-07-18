using DocumentFormat.OpenXml.Packaging;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Services;

namespace EcmisWitness.Tests;

public sealed class WitnessReportTests
{
    [Fact]
    public void Registry_export_is_a_real_xlsx_with_case_data()
    {
        var item = new WitnessCaseSummaryDto(
            Guid.NewGuid(), "WP-2569-000001", 1, "คบ.1", "นางสาว ท***", "นาย ผ***",
            "staff_review", "รอตรวจคำร้อง", "officer", "เจ้าหน้าที่ทดสอบ",
            "สูง", true, "awaiting_kb4", 3,
            DateTimeOffset.Parse("2026-07-15T08:00:00+07:00"),
            DateTimeOffset.Parse("2026-07-15T09:00:00+07:00"), "ตรวจคำร้อง");

        using var stream = new MemoryStream(new WitnessReportService().GenerateRegistryXlsx([item]));
        using var workbook = SpreadsheetDocument.Open(stream, false);
        var worksheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var text = string.Concat(worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>()
            .Select(value => value.Text));

        Assert.Equal("ทะเบียนคำร้อง", workbook.WorkbookPart.Workbook.Sheets!
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single().Name!.Value);
        Assert.Contains("WP-2569-000001", text);
        Assert.Contains("รอตรวจคำร้อง", text);
        Assert.Contains("กรณีเร่งด่วน", text);
    }
}
