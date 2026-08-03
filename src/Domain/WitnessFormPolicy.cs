using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EcmisWitness.Api.Forms;
using EcmisWitness.Api.Security;

namespace EcmisWitness.Api.Domain;

public sealed class WitnessFormPolicy
{
    private static readonly IReadOnlyDictionary<int, IReadOnlySet<string>> FormEditPermissions =
        new Dictionary<int, IReadOnlySet<string>>
        {
            [1] = Permissions(WitnessPermissions.Create),
            [2] = Permissions(WitnessPermissions.OfficerReview),
            [3] = Permissions(WitnessPermissions.OfficerReview),
            [4] = Permissions(WitnessPermissions.OfficerReview, WitnessPermissions.SupervisorReview,
                WitnessPermissions.DirectorReview),
            [5] = Permissions(WitnessPermissions.DirectorReview),
            [6] = Permissions(WitnessPermissions.OfficerReview, WitnessPermissions.SupervisorReview,
                WitnessPermissions.DirectorReview, WitnessPermissions.ExternalReceive),
            [7] = Permissions(WitnessPermissions.ProtectionManage),
            [8] = Permissions(WitnessPermissions.ProtectionManage, WitnessPermissions.ExternalReceive),
            [9] = Permissions(WitnessPermissions.NoticeManage),
            [10] = Permissions(WitnessPermissions.NoticeManage),
            [11] = Permissions(WitnessPermissions.ProtectionManage),
            [12] = Permissions(WitnessPermissions.ProtectionManage),
            [13] = Permissions(WitnessPermissions.ProtectionManage),
            [14] = Permissions(WitnessPermissions.ProtectionManage, WitnessPermissions.SupervisorReview,
                WitnessPermissions.DirectorReview, WitnessPermissions.ExternalReceive),
            [15] = Permissions(WitnessPermissions.ProtectionManage, WitnessPermissions.DirectorReview,
                WitnessPermissions.ExternalReceive),
            [16] = Permissions(WitnessPermissions.ExternalReceive),
            [17] = Permissions(WitnessPermissions.NoticeManage)
        };

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> OwnedOpinionFields =
        new Dictionary<int, IReadOnlyDictionary<string, string>>
        {
            [4] = OpinionFields(
                ("officer_recommendation", WitnessPermissions.OfficerReview),
                ("supervisor_opinion", WitnessPermissions.SupervisorReview),
                ("director_opinion", WitnessPermissions.DirectorReview)),
            [6] = OpinionFields(
                ("officer_recommendation", WitnessPermissions.OfficerReview),
                ("supervisor_opinion", WitnessPermissions.SupervisorReview),
                ("director_opinion", WitnessPermissions.DirectorReview),
                ("deputy_secretary_opinion", WitnessPermissions.ExternalReceive),
                ("secretary_opinion", WitnessPermissions.ExternalReceive)),
            [14] = OpinionFields(
                ("supervisor_opinion", WitnessPermissions.SupervisorReview),
                ("director_opinion", WitnessPermissions.DirectorReview),
                ("deputy_secretary_opinion", WitnessPermissions.ExternalReceive),
                ("secretary_opinion", WitnessPermissions.ExternalReceive)),
            [15] = OpinionFields(
                ("team_leader_opinion", WitnessPermissions.ProtectionManage),
                ("director_opinion", WitnessPermissions.DirectorReview),
                ("deputy_secretary_opinion", WitnessPermissions.ExternalReceive),
                ("secretary_opinion", WitnessPermissions.ExternalReceive))
        };

    public void EnsureCanEdit(int formNumber, WitnessUserContext user)
    {
        _ = WitnessProtectionFormCatalog.Get(formNumber);
        if (!FormEditPermissions[formNumber].Any(user.HasExplicitPermission))
            throw new WitnessAuthorizationException($"ไม่มีสิทธิ์แก้ไขแบบ คบ.{formNumber}");
    }

