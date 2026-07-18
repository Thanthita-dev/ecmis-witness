using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Security;

namespace EcmisWitness.Api.Domain;

public static class WitnessStatuses
{
    public const string IntakeDraft = "intake_draft";
    public const string StaffReview = "staff_review";
    public const string SupervisorReview = "supervisor_review";
    public const string DirectorReview = "director_review";
    public const string ExternalPending = "external_pending";
    public const string WithdrawalStaffRevision = "withdrawal_staff_revision";
    public const string WithdrawalSupervisorReview = "withdrawal_supervisor_review";
    public const string WithdrawalDirectorReview = "withdrawal_director_review";
    public const string WithdrawalExternalPending = "withdrawal_external_pending";
    public const string ApprovedPendingNotice = "approved_pending_notice";
    public const string RejectedPendingNotice = "rejected_pending_notice";
    public const string NoticeSent = "notice_sent";
    public const string ProtectionSetup = "protection_setup";
    public const string ProtectionActive = "protection_active";
    public const string ExtensionSupervisorReview = "extension_supervisor_review";
    public const string ExtensionDirectorReview = "extension_director_review";
    public const string ExtensionExternalPending = "extension_external_pending";
    public const string TerminationExternalPending = "termination_external_pending";
    public const string TerminationOrdered = "termination_ordered";
    public const string AppealWindow = "appeal_window";
    public const string AppealReceived = "appeal_received";
    public const string AppealExternalPending = "appeal_external_pending";
    public const string AppealDecided = "appeal_decided";
    public const string TransferExternalPending = "transfer_external_pending";
    public const string TransferWaiting = "transfer_waiting";
    public const string TransferAccepted = "transfer_accepted";
    public const string TransferRejected = "transfer_rejected";
    public const string Transferred = "transferred";
    public const string Closed = "closed";
}

public sealed record WitnessTransitionDefinition(
    string Action,
    string Label,
    IReadOnlyList<string> FromStatuses,
    string ToStatus,
    string Permission,
    bool RequiresReason = false,
    int? RequiredFormNumber = null);

