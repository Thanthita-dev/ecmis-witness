namespace EcmisWitness.Api.Domain;

public sealed record WitnessRequiredSignature(int FormNumber, string Purpose);

public static class WitnessSignaturePolicy
{
    public static string? PurposeRequiredByWorkflowStage(
        int formNumber,
        string status,
        string urgentStatus)
        => (formNumber, status, urgentStatus) switch
        {
            (4, WitnessStatuses.StaffReview, "awaiting_kb4") => "เจ้าหน้าที่ผู้เสนอ",
            (4, WitnessStatuses.StaffReview, "supervisor_review") => "ผู้บังคับบัญชาชั้นต้น",
            (4, WitnessStatuses.StaffReview, "director_review") => "ผู้อำนวยการสำนัก/กอง",
            (5, WitnessStatuses.StaffReview, "director_review") => "ผู้อำนวยการสำนัก/กองผู้ออกคำสั่ง",
            (6, WitnessStatuses.StaffReview, _) => "เจ้าหน้าที่เจ้าของเรื่อง",
            (6, WitnessStatuses.SupervisorReview, _) => "ผู้บังคับบัญชาชั้นต้น",
            (6, WitnessStatuses.DirectorReview, _) => "ผู้อำนวยการสำนัก/กอง",
            (6, WitnessStatuses.ExternalPending, _) => "ผู้มีอำนาจจาก External Module",
            (14, WitnessStatuses.ExtensionSupervisorReview, _) => "ผู้บังคับบัญชาชั้นต้น",
            (14, WitnessStatuses.ExtensionDirectorReview, _) => "ผู้อำนวยการสำนัก/กอง",
            (14, WitnessStatuses.ExtensionExternalPending, _) => "ผู้มีอำนาจจาก External Module",
            (16, WitnessStatuses.TerminationExternalPending, _) => "เลขาธิการผู้ลงนามคำสั่ง",
            _ => null
        };

    public static void EnsurePurposeMatchesWorkflowStage(
        int formNumber,
        string status,
        string urgentStatus,
        string purpose)
    {
        var required = PurposeRequiredByWorkflowStage(formNumber, status, urgentStatus);
        if (!string.IsNullOrWhiteSpace(required)
            && !string.Equals(required, purpose, StringComparison.Ordinal))
        {
            throw new WitnessAuthorizationException(
                $"ขั้นตอนปัจจุบันต้องลงนามในฐานะ “{required}” เท่านั้น");
        }
    }

    public static IReadOnlyList<string> PrerequisitePurposes(int formNumber, string purpose)
        => (formNumber, purpose) switch
        {
            (5, "พยานผู้รับทราบ") => ["ผู้อำนวยการสำนัก/กองผู้ออกคำสั่ง"],
            (8, "พยานผู้รับทราบ") => ["เลขาธิการผู้ลงนามคำสั่ง"],
            _ => []
        };

    public static IReadOnlyList<WitnessRequiredSignature> Requirements(string action, string status)
        => (action, status) switch
        {
            ("submit-supervisor", _) =>
            [
                R(3, "ผู้ให้ถ้อยคำ"),
                R(3, "เจ้าหน้าที่ผู้บันทึกถ้อยคำ"),
                R(6, "เจ้าหน้าที่เจ้าของเรื่อง")
            ],
            ("request-withdrawal", _) or ("withdrawal-submit-supervisor", _) =>
            [
                R(3, "ผู้ให้ถ้อยคำ"),
                R(3, "เจ้าหน้าที่ผู้บันทึกถ้อยคำ")
            ],
            ("supervisor-forward", _) =>
            [
                R(6, "เจ้าหน้าที่เจ้าของเรื่อง"),
                R(6, "ผู้บังคับบัญชาชั้นต้น")
            ],
            ("director-forward", _) =>
            [
                R(6, "เจ้าหน้าที่เจ้าของเรื่อง"),
                R(6, "ผู้บังคับบัญชาชั้นต้น"),
                R(6, "ผู้อำนวยการสำนัก/กอง")
            ],
            ("urgent-submit-supervisor", _) => [R(4, "เจ้าหน้าที่ผู้เสนอ")],
            ("urgent-supervisor-forward", _) =>
            [
                R(4, "เจ้าหน้าที่ผู้เสนอ"),
                R(4, "ผู้บังคับบัญชาชั้นต้น")
            ],
            ("urgent-director-approve", _) =>
            [
                R(4, "เจ้าหน้าที่ผู้เสนอ"),
                R(4, "ผู้บังคับบัญชาชั้นต้น"),
                R(5, "ผู้อำนวยการสำนัก/กองผู้ออกคำสั่ง")
            ],
            ("send-notice", WitnessStatuses.ApprovedPendingNotice) =>
            [
                R(8, "เลขาธิการผู้ลงนามคำสั่ง"),
                R(9, "ผู้มีอำนาจลงนามหนังสือ")
            ],
            ("send-notice", WitnessStatuses.RejectedPendingNotice) =>
            [R(10, "ผู้มีอำนาจลงนามหนังสือ")],
            ("send-notice", WitnessStatuses.TerminationOrdered) =>
            [
                R(16, "เลขาธิการผู้ลงนามคำสั่ง"),
                R(17, "ผู้มีอำนาจลงนามหนังสือ")
            ],
            ("start-protection", _) =>
            [
                R(8, "เลขาธิการผู้ลงนามคำสั่ง"),
                R(8, "พยานผู้รับทราบ"),
                R(11, "พยานผู้รับความคุ้มครอง"),
                R(11, "เจ้าหน้าที่ผู้ให้ความคุ้มครอง"),
                R(11, "พยานรับรองคนที่ 1"),
                R(11, "พยานรับรองคนที่ 2")
            ],
            ("request-extension", _) =>
            [
                R(14, "พยาน/เจ้าหน้าที่ผู้ยื่นขยายเวลา")
            ],
            ("extension-supervisor-forward", _) =>
            [
                R(14, "พยาน/เจ้าหน้าที่ผู้ยื่นขยายเวลา"),
                R(14, "ผู้บังคับบัญชาชั้นต้น")
            ],
            ("extension-director-forward", _) =>
            [
                R(14, "พยาน/เจ้าหน้าที่ผู้ยื่นขยายเวลา"),
                R(14, "ผู้บังคับบัญชาชั้นต้น"),
                R(14, "ผู้อำนวยการสำนัก/กอง")
            ],
            ("external-result", WitnessStatuses.ExternalPending) =>
            [
                R(6, "ผู้มีอำนาจจาก External Module")
            ],
            ("external-approved", WitnessStatuses.ExternalPending) =>
            [
                R(8, "เลขาธิการผู้ลงนามคำสั่ง")
            ],
            ("external-result", WitnessStatuses.ExtensionExternalPending) =>
            [
                R(14, "ผู้มีอำนาจจาก External Module")
            ],
            ("external-result", WitnessStatuses.TerminationExternalPending) =>
            [
                R(16, "เลขาธิการผู้ลงนามคำสั่ง")
            ],
            ("complete-transfer", _) =>
            [
                R(12, "ผู้ส่งมอบ"),
                R(12, "ผู้รับมอบ"),
                R(12, "พยานฝ่ายส่งมอบ"),
                R(12, "พยานฝ่ายรับมอบ")
            ],
            _ => []
        };

    private static WitnessRequiredSignature R(int formNumber, string purpose)
        => new(formNumber, purpose);
}
