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

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)]
    public void Every_official_form_declares_signature_purposes(int number)
        => Assert.NotEmpty(WitnessProtectionFormCatalog.SignaturePurposes(number));

    private static Dictionary<string, string> CompleteValues(int number)
    {
        var form = WitnessProtectionFormCatalog.Get(number);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.Fields)
        {
            values[field.Key] = field.Type switch
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
        return values;
    }
}