public sealed class WitnessWorkflowStateMachine
{
    private static readonly IReadOnlyList<WitnessTransitionDefinition> Definitions =
    [
        D("submit-intake", "ส่งคำร้อง", [WitnessStatuses.IntakeDraft], WitnessStatuses.StaffReview, WitnessPermissions.Create, true),
        D("request-withdrawal", "เสนอถอนคำร้องก่อนอนุมัติ", [WitnessStatuses.StaffReview], WitnessStatuses.WithdrawalSupervisorReview, WitnessPermissions.OfficerReview, true, 3),
        D("withdrawal-submit-supervisor", "ส่งเรื่องถอนคำร้องให้ผู้บังคับบัญชาชั้นต้น", [WitnessStatuses.WithdrawalStaffRevision], WitnessStatuses.WithdrawalSupervisorReview, WitnessPermissions.OfficerReview, true, 3),
        D("withdrawal-supervisor-forward", "ส่งเรื่องถอนคำร้องให้ ผอ.สำนัก/กอง", [WitnessStatuses.WithdrawalSupervisorReview], WitnessStatuses.WithdrawalDirectorReview, WitnessPermissions.SupervisorReview, true, 3),
        D("withdrawal-supervisor-return", "ส่งเรื่องถอนคำร้องกลับเจ้าหน้าที่เจ้าของเรื่อง", [WitnessStatuses.WithdrawalSupervisorReview], WitnessStatuses.WithdrawalStaffRevision, WitnessPermissions.SupervisorReview, true),
        D("withdrawal-director-forward", "ส่งเรื่องถอนคำร้องไป External Module", [WitnessStatuses.WithdrawalDirectorReview], WitnessStatuses.WithdrawalExternalPending, WitnessPermissions.DirectorReview, true, 3),
        D("withdrawal-director-return", "ส่งเรื่องถอนคำร้องกลับเจ้าหน้าที่เจ้าของเรื่อง", [WitnessStatuses.WithdrawalDirectorReview], WitnessStatuses.WithdrawalStaffRevision, WitnessPermissions.DirectorReview, true),
        D("submit-supervisor", "ส่งผู้บังคับบัญชาชั้นต้น", [WitnessStatuses.StaffReview], WitnessStatuses.SupervisorReview, WitnessPermissions.OfficerReview, true, 6),
        D("supervisor-forward", "ส่ง ผอ.สำนัก/กอง", [WitnessStatuses.SupervisorReview], WitnessStatuses.DirectorReview, WitnessPermissions.SupervisorReview, true, 6),
        D("supervisor-return", "ส่งกลับเจ้าหน้าที่เจ้าของเรื่อง", [WitnessStatuses.SupervisorReview], WitnessStatuses.StaffReview, WitnessPermissions.SupervisorReview, true),
        D("director-forward", "ส่งผลพิจารณาไป External Module", [WitnessStatuses.DirectorReview], WitnessStatuses.ExternalPending, WitnessPermissions.DirectorReview, true, 6),
        D("director-return", "ส่งกลับเจ้าหน้าที่เจ้าของเรื่อง", [WitnessStatuses.DirectorReview], WitnessStatuses.StaffReview, WitnessPermissions.DirectorReview, true),
        D("send-notice", "ส่งหนังสือแจ้งผล", [WitnessStatuses.ApprovedPendingNotice, WitnessStatuses.RejectedPendingNotice, WitnessStatuses.TerminationOrdered], WitnessStatuses.NoticeSent, WitnessPermissions.NoticeManage, true),
        D("record-notice-receipt-approved", "บันทึกวันรับหนังสืออนุมัติ", [WitnessStatuses.NoticeSent], WitnessStatuses.ProtectionSetup, WitnessPermissions.NoticeManage, true, 9),
        D("record-notice-receipt-rejected", "บันทึกวันรับหนังสือไม่อนุมัติ/ยุติ", [WitnessStatuses.NoticeSent], WitnessStatuses.AppealWindow, WitnessPermissions.NoticeManage, true),
        D("start-protection", "เริ่มการคุ้มครอง", [WitnessStatuses.ProtectionSetup], WitnessStatuses.ProtectionActive, WitnessPermissions.ProtectionManage, true, 11),
        D("request-extension", "ส่งคำขอขยายเวลาให้ผู้บังคับบัญชาชั้นต้น", [WitnessStatuses.ProtectionActive], WitnessStatuses.ExtensionSupervisorReview, WitnessPermissions.ProtectionManage, true, 14),
        D("extension-supervisor-forward", "ส่งคำขอขยายเวลาให้ ผอ.สำนัก/กอง", [WitnessStatuses.ExtensionSupervisorReview], WitnessStatuses.ExtensionDirectorReview, WitnessPermissions.SupervisorReview, true, 14),
        D("extension-supervisor-return", "ส่งคำขอขยายเวลากลับชุดคุ้มครอง", [WitnessStatuses.ExtensionSupervisorReview], WitnessStatuses.ProtectionActive, WitnessPermissions.SupervisorReview, true),
        D("extension-director-forward", "ส่งคำขอขยายเวลาไป External Module", [WitnessStatuses.ExtensionDirectorReview], WitnessStatuses.ExtensionExternalPending, WitnessPermissions.DirectorReview, true, 14),
        D("extension-director-return", "ส่งคำขอขยายเวลากลับชุดคุ้มครอง", [WitnessStatuses.ExtensionDirectorReview], WitnessStatuses.ProtectionActive, WitnessPermissions.DirectorReview, true),
        D("request-termination", "ส่งคำขอยุติไป External Module", [WitnessStatuses.ProtectionActive], WitnessStatuses.TerminationExternalPending, WitnessPermissions.ProtectionManage, true),
        D("receive-appeal", "รับคำอุทธรณ์", [WitnessStatuses.AppealWindow], WitnessStatuses.AppealReceived, WitnessPermissions.AppealManage, true),
        D("submit-appeal", "ส่งอุทธรณ์ไป External Module", [WitnessStatuses.AppealReceived], WitnessStatuses.AppealExternalPending, WitnessPermissions.AppealManage, true),
        D("request-transfer", "เสนอส่งต่อกรมคุ้มครองสิทธิฯ ผ่าน External Module", [WitnessStatuses.ProtectionActive], WitnessStatuses.TransferExternalPending, WitnessPermissions.ProtectionManage, true),
        D("record-transfer-accepted", "กรมรับตัว", [WitnessStatuses.TransferWaiting], WitnessStatuses.TransferAccepted, WitnessPermissions.ProtectionManage, true),
        D("record-transfer-rejected", "กรมไม่รับตัว", [WitnessStatuses.TransferWaiting], WitnessStatuses.TransferRejected, WitnessPermissions.ProtectionManage, true),
        D("complete-transfer", "บันทึกส่งมอบพยาน", [WitnessStatuses.TransferAccepted], WitnessStatuses.Transferred, WitnessPermissions.ProtectionManage, true, 12),
        D("resume-after-transfer-rejected", "กลับมาคุ้มครองต่อ", [WitnessStatuses.TransferRejected], WitnessStatuses.ProtectionActive, WitnessPermissions.ProtectionManage, true),
        D("close-after-transfer-rejected", "สิ้นสุดการคุ้มครองเมื่อกรมไม่รับตัว", [WitnessStatuses.TransferRejected], WitnessStatuses.Closed, WitnessPermissions.ProtectionManage, true),
        D("close-no-appeal", "ปิดเรื่องเมื่อพ้นกำหนดอุทธรณ์", [WitnessStatuses.AppealWindow, WitnessStatuses.AppealDecided], WitnessStatuses.Closed, WitnessPermissions.AppealManage, true)
    ];

