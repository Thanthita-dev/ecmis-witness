namespace EcmisWitness.Api.Forms;

public enum WitnessFormAccessRole
{
    Petitioner,
    Officer,
    Supervisor,
    Director,
    DeputySecretary,
    Secretary,
    CommitteeSecretary,
    Committee,
    ProtectionOfficer
}

public enum WitnessFormFieldType
{
    Text,
    TextArea,
    Date,
    Number,
    Select,
    MultiSelect,
    Repeating,
    Address,
    Checkbox,
    ReadOnly
}

public sealed record WitnessFormFieldDefinition(
    string Key,
    string Label,
    WitnessFormFieldType Type = WitnessFormFieldType.Text,
    bool Required = false,
    string Hint = "",
    IReadOnlyList<string>? Options = null,
    bool Sensitive = false,
    IReadOnlyList<WitnessRepeatingColumnDefinition>? Columns = null);

public sealed record WitnessRepeatingColumnDefinition(
    string Key,
    string Label,
    bool Required = false,
    string InputType = "text");

public sealed record WitnessFormSectionDefinition(
    string Title,
    string Description,
    IReadOnlyList<WitnessFormFieldDefinition> Fields);

public sealed record WitnessFormDefinition(
    int Number,
    string Code,
    string Title,
    string Purpose,
    int ReferencePage,
    IReadOnlyList<WitnessFormAccessRole> EditorRoles,
    IReadOnlyList<WitnessFormAccessRole> SignerRoles,
    IReadOnlyList<WitnessFormSectionDefinition> Sections)
{
    public IEnumerable<WitnessFormFieldDefinition> Fields => Sections.SelectMany(section => section.Fields);
}

public static class WitnessProtectionFormCatalog
{
    public const string Kb8StandardDuties = """
        1. ดำเนินการคุ้มครองพยานตามมาตรการคุ้มครองเบื้องต้นและปฏิบัติในส่วนที่เกี่ยวข้องกับการคุ้มครองพยานตามระเบียบโดยเคร่งครัด
        2. พิจารณาและเสนอความเห็นในวิธีการ การเปลี่ยนแปลง หรือยกเลิกวิธีการคุ้มครองพยานทั้งหมดหรือบางส่วนต่อเลขาธิการคณะกรรมการ ป.ป.ท.
        3. รายงานผลการดำเนินการ การปฏิบัติ และเหตุสำคัญต่อเลขาธิการคณะกรรมการ ป.ป.ท. ในห้วงเวลาที่จำเป็นหรือทุกระยะตามความเหมาะสม พร้อมเสนอความเห็นประกอบได้
        4. ดำเนินการอื่นใดตามที่เลขาธิการคณะกรรมการ ป.ป.ท. มอบหมาย
        """;

    private static readonly string[] NamePrefixes = ["นาย", "นาง", "นางสาว", "อื่น ๆ"];
    private static readonly string[] YesNo = ["ใช่", "ไม่ใช่"];
    private static readonly string[] ProtectionMethods =
    [
        "จัดเจ้าพนักงานเป็นชุดคุ้มครองความปลอดภัย",
        "จัดให้อยู่ในสถานที่เหมาะสม",
        "ปกปิดและรักษาความลับข้อมูลพยาน",
        "ประสานหน่วยงานอื่นให้การคุ้มครอง",
        "ดำเนินการอื่นใดตามความเหมาะสม"
    ];

