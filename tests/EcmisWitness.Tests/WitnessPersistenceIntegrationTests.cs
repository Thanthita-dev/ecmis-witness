using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

public sealed class WitnessPersistenceIntegrationTests
{
    [Fact]
    public async Task Activity12_assignment_grants_only_the_named_user_and_rejects_cross_organization_target()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        var manager = new WitnessUserContext(Guid.NewGuid(), "assignment-admin", "ผู้ดูแลการมอบหมาย", "ผู้ดูแลระบบ",
            new HashSet<string> { "super_admin" }, new HashSet<string>());
        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var assignedUserId = Guid.NewGuid();
        var assignedUser = new WitnessUserContext(
            assignedUserId, "activity12-officer", "เจ้าหน้าที่กิจกรรมที่ 12", "นักสืบสวน",
            new HashSet<string> { "witness_officer" },
            new HashSet<string>
            {
                WitnessPermissions.ViewMasked,
                WitnessPermissions.ViewPii,
                WitnessPermissions.ViewSafeHouse,
                WitnessPermissions.Edit,
                WitnessPermissions.OfficerReview,
                WitnessPermissions.ProtectionManage
            },
            organizationA, "สำนักทดสอบ เอ", "department");
        Guid caseId = Guid.Empty;

        try
        {
            var created = await repository.CreateAsync(new CreateWitnessCaseRequest(
                1,
                new Dictionary<string, string>
                {
                    ["witness_first_name"] = "ผู้รับมอบหมาย",
                    ["witness_last_name"] = "ทดสอบ",
                    ["petitioner_first_name"] = "ผู้รับมอบหมาย",
                    ["petitioner_last_name"] = "ทดสอบ",
                    ["safe_house_address"] = "สถานที่ปลอดภัยสำหรับทดสอบ"
                },
                Submit: false,
                IdempotencyKey: $"assignment-{Guid.NewGuid():N}"), manager, "127.0.0.1", default);
            caseId = created.Case.Id;
            await using (var prepare = dataSource.CreateCommand("""
                UPDATE witness.cases
                SET status='staff_review', owning_org_id=$2, current_owner_org_id=$2,
                    owning_org_name=$3, current_owner_org_name=$3,
                    current_owner_user_id=NULL, current_owner_name=''
                WHERE id=$1
                """))
            {
                prepare.Parameters.AddWithValue(caseId);
                prepare.Parameters.AddWithValue(organizationA);
                prepare.Parameters.AddWithValue("สำนักทดสอบ เอ");
                await prepare.ExecuteNonQueryAsync();
            }

            Assert.Null(await repository.GetDetailAsync(caseId, assignedUser, default));
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.AssignCaseAsync(
                caseId,
                new CreateWitnessCaseAssignmentRequest(
                    "wrong-org-officer", "officer", "ทดสอบข้ามหน่วยงาน", null, created.Case.Version),
                new WitnessAssignmentTarget(Guid.NewGuid(), "wrong-org-officer", "นักสืบสวน",
                    organizationB, "สำนักทดสอบ บี", "department"),
                manager, "127.0.0.1", default));

            var assigned = await repository.AssignCaseAsync(
                caseId,
                new CreateWitnessCaseAssignmentRequest(
                    assignedUser.Username, "officer", "มอบหมายตรวจคำร้อง", null, created.Case.Version),
                new WitnessAssignmentTarget(assignedUserId, assignedUser.Username, assignedUser.Position,
                    organizationA, "สำนักทดสอบ เอ", "department"),
                manager, "127.0.0.1", default);

            var activeAssignment = Assert.Single(assigned.Assignments.Where(item => item.EndedAt is null));
            Assert.Equal(assignedUserId, activeAssignment.UserId);
            Assert.Equal(assignedUser.Username, assigned.Case.CurrentOwnerName);
            Assert.NotNull(await repository.GetDetailAsync(caseId, assignedUser, default));
            Assert.Contains(await repository.ListAsync(assignedUser, null, null, default), item => item.Id == caseId);

