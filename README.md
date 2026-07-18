# ecmis-witness

Microservice ระบบคุ้มครองพยาน กิจกรรมที่ 6 รองรับ Workflow และแบบ คบ.1–17

## ความรับผิดชอบของ Service

- บันทึกแฟ้มคำร้อง แบบฟอร์มเวอร์ชัน ลายมือชื่อ ไฟล์แนบ และ Audit Log แบบถาวร
- ตรวจ Workflow transition และ permission ฝั่ง Server
- รับผลจาก External Module และดำเนินการแจ้งผล คุ้มครอง ยุติ และอุทธรณ์ต่อ
- สร้างเอกสาร Word จากข้อมูลที่บันทึกแล้ว
- ตรวจ Bearer token และ Claims ผ่าน `ecmis-admin`

## โครงสร้าง

```text
src/
├── Contracts/       API request/response contracts
├── Domain/          Workflow, validation และ catalog แบบ คบ.1–17
├── Endpoints/       HTTP endpoints
├── Infrastructure/  PostgreSQL repository และ migration runner
├── Migrations/      Schema `witness` migrations 001–004
├── Security/        Admin API authentication และ permission mapping
├── Services/        Attachment validation และ document generation
├── Program.cs
└── EcmisWitness.Api.csproj
tests/EcmisWitness.Tests/
Dockerfile
ecmis-witness.sln
```

## Configuration

ต้องส่งค่าผ่าน Environment Variable/Secret Store ห้าม commit password ลง repository

```bash
export ConnectionStrings__Ecmis='Host=...;Database=...;Username=...;Password=...'
export Witness__AdminApiBaseUrl='https://ecmis-admin.example/'
export Witness__AllowedOrigins__0='http://localhost:5000'
```

ค่าเริ่มต้นเปิด Npgsql connection pooling สูงสุด 20 connections และ cache ผลตรวจสิทธิ์ 30 วินาที เพื่อลด latency จาก Admin API/Supabase โดยไม่ cache raw token

Witness API เป็น persistent backend จึงเลือก Supabase Shared Pooler แบบ session mode (พอร์ต 5432) อัตโนมัติเมื่อพบ connection string ของ Shared Pooler ที่ใช้พอร์ต 6543 เพื่อลด stale connection/read timeout หากต้องใช้ transaction mode โดยตั้งใจ ให้กำหนด `Witness__PreferSupabaseSessionPooler=false`

Migration runner ตรวจ `witness.schema_migrations` และข้าม migration ที่ใช้งานแล้ว จึงไม่รัน DDL ทั้งชุดซ้ำทุกครั้งที่ service เริ่มทำงาน

## Run Local

```bash
dotnet restore ecmis-witness.sln
dotnet run --project src/EcmisWitness.Api.csproj
```

Service เปิดที่ `http://localhost:5013` ตาม launch profile และตรวจสุขภาพได้ที่ `/health`

## Test

```bash
dotnet test tests/EcmisWitness.Tests/EcmisWitness.Tests.csproj
```

Integration test จะเชื่อมฐานข้อมูลเมื่อกำหนด `ConnectionStrings__Ecmis`; หากไม่มีจะรันเฉพาะ pure tests

## Docker

```bash
docker build -t ecmis-witness:local .
docker run --rm -p 5013:8080 \
  -e ConnectionStrings__Ecmis='...' \
  -e Witness__AdminApiBaseUrl='https://ecmis-admin.example/' \
  ecmis-witness:local
```

Frontend `ecmis-web` เรียก service ผ่านค่า `ApiBaseUrl:EcmisWitness` และไม่มี backend source อยู่ใน web repository
