using System.Globalization;
using System.Text.Json;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Security;
using EcmisWitness.Api.Forms;

namespace EcmisWitness.Tests;

public sealed class WitnessFormValidationTests
{
    [Theory]
    [InlineData(4, "ผู้บังคับบัญชาชั้นต้น")]
    [InlineData(6, "ผู้อำนวยการสำนัก/กอง")]
    [InlineData(14, "ผู้มีอำนาจจาก External Module")]
    [InlineData(15, "ผู้อำนวยการสำนัก/กอง")]
    public void Hierarchical_opinion_is_required_before_signing(int formNumber, string purpose)
        => Assert.True(WitnessOpinionPolicy.RequiresOpinion(formNumber, purpose));

    [Fact]
    public void Initial_officer_signature_does_not_require_duplicate_hierarchical_opinion()
        => Assert.False(WitnessOpinionPolicy.RequiresOpinion(6, "เจ้าหน้าที่เจ้าของเรื่อง"));

    [Theory]
    [InlineData(3, WitnessStatuses.StaffReview, "none")]
    [InlineData(8, WitnessStatuses.ExternalPending, "none")]
    [InlineData(12, WitnessStatuses.TransferAccepted, "none")]
    [InlineData(16, WitnessStatuses.TerminationExternalPending, "none")]
    [InlineData(17, WitnessStatuses.TerminationOrdered, "none")]
    [InlineData(5, WitnessStatuses.StaffReview, "director_review")]
    public void Form_can_be_saved_only_at_its_workflow_stage(int formNumber, string status, string urgentStatus)
        => Assert.True(WitnessFormStagePolicy.CanSave(formNumber, status, urgentStatus));

    [Theory]
    [InlineData(12, WitnessStatuses.ProtectionActive, "none")]
    [InlineData(16, WitnessStatuses.TerminationOrdered, "none")]
    [InlineData(17, WitnessStatuses.ProtectionActive, "none")]
    [InlineData(5, WitnessStatuses.StaffReview, "awaiting_kb4")]
    public void Form_is_rejected_outside_its_workflow_stage(int formNumber, string status, string urgentStatus)
        => Assert.Throws<WitnessWorkflowException>(() =>
            WitnessFormStagePolicy.EnsureCanSave(formNumber, status, urgentStatus));

    private readonly WitnessFormPolicy policy = new();

    [Fact]
    public void Kb1_accepts_citizen_id_or_government_officer_id()
    {
        var values = CompleteValues(1);
        values["petitioner_citizen_id"] = "";
        values["petitioner_officer_id"] = "OFF-001";
        values["witness_citizen_id"] = "";
        values["witness_officer_id"] = "OFF-002";

        policy.Validate(1, values, completed: true);
    }

