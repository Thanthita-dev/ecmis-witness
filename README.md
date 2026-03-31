# ecmis-witness

คุ้มครองพยาน | TOR กิจกรรม 6

.NET Core 8 API — Microservice (Clean Architecture)

## โครงสร้าง

```
src/
├── Controllers/     # HTTP endpoints
├── Services/        # Business Logic
├── Repositories/    # Data Access (EF Core)
├── Models/Entities/ # Database entities
├── Models/DTOs/     # Request/Response models
├── Events/          # Domain Events
├── Validators/      # FluentValidation
├── Mappings/        # AutoMapper
├── Program.cs
└── appsettings.json
tests/
├── Unit/
└── Integration/
Dockerfile
```

## Dependencies

- `ecmis-shared` — Shared NuGet package

## Getting Started

```bash
dotnet restore && dotnet run --project src
```
