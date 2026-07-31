using System.Text.Json;
using EcmisWitness.Api.Domain;

namespace EcmisWitness.Api.Contracts;

// Canonical HTTP contract for ecmis-witness. The Blazor client keeps a source mirror
// because the repositories are deployed independently; evolve both sides compatibly.

public sealed record ApiEnvelope<T>(bool Success, T? Data, string? Message = null, string? Error = null)
{
    public static ApiEnvelope<T> Ok(T data, string? message = null) => new(true, data, message);
    public static ApiEnvelope<T> Fail(string error) => new(false, default, Error: error);
}

public sealed record WitnessCaseSummaryDto(
    Guid Id,
    string RequestNo,
    int IntakeFormNumber,
    string IntakeFormCode,
    string WitnessDisplayName,
    string PetitionerDisplayName,
    string Status,
    string StatusLabel,
    string CurrentOwnerRole,
    string CurrentOwnerName,
    string RiskLevel,
    bool IsUrgent,
    string UrgentStatus,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string NextAction,
    DateOnly? AppealDeadline = null,
    DateOnly? ProtectionEndDate = null,
    int ProtectionAccumulatedDays = 0,
    string OrganizationName = "");

public sealed record WitnessPagedResultDto<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long Total,
    int TotalPages,
    string SortBy,
    string SortDirection,
    IReadOnlyDictionary<string, long> StatusCounts);

public sealed record WitnessCaseDetailDto(
    WitnessCaseSummaryDto Case,
    IReadOnlyDictionary<string, string> IntakeValues,
    IReadOnlyList<WitnessFormSummaryDto> Forms,
    IReadOnlyList<WitnessAttachmentDto> Attachments,
    IReadOnlyList<WitnessWorkflowEventDto> WorkflowHistory,
    IReadOnlyList<WitnessAvailableActionDto> AvailableActions,
    WitnessCaseLinkDto? CaseLink,
    IReadOnlyList<WitnessCaseAssignmentDto> Assignments,
    WitnessAppealDto? CurrentAppeal = null,
    WitnessAppealResultNoticeDto? CurrentAppealResultNotice = null);

