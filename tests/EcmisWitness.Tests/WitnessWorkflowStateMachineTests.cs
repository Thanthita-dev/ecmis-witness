using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Security;

namespace EcmisWitness.Tests;

public sealed class WitnessWorkflowStateMachineTests
{
    [Fact]
    public void Kb5_director_must_sign_before_witness_acknowledges()
        => Assert.Equal(
            ["ผู้อำนวยการสำนัก/กองผู้ออกคำสั่ง"],
            WitnessSignaturePolicy.PrerequisitePurposes(5, "พยานผู้รับทราบ"));

    [Fact]
    public void Kb8_secretary_must_sign_before_witness_acknowledges()
        => Assert.Equal(
            ["เลขาธิการผู้ลงนามคำสั่ง"],
            WitnessSignaturePolicy.PrerequisitePurposes(8, "พยานผู้รับทราบ"));

    private readonly WitnessWorkflowStateMachine machine = new();

    [Fact]
    public void Normal_flow_requires_kb3_and_kb6_before_supervisor()
    {
        var officer = User("officer", WitnessPermissions.OfficerReview);

        Assert.Throws<WitnessWorkflowException>(() =>
            machine.RequireTransition(WitnessStatuses.StaffReview, "submit-supervisor", officer, new HashSet<int> { 6 }));

        var result = machine.RequireTransition(WitnessStatuses.StaffReview, "submit-supervisor", officer, new HashSet<int> { 3, 6 });
        Assert.Equal(WitnessStatuses.SupervisorReview, result.ToStatus);
    }

    [Fact]
    public void Send_back_returns_to_case_owner_from_every_internal_review_layer()
    {
        var supervisor = User("supervisor", WitnessPermissions.SupervisorReview);
        var director = User("director", WitnessPermissions.DirectorReview);

        Assert.Equal(WitnessStatuses.StaffReview,
            machine.RequireTransition(WitnessStatuses.SupervisorReview, "supervisor-return", supervisor).ToStatus);
        Assert.Equal(WitnessStatuses.StaffReview,
            machine.RequireTransition(WitnessStatuses.DirectorReview, "director-return", director).ToStatus);
    }

    [Fact]
    public void Withdrawal_uses_dedicated_states_and_cannot_enter_normal_approval_path()
    {
        var officer = User("officer", WitnessPermissions.OfficerReview);
        var supervisor = User("supervisor", WitnessPermissions.SupervisorReview);
        var director = User("director", WitnessPermissions.DirectorReview);
        var completedKb3 = new HashSet<int> { 3 };

        var requested = machine.RequireTransition(
            WitnessStatuses.StaffReview, "request-withdrawal", officer, completedKb3);
        Assert.Equal(WitnessStatuses.WithdrawalSupervisorReview, requested.ToStatus);
        Assert.DoesNotContain(
            machine.GetAvailableActions(WitnessStatuses.WithdrawalSupervisorReview, supervisor, completedKb3),
            action => action.Code == "supervisor-forward");

        var reviewed = machine.RequireTransition(
            WitnessStatuses.WithdrawalSupervisorReview, "withdrawal-supervisor-forward", supervisor, completedKb3);
        Assert.Equal(WitnessStatuses.WithdrawalDirectorReview, reviewed.ToStatus);
        var submitted = machine.RequireTransition(
            WitnessStatuses.WithdrawalDirectorReview, "withdrawal-director-forward", director, completedKb3);
        Assert.Equal(WitnessStatuses.WithdrawalExternalPending, submitted.ToStatus);
    }

    [Fact]
    public void Urgent_flow_is_side_flow_and_main_flow_remains_staff_review()
    {
        var officer = User("officer", WitnessPermissions.OfficerReview);
        Assert.Empty(machine.GetUrgentAvailableActions(
            WitnessStatuses.StaffReview, "awaiting_kb4", officer, new HashSet<int> { 4 }));
        var actions = machine.GetUrgentAvailableActions(
            WitnessStatuses.StaffReview, "awaiting_kb4", officer, new HashSet<int> { 3, 4 });

        var action = Assert.Single(actions);
        Assert.Equal("urgent-submit-supervisor", action.Code);
        Assert.Equal("supervisor_review", action.TargetStatus);
    }

