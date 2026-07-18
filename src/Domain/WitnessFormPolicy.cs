using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EcmisWitness.Api.Forms;
using EcmisWitness.Api.Security;

namespace EcmisWitness.Api.Domain;

public sealed class WitnessFormPolicy
{
    private static readonly IReadOnlyDictionary<int, string> FormPermissions =
        new Dictionary<int, string>
        {
            [1] = WitnessPermissions.Create,
            [2] = WitnessPermissions.OfficerReview,
            [3] = WitnessPermissions.OfficerReview,
            [4] = WitnessPermissions.OfficerReview,
            [5] = WitnessPermissions.DirectorReview,
            [6] = WitnessPermissions.Edit,
            [7] = WitnessPermissions.ProtectionManage,
            [8] = WitnessPermissions.ProtectionManage,
            [9] = WitnessPermissions.NoticeManage,
            [10] = WitnessPermissions.NoticeManage,
            [11] = WitnessPermissions.ProtectionManage,
            [12] = WitnessPermissions.ProtectionManage,
            [13] = WitnessPermissions.ProtectionManage,
            [14] = WitnessPermissions.ProtectionManage,
            [15] = WitnessPermissions.ProtectionManage,
            [16] = WitnessPermissions.NoticeManage,
            [17] = WitnessPermissions.NoticeManage
        };

    public void EnsureCanEdit(int formNumber, WitnessUserContext user)
    {
        _ = WitnessProtectionFormCatalog.Get(formNumber);
        if (formNumber is 8 or 16 && user.HasPermission(WitnessPermissions.ExternalReceive))
            return;
        var permission = FormPermissions[formNumber];
        if (!user.HasPermission(permission) && !user.HasPermission(WitnessPermissions.Edit))
            throw new WitnessAuthorizationException($"ไม่มีสิทธิ์แก้ไขแบบ คบ.{formNumber}");
    }

    public void EnsureCanSign(int formNumber, WitnessUserContext user)
    {
        _ = WitnessProtectionFormCatalog.Get(formNumber);
        var allowed = formNumber switch
        {
            1 => user.HasPermission(WitnessPermissions.Create) || user.HasPermission(WitnessPermissions.OfficerReview),
            2 => user.HasPermission(WitnessPermissions.OfficerReview),
            3 => user.HasPermission(WitnessPermissions.OfficerReview) || user.HasPermission(WitnessPermissions.Create),
            4 or 6 => user.HasPermission(WitnessPermissions.DirectorReview)
                                || user.HasPermission(WitnessPermissions.SupervisorReview)
                                || user.HasPermission(WitnessPermissions.OfficerReview)
                                || user.HasPermission(WitnessPermissions.ExternalReceive),
            5 => user.HasPermission(WitnessPermissions.DirectorReview) || user.HasPermission(WitnessPermissions.Create),
            7 or 11 or 12 or 13 => user.HasPermission(WitnessPermissions.ProtectionManage)
                                     || user.HasPermission(WitnessPermissions.Create),
            8 => user.HasPermission(WitnessPermissions.ProtectionManage)
                 || user.HasPermission(WitnessPermissions.ExternalReceive)
                 || user.HasPermission(WitnessPermissions.Create),
            14 => user.HasPermission(WitnessPermissions.ProtectionManage)
                  || user.HasPermission(WitnessPermissions.Create)
                  || user.HasPermission(WitnessPermissions.OfficerReview)
                  || user.HasPermission(WitnessPermissions.SupervisorReview)
                  || user.HasPermission(WitnessPermissions.DirectorReview)
                  || user.HasPermission(WitnessPermissions.ExternalReceive),
            15 => user.HasPermission(WitnessPermissions.ProtectionManage)
                  || user.HasPermission(WitnessPermissions.DirectorReview)
                  || user.HasPermission(WitnessPermissions.ExternalReceive),
            9 or 10 or 17 => user.HasPermission(WitnessPermissions.NoticeManage)
                              || user.HasPermission(WitnessPermissions.ExternalReceive),
            16 => user.HasPermission(WitnessPermissions.ExternalReceive),
            _ => false
        };
        if (!allowed)
            throw new WitnessAuthorizationException($"ไม่มีสิทธิ์ลงนามแบบ คบ.{formNumber}");
    }