public sealed record WitnessAppealDto(
    Guid Id,
    Guid CaseId,
    DateTimeOffset FiledAt,
    string FiledChannel,
    string Statement,
    string? LateReason,
    bool IsLate,
    string Status,
    long Version,
    string? ExternalReference,
    string? Decision,
    DateTimeOffset? DecidedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WitnessAppealResultNoticeDto(
    Guid Id,
    Guid CaseId,
    Guid AppealId,
    Guid ExternalResultId,
    string ExternalReference,
    string Recipient,
    string DeliveryChannel,
    DateTimeOffset SentAt,
    Guid ProofAttachmentId,
    DateTimeOffset? ReceivedAt,
    string? ActualRecipient,
    string? ReceiptNote,
    Guid? ReceiptProofAttachmentId,
    string DeliveryStatus,
    string CompletionStatus,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record WitnessCaseAssignmentDto(
    Guid Id,
    Guid UserId,
    string Username,
    string AssignmentRole,
    Guid? OrganizationId,
    string OrganizationName,
    int? SourceFormNumber,
    string Reason,
    string AssignedBy,
    DateTimeOffset AssignedAt,
    DateTimeOffset? EndedAt);

public sealed record WitnessCaseSearchQuery(
    string? Status = null,
    string? Search = null,
    int? FormNumber = null,
    DateOnly? ReceivedFrom = null,
    DateOnly? ReceivedTo = null,
    bool? IsUrgent = null,
    string? RiskLevel = null,
    string? Owner = null,
    string? MainCase = null,
    string? AppealSla = null,
    DateOnly? ProtectionExpiryBefore = null,
    string? TransferStatus = null,
    string? Organization = null,
    int Page = WitnessRegistryQueryContract.DefaultPage,
    int PageSize = WitnessRegistryQueryContract.DefaultPageSize,
    string? SortBy = WitnessRegistryQueryContract.DefaultSortBy,
    string? SortDirection = WitnessRegistryQueryContract.DefaultSortDirection,
    string? StatusGroup = null);

public sealed record WitnessCaseLinkCandidateDto(
    long ComplaintId,
    string CaseNo,
    string TrackNo,
    string PcmsNo,
    string InvestigationNo,
    string AccusedDisplayName,
    string AccusedAgency,
    string Description,
    int MatchScore);

public sealed record WitnessCaseLinkDto(
    long? ComplaintId,
    string CaseNo,
    string TrackNo,
    string PcmsNo,
    string InvestigationNo,
    string AccusedDisplayName,
    string AccusedAgency,
    string RelationshipReason,
    string RiskLevel,
    string LinkedBy,
    DateTimeOffset LinkedAt,
    string LinkType,
    string ProvisionalCaseSubject);

public sealed record WitnessFormSummaryDto(
    Guid Id,
    int FormNumber,
    int Version,
    string Status,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    int SignatureCount,
    IReadOnlyList<string> SignaturePurposes);

public sealed record WitnessFormDto(
    Guid Id,
    Guid CaseId,
    string RequestNo,
    int FormNumber,
    int Version,
    string Status,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<WitnessSignatureDto> Signatures,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    long CaseVersion,
    IReadOnlyList<WitnessFormOpinionDto> Opinions);

public sealed record WitnessFormOpinionDto(
    Guid Id,
    int FormNumber,
    int FormVersion,
    string OpinionPurpose,
    string OpinionText,
    string ActorName,
    string ActorPosition,
    string ActorRole,
    DateTimeOffset CreatedAt);

public sealed record WitnessSignatureDto(
    Guid Id,
    int FormVersion,
    string SignerName,
    string SignerPosition,
    string SignerRole,
    string SignerPurpose,
    string VerificationMethod,
    string EvidenceReference,
    string DocumentHash,
    DateTimeOffset SignedAt);

public sealed record WitnessAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    string Classification,
    int? FormNumber,
    int? FormVersion,
    DateTimeOffset UploadedAt,
    string UploadedBy,
    Guid? AppealId = null,
    string? EvidenceType = null);

public sealed record WitnessKb1ShareLinkDto(
    Guid Id,
    Guid CaseId,
    string RequestNo,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAccessedAt,
    DateTimeOffset? SubmittedAt,
    string? AccessToken = null);

public sealed record WitnessPublicKb1DraftDto(
    string RequestNo,
    string Status,
    DateTimeOffset ExpiresAt,
    int FormVersion,
    long CaseVersion,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<WitnessAttachmentDto> Attachments);

public sealed record SaveWitnessPublicKb1Request(
    Dictionary<string, string> Values,
    bool Complete,
    bool ConfirmAccuracy,
    int ExpectedFormVersion,
    long ExpectedCaseVersion,
    string? IdempotencyKey = null);

public sealed record WitnessWorkflowEventDto(
    Guid Id,
    string Action,
    string FromStatus,
    string ToStatus,
    string ActorName,
    string ActorRole,
    string Reason,
    string? ExternalReference,
    DateTimeOffset OccurredAt);

public sealed record WitnessAvailableActionDto(
    string Code,
    string Label,
    string TargetStatus,
    bool RequiresReason,
    int? RequiredFormNumber);

public sealed record CreateWitnessCaseRequest(
    int FormNumber,
    Dictionary<string, string> Values,
    bool IsUrgent = false,
    bool Submit = true,
    string? IdempotencyKey = null);

public sealed record SaveWitnessFormRequest(
    Dictionary<string, string> Values,
    bool Complete,
    int ExpectedFormVersion,
    long ExpectedCaseVersion);

public sealed record SignWitnessFormRequest(
    string Purpose,
    string Position,
    string VerificationMethod,
    string EvidenceReference,
    int ExpectedFormVersion,
    long ExpectedCaseVersion,
    string? OpinionText = null);

public sealed record ExecuteWitnessCommandRequest(
    string Reason,
    long ExpectedVersion,
    Dictionary<string, string>? Data = null,
    string? IdempotencyKey = null);

public sealed record ReceiveExternalResultRequest(
    string ResultType,
    string ReferenceNo,
    DateTimeOffset DecisionAt,
    string Reason,
    long ExpectedVersion,
    Dictionary<string, string>? Data = null,
    string? IdempotencyKey = null);

public sealed record LinkWitnessMainCaseRequest(
    long ComplaintId,
    string RelationshipReason,
    string RiskLevel,
    long ExpectedVersion);

public sealed record RecordNewMainCaseRequest(
    string CaseSubject,
    string NewCaseReason,
    string RiskLevel,
    long ExpectedVersion);

public sealed record CreateWitnessCaseAssignmentRequest(
    string TargetUsername,
    string AssignmentRole,
    string Reason,
    int? SourceFormNumber,
    long ExpectedVersion);

public sealed record EndWitnessCaseAssignmentRequest(
    string Reason,
    long ExpectedVersion);

public sealed record WitnessCommandResultDto(
    Guid CaseId,
    string RequestNo,
    string FromStatus,
    string ToStatus,
    long Version,
    IReadOnlyList<WitnessAvailableActionDto> AvailableActions);

public sealed record WitnessAuditDto(
    Guid Id,
    string Action,
    string EntityType,
    string EntityId,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    JsonElement Details);

public sealed record WitnessNotificationDto(
    Guid Id,
    Guid CaseId,
    string RequestNo,
    string AlertType,
    DateTimeOffset? DueAt,
    string Severity,
    string Title,
    string Message,
    string Status,
    DateTimeOffset CreatedAt);