    [Fact]
    public void Protection_requires_kb8_and_kb11()
    {
        var officer = User("protection_officer", WitnessPermissions.ProtectionManage);
        Assert.Empty(machine.GetAvailableActions(WitnessStatuses.ProtectionSetup, officer, new HashSet<int> { 11 }));
        Assert.Contains(machine.GetAvailableActions(WitnessStatuses.ProtectionSetup, officer, new HashSet<int> { 8, 11 }), item => item.Code == "start-protection");
    }

    [Fact]
    public void Extension_kb14_is_reviewed_by_supervisor_and_director_before_external_module()
    {
        var protectionOfficer = User("protection_officer", WitnessPermissions.ProtectionManage);
        var supervisor = User("supervisor", WitnessPermissions.SupervisorReview);
        var director = User("director", WitnessPermissions.DirectorReview);
        var forms = new HashSet<int> { 14 };

        var requested = machine.RequireTransition(
            WitnessStatuses.ProtectionActive, "request-extension", protectionOfficer, forms);
        Assert.Equal(WitnessStatuses.ExtensionSupervisorReview, requested.ToStatus);
        var reviewed = machine.RequireTransition(
            WitnessStatuses.ExtensionSupervisorReview, "extension-supervisor-forward", supervisor, forms);
        Assert.Equal(WitnessStatuses.ExtensionDirectorReview, reviewed.ToStatus);
        var forwarded = machine.RequireTransition(
            WitnessStatuses.ExtensionDirectorReview, "extension-director-forward", director, forms);
        Assert.Equal(WitnessStatuses.ExtensionExternalPending, forwarded.ToStatus);
    }

    [Fact]
    public void Transfer_requires_external_decision_before_waiting_for_department_response()
    {
        var officer = User("protection_officer", WitnessPermissions.ProtectionManage);

        var requested = machine.RequireTransition(
            WitnessStatuses.ProtectionActive, "request-transfer", officer);

        Assert.Equal(WitnessStatuses.TransferExternalPending, requested.ToStatus);
    }

    [Fact]
    public void Approval_notice_requires_kb8_before_kb9_is_sent()
    {
        var noticeOfficer = User("notice_officer", WitnessPermissions.NoticeManage);

        Assert.Empty(machine.GetAvailableActions(
            WitnessStatuses.ApprovedPendingNotice, noticeOfficer, new HashSet<int> { 9 }));
        Assert.Contains(machine.GetAvailableActions(
            WitnessStatuses.ApprovedPendingNotice, noticeOfficer, new HashSet<int> { 8, 9 }),
            item => item.Code == "send-notice");
    }

    [Fact]
    public void Signature_matrix_requires_every_role_for_active_protection()
    {
        var requirements = WitnessSignaturePolicy.Requirements(
            "start-protection", WitnessStatuses.ProtectionSetup);

        Assert.Contains(requirements, item => item == new WitnessRequiredSignature(8, "เลขาธิการผู้ลงนามคำสั่ง"));
        Assert.Contains(requirements, item => item == new WitnessRequiredSignature(8, "พยานผู้รับทราบ"));
        Assert.Equal(4, requirements.Count(item => item.FormNumber == 11));
    }

    [Fact]
    public void Permission_is_checked_on_server_transition()
    {
        var unauthorized = User("officer");
        Assert.Throws<WitnessAuthorizationException>(() =>
            machine.RequireTransition(WitnessStatuses.SupervisorReview, "supervisor-forward", unauthorized, new HashSet<int> { 6 }));
    }

    [Fact]
    public void Global_admin_scope_does_not_authorize_operational_workflow_actions()
    {
        var administrator = User("super_admin", "witness.*");

        Assert.Empty(machine.GetAvailableActions(
            WitnessStatuses.SupervisorReview, administrator, new HashSet<int> { 6 }));
        Assert.Throws<WitnessAuthorizationException>(() =>
            machine.RequireTransition(
                WitnessStatuses.SupervisorReview,
                "supervisor-forward",
                administrator,
                new HashSet<int> { 6 }));
    }

    private static WitnessUserContext User(string role, params string[] permissions)
        => new(Guid.NewGuid(), role, role, "", new HashSet<string> { role }, new HashSet<string>(permissions));
}