    [Theory]
    [InlineData("9999999999994", null)]
    [InlineData("123456789012", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("12345678901211", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("ABC-INVALID-!", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("123456789012!", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("1-2345-67890-12-1", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData(" 9999999999994", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("9999999999994 ", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("9999999999995", "เลขประจำตัวประชาชนไม่ถูกต้อง")]
    public void Thai_citizen_id_is_validated_immediately_even_for_draft(
        string citizenId,
        string? expectedError)
    {
        var values = CompleteValues(1);
        values["petitioner_citizen_id"] = citizenId;

        if (expectedError is null)
        {
            policy.Validate(1, values, completed: false);
            return;
        }

        var error = Assert.Throws<WitnessWorkflowException>(() =>
            policy.Validate(1, values, completed: false));
        Assert.Equal(expectedError, error.Message);
    }

    [Theory]
    [InlineData(1, "petitioner_citizen_id")]
    [InlineData(1, "witness_citizen_id")]
    [InlineData(6, "witness_id")]
    [InlineData(7, "citizen_id")]
    [InlineData(11, "citizen_id")]
    public void Shared_person_citizen_id_fields_use_the_same_checksum_rule(int formNumber, string fieldKey)
    {
        var values = CompleteValues(formNumber);
        values[fieldKey] = "9999999999995";

        Assert.Equal("เลขประจำตัวประชาชนไม่ถูกต้อง",
            Assert.Throws<WitnessWorkflowException>(() => policy.Validate(formNumber, values, completed: false)).Message);
    }

    [Fact]
    public void Kb3_applies_citizen_checksum_only_when_identity_type_is_citizen_card()
    {
        var values = CompleteValues(3);
        values["identity_type"] = "บัตรประจำตัวประชาชน";
        values["identity_no"] = "9999999999995";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(3, values, completed: false));

        values["identity_type"] = "บัตรประจำตัวเจ้าหน้าที่ของรัฐ";
        policy.Validate(3, values, completed: false);
    }

    [Fact]
    public void Government_officer_id_is_not_forced_through_citizen_checksum()
    {
        var values = CompleteValues(1);
        values["petitioner_citizen_id"] = "";
        values["petitioner_officer_id"] = "OFF-A/2569";
        values["witness_citizen_id"] = "";
        values["witness_officer_id"] = "OFF-B/2569";

        policy.Validate(1, values, completed: true);
    }

    [Fact]
    public void Related_person_citizen_id_in_repeating_group_is_validated()
    {
        var values = CompleteValues(1);
        values["related_people"] = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string>
            {
                ["full_name"] = "E2E-TEST บุคคลใกล้ชิด",
                ["identity_no"] = "ABC-INVALID-!",
                ["relationship"] = "ญาติ",
                ["address"] = "ที่อยู่ทดสอบ",
                ["threat"] = "ไม่มีข้อมูลบุคคลจริง"
            }
        });

        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(1, values, completed: false));
    }

    [Theory]
    [InlineData("2026-07-20", "2026-07-19", "วันหมดอายุต้องไม่ก่อนวันออกบัตร")]
    [InlineData("2026-07-20", "2026-07-20", null)]
    [InlineData("2026-07-20", "2026-07-21", null)]
    [InlineData("2026-07-20", "", null)]
    [InlineData("", "2026-07-21", null)]
    [InlineData("", "", null)]
    [InlineData("20/07/2569", "2026-07-21", "วันออกบัตรต้องอยู่ในรูปแบบ yyyy-MM-dd")]
    [InlineData("2024-02-29", "2024-02-29", null)]
    [InlineData("2023-02-29", "2024-02-29", "วันออกบัตรต้องอยู่ในรูปแบบ yyyy-MM-dd")]
    public void Identity_card_date_pair_uses_iso_gregorian_ordering(
        string issued,
        string expired,
        string? expectedError)
    {
        var values = CompleteValues(1);
        values["petitioner_card_issued"] = issued;
        values["petitioner_card_expired"] = expired;

        if (expectedError is null)
            policy.Validate(1, values, completed: false);
        else
            Assert.Equal(expectedError,
                Assert.Throws<WitnessWorkflowException>(() => policy.Validate(1, values, completed: false)).Message);
    }

    [Theory]
    [InlineData("2026-07-20", "2026-07-19", "วันสิ้นสุดการคุ้มครองต้องไม่ก่อนวันเริ่มต้นการคุ้มครอง")]
    [InlineData("2026-07-20", "2026-07-20", null)]
    [InlineData("2026-07-20", "2026-07-21", null)]
    [InlineData("2026-07-20", "", null)]
    [InlineData("", "2026-07-21", null)]
    [InlineData("20/07/2569", "2026-07-21", "วันเริ่มต้นการคุ้มครองต้องอยู่ในรูปแบบ yyyy-MM-dd")]
    public void Kb4_protection_period_is_validated_even_for_draft(
        string start,
        string end,
        string? expectedError)
    {
        var values = CompleteValues(4);
        values["start_date"] = start;
        values["end_date"] = end;

        if (expectedError is null)
            policy.Validate(4, values, completed: false);
        else
            Assert.Equal(expectedError,
                Assert.Throws<WitnessWorkflowException>(() => policy.Validate(4, values, completed: false)).Message);
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("en-US")]
    public void Citizen_id_and_date_validation_are_culture_independent(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
            var values = CompleteValues(1);
            values["petitioner_citizen_id"] = "9999999999994";
            values["petitioner_card_issued"] = "2026-07-20";
            values["petitioner_card_expired"] = "2026-07-20";
            policy.Validate(1, values, completed: false);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void Kb1_requires_threat_details_only_when_threat_exists()
    {
        var values = CompleteValues(1);
        values["threat_status"] = "ไม่มี";
        values["threat_details"] = "";
        policy.Validate(1, values, completed: true);

        values["threat_status"] = "มี";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(1, values, completed: true));
    }

    [Fact]
    public void Selected_other_option_requires_detail_for_preview_and_export()
    {
        var values = CompleteValues(1);
        values["petitioner_prefix"] = "อื่น ๆ";
        values["petitioner_prefix_other"] = "";

        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(1, values, completed: true));

        values["petitioner_prefix_other"] = "ดร.";
        policy.Validate(1, values, completed: true);
    }

    [Fact]
    public void Kb3_withdrawal_uses_dedicated_reason_instead_of_threat_details()
    {
        var values = CompleteValues(3);
        values["statement_type"] = "บันทึกกรณีพยานขอถอนคำร้อง";
        values["threat_circumstances"] = "";
        values["withdrawal_reason"] = "พยานยืนยันความประสงค์ขอถอนคำร้องโดยสมัครใจ";

        policy.Validate(3, values, completed: true);
        values["withdrawal_reason"] = "";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(3, values, completed: true));
    }

    [Fact]
    public void Kb4_officer_does_not_fill_director_opinion_and_conflicting_proposals_are_blocked()
    {
        var values = CompleteValues(4);
        values["director_opinion"] = "";
        values["proposal_5_1"] = "true";
        values["proposal_5_2"] = "false";
        policy.Validate(4, values, completed: true);

        values["proposal_5_2"] = "true";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(4, values, completed: true));
    }