    public IReadOnlyList<WitnessAvailableActionDto> GetAvailableActions(
        string status,
        WitnessUserContext user,
        IReadOnlySet<int>? completedForms = null)
        => Definitions
            .Where(definition => definition.FromStatuses.Contains(status))
            .Where(definition => HasOperationalPermission(user, definition.Permission))
            .Where(definition => HasPrerequisites(definition, status, completedForms))
            .Select(ToDto)
            .ToArray();

    public IReadOnlyList<WitnessAvailableActionDto> GetAvailableActions(
        string status,
        string urgentStatus,
        WitnessUserContext user,
        IReadOnlySet<int>? completedForms = null)
        => GetAvailableActions(status, user, completedForms)
            .Concat(GetUrgentAvailableActions(status, urgentStatus, user, completedForms))
            .ToArray();

    public IReadOnlyList<WitnessAvailableActionDto> GetUrgentAvailableActions(
        string status,
        string urgentStatus,
        WitnessUserContext user,
        IReadOnlySet<int>? completedForms)
    {
        if (status != WitnessStatuses.StaffReview)
            return [];
        return urgentStatus switch
        {
            "awaiting_kb4" when HasOperationalPermission(user, WitnessPermissions.OfficerReview)
                && completedForms?.Contains(3) == true
                && completedForms.Contains(4)
                => [new("urgent-submit-supervisor", "ส่งกรณีเร่งด่วนให้ผู้บังคับบัญชา", "supervisor_review", true, 4)],
            "supervisor_review" when HasOperationalPermission(user, WitnessPermissions.SupervisorReview)
                => [new("urgent-supervisor-forward", "ส่งกรณีเร่งด่วนให้ ผอ.", "director_review", true, 4),
                    new("urgent-supervisor-return", "ส่งกรณีเร่งด่วนกลับเจ้าหน้าที่", "awaiting_kb4", true, 4)],
            "director_review" when HasOperationalPermission(user, WitnessPermissions.DirectorReview)
                => [new("urgent-director-approve", "อนุมัติคุ้มครองชั่วคราว คบ.5", "temporary_active", true, 5),
                    new("urgent-director-return", "ส่งกรณีเร่งด่วนกลับเจ้าหน้าที่", "awaiting_kb4", true, 4)],
            _ => []
        };
    }

    public WitnessTransitionDefinition RequireTransition(
        string status,
        string action,
        WitnessUserContext user,
        IReadOnlySet<int>? completedForms = null)
    {
        var definition = Definitions.FirstOrDefault(item =>
            string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase)
            && item.FromStatuses.Contains(status));

        if (definition is null)
            throw new WitnessWorkflowException("ไม่สามารถดำเนินการนี้จากสถานะปัจจุบันได้");
        if (!HasOperationalPermission(user, definition.Permission))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ดำเนินการในขั้นตอนนี้");
        if (!HasPrerequisites(definition, status, completedForms))
        {
            throw new WitnessWorkflowException(PrerequisiteMessage(definition.Action, status));
        }

