using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using EcmisWitness.Api.Services;
using EcmisWitness.Api.Forms;

namespace EcmisWitness.Api.Endpoints;

public static class WitnessEndpoints
{
    public static IEndpointRouteBuilder MapWitnessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/witness");

        group.MapGet("/forms/catalog", async (
            HttpContext http,
            WitnessUserContextService users,
            CancellationToken ct) =>
        {
            _ = await RequireUserAsync(http, users, ct);
            var mapping = WitnessProtectionFormCatalog.All.Select(form => new
            {
                form.Number,
                form.Code,
                form.Title,
                form.ReferencePage,
                SignaturePurposes = WitnessProtectionFormCatalog.SignaturePurposes(form.Number),
                Sections = form.Sections.Select((section, sectionIndex) => new
                {
                    Number = sectionIndex + 1,
                    section.Title,
                    section.Description,
                    Fields = section.Fields.Select(field => new
                    {
                        field.Key,
                        field.Label,
                        Type = field.Type.ToString(),
                        field.Required,
                        field.Hint,
                        field.Sensitive,
                        field.Options,
                        field.Columns
                    })
                })
            });
            return Results.Ok(ApiEnvelope<object>.Ok(mapping));
        });

        group.MapGet("/cases", async (
            HttpContext http,
            string? status,
            string? search,
            int? formNumber,
            DateOnly? receivedFrom,
            DateOnly? receivedTo,
            bool? isUrgent,
            string? riskLevel,
            string? owner,
            string? mainCase,
            string? appealSla,
            DateOnly? protectionExpiryBefore,
            string? transferStatus,
            string? organization,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var cases = await repository.ListAsync(user, new WitnessCaseSearchQuery(
                status, search, formNumber, receivedFrom, receivedTo, isUrgent,
                riskLevel, owner, mainCase, appealSla, protectionExpiryBefore,
                transferStatus, organization), ct);
            return Results.Ok(ApiEnvelope<IReadOnlyList<WitnessCaseSummaryDto>>.Ok(cases));
        });

        group.MapGet("/cases/export.xlsx", async (
            HttpContext http,
            string? status,
            string? search,
            int? formNumber,
            DateOnly? receivedFrom,
            DateOnly? receivedTo,
            bool? isUrgent,
            string? riskLevel,
            string? owner,
            string? mainCase,
            string? appealSla,
            DateOnly? protectionExpiryBefore,
            string? transferStatus,
            string? organization,
            WitnessUserContextService users,
            WitnessRepository repository,
            WitnessReportService reports,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var cases = await repository.ListAsync(user, new WitnessCaseSearchQuery(
                status, search, formNumber, receivedFrom, receivedTo, isUrgent,
                riskLevel, owner, mainCase, appealSla, protectionExpiryBefore,
                transferStatus, organization), ct);
            var content = reports.GenerateRegistryXlsx(cases);
            var timestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMdd-HHmm");
            return Results.File(content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"witness-registry-{timestamp}.xlsx");
        });

        group.MapGet("/alerts", async (
            HttpContext http,
            bool? unreadOnly,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var alerts = await repository.ListNotificationsAsync(user, unreadOnly ?? true, ct);
            return Results.Ok(ApiEnvelope<IReadOnlyList<WitnessNotificationDto>>.Ok(alerts));
        });

        group.MapPost("/alerts/{notificationId:guid}/acknowledge", async (
            Guid notificationId,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            await repository.AcknowledgeNotificationAsync(notificationId, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<object>.Ok(new { notificationId }, "รับทราบแจ้งเตือนแล้ว"));
        });

        group.MapPost("/cases", async (
            HttpContext http,
            CreateWitnessCaseRequest request,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var created = await repository.CreateAsync(request, user, ClientIp(http), ct);
            return Results.Created($"/api/witness/cases/{created.Case.Id}", ApiEnvelope<WitnessCaseDetailDto>.Ok(created, "บันทึกคำร้องเรียบร้อย"));
        });