    public void EnsureCanSignPurpose(string purpose, WitnessUserContext user)
    {
        var allowed = purpose switch
        {
            var text when text.Contains("External Module", StringComparison.Ordinal)
                          || text.Contains("เลขาธิการ", StringComparison.Ordinal)
                => HasExplicitOperationalPermission(user, WitnessPermissions.ExternalReceive),
            var text when text.Contains("ผู้อำนวยการ", StringComparison.Ordinal)
                          || text.Contains("ผอ.", StringComparison.Ordinal)
                => HasExplicitOperationalPermission(user, WitnessPermissions.DirectorReview),
            var text when text.Contains("ผู้บังคับบัญชา", StringComparison.Ordinal)
                => HasExplicitOperationalPermission(user, WitnessPermissions.SupervisorReview),
            var text when text.Contains("ผู้มีอำนาจลงนามหนังสือ", StringComparison.Ordinal)
                => HasExplicitOperationalPermission(user, WitnessPermissions.NoticeManage),
            var text when text.Contains("หัวหน้าชุด", StringComparison.Ordinal)
                          || text.Contains("ผู้ส่งมอบ", StringComparison.Ordinal)
                          || text.Contains("ผู้รับมอบ", StringComparison.Ordinal)
                          || text.Contains("เจ้าหน้าที่ผู้ให้ความคุ้มครอง", StringComparison.Ordinal)
                          || text.Contains("เจ้าหน้าที่ผู้ปฏิบัติ", StringComparison.Ordinal)
                => HasExplicitOperationalPermission(user, WitnessPermissions.ProtectionManage),
            var text when text.Contains("ผู้ยื่น", StringComparison.Ordinal)
                          || text.Contains("พยาน", StringComparison.Ordinal)
                          || text.Contains("ผู้ให้ถ้อยคำ", StringComparison.Ordinal)
                          || text.Contains("ผู้ขอยุติ", StringComparison.Ordinal)
                => HasExplicitOperationalPermission(user, WitnessPermissions.Create)
                   || HasExplicitOperationalPermission(user, WitnessPermissions.ProtectionManage),
            _ => HasExplicitOperationalPermission(user, WitnessPermissions.OfficerReview)
                 || HasExplicitOperationalPermission(user, WitnessPermissions.NoticeManage)
                 || HasExplicitOperationalPermission(user, WitnessPermissions.ProtectionManage)
        };
        if (!allowed)
            throw new WitnessAuthorizationException($"ไม่มีสิทธิ์ลงนามในหน้าที่ “{purpose}”");
    }

    private static bool HasExplicitOperationalPermission(WitnessUserContext user, string permission)
        => user.Permissions.Contains(permission);

