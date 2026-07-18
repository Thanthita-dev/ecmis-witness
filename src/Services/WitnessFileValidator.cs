namespace EcmisWitness.Api.Services;

public sealed class WitnessFileValidator(IConfiguration configuration)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    public long MaxBytes => configuration.GetValue<long?>("Witness:MaxAttachmentBytes") ?? 50L * 1024 * 1024;

    public void Validate(string fileName, string contentType, ReadOnlySpan<byte> content)
    {
        var extension = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException("รองรับเฉพาะ Word, Excel, PDF และไฟล์รูปภาพ");
        if (content.Length == 0)
            throw new InvalidOperationException("ไฟล์แนบไม่มีเนื้อหา");
        if (content.Length > MaxBytes)
            throw new InvalidOperationException($"ไฟล์แนบต้องมีขนาดไม่เกิน {MaxBytes / 1024 / 1024} MB");
        if (!SignatureMatches(extension, content))
            throw new InvalidOperationException("ชนิดเนื้อหาไฟล์ไม่ตรงกับนามสกุล กรุณาตรวจสอบไฟล์อีกครั้ง");

        _ = contentType;
    }

    private static bool SignatureMatches(string extension, ReadOnlySpan<byte> content)
    {
        if (content.Length < 4)
            return false;
        var isZip = content[0] == 0x50 && content[1] == 0x4B;
        var isOle = content.Length >= 8
                    && content[0] == 0xD0 && content[1] == 0xCF
                    && content[2] == 0x11 && content[3] == 0xE0;
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => content[0] == 0x25 && content[1] == 0x50 && content[2] == 0x44 && content[3] == 0x46,
            ".png" => content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47,
            ".jpg" or ".jpeg" => content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            ".gif" => content[0] == 0x47 && content[1] == 0x49 && content[2] == 0x46,
            ".webp" => content.Length >= 12
                       && content[0] == 0x52 && content[1] == 0x49 && content[2] == 0x46 && content[3] == 0x46
                       && content[8] == 0x57 && content[9] == 0x45 && content[10] == 0x42 && content[11] == 0x50,
            ".doc" or ".xls" => isOle,
            ".docx" or ".xlsx" => isZip,
            _ => false
        };
    }
}