        group.MapGet("/cases/{caseId:guid}", async (
            Guid caseId,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.GetDetailAsync(caseId, user, ClientIp(http), ct);
            return result is null
                ? Results.NotFound(ApiEnvelope<object>.Fail("ไม่พบแฟ้มคำร้อง"))
                : Results.Ok(ApiEnvelope<WitnessCaseDetailDto>.Ok(result));
        });

        group.MapPost("/cases/{caseId:guid}/assignments", async (
            Guid caseId,
            CreateWitnessCaseAssignmentRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            if (!user.HasPermission(WitnessPermissions.AssignmentManage))
                throw new WitnessAuthorizationException("ไม่มีสิทธิ์มอบหมายผู้รับผิดชอบแฟ้มคุ้มครองพยาน");
            var target = await users.ResolveAssignmentTargetAsync(request.TargetUsername, ct);
            var result = await repository.AssignCaseAsync(
                caseId, request, target, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessCaseDetailDto>.Ok(result, "มอบหมายผู้รับผิดชอบแล้ว"));
        });

        group.MapPost("/cases/{caseId:guid}/assignments/{assignmentId:guid}/end", async (
            Guid caseId,
            Guid assignmentId,
            EndWitnessCaseAssignmentRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.EndAssignmentAsync(
                caseId, assignmentId, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessCaseDetailDto>.Ok(result, "ยุติการมอบหมายแล้ว"));
        });

        group.MapGet("/main-cases/search", async (
            string search,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var results = await repository.SearchMainCasesAsync(search, user, ct);
            return Results.Ok(ApiEnvelope<IReadOnlyList<WitnessCaseLinkCandidateDto>>.Ok(results));
        });

        group.MapPut("/cases/{caseId:guid}/main-case", async (
            Guid caseId,
            LinkWitnessMainCaseRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.LinkMainCaseAsync(caseId, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessCaseDetailDto>.Ok(result, "เชื่อมโยงคดีหลักและบันทึกการประเมินภัยแล้ว"));
        });

        group.MapPut("/cases/{caseId:guid}/main-case/new", async (
            Guid caseId,
            RecordNewMainCaseRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.RecordNewMainCaseAsync(caseId, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessCaseDetailDto>.Ok(result, "บันทึกเป็นคดีใหม่และประเมินภัยแล้ว"));
        });

        group.MapGet("/cases/{caseId:guid}/forms/{formNumber:int}", async (
            Guid caseId,
            int formNumber,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.GetFormAsync(caseId, formNumber, user, ClientIp(http), ct);
            return result is null
                ? Results.NotFound(ApiEnvelope<object>.Fail($"ยังไม่มีแบบ คบ.{formNumber} ในแฟ้มนี้"))
                : Results.Ok(ApiEnvelope<WitnessFormDto>.Ok(result));
        });

        group.MapPut("/cases/{caseId:guid}/forms/{formNumber:int}", async (
            Guid caseId,
            int formNumber,
            SaveWitnessFormRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.SaveFormAsync(caseId, formNumber, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessFormDto>.Ok(result, request.Complete ? "บันทึกฉบับสมบูรณ์แล้ว" : "บันทึกร่างแล้ว"));
        });

        group.MapPost("/cases/{caseId:guid}/forms/{formNumber:int}/sign", async (
            Guid caseId,
            int formNumber,
            SignWitnessFormRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.SignFormAsync(caseId, formNumber, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessFormDto>.Ok(result, "ลงนามอิเล็กทรอนิกส์แล้ว"));
        });

        group.MapPost("/cases/{caseId:guid}/commands/{action}", async (
            Guid caseId,
            string action,
            ExecuteWitnessCommandRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.ExecuteCommandAsync(caseId, action, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessCommandResultDto>.Ok(result, "บันทึกการส่งต่องานแล้ว"));
        });

        group.MapPost("/cases/{caseId:guid}/external-results", async (
            Guid caseId,
            ReceiveExternalResultRequest request,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.ReceiveExternalResultAsync(caseId, request, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessCommandResultDto>.Ok(result, "รับผลจาก External Module แล้ว"));
        });