    public void Validate(int formNumber, IReadOnlyDictionary<string, string> values, bool completed)
    {
        var definition = WitnessProtectionFormCatalog.Get(formNumber);
        if (!completed)
            return;

        var missing = definition.Fields
            .Where(field => field.Required)
            .Where(field => !IsFieldComplete(field, values))
            .Select(field => field.Label)
            .ToArray();
        if (missing.Length > 0)
            throw new WitnessWorkflowException($"กรุณากรอกข้อมูลบังคับ: {string.Join(", ", missing)}");

        foreach (var field in definition.Fields)
        {
            if (SelectedOther(field, values) && !IsPresent(values, field.Key + "_other"))
                throw new WitnessWorkflowException($"กรุณาระบุรายละเอียด ‘อื่น ๆ’ สำหรับ {field.Label}");
        }

        if (formNumber == 1)
        {
            RequireEither(values, "petitioner_citizen_id", "petitioner_officer_id", "เลขประจำตัวของผู้ยื่นคำร้อง");
            RequireEither(values, "witness_citizen_id", "witness_officer_id", "เลขประจำตัวของพยาน");
            if (EqualsValue(values, "threat_status", "มี") && !IsPresent(values, "threat_details"))
                throw new WitnessWorkflowException("กรุณาระบุรายละเอียดพฤติการณ์ความไม่ปลอดภัย");
        }
        if (formNumber == 3)
        {
            if (EqualsValue(values, "statement_type", "บันทึกกรณีพยานขอถอนคำร้อง"))
            {
                if (!IsPresent(values, "withdrawal_reason"))
                    throw new WitnessWorkflowException("กรุณาระบุเหตุผลและความประสงค์ขอถอนคำร้อง");
            }
            else if (!IsPresent(values, "threat_circumstances"))
            {
                throw new WitnessWorkflowException("กรุณาระบุพฤติการณ์แห่งความไม่ปลอดภัย");
            }
        }
        if (formNumber == 4)
        {
            var temporaryProtection = IsPresent(values, "proposal_5_1");
            var coordinateOtherAgency = IsPresent(values, "proposal_5_2");
            if (temporaryProtection == coordinateOtherAgency)
                throw new WitnessWorkflowException("กรุณาเลือกข้อเสนอ 5.1 หรือ 5.2 เพียงหนึ่งแนวทาง");
            if (coordinateOtherAgency && !IsPresent(values, "coordination_agency"))
                throw new WitnessWorkflowException("กรุณาระบุหน่วยงานที่ขอประสานให้การคุ้มครอง");
        }
        if (formNumber == 7)
            RequireEither(values, "citizen_id", "officer_id", "เลขประจำตัวของผู้ขอยุติ");
        if (formNumber == 8
            && (!values.TryGetValue("duties", out var duties)
                || !string.Equals(duties.Trim(), WitnessProtectionFormCatalog.Kb8StandardDuties.Trim(), StringComparison.Ordinal)))
            throw new WitnessWorkflowException("อำนาจหน้าที่ในแบบ คบ.8 ต้องเป็นข้อความมาตรฐาน 4 ข้อตามแบบทางการ");
        if (formNumber == 11)
            RequireEither(values, "citizen_id", "officer_id", "เลขประจำตัวของพยาน");
        if (formNumber == 13)
        {
            if (!HasRepeatingRows(values, "activity_log"))
                throw new WitnessWorkflowException("แบบ คบ.13 ต้องมีรายการปฏิบัติหน้าที่อย่างน้อย 1 รายการ");
            if (EqualsValue(values, "report_type", "รายงานเหตุสำคัญ/เร่งด่วน")
                && (!IsPresent(values, "incident_occurred_at") || !IsPresent(values, "incident_details")))
                throw new WitnessWorkflowException("รายงานเหตุสำคัญต้องระบุวันเวลาเกิดเหตุและรายละเอียดการตอบสนอง");
        }
        if (formNumber == 14)
            ValidateExtension(values);
    }