        return definition;
    }

    public static string StatusLabel(string status) => status switch
    {
        WitnessStatuses.IntakeDraft => "ร่างคำร้อง",
        WitnessStatuses.StaffReview => "รอตรวจคำร้อง",
        WitnessStatuses.SupervisorReview => "รอผู้บังคับบัญชาชั้นต้นกลั่นกรอง",
        WitnessStatuses.DirectorReview => "รอ ผอ.สำนัก/กองพิจารณา",
        WitnessStatuses.ExternalPending => "รอผลจาก External Module",
        WitnessStatuses.WithdrawalStaffRevision => "เรื่องถอนคำร้อง รอเจ้าหน้าที่แก้ไข",
        WitnessStatuses.WithdrawalSupervisorReview => "พยานขอถอน รอผู้บังคับบัญชากลั่นกรอง",
        WitnessStatuses.WithdrawalDirectorReview => "พยานขอถอน รอ ผอ.สำนัก/กองพิจารณา",
        WitnessStatuses.WithdrawalExternalPending => "พยานขอถอน รอคำสั่งจาก External Module",
        WitnessStatuses.ApprovedPendingNotice => "อนุมัติแล้ว รอแจ้งผล",
        WitnessStatuses.RejectedPendingNotice => "ไม่อนุมัติ รอแจ้งผล",
        WitnessStatuses.NoticeSent => "ส่งหนังสือแล้ว รอบันทึกวันรับ",
        WitnessStatuses.ProtectionSetup => "รอจัดชุดและข้อตกลงคุ้มครอง",
        WitnessStatuses.ProtectionActive => "กำลังคุ้มครอง",
        WitnessStatuses.ExtensionSupervisorReview => "ขยายเวลา รอผู้บังคับบัญชากลั่นกรอง",
        WitnessStatuses.ExtensionDirectorReview => "ขยายเวลา รอ ผอ.สำนัก/กองพิจารณา",
        WitnessStatuses.ExtensionExternalPending => "รอผลขยายเวลาจาก External Module",
        WitnessStatuses.TerminationExternalPending => "รอคำสั่งยุติจาก External Module",
        WitnessStatuses.TerminationOrdered => "มีคำสั่งยุติ รอแจ้งผล",
        WitnessStatuses.AppealWindow => "อยู่ในกำหนดอุทธรณ์ 30 วัน",
        WitnessStatuses.AppealReceived => "รับคำอุทธรณ์แล้ว",
        WitnessStatuses.AppealExternalPending => "รอผลอุทธรณ์จาก External Module",
        WitnessStatuses.AppealDecided => "บันทึกผลอุทธรณ์แล้ว",
        WitnessStatuses.TransferExternalPending => "รอผลอนุมัติส่งต่อจาก External Module",
        WitnessStatuses.TransferWaiting => "รอกรมคุ้มครองสิทธิฯ ตอบรับ",
        WitnessStatuses.TransferAccepted => "กรมรับตัว รอส่งมอบ",
        WitnessStatuses.TransferRejected => "กรมไม่รับตัว รอพิจารณาทางออก",
        WitnessStatuses.Transferred => "ส่งมอบหน่วยงานภายนอกแล้ว",
        WitnessStatuses.Closed => "ปิดเรื่องแล้ว",
        _ => status
    };

    public static string NextAction(string status) => status switch
    {
        WitnessStatuses.IntakeDraft => "กรอกคำร้องให้ครบและส่งคำร้อง",
        WitnessStatuses.StaffReview => "ตรวจ คบ.1/2 บันทึก คบ.3 ประเมินภัย และจัดทำ คบ.6",
        WitnessStatuses.SupervisorReview => "กลั่นกรองและลงนามรับรอง",
        WitnessStatuses.DirectorReview => "พิจารณาและส่ง External Module",
        WitnessStatuses.ExternalPending => "รอรับผลคำสั่งภายนอก",
        WitnessStatuses.WithdrawalStaffRevision => "แก้ไข คบ.3 ประเภทถอนคำร้องและส่งกลับผู้บังคับบัญชา",
        WitnessStatuses.WithdrawalSupervisorReview => "กลั่นกรองความประสงค์ถอนคำร้องและส่ง ผอ.สำนัก/กอง",
        WitnessStatuses.WithdrawalDirectorReview => "พิจารณาความเห็นเสนอไม่อนุมัติและส่ง External Module",
        WitnessStatuses.WithdrawalExternalPending => "รอรับคำสั่งไม่อนุมัติจาก External Module",
        WitnessStatuses.ApprovedPendingNotice => "จัดทำ คบ.9",
        WitnessStatuses.RejectedPendingNotice => "จัดทำ คบ.10",
        WitnessStatuses.NoticeSent => "บันทึกหลักฐานวันรับหนังสือ",
        WitnessStatuses.ProtectionSetup => "จัดทำ คบ.8 และ คบ.11",
        WitnessStatuses.ProtectionActive => "บันทึก คบ.13 ติดตาม ขยาย ยุติ หรือส่งต่อ",
        WitnessStatuses.ExtensionSupervisorReview => "กลั่นกรอง คบ.14 และส่ง ผอ.สำนัก/กอง",
        WitnessStatuses.ExtensionDirectorReview => "พิจารณา คบ.14 และส่ง External Module",
        WitnessStatuses.ExtensionExternalPending => "รอผลขยายเวลา",
        WitnessStatuses.TerminationExternalPending => "รอ คบ.16 จาก External Module",
        WitnessStatuses.TerminationOrdered => "จัดทำ คบ.17",
        WitnessStatuses.AppealWindow => "รับอุทธรณ์หรือปิดเรื่องเมื่อครบกำหนด",
        WitnessStatuses.AppealReceived => "ส่งอุทธรณ์ไป External Module",
        WitnessStatuses.AppealExternalPending => "รอบันทึกผลชี้ขาด",
        WitnessStatuses.TransferExternalPending => "รอผลอนุมัติส่งต่อจาก External Module",
        WitnessStatuses.TransferWaiting => "บันทึกผลตอบรับจากกรมฯ",
        WitnessStatuses.TransferAccepted => "จัดทำ คบ.12",
        WitnessStatuses.TransferRejected => "เสนอคณะกรรมการหรือกลับมาคุ้มครองต่อ",
        _ => "ตรวจประวัติแฟ้มคำร้อง"
    };

    private static WitnessTransitionDefinition D(
        string action,
        string label,
        IReadOnlyList<string> from,
        string to,
        string permission,
        bool requiresReason = false,
        int? requiredForm = null)
        => new(action, label, from, to, permission, requiresReason, requiredForm);

    private static bool HasOperationalPermission(WitnessUserContext user, string permission)
        => user.Permissions.Contains(permission);

    private static WitnessAvailableActionDto ToDto(WitnessTransitionDefinition definition)
        => new(definition.Action, definition.Label, definition.ToStatus, definition.RequiresReason, definition.RequiredFormNumber);

    private static bool HasPrerequisites(
        WitnessTransitionDefinition definition,
        string status,
        IReadOnlySet<int>? completedForms)
    {
        var forms = completedForms ?? new HashSet<int>();
        return definition.Action switch
        {
            "submit-supervisor" => forms.Contains(3) && forms.Contains(6),
            "request-withdrawal" or "withdrawal-submit-supervisor" or "withdrawal-supervisor-forward" or "withdrawal-director-forward" => forms.Contains(3),
            "send-notice" when status == WitnessStatuses.ApprovedPendingNotice => forms.Contains(8) && forms.Contains(9),
            "send-notice" when status == WitnessStatuses.RejectedPendingNotice => forms.Contains(10),
            "send-notice" when status == WitnessStatuses.TerminationOrdered => forms.Contains(16) && forms.Contains(17),
            "record-notice-receipt-rejected" => forms.Contains(10) || forms.Contains(17),
            "start-protection" => forms.Contains(8) && forms.Contains(11),
            "request-termination" => forms.Contains(7) || forms.Contains(15),
            _ => !definition.RequiredFormNumber.HasValue || forms.Contains(definition.RequiredFormNumber.Value)
        };
    }

    private static string PrerequisiteMessage(string action, string status) => action switch
    {
        "submit-supervisor" => "ต้องบันทึกแบบ คบ.3 และ คบ.6 ให้สมบูรณ์ก่อนส่งผู้บังคับบัญชาชั้นต้น",
        "request-withdrawal" or "withdrawal-submit-supervisor" or "withdrawal-supervisor-forward" or "withdrawal-director-forward" => "ต้องบันทึกแบบ คบ.3 ประเภทพยานขอถอนคำร้องให้สมบูรณ์ก่อนส่งต่อ",
        "send-notice" when status == WitnessStatuses.ApprovedPendingNotice => "ต้องบันทึกแบบ คบ.8 และ คบ.9 ให้สมบูรณ์ก่อนส่งแจ้งผล",
        "send-notice" when status == WitnessStatuses.RejectedPendingNotice => "ต้องบันทึกแบบ คบ.10 ให้สมบูรณ์ก่อนส่งแจ้งผล",
        "send-notice" when status == WitnessStatuses.TerminationOrdered => "ต้องบันทึกแบบ คบ.16 และ คบ.17 ให้สมบูรณ์ก่อนส่งแจ้งผล",
        "start-protection" => "ต้องบันทึกแบบ คบ.8 และ คบ.11 ให้สมบูรณ์ก่อนเริ่มการคุ้มครอง",
        "request-termination" => "ต้องบันทึกแบบ คบ.7 หรือ คบ.15 ให้สมบูรณ์ก่อนส่งขอยุติ",
        _ => "ต้องบันทึกแบบฟอร์มที่กำหนดให้สมบูรณ์ก่อนส่งต่อ"
    };
}

public sealed class WitnessWorkflowException(string message) : InvalidOperationException(message);
public sealed class WitnessAuthorizationException(string message) : UnauthorizedAccessException(message);
public sealed class WitnessConcurrencyException(string message) : InvalidOperationException(message);
