namespace EcmisWitness.Api.Domain;

public static class WitnessRegistryQueryContract
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const string DefaultSortBy = "updatedAt";
    public const string DefaultSortDirection = "desc";
    public const string StableTieBreakerSql = "visible_case.request_no ASC, visible_case.id ASC";

    public static readonly IReadOnlyList<string> MainCaseSummaryFields =
    [
        "provisional_case_subject",
        "new_case_subject",
        "linked_case_no",
        "linked_investigation_no"
    ];

    public static readonly IReadOnlyList<string> MainCaseLinkFields =
    [
        "complaint_case_no",
        "track_no",
        "pcms_no",
        "investigation_no",
        "complaint.case_no",
        "complaint.track_no",
        "complaint.metadata_json.red_case_no"
    ];

    public static readonly IReadOnlyDictionary<string, string> SortColumns =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["updatedAt"] = "updated_at",
            ["createdAt"] = "created_at",
            ["requestNumber"] = "request_no",
            ["status"] = "status",
            ["riskLevel"] = "risk_level",
            ["currentOwner"] = "current_owner_name"
        };

    public static readonly IReadOnlySet<string> StatusGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "staff_review",
        "supervisor_review",
        "director_review",
        "external_pending",
        "notice",
        "appeal",
        "protection"
    };

    public static string CanonicalSortBy(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? DefaultSortBy : value.Trim();
        var match = SortColumns.Keys.FirstOrDefault(key =>
            string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new WitnessWorkflowException(
            "ไม่รองรับฟิลด์ที่ใช้เรียงลำดับรายการคำร้อง");
    }

    public static string CanonicalSortDirection(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? DefaultSortDirection
            : value.Trim().ToLowerInvariant();
        return normalized is "asc" or "desc"
            ? normalized
            : throw new WitnessWorkflowException("ทิศทางการเรียงลำดับต้องเป็น asc หรือ desc");
    }

    public static string? CanonicalStatusGroup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        var match = StatusGroups.FirstOrDefault(group =>
            string.Equals(group, normalized, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new WitnessWorkflowException("กลุ่มสถานะทะเบียนคำร้องไม่ถูกต้อง");
    }

    public static string MainCasePredicate(string caseAlias, string parameter)
        => $"""
           (
               concat_ws(' ',
                   {caseAlias}.summary_data->>'provisional_case_subject',
                   {caseAlias}.summary_data->>'new_case_subject',
                   {caseAlias}.summary_data->>'linked_case_no',
                   {caseAlias}.summary_data->>'linked_investigation_no')
                   ILIKE '%' || {parameter} || '%' ESCAPE '\'
               OR EXISTS(
                   SELECT 1
                   FROM witness.case_links searched_link
                   LEFT JOIN public.tbl_cmp_complaint complaint
                     ON complaint.cmp_complaint_id=searched_link.complaint_id
                   WHERE searched_link.witness_case_id={caseAlias}.id
                     AND concat_ws(' ',
                         searched_link.complaint_case_no,
                         searched_link.track_no,
                         searched_link.pcms_no,
                         searched_link.investigation_no,
                         complaint.case_no,
                         complaint.track_no,
                         complaint.metadata_json->>'red_case_no')
                         ILIKE '%' || {parameter} || '%' ESCAPE '\')
           )
           """;

    public static string StatusGroupPredicate(string caseAlias, string parameter)
        => $"""
           ({parameter}::text IS NULL
            OR ({parameter}='staff_review' AND {caseAlias}.status IN ('staff_review','withdrawal_staff_revision'))
            OR ({parameter}='supervisor_review' AND {caseAlias}.status IN ('supervisor_review','withdrawal_supervisor_review','extension_supervisor_review'))
            OR ({parameter}='director_review' AND {caseAlias}.status IN ('director_review','withdrawal_director_review','extension_director_review'))
            OR ({parameter}='external_pending' AND {caseAlias}.status IN ('external_pending','withdrawal_external_pending','extension_external_pending','termination_external_pending','appeal_external_pending','transfer_external_pending'))
            OR ({parameter}='notice' AND {caseAlias}.status IN ('approved_pending_notice','rejected_pending_notice','termination_ordered','notice_sent'))
            OR ({parameter}='appeal' AND {caseAlias}.status LIKE 'appeal\_%' ESCAPE '\')
            OR ({parameter}='protection' AND {caseAlias}.status IN ('protection_setup','protection_active','extension_supervisor_review','extension_director_review','extension_external_pending','termination_external_pending','transfer_external_pending','transfer_waiting','transfer_accepted','transfer_rejected')))
           """;
}