        group.MapPost("/cases/{caseId:guid}/attachments", async (
            Guid caseId,
            HttpContext http,
            IFormFile file,
            int? formNumber,
            int? formVersion,
            WitnessUserContextService users,
            WitnessRepository repository,
            WitnessFileValidator validator,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            if (file.Length > validator.MaxBytes)
                throw new InvalidOperationException($"ไฟล์แนบต้องมีขนาดไม่เกิน {validator.MaxBytes / 1024 / 1024} MB");
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var content = buffer.ToArray();
            validator.Validate(file.FileName, file.ContentType, content);
            var result = await repository.AddAttachmentAsync(caseId, formNumber, formVersion,
                Path.GetFileName(file.FileName), string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                content, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<WitnessAttachmentDto>.Ok(result, "อัปโหลดเอกสารแล้ว"));
        }).DisableAntiforgery();

        group.MapGet("/cases/{caseId:guid}/attachments/{attachmentId:guid}", async (
            Guid caseId,
            Guid attachmentId,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.GetAttachmentContentAsync(caseId, attachmentId, user, ClientIp(http), ct);
            return result is null
                ? Results.NotFound(ApiEnvelope<object>.Fail("ไม่พบเอกสารแนบ"))
                : Results.File(result.Content, result.ContentType, result.FileName, enableRangeProcessing: true);
        });

        group.MapDelete("/cases/{caseId:guid}/attachments/{attachmentId:guid}", async (
            Guid caseId,
            Guid attachmentId,
            string reason,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            await repository.DeleteAttachmentAsync(caseId, attachmentId, reason, user, ClientIp(http), ct);
            return Results.Ok(ApiEnvelope<object>.Ok(new { deleted = true }, "ลบเอกสารและบันทึก Audit Log แล้ว"));
        });

        group.MapGet("/cases/{caseId:guid}/forms/{formNumber:int}/document.docx", async (
            Guid caseId,
            int formNumber,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            WitnessDocumentService documents,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            if (!user.HasPermission(WitnessPermissions.DocumentDownload))
                throw new UnauthorizedAccessException("ไม่มีสิทธิ์สร้างหรือดาวน์โหลดเอกสาร");
            var form = await repository.GetFormAsync(caseId, formNumber, user, ClientIp(http), ct)
                       ?? throw new InvalidOperationException($"ยังไม่มีแบบ คบ.{formNumber} ในแฟ้มนี้");
            if (form.Status == "draft")
                throw new InvalidOperationException("ต้องบันทึกแบบฟอร์มฉบับสมบูรณ์ก่อนสร้างเอกสาร");
            var content = documents.GenerateOfficialDocx(form);
            var fileName = $"{form.RequestNo}-คบ-{formNumber}-v{form.Version}.docx";
            await repository.RecordDocumentDownloadAsync(caseId, form.Id, formNumber, form.Version,
                user, ClientIp(http), ct);
            return Results.File(content,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        });

        group.MapGet("/cases/{caseId:guid}/audit", async (
            Guid caseId,
            HttpContext http,
            WitnessUserContextService users,
            WitnessRepository repository,
            CancellationToken ct) =>
        {
            var user = await RequireUserAsync(http, users, ct);
            var result = await repository.GetAuditAsync(caseId, user, ct);
            return Results.Ok(ApiEnvelope<IReadOnlyList<WitnessAuditDto>>.Ok(result));
        });

        return app;
    }

    private static async Task<WitnessUserContext> RequireUserAsync(
        HttpContext http,
        WitnessUserContextService users,
        CancellationToken ct)
        => await users.GetAsync(http, ct)
           ?? throw new UnauthorizedAccessException("กรุณาเข้าสู่ระบบก่อนใช้งานระบบคุ้มครองพยาน");

    private static string ClientIp(HttpContext http)
        => http.Connection.RemoteIpAddress?.ToString() ?? "";
}
