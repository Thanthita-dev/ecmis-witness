using DocumentFormat.OpenXml.Packaging;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Services;
using Microsoft.Extensions.Configuration;

namespace EcmisWitness.Tests;

public sealed class WitnessAttachmentAndDocumentTests
{
    [Fact]
    public void Attachment_rejects_mismatched_extension_and_content()
    {
        var validator = Validator();
        Assert.Throws<InvalidOperationException>(() =>
            validator.Validate("หลักฐาน.pdf", "application/pdf", new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
    }

    [Theory]
    [InlineData("หลักฐาน.pdf", "application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 })]
    [InlineData("ภาพ.jpg", "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("แบบ.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    public void Attachment_accepts_supported_real_signatures(string name, string type, byte[] content)
        => Validator().Validate(name, type, content);

    [Fact]
    public void Generated_docx_is_a_real_openxml_document()
    {
        var form = new WitnessFormDto(Guid.NewGuid(), Guid.NewGuid(), "WP-2569-000001", 9, 1, "signed",
            new Dictionary<string, string> { ["letter_no"] = "ปปท 001/2569", ["letter_date"] = "2026-07-14", ["recipient"] = "นาย ก." },
            [], DateTimeOffset.UtcNow, "เจ้าหน้าที่", 1, []);
        var bytes = new WitnessDocumentService().GenerateOfficialDocx(form);

        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, false);
        var text = document.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("คบ.9", text);
        Assert.Contains("WP-2569-000001", text);
    }

    [Fact]
    public void Generated_docx_includes_text_entered_for_other_option()
    {
        var form = new WitnessFormDto(Guid.NewGuid(), Guid.NewGuid(), "WP-2569-000002", 1, 1, "completed",
            new Dictionary<string, string>
            {
                ["petitioner_prefix"] = "อื่น ๆ",
                ["petitioner_prefix_other"] = "ศาสตราจารย์"
            },
            [], DateTimeOffset.UtcNow, "เจ้าหน้าที่", 1, []);

        using var stream = new MemoryStream(new WitnessDocumentService().GenerateOfficialDocx(form));
        using var document = WordprocessingDocument.Open(stream, false);

        Assert.Contains("อื่น ๆ: ศาสตราจารย์", document.MainDocumentPart!.Document.Body!.InnerText);
    }

    [Fact]
    public void Generated_docx_includes_append_only_hierarchical_opinions()
    {
        var form = new WitnessFormDto(Guid.NewGuid(), Guid.NewGuid(), "WP-2569-000003", 6, 2, "completed",
            new Dictionary<string, string> { ["office"] = "สำนักงาน ป.ป.ท." },
            [], DateTimeOffset.UtcNow, "เจ้าหน้าที่", 7,
            [new WitnessFormOpinionDto(Guid.NewGuid(), 6, 2, "ผู้บังคับบัญชาชั้นต้น",
                "ตรวจสอบข้อเท็จจริงแล้ว เห็นควรเสนอ ผอ.", "หัวหน้ากลุ่มทดสอบ", "หัวหน้ากลุ่มงาน",
                "supervisor", DateTimeOffset.UtcNow)]);

        using var stream = new MemoryStream(new WitnessDocumentService().GenerateOfficialDocx(form));
        using var document = WordprocessingDocument.Open(stream, false);
        var text = document.MainDocumentPart!.Document.Body!.InnerText;

        Assert.Contains("ความเห็นตามลำดับชั้น", text);
        Assert.Contains("ตรวจสอบข้อเท็จจริงแล้ว เห็นควรเสนอ ผอ.", text);
        Assert.Contains("หัวหน้ากลุ่มทดสอบ", text);
    }

    [Fact]
    public void Persistence_schema_contains_durable_case_file_and_audit_tables()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Migrations/001_witness_schema.sql"));
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE IF NOT EXISTS witness.cases", sql);
        Assert.Contains("content bytea NOT NULL", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS witness.form_versions", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS witness.audit_events", sql);
        Assert.DoesNotContain("in-memory", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Notice_delivery_schema_persists_sent_received_and_proof_data()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Migrations/002_notice_delivery.sql"));
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE IF NOT EXISTS witness.notice_deliveries", sql);
        Assert.Contains("delivery_channel", sql);
        Assert.Contains("receipt_proof_attachment_id", sql);
        Assert.Contains("received_at", sql);
    }

    [Fact]
    public void Signature_schema_records_official_signer_purpose()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Migrations/003_signature_purpose.sql"));
        Assert.Contains("signer_purpose", File.ReadAllText(path));
    }

    [Fact]
    public void Main_case_link_schema_persists_relationship_and_risk()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Migrations/004_case_link.sql"));
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE IF NOT EXISTS witness.case_links", sql);
        Assert.Contains("relationship_reason", sql);
        Assert.Contains("risk_level", sql);
    }

    [Fact]
    public void Notification_schema_persists_expiry_and_important_report_alerts()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Migrations/006_notifications.sql"));
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE IF NOT EXISTS witness.notifications", sql);
        Assert.Contains("dedupe_key", sql);
        Assert.Contains("acknowledged_at", sql);
    }

    private static WitnessFileValidator Validator()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Witness:MaxAttachmentBytes"] = "52428800"
        }).Build();
        return new WitnessFileValidator(config);
    }
}