            var ended = await repository.EndAssignmentAsync(caseId, activeAssignment.Id,
                new EndWitnessCaseAssignmentRequest("ยุติหลังทดสอบ", assigned.Case.Version),
                manager, "127.0.0.1", default);
            Assert.DoesNotContain(ended.Assignments, item => item.EndedAt is null);
            Assert.Null(await repository.GetDetailAsync(caseId, assignedUser, default));

            var form8Id = Guid.NewGuid();
            var protectionEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
            await using (var prepareProtectionStatus = dataSource.CreateCommand(
                "UPDATE witness.cases SET status='protection_setup' WHERE id=$1"))
            {
                prepareProtectionStatus.Parameters.AddWithValue(caseId);
                await prepareProtectionStatus.ExecuteNonQueryAsync();
            }
            await using (var prepareProtectionForm = dataSource.CreateCommand("""
                INSERT INTO witness.forms(
                    id, case_id, form_number, version, status, values_data,
                    updated_by, updated_by_name, updated_at)
                VALUES($2,$1,8,1,'completed',jsonb_build_object('end_date',$3::text),$4,$5,NOW());
                """))
            {
                prepareProtectionForm.Parameters.AddWithValue(caseId);
                prepareProtectionForm.Parameters.AddWithValue(form8Id);
                prepareProtectionForm.Parameters.AddWithValue(protectionEnd);
                prepareProtectionForm.Parameters.AddWithValue(manager.UserId);
                prepareProtectionForm.Parameters.AddWithValue(manager.DisplayName);
                await prepareProtectionForm.ExecuteNonQueryAsync();
            }
            var protectionAssignment = await repository.AssignCaseAsync(
                caseId,
                new CreateWitnessCaseAssignmentRequest(
                    assignedUser.Username, "protection_officer", "แต่งตั้งตาม คบ.8", 8, ended.Case.Version),
                new WitnessAssignmentTarget(assignedUserId, assignedUser.Username, assignedUser.Position,
                    organizationA, "สำนักทดสอบ เอ", "department"),
                manager, "127.0.0.1", default);
            var protectionMember = Assert.Single(protectionAssignment.Assignments.Where(item =>
                item.EndedAt is null && item.AssignmentRole == "protection_officer"));
            var visibleForm = await repository.GetFormAsync(caseId, 1, assignedUser, default);
            Assert.Equal("สถานที่ปลอดภัยสำหรับทดสอบ", visibleForm!.Values["safe_house_address"]);

            await using (var expireGrant = dataSource.CreateCommand("""
                UPDATE witness.case_secret_grants
                SET valid_to=NOW()-INTERVAL '1 minute'
                WHERE source_assignment_id=$1
                """))
            {
                expireGrant.Parameters.AddWithValue(protectionMember.Id);
                Assert.Equal(1, await expireGrant.ExecuteNonQueryAsync());
            }
            var maskedAfterExpiry = await repository.GetFormAsync(caseId, 1, assignedUser, default);
            Assert.Equal("***", maskedAfterExpiry!.Values["safe_house_address"]);

