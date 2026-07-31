namespace EcmisWitness.Api.Domain;

public static class WitnessFormStagePolicy
{
    public static void EnsureCanSave(int formNumber, string caseStatus, string urgentStatus)
    {
        if (CanSave(formNumber, caseStatus, urgentStatus))
            return;
        throw new WitnessWorkflowException(
            $"แบบ คบ.{formNumber} ไม่สามารถสร้างหรือแก้ไขในขั้นตอนปัจจุบันของแฟ้มได้");
    }

    public static bool CanSave(int formNumber, string caseStatus, string urgentStatus)
        => formNumber switch
        {
            1 or 2 => caseStatus == WitnessStatuses.IntakeDraft,
            3 => caseStatus is WitnessStatuses.StaffReview or WitnessStatuses.WithdrawalStaffRevision,
            4 => caseStatus == WitnessStatuses.StaffReview
                 && urgentStatus is "awaiting_kb4" or "supervisor_review" or "director_review",
            5 => caseStatus == WitnessStatuses.StaffReview && urgentStatus == "director_review",
            6 => caseStatus is WitnessStatuses.StaffReview
                or WitnessStatuses.SupervisorReview
                or WitnessStatuses.DirectorReview
                or WitnessStatuses.ExternalPending,
            7 or 13 => caseStatus == WitnessStatuses.ProtectionActive,
            14 => caseStatus is WitnessStatuses.ProtectionActive
                or WitnessStatuses.ExtensionSupervisorReview
                or WitnessStatuses.ExtensionDirectorReview
                or WitnessStatuses.ExtensionExternalPending,
            15 => caseStatus is WitnessStatuses.ProtectionActive
                or WitnessStatuses.TerminationExternalPending,
            8 => caseStatus is WitnessStatuses.ExternalPending
                or WitnessStatuses.ApprovedPendingNotice
                or WitnessStatuses.ProtectionSetup,
            9 => caseStatus == WitnessStatuses.ApprovedPendingNotice,
            10 => caseStatus == WitnessStatuses.RejectedPendingNotice,
            11 => caseStatus == WitnessStatuses.ProtectionSetup,
            12 => caseStatus == WitnessStatuses.TransferAccepted,
            16 => caseStatus == WitnessStatuses.TerminationExternalPending,
            17 => caseStatus == WitnessStatuses.TerminationOrdered,
            _ => false
        };
}