    public IReadOnlyDictionary<string, string> AuthorizeAndMergeValues(
        int formNumber,
        IReadOnlyDictionary<string, string> submittedValues,
        IReadOnlyDictionary<string, string>? existingValues,
        WitnessUserContext user,
        string caseStatus,
        string urgentStatus)
    {
        EnsureCanEdit(formNumber, user);
        var activePermissions = ActiveEditPermissions(formNumber, caseStatus, urgentStatus);
        if (!activePermissions.Any(user.HasExplicitPermission))
            throw new WitnessAuthorizationException($"ไม่มีสิทธิ์แก้ไขแบบ คบ.{formNumber} ในขั้นตอนปัจจุบัน");

        if (!OwnedOpinionFields.TryGetValue(formNumber, out var ownedFields))
            return new Dictionary<string, string>(submittedValues, StringComparer.OrdinalIgnoreCase);

        var canEditBaseFields = CanEditBaseFields(formNumber, caseStatus, urgentStatus, user);
        if (existingValues is null && !canEditBaseFields)
            throw new WitnessAuthorizationException($"ต้องให้ผู้จัดทำแบบ คบ.{formNumber} บันทึกข้อมูลก่อนความเห็นตามลำดับชั้น");

        var result = existingValues is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existingValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in submittedValues)
        {
            if (string.Equals(key, "request_no", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ownedFields.TryGetValue(key, out var ownerPermission))
            {
                if (!activePermissions.Contains(ownerPermission)
                    || !user.HasExplicitPermission(ownerPermission))
                    throw new WitnessAuthorizationException(
                        $"ไม่มีสิทธิ์บันทึกฟิลด์ “{FieldLabel(formNumber, key)}” ในแบบ คบ.{formNumber}");
            }
            else if (!canEditBaseFields)
            {
                throw new WitnessAuthorizationException(
                    $"บทบาทปัจจุบันแก้ไขได้เฉพาะความเห็นของตนในแบบ คบ.{formNumber}");
            }

            result[key] = value;
        }
        return result;
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

    public static IReadOnlySet<string> RequiredEditPermissions(int formNumber)
        => FormEditPermissions.TryGetValue(formNumber, out var permissions)
            ? permissions
            : Permissions();

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

    public void EnsureCanSignPurpose(int formNumber, string purpose, WitnessUserContext user)
    {
        if (formNumber == 1 && string.Equals(purpose, "ผู้ยื่นคำร้อง", StringComparison.Ordinal))
            throw new WitnessAuthorizationException(
                "ลายมือชื่อผู้ยื่นคำร้องต้องลงนามโดยผู้ยื่นผ่านลิงก์แบบ คบ.1");
        EnsureCanSignPurpose(purpose, user);
    }

    private static bool HasExplicitOperationalPermission(WitnessUserContext user, string permission)
        => user.HasExplicitPermission(permission);

    private static IReadOnlySet<string> ActiveEditPermissions(
        int formNumber,
        string caseStatus,
        string urgentStatus)
        => (formNumber, caseStatus, urgentStatus) switch
        {
            (4, WitnessStatuses.StaffReview, "awaiting_kb4") => Permissions(WitnessPermissions.OfficerReview),
            (4, WitnessStatuses.StaffReview, "supervisor_review") => Permissions(WitnessPermissions.SupervisorReview),
            (4, WitnessStatuses.StaffReview, "director_review") => Permissions(WitnessPermissions.DirectorReview),
            (6, WitnessStatuses.StaffReview, _) => Permissions(WitnessPermissions.OfficerReview),
            (6, WitnessStatuses.SupervisorReview, _) => Permissions(WitnessPermissions.SupervisorReview),
            (6, WitnessStatuses.DirectorReview, _) => Permissions(WitnessPermissions.DirectorReview),
            (6, WitnessStatuses.ExternalPending, _) => Permissions(WitnessPermissions.ExternalReceive),
            (14, WitnessStatuses.ProtectionActive, _) => Permissions(WitnessPermissions.ProtectionManage),
            (14, WitnessStatuses.ExtensionSupervisorReview, _) => Permissions(WitnessPermissions.SupervisorReview),
            (14, WitnessStatuses.ExtensionDirectorReview, _) => Permissions(WitnessPermissions.DirectorReview),
            (14, WitnessStatuses.ExtensionExternalPending, _) => Permissions(WitnessPermissions.ExternalReceive),
            (15, WitnessStatuses.ProtectionActive, _) => Permissions(
                WitnessPermissions.ProtectionManage, WitnessPermissions.DirectorReview),
            (15, WitnessStatuses.TerminationExternalPending, _) => Permissions(WitnessPermissions.ExternalReceive),
            _ => RequiredEditPermissions(formNumber)
        };

    private static bool CanEditBaseFields(
        int formNumber,
        string caseStatus,
        string urgentStatus,
        WitnessUserContext user)
        => (formNumber, caseStatus, urgentStatus) switch
        {
            (4, WitnessStatuses.StaffReview, "awaiting_kb4")
                => user.HasExplicitPermission(WitnessPermissions.OfficerReview),
            (6, WitnessStatuses.StaffReview, _)
                => user.HasExplicitPermission(WitnessPermissions.OfficerReview),
            (14, WitnessStatuses.ProtectionActive, _)
                => user.HasExplicitPermission(WitnessPermissions.ProtectionManage),
            (15, WitnessStatuses.ProtectionActive, _)
                => user.HasExplicitPermission(WitnessPermissions.ProtectionManage),
            _ => false
        };

    private static string FieldLabel(int formNumber, string key)
        => WitnessProtectionFormCatalog.Get(formNumber).Fields
               .FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))?.Label
           ?? key;

    private static IReadOnlySet<string> Permissions(params string[] permissions)
        => new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> OpinionFields(
        params (string Field, string Permission)[] fields)
        => fields.ToDictionary(item => item.Field, item => item.Permission, StringComparer.OrdinalIgnoreCase);

    public void Validate(int formNumber, IReadOnlyDictionary<string, string> values, bool completed)
    {
        var definition = WitnessProtectionFormCatalog.Get(formNumber);
        ValidatePersistentInvariants(formNumber, values);
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

    public void ValidatePersistentInvariants(
        int formNumber,
        IReadOnlyDictionary<string, string> values)
    {
        switch (formNumber)
        {
            case 1:
                ValidateCitizenIdIfPresent(values, "petitioner_citizen_id");
                ValidateCitizenIdIfPresent(values, "witness_citizen_id");
                ValidateCitizenIdsInRepeatingGroup(values, "related_people", "identity_no");
                ValidateDatePair(values, "petitioner_card_issued", "petitioner_card_expired");
                ValidateDatePair(values, "witness_card_issued", "witness_card_expired");
                break;
            case 3:
                if (values.TryGetValue("identity_type", out var identityType)
                    && identityType.Contains("ประชาชน", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateCitizenIdIfPresent(values, "identity_no");
                }
                ValidateDatePair(values, "identity_issued", "identity_expired");
                break;
            case 6:
                ValidateCitizenIdIfPresent(values, "witness_id");
                break;
            case 7:
            case 11:
                ValidateCitizenIdIfPresent(values, "citizen_id");
                break;
            case 4:
                ValidateProtectionPeriod(values, "start_date", "end_date");
                break;
        }
    }

    public static bool IsValidThaiCitizenId(string value)
    {
        if (value.Length != 13 || value.Any(character => character is < '0' or > '9'))
            return false;

        var sum = 0;
        for (var index = 0; index < 12; index++)
            sum += (value[index] - '0') * (13 - index);
        var expectedCheckDigit = (11 - (sum % 11)) % 10;
        return value[12] - '0' == expectedCheckDigit;
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

    private static void ValidateCitizenIdIfPresent(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
            return;
        if (value.Length != 13 || value.Any(character => character is < '0' or > '9'))
            throw new WitnessWorkflowException("เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก");
        if (!IsValidThaiCitizenId(value))
            throw new WitnessWorkflowException("เลขประจำตัวประชาชนไม่ถูกต้อง");
    }

    private static void ValidateCitizenIdsInRepeatingGroup(
        IReadOnlyDictionary<string, string> values,
        string groupKey,
        string citizenIdKey)
    {
        if (!values.TryGetValue(groupKey, out var json) || string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? [];
            foreach (var row in rows)
                ValidateCitizenIdIfPresent(row, citizenIdKey);
        }
        catch (JsonException)
        {
            throw new WitnessWorkflowException("ข้อมูลบุคคลที่เกี่ยวข้องมีรูปแบบไม่ถูกต้อง");
        }
    }

    private static void ValidateDatePair(
        IReadOnlyDictionary<string, string> values,
        string issuedKey,
        string expiryKey)
    {
        var hasIssued = values.TryGetValue(issuedKey, out var issuedRaw)
                        && !string.IsNullOrEmpty(issuedRaw);
        var hasExpiry = values.TryGetValue(expiryKey, out var expiryRaw)
                        && !string.IsNullOrEmpty(expiryRaw);
        DateOnly issued = default;
        DateOnly expiry = default;
        if (hasIssued && !WitnessIsoDate.TryParse(issuedRaw, out issued))
            throw new WitnessWorkflowException("วันออกบัตรต้องอยู่ในรูปแบบ yyyy-MM-dd");
        if (hasExpiry && !WitnessIsoDate.TryParse(expiryRaw, out expiry))
            throw new WitnessWorkflowException("วันหมดอายุต้องอยู่ในรูปแบบ yyyy-MM-dd");
        if (hasIssued && hasExpiry && expiry < issued)
            throw new WitnessWorkflowException("วันหมดอายุต้องไม่ก่อนวันออกบัตร");
    }

    private static void ValidateProtectionPeriod(
        IReadOnlyDictionary<string, string> values,
        string startKey,
        string endKey)
    {
        var hasStart = values.TryGetValue(startKey, out var startRaw)
                       && !string.IsNullOrEmpty(startRaw);
        var hasEnd = values.TryGetValue(endKey, out var endRaw)
                     && !string.IsNullOrEmpty(endRaw);
        DateOnly start = default;
        DateOnly end = default;
        if (hasStart && !WitnessIsoDate.TryParse(startRaw, out start))
            throw new WitnessWorkflowException("วันเริ่มต้นการคุ้มครองต้องอยู่ในรูปแบบ yyyy-MM-dd");
        if (hasEnd && !WitnessIsoDate.TryParse(endRaw, out end))
            throw new WitnessWorkflowException("วันสิ้นสุดการคุ้มครองต้องอยู่ในรูปแบบ yyyy-MM-dd");
        if (hasStart && hasEnd && end < start)
        {
            throw new WitnessWorkflowException(
                "วันสิ้นสุดการคุ้มครองต้องไม่ก่อนวันเริ่มต้นการคุ้มครอง");
        }
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
        return values.TryGetValue(key, out var raw) && WitnessIsoDate.TryParse(raw, out value);
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