    public static IReadOnlyList<WitnessFormDefinition> All { get; } =
    [
        new(
            1,
            "คบ.1",
            "คำร้องขอให้มีการคุ้มครองพยานตามมาตรการคุ้มครองเบื้องต้น",
            "พยาน ผู้มีประโยชน์เกี่ยวข้อง หรือผู้ยื่นแทน ใช้ยื่นคำร้องต่อสำนักงาน ป.ป.ท.",
            1,
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer],
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer],
            [
                Section("หัวเอกสาร", "เลขคำร้อง วันที่ยื่น และการจัดลำดับเร่งด่วนในระบบ",
                    R("request_no", "เลขที่คำร้อง"), D("request_date", "วัน/เดือน/ปี", true),
                    C("urgent", "จัดเป็นกรณีจำเป็นเร่งด่วน ต้องพิจารณาคุ้มครองชั่วคราว")),
                Section("1. ผู้ยื่นคำร้อง", "ข้อมูลผู้ยื่นและฐานะที่เกี่ยวข้อง",
                    S("petitioner_prefix", "คำนำหน้า", NamePrefixes, true), T("petitioner_first_name", "ชื่อ", true, sensitive: true), T("petitioner_last_name", "นามสกุล", true, sensitive: true),
                    T("petitioner_roles", "เกี่ยวข้องในฐานะ", true, "พยาน, ผู้ซึ่งมีประโยชน์เกี่ยวข้อง หรือผู้ยื่นคำร้องแทน"), ADR("petitioner_address", "ที่อยู่ปัจจุบัน", true),
                    T("petitioner_phone", "โทรศัพท์", true, sensitive: true), T("petitioner_citizen_id", "เลขประจำตัวประชาชน", false, sensitive: true),
                    T("petitioner_officer_id", "เลขบัตรประจำตัวเจ้าหน้าที่ของรัฐ", false, sensitive: true), T("petitioner_agency", "สังกัด"),
                    D("petitioner_card_issued", "วันออกบัตร"), D("petitioner_card_expired", "บัตรหมดอายุ")),
                Section("2. ข้อมูลพยาน", "ข้อมูลประจำตัว ครอบครัว ที่อยู่ และการติดต่อ",
                    S("witness_prefix", "คำนำหน้า", NamePrefixes, true), T("witness_first_name", "ชื่อ", true, sensitive: true), T("witness_last_name", "นามสกุล", true, sensitive: true),
                    T("witness_occupation", "อาชีพ", true), T("witness_marital_status", "สถานภาพ", true),
                    C("witness_has_children", "มีบุตร"), N("witness_children", "จำนวนบุตร"),
                    T("witness_father", "ชื่อบิดา"), T("witness_mother", "ชื่อมารดา"), ADR("witness_address", "ที่อยู่ปัจจุบัน", true),
                    T("witness_phone", "โทรศัพท์", true, sensitive: true), T("witness_workplace", "สถานที่ทำงาน", false, sensitive: true), T("witness_work_phone", "โทรศัพท์ที่ทำงาน", false, sensitive: true),
                    T("witness_citizen_id", "เลขประจำตัวประชาชน", false, sensitive: true), T("witness_officer_id", "เลขบัตรประจำตัวเจ้าหน้าที่ของรัฐ", false, sensitive: true),
                    T("witness_agency", "สังกัด"), D("witness_card_issued", "วันออกบัตร"), D("witness_card_expired", "บัตรหมดอายุ"),
                    T("witness_line", "Line ID", false, sensitive: true), T("witness_instagram", "Instagram", false, sensitive: true), T("witness_facebook", "Facebook", false, sensitive: true)),
                Section("3. บุคคลที่สามารถติดต่อได้", "ข้อมูลบุคคลติดต่อสำรอง",
                    T("contact_name", "ชื่อ-นามสกุล", true, sensitive: true), ADR("contact_address", "ที่อยู่"), T("contact_phone", "โทรศัพท์", true, sensitive: true),
                    T("contact_workplace", "สถานที่ทำงาน", false, sensitive: true), T("contact_work_phone", "โทรศัพท์ที่ทำงาน", false, sensitive: true)),
                Section("4. ความประสงค์", "ฐานะพยานและเรื่องร้องเรียน/กล่าวหา",
                    S("witness_stage", "ขอรับการคุ้มครองจากการ", ["จะมาเป็นพยาน", "ได้มาเป็นพยาน"], true), A("complaint_subject", "เรื่องร้องเรียน/กล่าวหา", true)),
                Section("5. พฤติการณ์แห่งคดีและความไม่ปลอดภัย", "ระบุรายละเอียดคดี การเข้าเป็นพยาน และเหตุไม่ปลอดภัย",
                    S("threat_status", "มีพฤติการณ์ความไม่ปลอดภัย", ["ไม่มี", "มี"], true), C("threat_unknown_call", "มีบุคคลไม่ทราบชื่อโทรศัพท์มาข่มขู่"),
                    C("threat_vehicle_follow", "มีรถยนต์/รถจักรยานยนต์ติดตาม"), C("threat_person_follow", "มีบุคคลเฝ้าติดตาม"),
                    C("threat_electronic", "ข่มขู่ทางวาจาหรือสื่ออิเล็กทรอนิกส์"), C("threat_physical", "ทำร้ายร่างกายหรือทรัพย์สิน"),
                    C("threat_hired", "จ้างวานให้ผู้อื่นข่มขู่หรือทำร้าย"), A("threat_details", "รายละเอียดพฤติการณ์", false, sensitive: true)),
                Section("6. บุคคลที่เกี่ยวข้อง", "เพิ่มได้หลายราย โดยระบุชื่อ เลขประจำตัว ความสัมพันธ์ และภัยที่ได้รับ",
                    G("related_people", "บุคคลใกล้ชิดที่ขอคุ้มครองร่วม", false, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("identity_no", "เลขประจำตัว"),
                        Col("relationship", "ความสัมพันธ์", true), Col("address", "ที่อยู่"), Col("threat", "ภัยที่ได้รับ"))),
                Section("7–8. เอกสารและการรับทราบสิทธิ", "รายการเอกสารแนบและคำยินยอมตามเงื่อนไขทั้ง 8 ข้อ",
                    C("attachment_witness", "เอกสารหลักฐานแสดงการเป็นพยาน"), C("attachment_damage", "เอกสารหลักฐานแสดงความเสียหาย"),
                    T("attachment_other", "เอกสารอื่น"), C("acknowledged_rights", "ผู้ยื่นรับทราบสิทธิ วิธีการ และเงื่อนไข", true))
            ]),
        new(
            2,
            "คบ.2",
            "คำร้องขอให้มีการคุ้มครองพยานผ่านช่องทางการสื่อสาร",
            "เจ้าพนักงานใช้บันทึกกรณีเร่งด่วนที่ผู้ร้องขอไม่อาจมายื่นคำร้องด้วยตนเอง",
            5,
            [WitnessFormAccessRole.Officer],
            [WitnessFormAccessRole.Officer],
            [
                Section("หัวเอกสาร", "เลขคำร้อง วันที่ และผู้บันทึก",
                    R("request_no", "เลขที่คำร้อง"), D("request_date", "วัน/เดือน/ปี", true), T("officer_recorder", "เจ้าพนักงานผู้บันทึก", true), T("officer_position", "ตำแหน่ง", true)),
                Section("เงื่อนไขการใช้แบบ คบ.2", "ใช้เมื่อเป็นกรณีเร่งด่วนและผู้ร้องไม่สามารถมายื่นคำร้องด้วยตนเอง",
                    C("unable_to_submit_in_person", "ผู้ร้องไม่สามารถมายื่นคำร้องด้วยตนเอง", true),
                    A("unable_to_submit_reason", "เหตุผลที่ไม่สามารถมายื่นด้วยตนเอง", true)),
                Section("ข้อมูลผู้ร้องขอ", "ข้อมูลตามทะเบียนบ้านและช่องทางติดต่อ",
                    S("reporter_prefix", "คำนำหน้า", NamePrefixes, true), T("reporter_first_name", "ชื่อตัว", true, sensitive: true), T("reporter_last_name", "ชื่อสกุล", true, sensitive: true),
                    N("reporter_age", "อายุ", true), ADR("registered_address", "ที่อยู่ตามทะเบียนบ้าน", true),
                    T("phone", "โทรศัพท์", true, sensitive: true), T("fax", "โทรสาร"), T("email", "E-mail", false, sensitive: true)),
                Section("คดีและภัยคุกคาม", "ฐานะในคดี ช่วงเวลาที่ขอคุ้มครอง และเหตุไม่ปลอดภัย",
                    A("case_relation", "เกี่ยวข้องกับเรื่องกล่าวหาร้องเรียนในฐานะ", true),
                    S("witness_stage", "ขอรับการคุ้มครอง", ["ก่อนมาเป็นพยาน", "ขณะเป็นพยาน", "หลังมาเป็นพยาน"], true),
                    A("case_subject", "คดีทุจริตในภาครัฐเกี่ยวกับเรื่อง", true), A("threat_details", "พฤติการณ์แห่งความไม่ปลอดภัย", true, sensitive: true)),
                Section("ช่องทางการสื่อสาร", "บันทึกหลักฐานว่ารับคำร้องผ่านช่องทางใด",
                    S("communication_method", "ช่องทางที่แจ้ง", ["โทรศัพท์", "โทรสาร", "E-mail", "หนังสือ", "แอปพลิเคชันสนทนา", "อื่น ๆ"], true),
                    T("communication_reference", "รายละเอียด/เลขอ้างอิงช่องทาง", true), A("other_details", "รายละเอียดอื่น"))
            ]),
        new(
            3,
            "คบ.3",
            "บันทึกข้อเท็จจริงประกอบการขอใช้มาตรการคุ้มครองเบื้องต้น",
            "เจ้าพนักงานบันทึกถ้อยคำของผู้ให้ถ้อยคำและพฤติการณ์ความไม่ปลอดภัย",
            6,
            [WitnessFormAccessRole.Officer],
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer],
            [
                Section("สถานที่และวันบันทึก", "ข้อมูลหัวบันทึกข้อเท็จจริง",
                    R("request_no", "เลขที่คำร้อง"), S("statement_type", "ประเภทการบันทึก", ["บันทึกข้อเท็จจริงปกติ", "บันทึกกรณีพยานขอถอนคำร้อง"], true),
                    T("written_at", "เขียนที่", true), D("statement_date", "วันที่ให้ถ้อยคำ", true)),
                Section("1–3. ผู้ให้ถ้อยคำ", "ข้อมูลประจำตัว ที่อยู่ และหนังสือสำคัญแสดงตน",
                    S("speaker_prefix", "คำนำหน้า", NamePrefixes, true), T("speaker_name", "ชื่อ-นามสกุล", true, sensitive: true), D("birth_date", "วันเกิด"), N("age", "อายุ", true),
                    T("race", "เชื้อชาติ"), T("nationality", "สัญชาติ"), T("occupation", "อาชีพ"), ADR("registered_address", "ที่อยู่ตามทะเบียนบ้าน", true),
                    ADR("current_address", "ที่อยู่ปัจจุบัน", true), T("phone", "โทรศัพท์", true, sensitive: true), T("identity_type", "ชนิดหนังสือสำคัญแสดงตน", true),
                    T("identity_no", "หมายเลข", true, sensitive: true), T("identity_issuer", "ออกให้ที่"), D("identity_issued", "วันออก"), D("identity_expired", "วันสิ้นอายุ")),
                Section("4–5. ข้อเท็จจริง", "ถ้อยคำประกอบคำร้องและพฤติการณ์ความไม่ปลอดภัย",
                    T("protected_person_name", "ชื่อพยานที่ขอคุ้มครอง", true, sensitive: true), A("corruption_case", "คดีทุจริตในภาครัฐเรื่อง", true),
                    A("statement", "ถ้อยคำประกอบคำร้อง", true, sensitive: true), A("threat_circumstances", "พฤติการณ์แห่งความไม่ปลอดภัย", false, sensitive: true),
                    A("withdrawal_reason", "เหตุผลและความประสงค์ขอถอนคำร้อง", false, sensitive: true),
                    C("statement_certified", "ผู้ให้ถ้อยคำรับรองว่าถ้อยคำถูกต้อง", true))
            ]),
        new(
            4,
            "คบ.4",
            "บันทึกเสนอขอคุ้มครองพยานชั่วคราว กรณีจำเป็นเร่งด่วน",
            "เจ้าหน้าที่เสนอ ผอ.สำนัก/กอง เพื่ออนุมัติคุ้มครองชั่วคราวและค่าใช้จ่าย",
            8,
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Director],
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Director],
            [
                Section("หัวบันทึกข้อความ", "ส่วนราชการ เลขหนังสือ วันที่ และสำนวน",
                    T("office", "ส่วนราชการ", true), T("office_phone", "โทร."), T("memo_no", "ที่", true), D("memo_date", "วันที่", true),
                    T("case_no", "สำนวนคดีเลขที่", true), T("director_office", "เรียน ผู้อำนวยการสำนัก/กอง/ศูนย์", true)),
                Section("1–3. เรื่องเดิม ข้อเท็จจริง และข้อกฎหมาย", "อ้างคำร้อง คดี สถานภาพพยาน และเหตุเร่งด่วน",
                    T("requester_name", "ผู้ร้อง/พยาน", true, sensitive: true), T("request_no", "คำร้องหมายเลขที่", true), A("case_background", "เรื่องเดิมและรายละเอียดพฤติการณ์แห่งคดี", true),
                    T("witness_role", "ฐานะของพยาน", true), A("threat_details", "พฤติการณ์คุกคามและระดับความรุนแรง", true, sensitive: true),
                    S("already_protected", "สถานภาพก่อนยื่นคำร้อง", ["ยังไม่อยู่ในการคุ้มครอง", "อยู่ในการคุ้มครองของเจ้าหน้าที่", "อื่น ๆ"], true),
                    A("legal_basis", "ข้อกฎหมาย", true)),
                Section("4. มาตรการชั่วคราว", "ชุดปฏิบัติการ รูปแบบ พื้นที่ ยานพาหนะ และงบประมาณ",
                    G("team_members", "หัวหน้าชุดและสมาชิกชุดคุ้มครอง", true, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("position", "ตำแหน่ง", true), Col("duty", "หน้าที่")),
                    M("protection_method", "มาตรการคุ้มครอง", ProtectionMethods, true),
                    D("start_date", "ตั้งแต่วันที่", true), D("end_date", "ถึงวันที่", true), T("area", "พื้นที่คุ้มครอง", true, sensitive: true),
                    T("vehicle", "รถยนต์ส่วนกลาง/ทะเบียน", false, sensitive: true),
                    N("travel_expense", "ค่าเดินทาง (บาท)"), N("operating_expense", "ค่าใช้จ่ายดำเนินการคุ้มครอง (บาท)"),
                    N("advance_amount", "เงินทดรองราชการ (บาท)"), A("expense_estimate", "รายละเอียดประมาณการค่าใช้จ่าย")),
                Section("5. ความเห็นและข้อเสนอแนะ", "ข้อเสนอ 5.1–5.3 ตามแบบทางการและความเห็น ผอ.",
                    C("proposal_5_1", "5.1 เห็นควรจัดให้มีการคุ้มครองชั่วคราว ออก คบ.5 และแจ้งพยาน"),
                    C("proposal_5_2", "5.2 เห็นควรประสานหน่วยงานอื่นให้การคุ้มครอง"), T("coordination_agency", "หน่วยงานที่ขอประสาน"),
                    C("proposal_5_3", "5.3 เห็นควรอนุมัติการเดินทางและค่าใช้จ่ายตาม 4.3–4.4"),
                    A("officer_recommendation", "ความเห็นและข้อเสนอแนะเพิ่มเติมของเจ้าหน้าที่"), A("director_opinion", "ความเห็นผู้อำนวยการสำนัก/กอง"))
            ]),
        new(
            5,
            "คบ.5",
            "คำสั่งมอบหมายเจ้าพนักงานดำเนินการให้ความคุ้มครองชั่วคราว",
            "ผอ.สำนัก/กอง ออกคำสั่งแต่งตั้งชุดคุ้มครองชั่วคราวในกรณีเร่งด่วน",
            10,
            [WitnessFormAccessRole.Director],
            [WitnessFormAccessRole.Director, WitnessFormAccessRole.Petitioner],
            [
                Section("คำสั่ง", "หน่วยงาน เลขคำสั่ง และผู้ได้รับความคุ้มครอง",
                    T("office", "สำนัก/กอง/ศูนย์", true), T("order_no", "เลขที่คำสั่ง", true), T("protected_person", "ชื่อพยาน/ผู้รับการคุ้มครอง", true, sensitive: true),
                    D("start_date", "ตั้งแต่วันที่", true), D("end_date", "ถึงวันที่", true)),
                Section("ชุดคุ้มครองชั่วคราว", "รายชื่อ ตำแหน่ง และรอบรายงาน",
                    G("team_members", "หัวหน้าชุดและสมาชิกชุดปฏิบัติการ", true, true,
                        Col("member_role", "ฐานะในชุด (หัวหน้าชุด/สมาชิก)", true), Col("full_name", "ชื่อ-นามสกุล", true),
                        Col("position", "ตำแหน่ง", true), Col("duty", "หน้าที่")),
                    S("report_cadence", "รายงานผลทุกระยะ", ["ทุกวัน", "ทุก 3 วัน", "ทุก 5 วัน"], true), D("ordered_at", "สั่ง ณ วันที่", true),
                    C("witness_acknowledged", "พยานได้รับทราบและยินยอม", true))
            ]),
        new(
            6,
            "คบ.6",
            "บันทึกการคุ้มครองพยานในเบื้องต้น",
            "เอกสารกลางสำหรับเสนอความเห็นตามลำดับ เจ้าหน้าที่ หัวหน้ากลุ่ม ผอ. รองเลขาธิการ และเลขาธิการ",
            12,
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Supervisor, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Supervisor, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [
                Section("หัวบันทึกและการรับเรื่อง", "ข้อมูลอ้างอิงคำร้องและผู้รับมอบ",
                    T("office", "ส่วนราชการ", true), T("memo_no", "ที่", true), D("memo_date", "วันที่", true), T("case_no", "สำนวนคดีเลขที่", true),
                    T("request_source", "ได้รับคำร้องจาก", true, sensitive: true), D("request_received", "วันที่รับคำร้อง", true), T("request_reference", "คำร้อง/เลขอ้างอิง", true),
                    T("assigned_by", "ผอ.มอบให้", true), T("assigned_to", "ผู้ได้รับมอบหมาย", true), D("assigned_at", "วันที่มอบหมาย", true)),
                Section("2–5. ข้อเท็จจริงและความยินยอม", "ผู้ถูกกล่าวหา พยาน ภัยคุกคาม รูปแบบ และสถานภาพ",
                    A("accused_facts", "ผู้ถูกกล่าวหาและพฤติการณ์คดี", true, sensitive: true), T("witness_name", "ชื่อ-นามสกุลพยาน", true, sensitive: true),
                    T("witness_id", "เลขประจำตัวประชาชน", true, sensitive: true), N("witness_age", "อายุ"), T("witness_occupation", "อาชีพ"),
                    ADR("witness_registered_address", "ที่อยู่ตามทะเบียนบ้าน", true), ADR("witness_current_address", "ที่อยู่ปัจจุบัน", true),
                    T("witness_role", "ฐานะของพยาน", true), A("threat_facts", "ข้อเท็จจริงเกี่ยวกับพฤติการณ์ความไม่ปลอดภัย", true, sensitive: true),
                    M("protection_method", "รูปแบบการคุ้มครอง", ProtectionMethods, true), D("start_date", "เริ่มคุ้มครอง", true), D("end_date", "สิ้นสุดรอบ", true),
                    N("duration_years", "ระยะเวลา (ปี)"), N("duration_months", "ระยะเวลา (เดือน)"), N("duration_days", "ระยะเวลา (วัน)", true),
                    S("current_status", "สถานภาพขณะยื่น", ["ยังไม่คุ้มครอง", "คุ้มครองชั่วคราว", "คุ้มครองโดยหน่วยงานอื่น"], true),
                    C("explicit_consent", "พยานยินยอมรับการคุ้มครองโดยชัดแจ้ง", true)),
                Section("6–9. เอกสาร กฎหมาย ข้อพิจารณา และข้อเสนอ", "เอกสารอย่างน้อย กฎหมาย และข้อเสนอเจ้าหน้าที่",
                    G("required_documents", "รายการเอกสารประกอบคำร้อง", true, false,
                        Col("document_name", "ชื่อเอกสาร", true), Col("document_no", "เลขที่/วันที่"), Col("certified", "รับรองสำเนา", false, "checkbox")),
                    A("legal_basis", "กฎหมาย กฎ ระเบียบที่เกี่ยวข้อง", true),
                    A("consideration", "ข้อพิจารณาความเกี่ยวพันและความสำคัญต่อรูปคดี", true),
                    G("team_members", "องค์ประกอบชุดคุ้มครอง", true, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("position", "ตำแหน่ง", true), Col("duty", "หน้าที่")),
                    A("officer_recommendation", "ความเห็นและข้อเสนอแนะ", true)),
                Section("10–13. ความเห็นตามลำดับ", "ทุกชั้นต้องบันทึกความเห็นและลงนามในระบบ",
                    A("supervisor_opinion", "ความเห็นผู้บังคับบัญชาชั้นต้น"), A("director_opinion", "ความเห็นผู้อำนวยการสำนัก"),
                    A("deputy_secretary_opinion", "ความเห็นรองเลขาธิการ"), A("secretary_opinion", "ความเห็นเลขาธิการ"))
            ]),
        new(
            7,
            "คบ.7",
            "คำร้องขอยุติการคุ้มครองพยาน",
            "พยานหรือบุคคลใกล้ชิดใช้ร้องขอให้ยุติการคุ้มครอง",
            15,
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer, WitnessFormAccessRole.ProtectionOfficer],
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer, WitnessFormAccessRole.ProtectionOfficer],
            [
                Section("ข้อมูลผู้ยื่นและมาตรการ", "ฐานะผู้ยื่น วิธีการ และหน่วยงานผู้คุ้มครอง",
                    R("request_no", "เลขที่คำร้อง"), D("request_date", "วันที่", true), T("requester_name", "ชื่อ-นามสกุลผู้ยื่น", true, sensitive: true),
                    S("requester_role", "เกี่ยวข้องในฐานะ", ["พยาน", "บุคคลผู้มีความสัมพันธ์ใกล้ชิดกับพยาน"], true),
                    S("protection_type", "วิธีการ/มาตรการที่ใช้", ["การคุ้มครองชั่วคราว", "การคุ้มครองเบื้องต้น"], true), T("protecting_agency", "หน่วยงานที่คุ้มครอง", true)),
                Section("ข้อมูลพยานและผู้ติดต่อ", "ข้อมูลพยานที่อยู่ในการคุ้มครอง",
                    T("witness_name", "ชื่อ-นามสกุลพยาน", true, sensitive: true), T("occupation", "อาชีพ"), T("marital_status", "สถานภาพ"),
                    ADR("current_address", "ที่อยู่ปัจจุบัน", true), T("workplace", "สถานที่ทำงาน", false, sensitive: true),
                    T("work_phone", "โทรศัพท์ที่ทำงาน", false, sensitive: true), T("phone", "โทรศัพท์", true, sensitive: true),
                    T("citizen_id", "เลขประจำตัวประชาชน", false, sensitive: true), T("officer_id", "เลขบัตรประจำตัวเจ้าหน้าที่ของรัฐ", false, sensitive: true),
                    T("contact_name", "บุคคลที่สามารถติดต่อได้", false, sensitive: true), ADR("contact_address", "ที่อยู่ผู้ติดต่อ"),
                    T("contact_phone", "โทรศัพท์ผู้ติดต่อ", false, sensitive: true), T("contact_workplace", "สถานที่ทำงานผู้ติดต่อ", false, sensitive: true),
                    T("contact_work_phone", "โทรศัพท์ที่ทำงานผู้ติดต่อ", false, sensitive: true)),
                Section("ความประสงค์และเหตุผล", "ระบุผู้ที่ขอยุติ เหตุผล และเอกสารประกอบ",
                    A("termination_targets", "ขอให้ยุติการคุ้มครองแก่บุคคลใด", true, sensitive: true), A("termination_reason", "เหตุผลในการขอยุติ", true, sensitive: true),
                    A("attachments", "เอกสารประกอบการยื่นคำร้อง", true))
            ]),
        new(
            8,
            "คบ.8",
            "คำสั่งมอบหมายเจ้าพนักงานดำเนินการให้ความคุ้มครองพยานตามมาตรการคุ้มครองเบื้องต้น",
            "เลขาธิการออกคำสั่งแต่งตั้งชุดปฏิบัติการและกำหนดอำนาจหน้าที่",
            17,
            [WitnessFormAccessRole.Officer],
            [WitnessFormAccessRole.Secretary],
            [
                Section("คำสั่งและระยะเวลา", "เลขคำสั่ง ผู้รับการคุ้มครอง และช่วงเวลาไม่เกิน 90 วันต่อรอบ",
                    T("order_no", "เลขที่คำสั่ง", true), T("protected_person", "พยาน/ผู้รับการคุ้มครอง", true, sensitive: true),
                    N("duration_days", "รวมจำนวนวัน", true), D("start_date", "ตั้งแต่วันที่", true), D("end_date", "ถึงวันที่", true)),
                Section("องค์ประกอบและอำนาจหน้าที่", "หัวหน้าชุด ชุดปฏิบัติการ และคำสั่งเพิ่มเติม",
                    T("team_leader", "หัวหน้าชุดปฏิบัติการ", true, sensitive: true), T("team_leader_position", "ตำแหน่งหัวหน้าชุด", true),
                    G("team_members", "รายชื่อชุดปฏิบัติการ", true, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("position", "ตำแหน่ง", true), Col("duty", "หน้าที่")),
                    R("duties", "อำนาจหน้าที่มาตรฐาน 4 ข้อ", true), D("ordered_at", "สั่ง ณ วันที่", true), C("witness_acknowledged", "พยานได้รับทราบและยินยอม", true))
            ]),
        new(
            9,
            "คบ.9",
            "หนังสือแจ้งตอบรับการให้ความคุ้มครอง",
            "แจ้งผู้ยื่นว่าคำร้องได้รับอนุมัติและให้ปฏิบัติตามเงื่อนไข",
            18,
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [
                Section("หนังสือภายนอก", "เลขหนังสือ วันที่ ผู้รับ และคำร้องอ้างถึง",
                    T("letter_no", "ที่", true), D("letter_date", "วันที่", true), T("recipient", "เรียน", true, sensitive: true),
                    T("request_reference", "อ้างถึงคำร้องลงวันที่", true), S("protected_target", "ผู้ได้รับความคุ้มครอง", ["พยาน", "บุคคลที่เกี่ยวข้อง"], true),
                    D("approved_at", "วันที่อนุมัติ", true), A("conditions", "เงื่อนไขระหว่างอยู่ในความคุ้มครอง", true),
                    T("office_contact", "สำนัก/โทร/โทรสาร", true))
            ]),
        new(
            10,
            "คบ.10",
            "หนังสือแจ้งคำสั่งไม่ให้พยานได้รับการคุ้มครอง",
            "แจ้งเหตุผลไม่อนุมัติและสิทธิอุทธรณ์ภายใน 30 วันนับแต่ได้รับแจ้ง",
            19,
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [
                Section("หนังสือภายนอก", "เลขหนังสือ วันที่ ผู้รับ และคำร้องอ้างถึง",
                    T("letter_no", "ที่", true), D("letter_date", "วันที่", true), T("recipient", "เรียน", true, sensitive: true),
                    T("request_reference", "อ้างถึงคำร้องลงวันที่", true), A("rejection_reason", "เหตุผลที่ไม่ให้ได้รับการคุ้มครอง", true),
                    C("appeal_right_notified", "แจ้งสิทธิอุทธรณ์ภายใน 30 วัน", true), T("office_contact", "สำนัก/โทร/โทรสาร", true))
            ]),
        new(
            11,
            "คบ.11",
            "บันทึกข้อตกลงการคุ้มครองพยานโดยใช้มาตรการเบื้องต้น",
            "พยานและผู้ให้การคุ้มครองตกลงรูปแบบ ระยะเวลา ค่าใช้จ่าย และเงื่อนไข 9 ข้อ",
            20,
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.ProtectionOfficer],
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.ProtectionOfficer],
            [
                Section("ข้อมูลคำร้องและพยาน", "อ้างการอนุมัติและข้อมูลพยาน",
                    R("request_no", "เลขที่คำร้อง"), D("agreement_date", "วัน/เดือน/ปี", true), T("requester_name", "ผู้ยื่นคำร้อง", true, sensitive: true),
                    T("protected_person", "ผู้ได้รับอนุมัติ", true, sensitive: true), T("witness_name", "ชื่อพยาน", true, sensitive: true), T("occupation", "อาชีพ"),
                    T("marital_status", "สถานภาพ"), T("father_name", "ชื่อบิดา"), T("mother_name", "ชื่อมารดา"), ADR("current_address", "ที่อยู่ปัจจุบัน", true),
                    T("phone", "โทรศัพท์", true, sensitive: true), T("workplace", "สถานที่ทำงาน", false, sensitive: true),
                    T("citizen_id", "เลขประจำตัวประชาชน", false, sensitive: true), T("officer_id", "เลขบัตรประจำตัวเจ้าหน้าที่ของรัฐ", false, sensitive: true)),
                Section("3–4. รูปแบบและระยะเวลา", "รูปแบบการคุ้มครองและกรอบเวลา 90/180 วัน",
                    M("protection_methods", "รูปแบบการคุ้มครองที่ตกลง", ProtectionMethods, true), D("start_date", "เริ่มคุ้มครอง", true), D("end_date", "สิ้นสุดรอบ", true),
                    N("round_days", "จำนวนวันรอบนี้", true), N("total_days", "จำนวนวันสะสม", true)),
                Section("5–6. วิธีการ เงื่อนไข และการสิ้นสุด", "ต้องยอมรับเงื่อนไขครบทั้ง 9 ข้อ",
                    C("condition_1", "1. รับทราบสิทธิและค่าใช้จ่าย", true), C("condition_2", "2. แจ้งก่อนเดินทางหรือปรากฏตัวสาธารณะ", true),
                    C("condition_3", "3. ออกนอกพื้นที่ได้เมื่อเจ้าหน้าที่เห็นชอบ", true), C("condition_4", "4. ให้ความร่วมมือและเชื่อฟังคำแนะนำ", true),
                    C("condition_5", "5. ไม่ทำให้เกิดความเสี่ยง", true), C("condition_6", "6. แจ้งทันทีเมื่อทราบข่าวการปองร้าย", true),
                    C("condition_7", "7. ยินยอมอยู่ในสถานที่ปลอดภัย/ปกปิดข้อมูล", true), C("condition_8", "8. กระทำผิดอาจถูกยกเลิกการคุ้มครอง", true),
                    C("condition_9", "9. รับทราบหลักเกณฑ์ค่าเช่าที่พักและค่าใช้จ่าย", true), A("additional_terms", "ข้อตกลงอื่น ๆ"),
                    C("termination_terms_acknowledged", "รับทราบเหตุสิ้นสุดและอำนาจตักเตือน", true))
            ]),
        new(
            12,
            "คบ.12",
            "บันทึกการส่งมอบพยานในคดีทุจริตในภาครัฐ",
            "บันทึกบุคคล หน่วยงาน ผู้ประสาน ผู้ส่งมอบ ผู้รับมอบ และลายมือชื่อทั้งสองฝ่าย",
            22,
            [WitnessFormAccessRole.ProtectionOfficer],
            [WitnessFormAccessRole.ProtectionOfficer, WitnessFormAccessRole.Petitioner],
            [
                Section("การส่งมอบ", "ผู้ส่งมอบ หน่วยงานปลายทาง วันเวลา และสถานที่",
                    R("request_no", "เลขที่คำร้อง"), D("handover_date", "วันที่", true), T("sender_name", "ผู้ส่งมอบ", true, sensitive: true),
                    T("sender_position", "ตำแหน่ง", true), T("sender_agency", "หน่วยงานผู้ส่งมอบ", true),
                    T("destination_agency", "หน่วยงานผู้รับมอบ", true), T("handover_place", "สถานที่ส่งมอบ", true, sensitive: true),
                    N("witness_count", "จำนวนพยาน", true),
                    G("witnesses", "รายชื่อพยานที่ส่งมอบ", true, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("identity_no", "เลขประจำตัว"), Col("address", "ที่อยู่")),
                    G("related_people", "รายชื่อบุคคลใกล้ชิด", false, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("relationship", "ความสัมพันธ์", true), Col("address", "ที่อยู่"))),
                Section("ผู้ประสานและลายมือชื่อ", "ข้อมูลติดต่อผู้ส่งและผู้รับมอบ",
                    A("sender_contact", "ผู้ประสานฝ่ายส่งมอบ", true, sensitive: true), A("receiver_contact", "ผู้ประสานฝ่ายรับมอบ", true, sensitive: true),
                    T("receiver_name", "ผู้รับมอบพยาน", true, sensitive: true), T("receiver_position", "ตำแหน่งผู้รับมอบ", true))
            ]),
        new(
            13,
            "คบ.13",
            "รายงานผลการปฏิบัติการคุ้มครองพยาน",
            "ชุดคุ้มครองรายงานอย่างน้อยเดือนละ 1 ครั้ง และรายงานเหตุสำคัญทันที",
            26,
            [WitnessFormAccessRole.ProtectionOfficer],
            [WitnessFormAccessRole.ProtectionOfficer, WitnessFormAccessRole.Petitioner],
            [
                Section("หัวรายงาน", "คดี ช่วงรายงาน สำนัก/กอง และข้อมูลพยาน",
                    R("request_no", "เลขที่รายงาน"), S("report_type", "ประเภทรายงาน", ["รายงานปกติ", "รายงานเหตุสำคัญ/เร่งด่วน"], true),
                    T("case_no", "คุ้มครองพยานคดี", true), D("period_start", "ตั้งแต่วันที่", true), D("period_end", "ถึงวันที่", true),
                    T("office", "สำนัก/กอง", true), T("witness_name", "ชื่อพยาน", true, sensitive: true), T("occupation", "อาชีพ"),
                    T("workplace", "สถานที่ทำงาน", false, sensitive: true), ADR("current_address", "ที่อยู่ปัจจุบัน"), T("phone", "โทรศัพท์", false, sensitive: true)),
                Section("เจ้าหน้าที่ คำสั่ง และผลดำเนินการ", "รายชื่อชุดคุ้มครอง อ้างคำสั่ง และสรุปผล",
                    G("officers", "เจ้าหน้าที่ที่ดำเนินการคุ้มครอง", true, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("position", "ตำแหน่ง", true), Col("duty", "หน้าที่")),
                    T("order_reference", "ตามคำสั่ง", true), D("order_date", "ลงวันที่", true),
                    N("duration_days", "ระยะเวลาให้การคุ้มครอง (วัน)", true), A("summary", "สรุปผลการดำเนินการ", true, sensitive: true),
                    T("incident_occurred_at", "วันเวลาเกิดเหตุสำคัญ"), A("incident_details", "รายละเอียดเหตุสำคัญและการตอบสนอง", false, sensitive: true)),
                Section("บันทึกการปฏิบัติรายวัน", "แต่ละรายการต้องมีวัน สถานการณ์ เจ้าหน้าที่ พยาน และหมายเหตุ",
                    G("activity_log", "รายการปฏิบัติหน้าที่/สถานการณ์ที่เกิดขึ้น", true, true,
                        Col("activity_date", "วันที่", true, "date"), Col("activity", "กิจกรรม/สถานการณ์", true),
                        Col("officer_signature", "ลายมือชื่อเจ้าหน้าที่", true), Col("witness_signature", "ลายมือชื่อพยาน", true), Col("note", "หมายเหตุ")))
            ]),
        new(
            14,
            "คบ.14",
            "หนังสือขยายระยะเวลาการคุ้มครองพยาน",
            "พยานหรือเจ้าหน้าที่เสนอขยายเวลา ครั้งละไม่เกิน 90 วัน และรวมไม่เกิน 180 วัน",
            25,
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer, WitnessFormAccessRole.ProtectionOfficer, WitnessFormAccessRole.Supervisor, WitnessFormAccessRole.Director, WitnessFormAccessRole.CommitteeSecretary, WitnessFormAccessRole.Committee, WitnessFormAccessRole.Secretary],
            [WitnessFormAccessRole.Petitioner, WitnessFormAccessRole.Officer, WitnessFormAccessRole.Supervisor, WitnessFormAccessRole.Director, WitnessFormAccessRole.CommitteeSecretary, WitnessFormAccessRole.Secretary],
            [
                Section("ข้อมูลการคุ้มครองเดิม", "พยาน คดี ช่วงเวลาเดิม และระยะเวลาสะสม",
                    R("request_no", "เลขที่คำร้อง"), D("request_date", "วันที่", true),
                    S("submitted_by_mode", "ผู้ยื่นคำขยายเวลา", ["พยานยื่นด้วยตนเอง", "เจ้าหน้าที่ชุดคุ้มครองยื่นแทน"], true),
                    A("proxy_submission_reason", "เหตุผลที่เจ้าหน้าที่ยื่นแทน"), T("witness_name", "ชื่อ-นามสกุลพยาน", true, sensitive: true),
                    T("case_no", "คดีหมายเลขที่", true), D("current_start", "เริ่มคุ้มครองเดิม", true), D("current_end", "สิ้นสุดรอบเดิม", true),
                    N("current_years", "ระยะเวลาเดิม (ปี)"), N("current_months", "ระยะเวลาเดิม (เดือน)"), N("current_days", "ระยะเวลาเดิม (วัน)", true),
                    N("total_days", "ระยะเวลาสะสมทั้งหมด (วัน)", true)),
                Section("ข้อพิจารณาและมติ", "เหตุผล มติอนุกรรมการ และช่วงเวลาที่ขอขยาย",
                    A("extension_reason", "ข้อพิจารณาและเหตุผล", true, sensitive: true), T("meeting_no", "มติที่ประชุมครั้งที่"), D("meeting_date", "วันที่ประชุม"),
                    N("extension_round", "ขยายครั้งที่", true), D("extension_start", "เริ่มช่วงขยาย", true), D("extension_end", "สิ้นสุดช่วงขยาย", true),
                    N("extension_years", "ระยะเวลาที่ขยาย (ปี)"), N("extension_months", "ระยะเวลาที่ขยาย (เดือน)"),
                    N("extension_days", "ระยะเวลาที่ขยาย (วัน)", true), A("secretary_opinion", "มติ/ความเห็นผู้มีอำนาจ"),
                    A("conditions", "เงื่อนไขการปฏิบัติตามคำสั่ง"))
            ]),
        new(
            15,
            "คบ.15",
            "รายงานการให้ความคุ้มครองพยานสิ้นสุด",
            "หัวหน้าชุดปฏิบัติการเสนอให้ยุติ พร้อมความเห็น ผอ. รองเลขาธิการ และเลขาธิการ",
            27,
            [WitnessFormAccessRole.ProtectionOfficer, WitnessFormAccessRole.Supervisor, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [WitnessFormAccessRole.ProtectionOfficer, WitnessFormAccessRole.Supervisor, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [
                Section("หัวบันทึกและคดี", "อ้างคำร้อง การอนุมัติ คู่กรณี และเรื่องกล่าวหา",
                    T("office", "สำนักงาน/ส่วนราชการ", true), T("memo_no", "ที่", true), D("memo_date", "วันที่", true),
                    T("memo_recipient", "เรียน/ผู้รับหนังสือ", true), T("requester_name", "ผู้ร้อง", true, sensitive: true),
                    A("case_parties", "คดีระหว่างผู้กล่าวหาและผู้ถูกกล่าวหา", true, sensitive: true), T("complaint_no", "เรื่องกล่าวหาที่", true),
                    T("original_approval_reference", "เลขที่คำสั่ง/หนังสืออนุมัติเดิม", true), D("original_approval_date", "วันที่อนุมัติเดิม", true),
                    D("original_protection_start", "เริ่มคุ้มครองเดิม", true), D("original_protection_end", "สิ้นสุดระยะคุ้มครองเดิม", true),
                    G("protected_people", "พยาน/บุคคลที่ได้รับความคุ้มครอง", true, true,
                        Col("full_name", "ชื่อ-นามสกุล", true), Col("relationship", "ฐานะ/ความสัมพันธ์"), Col("address", "ที่อยู่", true))),
                Section("เหตุสิ้นสุดและวันมีผล", "อ้างระเบียบข้อ 18/19 และข้อเท็จจริง",
                    A("termination_reason", "เหตุผลที่ความจำเป็นสิ้นสุดหรือควรยุติ", true, sensitive: true), D("effective_date", "ให้สิ้นสุดตั้งแต่วันที่", true),
                    A("team_leader_opinion", "ความเห็นหัวหน้าชุดปฏิบัติการ", true), A("director_opinion", "ความเห็นผู้อำนวยการกอง/สำนักงาน"),
                    A("deputy_secretary_opinion", "ความเห็นรองเลขาธิการ"), A("secretary_opinion", "ความเห็นเลขาธิการ"))
            ]),
        new(
            16,
            "คบ.16",
            "คำสั่งการให้ความคุ้มครองพยานสิ้นสุด",
            "เลขาธิการออกคำสั่งสิ้นสุด ระบุเหตุ วันมีผล และสิทธิอุทธรณ์",
            29,
            [WitnessFormAccessRole.Officer],
            [WitnessFormAccessRole.Secretary],
            [
                Section("คำสั่ง", "ผู้ร้อง พยาน การอนุมัติเดิม และเหตุสิ้นสุด",
                    T("order_no", "เลขที่คำสั่ง", true), T("requester_name", "ผู้ร้อง", true, sensitive: true), T("witness_name", "พยาน", true, sensitive: true),
                    D("protection_started", "คุ้มครองตั้งแต่วันที่", true), A("termination_reason", "เหตุที่ความจำเป็นสิ้นสุด", true, sensitive: true),
                    C("appeal_right", "แจ้งสิทธิอุทธรณ์ภายใน 30 วัน", true), D("effective_date", "มีผลตั้งแต่วันที่", true), D("ordered_at", "สั่ง ณ วันที่", true))
            ]),
        new(
            17,
            "คบ.17",
            "หนังสือแจ้งคำสั่งการให้ความคุ้มครองพยานสิ้นสุด",
            "แจ้งผู้ยื่นถึงคำสั่งสิ้นสุด และแจ้งสิทธิอุทธรณ์เฉพาะกรณีที่กฎหมายกำหนด",
            30,
            [WitnessFormAccessRole.Officer, WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [WitnessFormAccessRole.Director, WitnessFormAccessRole.DeputySecretary, WitnessFormAccessRole.Secretary],
            [
                Section("หนังสือภายนอก", "เลขหนังสือ วันที่ ผู้รับ และการอ้างถึง",
                    T("letter_no", "ที่", true), D("letter_date", "วันที่", true), T("recipient", "เรียน", true, sensitive: true),
                    T("request_reference", "อ้างถึง", false), A("termination_reason", "เหตุผลที่ความจำเป็นสิ้นสุด", true, sensitive: true),
                    C("appeal_right_notified", "แจ้งสิทธิอุทธรณ์ภายใน 30 วัน (เมื่อเข้าเงื่อนไข)", false),
                    A("appeal_note", "หมายเหตุเงื่อนไขการแจ้งสิทธิอุทธรณ์"), T("office_contact", "สำนัก/โทร/โทรสาร", true))
            ])
    ];

    public static WitnessFormDefinition Get(int number)
        => All.FirstOrDefault(form => form.Number == number)
           ?? throw new InvalidOperationException($"ไม่พบแบบ คบ.{number}");

    public static IReadOnlyList<string> SignaturePurposes(int formNumber) => formNumber switch
    {
        1 => ["ผู้ยื่นคำร้อง", "เจ้าหน้าที่ผู้รับคำร้อง"],
        2 => ["เจ้าหน้าที่ผู้รับแจ้ง"],
        3 => ["ผู้ให้ถ้อยคำ", "เจ้าหน้าที่ผู้บันทึกถ้อยคำ"],
        4 => ["เจ้าหน้าที่ผู้เสนอ", "ผู้บังคับบัญชาชั้นต้น", "ผู้อำนวยการสำนัก/กอง"],
        5 => ["ผู้อำนวยการสำนัก/กองผู้ออกคำสั่ง", "พยานผู้รับทราบ"],
        6 => ["เจ้าหน้าที่เจ้าของเรื่อง", "ผู้บังคับบัญชาชั้นต้น", "ผู้อำนวยการสำนัก/กอง", "ผู้มีอำนาจจาก External Module"],
        7 => ["ผู้ขอยุติการคุ้มครอง", "เจ้าหน้าที่ผู้รับคำร้อง"],
        8 => ["เลขาธิการผู้ลงนามคำสั่ง", "พยานผู้รับทราบ"],
        9 => ["ผู้มีอำนาจลงนามหนังสือ"],
        10 => ["ผู้มีอำนาจลงนามหนังสือ"],
        11 => ["พยานผู้รับความคุ้มครอง", "เจ้าหน้าที่ผู้ให้ความคุ้มครอง", "พยานรับรองคนที่ 1", "พยานรับรองคนที่ 2"],
        12 => ["ผู้ส่งมอบ", "ผู้รับมอบ", "พยานฝ่ายส่งมอบ", "พยานฝ่ายรับมอบ"],
        13 => ["เจ้าหน้าที่ผู้ปฏิบัติ", "พยานผู้รับการคุ้มครอง"],
        14 => ["พยาน/เจ้าหน้าที่ผู้ยื่นขยายเวลา", "ผู้บังคับบัญชาชั้นต้น", "ผู้อำนวยการสำนัก/กอง", "ผู้มีอำนาจจาก External Module"],
        15 => ["หัวหน้าชุดปฏิบัติการ", "ผู้อำนวยการสำนัก/กอง", "ผู้มีอำนาจจาก External Module"],
        16 => ["เลขาธิการผู้ลงนามคำสั่ง"],
        17 => ["ผู้มีอำนาจลงนามหนังสือ"],
        _ => []
    };

    private static WitnessFormSectionDefinition Section(
        string title,
        string description,
        params WitnessFormFieldDefinition[] fields)
        => new(title, description, fields);

    private static WitnessFormFieldDefinition T(
        string key,
        string label,
        bool required = false,
        string hint = "",
        bool sensitive = false)
        => new(key, label, WitnessFormFieldType.Text, required, hint, Sensitive: sensitive);

    private static WitnessFormFieldDefinition A(
        string key,
        string label,
        bool required = false,
        string hint = "",
        bool sensitive = false)
        => new(key, label, WitnessFormFieldType.TextArea, required, hint, Sensitive: sensitive);

    private static WitnessFormFieldDefinition D(string key, string label, bool required = false)
        => new(key, label, WitnessFormFieldType.Date, required);

    private static WitnessFormFieldDefinition N(string key, string label, bool required = false)
        => new(key, label, WitnessFormFieldType.Number, required);

    private static WitnessFormFieldDefinition S(
        string key,
        string label,
        IReadOnlyList<string> options,
        bool required = false)
        => new(key, label, WitnessFormFieldType.Select, required, Options: options);

    private static WitnessFormFieldDefinition M(
        string key,
        string label,
        IReadOnlyList<string> options,
        bool required = false)
        => new(key, label, WitnessFormFieldType.MultiSelect, required, Options: options);

    private static WitnessFormFieldDefinition G(
        string key,
        string label,
        bool required,
        bool sensitive,
        params WitnessRepeatingColumnDefinition[] columns)
        => new(key, label, WitnessFormFieldType.Repeating, required, Sensitive: sensitive, Columns: columns);

    private static WitnessFormFieldDefinition ADR(
        string key,
        string label,
        bool required = false,
        bool sensitive = true)
        => new(key, label, WitnessFormFieldType.Address, required, Sensitive: sensitive,
            Columns:
            [
                Col("house_no", "บ้านเลขที่", required), Col("village_no", "หมู่"), Col("building", "อาคาร/หมู่บ้าน"),
                Col("alley", "ซอย"), Col("road", "ถนน"), Col("subdistrict", "ตำบล/แขวง", required),
                Col("district", "อำเภอ/เขต", required), Col("province", "จังหวัด", required), Col("postcode", "รหัสไปรษณีย์", required)
            ]);

    private static WitnessRepeatingColumnDefinition Col(
        string key,
        string label,
        bool required = false,
        string inputType = "text")
        => new(key, label, required, inputType);

    private static WitnessFormFieldDefinition C(string key, string label, bool required = false)
        => new(key, label, WitnessFormFieldType.Checkbox, required);

    private static WitnessFormFieldDefinition R(string key, string label, bool required = false)
        => new(key, label, WitnessFormFieldType.ReadOnly, required);
}