            var protectionEnded = await repository.EndAssignmentAsync(caseId, protectionMember.Id,
                new EndWitnessCaseAssignmentRequest("สิ้นสุดหน้าที่ชุดคุ้มครอง", protectionAssignment.Case.Version),
                manager, "127.0.0.1", default);
            Assert.DoesNotContain(protectionEnded.Assignments, item => item.EndedAt is null);
        }
        finally
        {
            if (caseId != Guid.Empty)
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Hierarchical_opinions_are_required_and_appended_without_overwriting_prior_role()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        var supervisor = new WitnessUserContext(Guid.NewGuid(), "opinion-supervisor", "หัวหน้ากลุ่มทดสอบ", "หัวหน้ากลุ่มงาน",
            new HashSet<string> { "super_admin" }, new HashSet<string>
            {
                WitnessPermissions.OfficerReview,
                WitnessPermissions.SupervisorReview
            });
        var director = new WitnessUserContext(Guid.NewGuid(), "opinion-director", "ผู้อำนวยการทดสอบ", "ผู้อำนวยการสำนัก",
            new HashSet<string> { "super_admin" }, new HashSet<string> { WitnessPermissions.DirectorReview });
        Guid caseId = Guid.Empty;

        try
        {
            var created = await repository.CreateAsync(new CreateWitnessCaseRequest(
                1,
                new Dictionary<string, string>
                {
                    ["witness_first_name"] = "ทดสอบ",
                    ["witness_last_name"] = "ความเห็นตามลำดับ",
                    ["petitioner_first_name"] = "ทดสอบ",
                    ["petitioner_last_name"] = "ความเห็นตามลำดับ"
                },
                Submit: false,
                IdempotencyKey: $"opinion-{Guid.NewGuid():N}"), supervisor, "127.0.0.1", default);
            caseId = created.Case.Id;
            var formId = Guid.NewGuid();
            await using (var insertForm = dataSource.CreateCommand("""
                INSERT INTO witness.forms(
                    id, case_id, form_number, version, status, values_data,
                    updated_by, updated_by_name, updated_at)
                VALUES($1,$2,6,1,'completed','{}'::jsonb,$3,$4,NOW())
                """))
            {
                insertForm.Parameters.AddWithValue(formId);
                insertForm.Parameters.AddWithValue(caseId);
                insertForm.Parameters.AddWithValue(supervisor.UserId);
                insertForm.Parameters.AddWithValue(supervisor.DisplayName);
                await insertForm.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.SignFormAsync(
                caseId, 6,
                new SignWitnessFormRequest("ผู้บังคับบัญชาชั้นต้น", "หัวหน้ากลุ่มงาน",
                    "integration-test", "OPINION-1", 1, created.Case.Version),
                supervisor, "127.0.0.1", default));

            var officerSigned = await repository.SignFormAsync(
                caseId, 6,
                new SignWitnessFormRequest("เจ้าหน้าที่เจ้าของเรื่อง", "เจ้าหน้าที่เจ้าของเรื่อง",
                    "integration-test", "OFFICER-SIGNATURE", 1, created.Case.Version),
                supervisor, "127.0.0.1", default);
            var duplicate = await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.SignFormAsync(
                caseId, 6,
                new SignWitnessFormRequest("เจ้าหน้าที่เจ้าของเรื่อง", "เจ้าหน้าที่เจ้าของเรื่อง",
                    "integration-test", "OFFICER-SIGNATURE-DUPLICATE", 1, officerSigned.CaseVersion),
                supervisor, "127.0.0.1", default));
            Assert.Contains("ลงนามเอกสารรุ่นนี้แล้ว", duplicate.Message);
            var first = await repository.SignFormAsync(
                caseId, 6,
                new SignWitnessFormRequest("ผู้บังคับบัญชาชั้นต้น", "หัวหน้ากลุ่มงาน",
                    "integration-test", "OPINION-1", 1, officerSigned.CaseVersion,
                    "ตรวจสอบแล้ว เห็นควรเสนอผู้อำนวยการ"),
                supervisor, "127.0.0.1", default);
            var second = await repository.SignFormAsync(
                caseId, 6,
                new SignWitnessFormRequest("ผู้อำนวยการสำนัก/กอง", "ผู้อำนวยการสำนัก",
                    "integration-test", "OPINION-2", 1, first.CaseVersion,
                    "เห็นชอบให้ส่งเรื่องไปยัง External Module"),
                director, "127.0.0.1", default);

            Assert.Equal(2, second.Opinions.Count);
            Assert.Contains(second.Signatures, item => item.FormVersion == 1
                                                       && item.SignerPurpose == "เจ้าหน้าที่เจ้าของเรื่อง");
            Assert.Contains(second.Signatures, item => item.FormVersion == 1
                                                       && item.SignerPurpose == "ผู้บังคับบัญชาชั้นต้น");
            Assert.Contains(second.Opinions, item => item.OpinionPurpose == "ผู้บังคับบัญชาชั้นต้น"
                                                     && item.OpinionText.Contains("เห็นควรเสนอ"));
            Assert.Contains(second.Opinions, item => item.OpinionPurpose == "ผู้อำนวยการสำนัก/กอง"
                                                     && item.OpinionText.Contains("External Module"));
        }
        finally
        {
            if (caseId != Guid.Empty)
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Configured_or_latest_case_summary_can_load_its_full_detail()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var repository = new WitnessRepository(
            dataSource,
            new WitnessWorkflowStateMachine(),
            new WitnessFormPolicy());
        var user = new WitnessUserContext(Guid.NewGuid(), "integration-reader", "Integration Reader", "ผู้ทดสอบ",
            new HashSet<string> { "admin" }, new HashSet<string> { "witness.*" });

        var configuredCaseId = Environment.GetEnvironmentVariable("WITNESS_TEST_CASE_ID");
        var latest = Guid.TryParse(configuredCaseId, out var caseId)
            ? (await repository.ListAsync(user, null, null, default)).FirstOrDefault(item => item.Id == caseId)
            : (await repository.ListAsync(user, null, null, default)).FirstOrDefault();
        if (Guid.TryParse(configuredCaseId, out _))
            Assert.NotNull(latest);
        if (latest is null)
            return;

        var detail = await repository.GetDetailAsync(latest.Id, user, default);
        Assert.NotNull(detail);
        Assert.Equal(latest.Id, detail.Case.Id);
        Assert.Equal(latest.RequestNo, detail.Case.RequestNo);
        Assert.Contains(detail.Forms, form => form.FormNumber == latest.IntakeFormNumber);

        foreach (var summary in detail.Forms)
        {
            var form = await repository.GetFormAsync(latest.Id, summary.FormNumber, user, default);
            var currentPurposes = form!.Signatures
                .Where(signature => signature.FormVersion == form.Version)
                .Select(signature => signature.SignerPurpose)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(purpose => purpose, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                currentPurposes,
                summary.SignaturePurposes.OrderBy(purpose => purpose, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Draft_and_attachment_survive_a_new_repository_instance()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString))
            return; // Local/CI environments without an integration database still run all pure tests.

        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(firstDataSource).InitializeAsync();
        var user = new WitnessUserContext(Guid.NewGuid(), "integration-test", "Integration Test", "ผู้ทดสอบ",
            new HashSet<string> { "admin" }, new HashSet<string>
            {
                "witness.*",
                WitnessPermissions.NoticeManage
            });
        var repository = new WitnessRepository(firstDataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        Guid caseId = Guid.Empty;
        long? complaintIdToDelete = null;

        try
        {
            var created = await repository.CreateAsync(new CreateWitnessCaseRequest(
                1,
                new Dictionary<string, string>
                {
                    ["witness_first_name"] = "ทดสอบ",
                    ["witness_last_name"] = "ความคงอยู่",
                    ["petitioner_first_name"] = "ทดสอบ",
                    ["petitioner_last_name"] = "ความคงอยู่"
                },
                Submit: false,
                IdempotencyKey: $"integration-{Guid.NewGuid():N}"), user, "127.0.0.1", default);
            caseId = created.Case.Id;

            var content = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 };
            var attachment = await repository.AddAttachmentAsync(caseId, 1, 1, "หลักฐาน.pdf", "application/pdf",
                content, user, "127.0.0.1", default);

            await using var secondDataSource = NpgsqlDataSource.Create(connectionString);
            var restartedRepository = new WitnessRepository(secondDataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
            var reloaded = await restartedRepository.GetDetailAsync(caseId, user, default);
            var downloaded = await restartedRepository.GetAttachmentContentAsync(caseId, attachment.Id, user, "127.0.0.1", default);

            Assert.NotNull(reloaded);
            Assert.Equal("intake_draft", reloaded.Case.Status);
            Assert.Equal("ทดสอบ", reloaded.IntakeValues["witness_first_name"]);
            Assert.Contains(reloaded.Attachments, item => item.Id == attachment.Id);
            Assert.Equal(content, downloaded!.Content);

            await using (var staffStatus = firstDataSource.CreateCommand("UPDATE witness.cases SET status='staff_review' WHERE id=$1"))
            {
                staffStatus.Parameters.AddWithValue(caseId);
                await staffStatus.ExecuteNonQueryAsync();
            }
            await using var complaintLookup = firstDataSource.CreateCommand("""
                SELECT cmp_complaint_id, case_no FROM public.tbl_cmp_complaint
                WHERE NOT is_deleted ORDER BY cmp_complaint_id LIMIT 1
                """);
            await using var complaintReader = await complaintLookup.ExecuteReaderAsync();
            long complaintId;
            string complaintCaseNo;
            if (await complaintReader.ReadAsync())
            {
                complaintId = complaintReader.GetInt64(0);
                complaintCaseNo = complaintReader.GetString(1);
            }
            else
            {
                await complaintReader.CloseAsync();
                var suffix = Guid.NewGuid().ToString("N")[..10];
                await using var createComplaint = firstDataSource.CreateCommand("""
                    INSERT INTO public.tbl_cmp_complaint(
                        track_no, case_no, channel_code, complaint_title,
                        complaint_description, category_code, metadata_json)
                    VALUES($1,$2,'integration-test','คดีทดสอบเชื่อมโยงพยาน',
                           'ใช้ทดสอบ Auto-link และจะถูกลบหลังทดสอบ','misconduct',
                           '{"accused_name":"ผู้ถูกกล่าวหาทดสอบ","accused_agency":"หน่วยงานทดสอบ"}'::jsonb)
                    RETURNING cmp_complaint_id, case_no
                    """);
                createComplaint.Parameters.AddWithValue($"WIT-IT-{suffix}");
                createComplaint.Parameters.AddWithValue($"IT/{suffix}");
                await using var createdComplaint = await createComplaint.ExecuteReaderAsync();
                Assert.True(await createdComplaint.ReadAsync());
                complaintId = createdComplaint.GetInt64(0);
                complaintCaseNo = createdComplaint.GetString(1);
                complaintIdToDelete = complaintId;
            }
            await complaintReader.CloseAsync();

            var provisional = await repository.RecordNewMainCaseAsync(caseId,
                new RecordNewMainCaseRequest(
                    "คดีรับใหม่จากคำร้องคุ้มครองพยาน",
                    "ยังไม่มีเลขคดีหลักและอยู่ระหว่างตรวจสอบก่อนรับเข้าสู่ระบบคดี",
                    "สูง",
                    reloaded.Case.Version),
                user, "127.0.0.1", default);
            Assert.NotNull(provisional.CaseLink);
            Assert.Null(provisional.CaseLink.ComplaintId);
            Assert.Equal("new_case", provisional.CaseLink.LinkType);
            Assert.Equal("คดีรับใหม่จากคำร้องคุ้มครองพยาน", provisional.CaseLink.ProvisionalCaseSubject);
            Assert.Equal("สูง", provisional.Case.RiskLevel);

            var provisionalAfterReload = await restartedRepository.GetDetailAsync(caseId, user, default);
            Assert.Equal("new_case", provisionalAfterReload!.CaseLink!.LinkType);

            var candidates = await repository.SearchMainCasesAsync(complaintCaseNo, user, default);
            Assert.Contains(candidates, item => item.ComplaintId == complaintId);
            var linked = await repository.LinkMainCaseAsync(caseId,
                new LinkWitnessMainCaseRequest(complaintId, "พยานให้ข้อมูลเกี่ยวกับคดีหลัก", "สูง", provisional.Case.Version),
                user, "127.0.0.1", default);
            Assert.Equal(complaintId, linked.CaseLink!.ComplaintId);
            Assert.Equal("existing_case", linked.CaseLink.LinkType);
            Assert.Equal("สูง", linked.Case.RiskLevel);

            await using (var prepareNoticeStatus = firstDataSource.CreateCommand("""
                UPDATE witness.cases
                SET status='approved_pending_notice', current_owner_role='officer'
                WHERE id=$1
                """))
            {
                prepareNoticeStatus.Parameters.AddWithValue(caseId);
                await prepareNoticeStatus.ExecuteNonQueryAsync();
            }
            var orderFormId = Guid.NewGuid();
            var noticeFormId = Guid.NewGuid();
            await using (var prepareNoticeForm = firstDataSource.CreateCommand("""
                INSERT INTO witness.forms(
                    id, case_id, form_number, version, status, values_data,
                    updated_by, updated_by_name, updated_at)
                VALUES
                    ($1,$3,8,1,'signed','{}'::jsonb,$4,$5,NOW()),
                    ($2,$3,9,1,'signed','{}'::jsonb,$4,$5,NOW())
                """))
            {
                prepareNoticeForm.Parameters.AddWithValue(orderFormId);
                prepareNoticeForm.Parameters.AddWithValue(noticeFormId);
                prepareNoticeForm.Parameters.AddWithValue(caseId);
                prepareNoticeForm.Parameters.AddWithValue(user.UserId);
                prepareNoticeForm.Parameters.AddWithValue(user.DisplayName);
                await prepareNoticeForm.ExecuteNonQueryAsync();
            }
            await using (var prepareNoticeSignatures = firstDataSource.CreateCommand("""
                INSERT INTO witness.form_signatures(
                    id, form_id, form_version, signer_user_id, signer_name, signer_position,
                    signer_role, signer_purpose, verification_method, evidence_reference,
                    document_hash, signed_at)
                VALUES
                    ($1,$3,1,$5,$6,'เลขาธิการ ป.ป.ท.','external_module',
                     'เลขาธิการผู้ลงนามคำสั่ง','integration-test','EXT-ORDER',repeat('a',64),NOW()),
                    ($2,$4,1,$5,$6,'ผู้มีอำนาจลงนาม','admin',
                     'ผู้มีอำนาจลงนามหนังสือ','integration-test','NOTICE',repeat('b',64),NOW())
                """))
            {
                prepareNoticeSignatures.Parameters.AddWithValue(Guid.NewGuid());
                prepareNoticeSignatures.Parameters.AddWithValue(Guid.NewGuid());
                prepareNoticeSignatures.Parameters.AddWithValue(orderFormId);
                prepareNoticeSignatures.Parameters.AddWithValue(noticeFormId);
                prepareNoticeSignatures.Parameters.AddWithValue(user.UserId);
                prepareNoticeSignatures.Parameters.AddWithValue(user.DisplayName);
                await prepareNoticeSignatures.ExecuteNonQueryAsync();
            }

            var sent = await repository.ExecuteCommandAsync(caseId, "send-notice",
                new ExecuteWitnessCommandRequest(
                    "ส่งหนังสืออนุมัติให้ผู้ยื่น",
                    linked.Case.Version,
                    new Dictionary<string, string>
                    {
                        ["sent_at"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["delivery_channel"] = "เจ้าหน้าที่นำส่ง",
                        ["recipient"] = "ผู้ยื่นคำร้อง",
                        ["tracking_reference"] = "INTEGRATION-TEST"
                    }), user, "127.0.0.1", default);
            Assert.Equal(WitnessStatuses.NoticeSent, sent.ToStatus);

            var noticeDetail = await repository.GetDetailAsync(caseId, user, default);
            Assert.Contains(noticeDetail!.AvailableActions, item => item.Code == "record-notice-receipt-approved");
            Assert.DoesNotContain(noticeDetail.AvailableActions, item => item.Code == "record-notice-receipt-rejected");

            var received = await repository.ExecuteCommandAsync(caseId, "record-notice-receipt-approved",
                new ExecuteWitnessCommandRequest(
                    "ผู้ยื่นได้รับหนังสือพร้อมลงหลักฐาน",
                    sent.Version,
                    new Dictionary<string, string>
                    {
                        ["received_at"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["receipt_proof_attachment_id"] = attachment.Id.ToString()
                    }), user, "127.0.0.1", default);
            Assert.Equal(WitnessStatuses.ProtectionSetup, received.ToStatus);

            await using var noticeCount = firstDataSource.CreateCommand("""
                SELECT COUNT(*) FROM witness.notice_deliveries
                WHERE case_id=$1 AND form_number=9
                  AND received_at IS NOT NULL AND receipt_proof_attachment_id=$2
                """);
            noticeCount.Parameters.AddWithValue(caseId);
            noticeCount.Parameters.AddWithValue(attachment.Id);
            Assert.Equal(1L, Convert.ToInt64(await noticeCount.ExecuteScalarAsync()));
        }
        finally
        {
            if (caseId != Guid.Empty)
            {
                await using var cleanup = firstDataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
            if (complaintIdToDelete.HasValue)
            {
                await using var cleanupComplaint = firstDataSource.CreateCommand("DELETE FROM public.tbl_cmp_complaint WHERE cmp_complaint_id=$1");
                cleanupComplaint.Parameters.AddWithValue(complaintIdToDelete.Value);
                await cleanupComplaint.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Pii_permission_does_not_leak_cases_across_activity12_organization_scope()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(
            dataSource,
            new WitnessWorkflowStateMachine(),
            new WitnessFormPolicy());
        var administrator = new WitnessUserContext(
            Guid.NewGuid(), "scope-test-admin", "Scope Test Admin", "ผู้ทดสอบ",
            new HashSet<string> { "super_admin" }, new HashSet<string>());
        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var createdCaseIds = new List<Guid>();

        try
        {
            async Task<Guid> CreateCaseAsync(string firstName, Guid organizationId, string organizationName, bool promote = true)
            {
                var created = await repository.CreateAsync(
                    new CreateWitnessCaseRequest(
                        1,
                        new Dictionary<string, string>
                        {
                            ["witness_first_name"] = firstName,
                            ["witness_last_name"] = "ทดสอบขอบเขต",
                            ["petitioner_first_name"] = firstName,
                            ["petitioner_last_name"] = "ทดสอบขอบเขต"
                        },
                        Submit: false,
                        IdempotencyKey: $"scope-{Guid.NewGuid():N}"),
                    administrator,
                    "127.0.0.1",
                    default);
                createdCaseIds.Add(created.Case.Id);
                if (!promote)
                    return created.Case.Id;
                await using var update = dataSource.CreateCommand("""
                    UPDATE witness.cases
                    SET status='staff_review', owning_org_id=$2, current_owner_org_id=$2,
                        owning_org_name=$3, current_owner_org_name=$3,
                        current_owner_user_id=NULL, current_owner_name=''
                    WHERE id=$1
                    """);
                update.Parameters.AddWithValue(created.Case.Id);
                update.Parameters.AddWithValue(organizationId);
                update.Parameters.AddWithValue(organizationName);
                await update.ExecuteNonQueryAsync();
                return created.Case.Id;
            }

            var caseA = await CreateCaseAsync("หน่วยเอ", organizationA, "หน่วยเอ");
            var caseB = await CreateCaseAsync("หน่วยบี", organizationB, "หน่วยบี");
            var draftB = await CreateCaseAsync("ร่างหน่วยบี", organizationB, "หน่วยบี", promote: false);
            await using (var routeExternal = dataSource.CreateCommand("UPDATE witness.cases SET current_owner_role='external_module' WHERE id=$1"))
            {
                routeExternal.Parameters.AddWithValue(caseB);
                await routeExternal.ExecuteNonQueryAsync();
            }
            var secretAttachment = await repository.AddAttachmentAsync(
                caseB, 1, 1, "หลักฐานลับ.pdf", "application/pdf",
                new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, administrator, "127.0.0.1", default);
            var supervisorA = new WitnessUserContext(
                Guid.NewGuid(), "supervisor-a", "หัวหน้าหน่วยเอ", "หัวหน้ากลุ่มงาน",
                new HashSet<string> { "group_head" },
                new HashSet<string>
                {
                    WitnessPermissions.ViewMasked,
                    WitnessPermissions.ViewPii,
                    WitnessPermissions.SupervisorReview,
                    WitnessPermissions.DocumentDownload,
                    WitnessPermissions.AuditView
                },
                organizationA,
                "หน่วยเอ",
                "department");
            var piiOnly = new WitnessUserContext(
                Guid.NewGuid(), "pii-only", "ผู้มีสิทธิ์ดูข้อมูล", "ผู้ตรวจข้อมูล",
                new HashSet<string>(),
                new HashSet<string> { WitnessPermissions.ViewMasked, WitnessPermissions.ViewPii },
                organizationA,
                "หน่วยเอ",
                "department");
            var externalModule = new WitnessUserContext(
                Guid.NewGuid(), "external-module", "ผู้รับผลจากระบบภายนอก", "เจ้าหน้าที่ประสานผล",
                new HashSet<string> { "external_module" },
                new HashSet<string> { WitnessPermissions.ExternalReceive });

            var supervisorCases = await repository.ListAsync(supervisorA, null, null, default);
            Assert.Contains(supervisorCases, item => item.Id == caseA);
            Assert.DoesNotContain(supervisorCases, item => item.Id == caseB);
            Assert.DoesNotContain(supervisorCases, item => item.Id == draftB);
            Assert.NotNull(await repository.GetDetailAsync(caseA, supervisorA, default));
            Assert.Null(await repository.GetDetailAsync(caseB, supervisorA, default));
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() =>
                repository.GetAttachmentContentAsync(
                    caseB, secretAttachment.Id, supervisorA, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() =>
                repository.GetAuditAsync(caseB, supervisorA, default));

            var piiOnlyCases = await repository.ListAsync(piiOnly, null, null, default);
            Assert.DoesNotContain(piiOnlyCases, item => item.Id == caseA || item.Id == caseB);
            Assert.Null(await repository.GetFormAsync(caseA, 1, piiOnly, default));

            var externalCases = await repository.ListAsync(externalModule, null, null, default);
            Assert.DoesNotContain(externalCases, item => item.Id == caseA);
            Assert.Contains(externalCases, item => item.Id == caseB);
            Assert.Null(await repository.GetDetailAsync(caseA, externalModule, default));
            Assert.NotNull(await repository.GetDetailAsync(caseB, externalModule, default));

            var adminCases = await repository.ListAsync(administrator, null, null, default);
            Assert.Contains(adminCases, item => item.Id == caseA);
            Assert.Contains(adminCases, item => item.Id == caseB);
            Assert.Contains(adminCases, item => item.Id == draftB);
            var adminDrafts = await repository.ListAsync(administrator,
                new WitnessCaseSearchQuery(Status: WitnessStatuses.IntakeDraft), default);
            Assert.Contains(adminDrafts, item => item.Id == draftB);
            var organizationAResults = await repository.ListAsync(administrator,
                new WitnessCaseSearchQuery(
                    FormNumber: 1,
                    ReceivedFrom: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                    ReceivedTo: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
                    IsUrgent: false,
                    RiskLevel: "ยังไม่ประเมิน",
                    Organization: "หน่วยเอ"), default);
            Assert.Contains(organizationAResults, item => item.Id == caseA);
            Assert.DoesNotContain(organizationAResults, item => item.Id == caseB);
            Assert.NotNull(await repository.GetAttachmentContentAsync(
                caseB, secretAttachment.Id, administrator, "127.0.0.1", default));
        }
        finally
        {
            foreach (var caseId in createdCaseIds)
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