    public static string ComputeContentHash(IReadOnlyDictionary<string, string> values)
    {
        var canonical = JsonSerializer.Serialize(values.OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsPresent(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
           && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static bool EqualsValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string expected)
        => values.TryGetValue(key, out var value)
           && string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool SelectedOther(
        WitnessFormFieldDefinition field,
        IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(field.Key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        if (field.Type == WitnessFormFieldType.Select)
            return raw.Contains("อื่น", StringComparison.OrdinalIgnoreCase);
        if (field.Type != WitnessFormFieldType.MultiSelect)
            return false;

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(raw) ?? [])
                .Any(item => item.Contains("อื่น", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateExtension(IReadOnlyDictionary<string, string> values)
    {
        if (EqualsValue(values, "submitted_by_mode", "เจ้าหน้าที่ชุดคุ้มครองยื่นแทน")
            && !IsPresent(values, "proxy_submission_reason"))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลที่เจ้าหน้าที่ชุดคุ้มครองยื่นคำขยายเวลาแทนพยาน");

        var declaredDays = PositiveInt(values, "extension_days", "ระยะเวลาที่ขยาย");
        if (declaredDays > 90)
            throw new WitnessWorkflowException("ขยายเวลาคุ้มครองได้ครั้งละไม่เกิน 90 วัน");
        if (!TryDate(values, "extension_start", out var start)
            || !TryDate(values, "extension_end", out var end)
            || end < start)
            throw new WitnessWorkflowException("ช่วงวันที่ขยายเวลาคุ้มครองไม่ถูกต้อง");

        var calculatedDays = end.DayNumber - start.DayNumber + 1;
        if (calculatedDays > 90)
            throw new WitnessWorkflowException("ช่วงวันที่ขยายเวลาคุ้มครองต้องไม่เกิน 90 วัน");
        if (declaredDays != calculatedDays)
            throw new WitnessWorkflowException($"จำนวนวันที่ขยายต้องตรงกับช่วงวันที่เลือก ({calculatedDays} วัน)");

        var accumulatedDays = NonNegativeInt(values, "total_days", "ระยะเวลาสะสม");
        if (accumulatedDays + declaredDays > 180)
            throw new WitnessWorkflowException("ระยะเวลาคุ้มครองสะสมรวมช่วงขยายต้องไม่เกิน 180 วัน");
    }

    private static int PositiveInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        string label)
    {
        if (!values.TryGetValue(key, out var raw) || !int.TryParse(raw, out var value) || value <= 0)
            throw new WitnessWorkflowException($"กรุณาระบุ{label}เป็นจำนวนวันที่มากกว่า 0");
        return value;
    }

    private static int NonNegativeInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        string label)
    {
        if (!values.TryGetValue(key, out var raw) || !int.TryParse(raw, out var value) || value < 0)
            throw new WitnessWorkflowException($"กรุณาระบุ{label}เป็นจำนวนวันที่ถูกต้อง");
        return value;
    }

    private static bool TryDate(
        IReadOnlyDictionary<string, string> values,
        string key,
        out DateOnly value)
    {
        value = default;
        return values.TryGetValue(key, out var raw) && DateOnly.TryParse(raw, out value);
    }

    private static bool IsFieldComplete(
        WitnessFormFieldDefinition field,
        IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value))
            return false;
        if (field.Type == WitnessFormFieldType.Checkbox)
            return value is "true" or "1" or "yes" or "on" or "เลือก";
        if (field.Type == WitnessFormFieldType.MultiSelect)
        {
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(value)?.Count ?? 0) > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        if (field.Type == WitnessFormFieldType.Address)
        {
            try
            {
                var item = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [];
                return (field.Columns ?? []).Where(column => column.Required)
                    .All(column => item.TryGetValue(column.Key, out var cell) && !string.IsNullOrWhiteSpace(cell));
            }
            catch (JsonException)
            {
                return false;
            }
        }
        if (field.Type == WitnessFormFieldType.Repeating)
        {
            try
            {
                var rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(value) ?? [];
                return rows.Count > 0 && rows.All(row => (field.Columns ?? [])
                    .Where(column => column.Required)
                    .All(column => row.TryGetValue(column.Key, out var cell) && !string.IsNullOrWhiteSpace(cell)));
            }
            catch (JsonException)
            {
                return false;
            }
        }
        return true;
    }

    private static void RequireEither(
        IReadOnlyDictionary<string, string> values,
        string first,
        string second,
        string label)
    {
        if (!IsPresent(values, first) && !IsPresent(values, second))
            throw new WitnessWorkflowException($"กรุณาระบุ{label}อย่างน้อยหนึ่งประเภท");
    }

    private static bool HasRepeatingRows(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                   && document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
