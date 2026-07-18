using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EcmisWitness.Api.Forms;
using EcmisWitness.Api.Contracts;

namespace EcmisWitness.Api.Services;

public sealed class WitnessDocumentService
{
    public byte[] GenerateOfficialDocx(WitnessFormDto form)
    {
        var definition = WitnessProtectionFormCatalog.Get(form.FormNumber);
        using var output = new MemoryStream();
        using (var document = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document();
            AddStyles(mainPart);

            var body = mainPart.Document.AppendChild(new Body());
            body.Append(Paragraph("ลับ", "Confidential", JustificationValues.Center));
            body.Append(Paragraph($"แบบ {definition.Code}", "FormCode", JustificationValues.Right));
            body.Append(Paragraph(definition.Title, "TitleThai", JustificationValues.Center));
            body.Append(Paragraph($"เลขคำร้อง {form.RequestNo}", "Metadata", JustificationValues.Right));

            foreach (var section in definition.Sections)
            {
                body.Append(Paragraph(section.Title, "HeadingThai"));
                var table = new Table();
                table.AppendChild(new TableProperties(
                    new TableWidth { Width = "10000", Type = TableWidthUnitValues.Pct },
                    new TableBorders(
                        Border<TopBorder>(), Border<LeftBorder>(), Border<BottomBorder>(),
                        Border<RightBorder>(), Border<InsideHorizontalBorder>(), Border<InsideVerticalBorder>())));
                foreach (var field in section.Fields)
                {
                    var value = form.Values.GetValueOrDefault(field.Key, "");
                    table.Append(new TableRow(
                        Cell(field.Label + (field.Required ? " *" : ""), 3600, bold: true),
                        CellValue(field, value, 6400, form.Values)));
                }
                body.Append(table);
            }

            var currentOpinions = form.Opinions
                .Where(item => item.FormVersion == form.Version)
                .OrderBy(item => item.CreatedAt)
                .ToArray();
            if (currentOpinions.Length > 0)
            {
                body.Append(Paragraph("ความเห็นตามลำดับชั้น", "HeadingThai"));
                var opinionTable = new Table();
                opinionTable.AppendChild(new TableProperties(
                    new TableWidth { Width = "10000", Type = TableWidthUnitValues.Pct },
                    new TableBorders(
                        Border<TopBorder>(), Border<LeftBorder>(), Border<BottomBorder>(),
                        Border<RightBorder>(), Border<InsideHorizontalBorder>(), Border<InsideVerticalBorder>())));
                opinionTable.Append(new TableRow(
                    Cell("ลำดับ/ผู้ให้ความเห็น", 3200, true),
                    Cell("ความเห็น", 4800, true),
                    Cell("วันเวลา", 2000, true)));
                foreach (var opinion in currentOpinions)
                {
                    opinionTable.Append(new TableRow(
                        Cell($"{opinion.OpinionPurpose}\n{opinion.ActorName}\n{opinion.ActorPosition}", 3200),
                        Cell(opinion.OpinionText, 4800),
                        Cell(ToThaiDateTime(opinion.CreatedAt), 2000)));
                }
                body.Append(opinionTable);
            }

            body.Append(Paragraph("ลายมือชื่ออิเล็กทรอนิกส์", "HeadingThai"));
            if (form.Signatures.Count == 0)
            {
                body.Append(Paragraph("ยังไม่มีผู้ลงนาม", "BodyThai"));
            }
            else
            {
                var signatureTable = new Table();
                signatureTable.AppendChild(new TableProperties(
                    new TableWidth { Width = "10000", Type = TableWidthUnitValues.Pct },
                    new TableBorders(
                        Border<TopBorder>(), Border<LeftBorder>(), Border<BottomBorder>(),
                        Border<RightBorder>(), Border<InsideHorizontalBorder>(), Border<InsideVerticalBorder>())));
                signatureTable.Append(new TableRow(
                    Cell("ผู้ลงนาม/หน้าที่", 2600, true), Cell("ตำแหน่ง/บทบาท", 2600, true),
                    Cell("วันเวลา", 2200, true), Cell("หลักฐานยืนยัน", 2600, true)));
                foreach (var signature in form.Signatures.Where(item => item.FormVersion == form.Version))
                {
                    signatureTable.Append(new TableRow(
                        Cell($"{signature.SignerName}\n{signature.SignerPurpose}", 2600),
                        Cell($"{signature.SignerPosition}\n{signature.SignerRole}", 2600),
                        Cell(ToThaiDateTime(signature.SignedAt), 2200),
                        Cell($"{signature.VerificationMethod}\n{signature.EvidenceReference}\nHash: {signature.DocumentHash}", 2600)));
                }
                body.Append(signatureTable);
            }

            body.Append(Paragraph($"เอกสารรุ่น {form.Version} สร้างจากข้อมูลที่บันทึกใน E-CMIS เมื่อ {ToThaiDateTime(DateTimeOffset.UtcNow)}", "FooterNote"));
            body.Append(new SectionProperties(
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 1134, Right = 1134U, Bottom = 1134, Left = 1134U }));
            mainPart.Document.Save();
        }
        return output.ToArray();
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            Style("Normal", "Normal", 32, false),
            Style("BodyThai", "เนื้อหา", 32, false),
            Style("TitleThai", "ชื่อแบบ", 40, true),
            Style("HeadingThai", "หัวข้อ", 34, true),
            Style("FormCode", "รหัสแบบ", 32, true),
            Style("Metadata", "ข้อมูลกำกับ", 28, false),
            Style("Confidential", "ชั้นความลับ", 34, true, "C00000"),
            Style("FooterNote", "หมายเหตุท้ายเอกสาร", 22, false, "555555"));
        stylesPart.Styles.Save();
    }

    private static Style Style(string id, string name, int size, bool bold, string color = "111827")
    {
        var runProperties = new StyleRunProperties(
            new RunFonts { Ascii = "TH Sarabun New", HighAnsi = "TH Sarabun New", EastAsia = "TH Sarabun New", ComplexScript = "TH Sarabun New" },
            new Color { Val = color },
            new FontSize { Val = size.ToString() },
            new FontSizeComplexScript { Val = size.ToString() });
        if (bold)
            runProperties.Append(new Bold(), new BoldComplexScript());
        return new Style(new StyleName { Val = name }, runProperties)
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            CustomStyle = id != "Normal",
            Default = id == "Normal"
        };
    }

    private static Paragraph Paragraph(string text, string style, JustificationValues? justification = null)
    {
        var properties = new ParagraphProperties(new ParagraphStyleId { Val = style });
        if (justification.HasValue)
            properties.Append(new Justification { Val = justification.Value });
        return new Paragraph(properties, new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static TableCell CellValue(
        WitnessFormFieldDefinition field,
        string value,
        int width,
        IReadOnlyDictionary<string, string> allValues)
    {
        if (field.Type == WitnessFormFieldType.Checkbox)
            return Cell(IsChecked(value) ? "☒ เลือก" : "☐ ไม่เลือก", width);
        if (field.Type == WitnessFormFieldType.MultiSelect && TryReadStringList(value, out var selected))
        {
            var display = selected.Count == 0 ? "-" : string.Join("\n", selected.Select(item => $"☒ {item}"));
            return Cell(AppendOther(field, display, allValues), width);
        }
        if (field.Type == WitnessFormFieldType.Address && TryReadObject(value, out var address))
        {
            var ordered = field.Columns ?? [];
            var display = string.Join(" ", ordered
                .Select(column => address.GetValueOrDefault(column.Key, ""))
                .Where(item => !string.IsNullOrWhiteSpace(item)));
            return Cell(string.IsNullOrWhiteSpace(display) ? "-" : display, width);
        }
        if (TryReadRows(value, out var rows))
        {
            var display = rows.Count == 0
                ? "-"
                : string.Join("\n", rows.Select((row, index) => $"{index + 1}. {string.Join(" | ", row.Values.Where(item => !string.IsNullOrWhiteSpace(item)))}"));
            return Cell(display, width);
        }
        return Cell(string.IsNullOrWhiteSpace(value) ? "-" : AppendOther(field, value, allValues), width);
    }

    private static string AppendOther(
        WitnessFormFieldDefinition field,
        string value,
        IReadOnlyDictionary<string, string> allValues)
    {
        if (!value.Contains("อื่น", StringComparison.OrdinalIgnoreCase)
            || !allValues.TryGetValue(field.Key + "_other", out var other)
            || string.IsNullOrWhiteSpace(other)) return value;
        return $"{value}: {other}";
    }

    private static TableCell Cell(string text, int width, bool bold = false)
    {
        var runProperties = new RunProperties(
            new RunFonts { Ascii = "TH Sarabun New", HighAnsi = "TH Sarabun New", EastAsia = "TH Sarabun New", ComplexScript = "TH Sarabun New" },
            new FontSize { Val = "28" }, new FontSizeComplexScript { Val = "28" });
        if (bold)
            runProperties.Append(new Bold(), new BoldComplexScript());
        var paragraphs = text.Split('\n').Select(line => new Paragraph(new Run(runProperties.CloneNode(true), new Text(line)))).ToArray();
        var cell = new TableCell(new TableCellProperties(
            new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Pct },
            new TableCellMargin(
                new TopMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                new StartMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                new EndMargin { Width = "90", Type = TableWidthUnitValues.Dxa })));
        cell.Append(paragraphs);
        return cell;
    }

    private static T Border<T>() where T : BorderType, new()
        => new() { Val = BorderValues.Single, Size = 4U, Color = "9CA3AF" };

    private static bool IsChecked(string value)
        => value is "true" or "1" or "yes" or "on" or "เลือก";

    private static bool TryReadRows(string value, out List<Dictionary<string, string>> rows)
    {
        rows = [];
        if (string.IsNullOrWhiteSpace(value) || !value.TrimStart().StartsWith('['))
            return false;
        try
        {
            rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(value) ?? [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadStringList(string value, out List<string> items)
    {
        items = [];
        try
        {
            items = JsonSerializer.Deserialize<List<string>>(value) ?? [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadObject(string value, out Dictionary<string, string> item)
    {
        item = [];
        try
        {
            item = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ToThaiDateTime(DateTimeOffset value)
    {
        var local = value.ToOffset(TimeSpan.FromHours(7));
        return $"{local:dd/MM}/{local.Year + 543:0000} {local:HH:mm}";
    }
}