    [Fact]
    public void Kb14_limits_each_round_to_90_days_and_accumulated_total_to_180_days()
    {
        var values = CompleteValues(14);
        values["submitted_by_mode"] = "พยานยื่นด้วยตนเอง";
        values["extension_start"] = "2026-07-01";
        values["extension_end"] = "2026-09-28";
        values["extension_days"] = "90";
        values["total_days"] = "90";
        policy.Validate(14, values, completed: true);

        values["extension_end"] = "2026-09-29";
        values["extension_days"] = "91";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(14, values, completed: true));

        values["extension_end"] = "2026-09-28";
        values["extension_days"] = "90";
        values["total_days"] = "91";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(14, values, completed: true));
    }

    [Fact]
    public void Required_repeating_group_must_have_complete_row()
    {
        var values = CompleteValues(13);
        values["activity_log"] = "[]";
        Assert.Throws<WitnessWorkflowException>(() => policy.Validate(13, values, completed: true));

        values["activity_log"] = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string>
            {
                ["activity_date"] = "2026-07-14", ["activity"] = "ตรวจพื้นที่",
                ["officer_signature"] = "SIG-O", ["witness_signature"] = "SIG-W", ["note"] = "ปกติ"
            }
        });
        policy.Validate(13, values, completed: true);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)]
    public void Every_official_form_has_fields_and_sections(int number)
    {
        var form = WitnessProtectionFormCatalog.Get(number);
        Assert.NotEmpty(form.Sections);
        Assert.NotEmpty(form.Fields);
        Assert.Equal(number, form.Number);
    }

    [Fact]
    public void Signature_purpose_is_authorized_by_authenticated_role()
    {
        var officer = new WitnessUserContext(Guid.NewGuid(), "officer", "เจ้าหน้าที่", "",
            new HashSet<string> { "officer" }, new HashSet<string> { WitnessPermissions.OfficerReview });
        var external = new WitnessUserContext(Guid.NewGuid(), "external", "ระบบภายนอก", "",
            new HashSet<string> { "external_module" }, new HashSet<string> { WitnessPermissions.ExternalReceive });

        Assert.Throws<WitnessAuthorizationException>(() =>
            policy.EnsureCanSignPurpose("เลขาธิการผู้ลงนามคำสั่ง", officer));
        policy.EnsureCanSignPurpose("เลขาธิการผู้ลงนามคำสั่ง", external);
    }

    [Fact]
    public void Global_admin_can_view_every_case_but_cannot_sign_for_an_operational_role()
    {
        var administrator = new WitnessUserContext(Guid.NewGuid(), "admin", "ผู้ดูแลระบบ", "ผู้ดูแลระบบ",
            new HashSet<string> { "super_admin" }, new HashSet<string> { "witness.*" });

        Assert.True(administrator.IsGlobalAdministrator);
        Assert.Throws<WitnessAuthorizationException>(() =>
            policy.EnsureCanSignPurpose("ผู้บังคับบัญชาชั้นต้น", administrator));
    }

    [Theory]
    [InlineData(1, WitnessPermissions.Create)]
    [InlineData(2, WitnessPermissions.OfficerReview)]
    [InlineData(3, WitnessPermissions.OfficerReview)]
    [InlineData(4, WitnessPermissions.OfficerReview)]
    [InlineData(5, WitnessPermissions.DirectorReview)]
    [InlineData(6, WitnessPermissions.SupervisorReview)]
    [InlineData(7, WitnessPermissions.ProtectionManage)]
    [InlineData(8, WitnessPermissions.ExternalReceive)]
    [InlineData(9, WitnessPermissions.NoticeManage)]
    [InlineData(10, WitnessPermissions.NoticeManage)]
    [InlineData(11, WitnessPermissions.ProtectionManage)]
    [InlineData(12, WitnessPermissions.ProtectionManage)]
    [InlineData(13, WitnessPermissions.ProtectionManage)]
    [InlineData(14, WitnessPermissions.DirectorReview)]
    [InlineData(15, WitnessPermissions.ProtectionManage)]
    [InlineData(16, WitnessPermissions.ExternalReceive)]
    [InlineData(17, WitnessPermissions.NoticeManage)]
    public void Every_form_requires_an_explicit_form_permission_and_generic_edit_is_not_a_fallback(
        int formNumber,
        string explicitPermission)
    {
        var genericEditor = User(WitnessPermissions.Edit);
        var globalAdministrator = User("witness.*", role: "super_admin");
        var explicitlyAuthorized = User(explicitPermission);

        Assert.Throws<WitnessAuthorizationException>(() => policy.EnsureCanEdit(formNumber, genericEditor));
        Assert.Throws<WitnessAuthorizationException>(() => policy.EnsureCanEdit(formNumber, globalAdministrator));
        policy.EnsureCanEdit(formNumber, explicitlyAuthorized);
    }

    [Fact]
    public void Kb4_officer_cannot_add_change_or_clear_director_opinion()
    {
        var existing = new Dictionary<string, string>
        {
            ["case_background"] = "ข้อเท็จจริงเดิม",
            ["officer_recommendation"] = "ความเห็นเจ้าหน้าที่เดิม",
            ["director_opinion"] = "ความเห็น ผอ. เดิม"
        };
        var officer = User(WitnessPermissions.OfficerReview);

        foreach (var attemptedValue in new[] { "ปลอมความเห็น ผอ.", "ความเห็น ผอ. เดิม", "" })
        {
            var request = new Dictionary<string, string>
            {
                ["officer_recommendation"] = "ความเห็นเจ้าหน้าที่รุ่นใหม่",
                ["director_opinion"] = attemptedValue
            };
            Assert.Throws<WitnessAuthorizationException>(() => policy.AuthorizeAndMergeValues(
                4, request, existing, officer, WitnessStatuses.StaffReview, "awaiting_kb4"));
        }
    }

    [Theory]
    [InlineData(4, WitnessStatuses.StaffReview, "supervisor_review", WitnessPermissions.SupervisorReview, "supervisor_opinion")]
    [InlineData(4, WitnessStatuses.StaffReview, "director_review", WitnessPermissions.DirectorReview, "director_opinion")]
    [InlineData(6, WitnessStatuses.SupervisorReview, "none", WitnessPermissions.SupervisorReview, "supervisor_opinion")]
    [InlineData(6, WitnessStatuses.DirectorReview, "none", WitnessPermissions.DirectorReview, "director_opinion")]
    [InlineData(6, WitnessStatuses.ExternalPending, "none", WitnessPermissions.ExternalReceive, "secretary_opinion")]
    [InlineData(14, WitnessStatuses.ExtensionSupervisorReview, "none", WitnessPermissions.SupervisorReview, "supervisor_opinion")]
    [InlineData(14, WitnessStatuses.ExtensionDirectorReview, "none", WitnessPermissions.DirectorReview, "director_opinion")]
    [InlineData(14, WitnessStatuses.ExtensionExternalPending, "none", WitnessPermissions.ExternalReceive, "secretary_opinion")]
    [InlineData(15, WitnessStatuses.ProtectionActive, "none", WitnessPermissions.DirectorReview, "director_opinion")]
    [InlineData(15, WitnessStatuses.TerminationExternalPending, "none", WitnessPermissions.ExternalReceive, "secretary_opinion")]
    public void Hierarchical_actor_can_change_only_the_owned_opinion_field(
        int formNumber,
        string caseStatus,
        string urgentStatus,
        string permission,
        string fieldKey)
    {
        var existing = new Dictionary<string, string>
        {
            ["office"] = "สำนักทดสอบ",
            ["officer_recommendation"] = "ความเห็นเจ้าหน้าที่เดิม",
            ["team_leader_opinion"] = "ความเห็นหัวหน้าชุดเดิม"
        };
        var result = policy.AuthorizeAndMergeValues(
            formNumber,
            new Dictionary<string, string> { [fieldKey] = "ความเห็นของผู้มีสิทธิ์" },
            existing,
            User(permission),
            caseStatus,
            urgentStatus);

        Assert.Equal("ความเห็นของผู้มีสิทธิ์", result[fieldKey]);
        Assert.Equal("สำนักทดสอบ", result["office"]);
    }

    [Theory]
    [InlineData(6, WitnessStatuses.StaffReview, WitnessPermissions.OfficerReview, "director_opinion")]
    [InlineData(6, WitnessStatuses.SupervisorReview, WitnessPermissions.SupervisorReview, "officer_recommendation")]
    [InlineData(14, WitnessStatuses.ProtectionActive, WitnessPermissions.ProtectionManage, "secretary_opinion")]
    [InlineData(15, WitnessStatuses.ProtectionActive, WitnessPermissions.ProtectionManage, "director_opinion")]
    public void Actor_cannot_write_another_level_opinion(
        int formNumber,
        string status,
        string permission,
        string forbiddenField)
        => Assert.Throws<WitnessAuthorizationException>(() => policy.AuthorizeAndMergeValues(
            formNumber,
            new Dictionary<string, string> { [forbiddenField] = "ค่าที่ไม่มีสิทธิ์" },
            new Dictionary<string, string> { ["office"] = "สำนักทดสอบ" },
            User(permission),
            status,
            "none"));

    [Theory]
    [InlineData(6, WitnessStatuses.StaffReview, "", "เจ้าหน้าที่เจ้าของเรื่อง")]
    [InlineData(6, WitnessStatuses.SupervisorReview, "", "ผู้บังคับบัญชาชั้นต้น")]
    [InlineData(6, WitnessStatuses.DirectorReview, "", "ผู้อำนวยการสำนัก/กอง")]
    [InlineData(6, WitnessStatuses.ExternalPending, "", "ผู้มีอำนาจจาก External Module")]
    [InlineData(4, WitnessStatuses.StaffReview, "supervisor_review", "ผู้บังคับบัญชาชั้นต้น")]
    public void Signature_purpose_is_fixed_by_workflow_stage(
        int formNumber,
        string status,
        string urgentStatus,
        string expectedPurpose)
    {
        Assert.Equal(expectedPurpose,
            WitnessSignaturePolicy.PurposeRequiredByWorkflowStage(formNumber, status, urgentStatus));
        WitnessSignaturePolicy.EnsurePurposeMatchesWorkflowStage(
            formNumber, status, urgentStatus, expectedPurpose);
        Assert.Throws<WitnessAuthorizationException>(() =>
            WitnessSignaturePolicy.EnsurePurposeMatchesWorkflowStage(
                formNumber, status, urgentStatus, "เจ้าหน้าที่ผู้เสนอ"));
    }

    private static WitnessUserContext User(string permission, string role = "test_role")
        => new(Guid.NewGuid(), $"user-{Guid.NewGuid():N}", "ผู้ทดสอบ", "ผู้ทดสอบ",
            new HashSet<string> { role }, new HashSet<string> { permission });

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)]
    public void Every_official_form_declares_signature_purposes(int number)
        => Assert.NotEmpty(WitnessProtectionFormCatalog.SignaturePurposes(number));

    [Fact]
    public void Iso_form_date_is_gregorian_even_when_current_culture_is_thai()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            CultureInfo.CurrentUICulture = new CultureInfo("th-TH");

            Assert.True(WitnessIsoDate.TryParse("2026-07-19", out var parsed));
            Assert.Equal(new DateOnly(2026, 7, 19), parsed);
            Assert.False(WitnessIsoDate.TryParse("19/07/2569", out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static Dictionary<string, string> CompleteValues(int number)
    {
        var form = WitnessProtectionFormCatalog.Get(number);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.Fields)
        {
            values[field.Key] = field.Label.Contains("เลขประจำตัวประชาชน", StringComparison.Ordinal)
                ? "9999999999994"
                : field.Type switch
            {
                WitnessFormFieldType.Checkbox => "true",
                WitnessFormFieldType.MultiSelect => "[\"ตัวเลือก\"]",
                WitnessFormFieldType.Address => JsonSerializer.Serialize((field.Columns ?? []).ToDictionary(item => item.Key, _ => "ข้อมูล")),
                WitnessFormFieldType.Repeating => JsonSerializer.Serialize(new[] { (field.Columns ?? []).ToDictionary(item => item.Key, _ => "ข้อมูล") }),
                WitnessFormFieldType.Date => "2026-07-14",
                WitnessFormFieldType.Number => "1",
                _ => "ข้อมูล"
            };
        }
        if (number == 1 && values.TryGetValue("related_people", out var relatedPeopleJson))
        {
            var relatedPeople = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(relatedPeopleJson) ?? [];
            foreach (var person in relatedPeople)
                person["identity_no"] = "9999999999994";
            values["related_people"] = JsonSerializer.Serialize(relatedPeople);
        }
        return values;
    }
}
