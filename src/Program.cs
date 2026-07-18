using System.Text.Json.Serialization;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Endpoints;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using EcmisWitness.Api.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = builder.Configuration.GetConnectionString("Ecmis");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ไม่พบ ConnectionStrings:Ecmis สำหรับ Witness API");
var databaseConnection = new NpgsqlConnectionStringBuilder(connectionString)
{
    MinPoolSize = builder.Configuration.GetValue("Witness:DatabaseMinPoolSize", 0),
    MaxPoolSize = builder.Configuration.GetValue("Witness:DatabaseMaxPoolSize", 20),
    ConnectionIdleLifetime = builder.Configuration.GetValue("Witness:DatabaseConnectionIdleSeconds", 60),
    ConnectionPruningInterval = builder.Configuration.GetValue("Witness:DatabaseConnectionPruningSeconds", 10),
    KeepAlive = builder.Configuration.GetValue("Witness:DatabaseKeepAliveSeconds", 30)
};
var configuredDatabasePort = builder.Configuration.GetValue<int?>("Witness:DatabasePort");
if (configuredDatabasePort.HasValue)
    databaseConnection.Port = configuredDatabasePort.Value;
else if (builder.Configuration.GetValue("Witness:PreferSupabaseSessionPooler", true)
         && databaseConnection.Port == 6543
         && databaseConnection.Host?.EndsWith(".pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true)
    databaseConnection.Port = 5432;
var configuredPooling = builder.Configuration.GetValue<bool?>("Witness:DatabasePooling");
if (configuredPooling.HasValue)
    databaseConnection.Pooling = configuredPooling.Value;
builder.Services.AddSingleton(NpgsqlDataSource.Create(databaseConnection.ConnectionString));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<WitnessDatabaseInitializer>();
var adminApiBase = builder.Configuration["Witness:AdminApiBaseUrl"]
    ?? "https://ecmis-admin.onrender.com/";
if (!Uri.TryCreate(adminApiBase, UriKind.Absolute, out var adminApiUri))
    throw new InvalidOperationException("Witness:AdminApiBaseUrl ไม่ใช่ URL ที่ถูกต้อง");
builder.Services.AddHttpClient<WitnessUserContextService>(client =>
{
    client.BaseAddress = adminApiUri;
    client.Timeout = TimeSpan.FromSeconds(
        Math.Clamp(builder.Configuration.GetValue("Witness:AuthTimeoutSeconds", 15), 5, 60));
});
builder.Services.AddSingleton<WitnessWorkflowStateMachine>();
builder.Services.AddSingleton<WitnessFormPolicy>();
builder.Services.AddScoped<WitnessRepository>();
builder.Services.AddSingleton<WitnessFileValidator>();
builder.Services.AddSingleton<WitnessDocumentService>();
builder.Services.AddSingleton<WitnessReportService>();
builder.Services.AddHostedService<WitnessExpiryAlertService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var allowedOrigins = builder.Configuration.GetSection("Witness:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5000"];
builder.Services.AddCors(options => options.AddPolicy("EcmisWeb", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (WitnessConcurrencyException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(ex.Message));
    }
    catch (WitnessAuthorizationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(ex.Message));
    }
    catch (UnauthorizedAccessException ex)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(ex.Message));
    }
    catch (WitnessWorkflowException ex)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(ex.Message));
    }
    catch (WitnessDependencyException ex)
    {
        app.Logger.LogError(ex, "Witness API dependency failure");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(ex.Message));
    }
    catch (Exception ex)
    {
        var reference = $"WIT-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        app.Logger.LogError(ex, "Witness API unhandled failure {Reference}", reference);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(
            ApiEnvelope<object>.Fail($"ระบบคุ้มครองพยานเกิดข้อผิดพลาด รหัสอ้างอิง {reference}"));
    }
});

app.UseCors("EcmisWeb");
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ecmis-witness" }));
app.MapWitnessEndpoints();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<WitnessDatabaseInitializer>().InitializeAsync();

await app.RunAsync();

public partial class Program;
