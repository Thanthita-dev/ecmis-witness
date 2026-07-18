using System.Net;
using System.Net.Http.Json;
using EcmisWitness.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EcmisWitness.Tests;

public sealed class WitnessUserContextServiceTests
{
    [Fact]
    public async Task Current_user_is_resolved_by_admin_api_and_bearer_is_forwarded()
    {
        string? forwardedAuthorization = null;
        var requestCount = 0;
        var handler = new StubHandler(request =>
        {
            requestCount++;
            forwardedAuthorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    success = true,
                    data = new
                    {
                        userId = Guid.Parse("a24d793f-a980-43fb-b722-ad481c38876b"),
                        username = "witness.officer",
                        userType = "official",
                        firstName = "เจ้าหน้าที่",
                        lastName = "คุ้มครองพยาน",
                        positionName = "นักสืบสวนสอบสวนชำนาญการ",
                        organizationId = Guid.Parse("974aa14a-ebbb-4e64-ae35-e2a76899cd60"),
                        organizationName = "กลุ่มงานคุ้มครองพยาน",
                        organizationType = "group",
                        roles = new[] { "witness_officer" },
                        permissions = new[]
                        {
                            WitnessPermissions.ViewMasked,
                            WitnessPermissions.ViewPii,
                            WitnessPermissions.OfficerReview
                        }
                    }
                })
            };
        });

        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var service = new WitnessUserContextService(dataSource,
            new HttpClient(handler) { BaseAddress = new Uri("https://admin.example/") },
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build());
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer access-token";

        var first = await service.GetAsync(http, default);
        var second = await service.GetAsync(http, default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("Bearer access-token", forwardedAuthorization);
        Assert.Equal("witness.officer", first.Username);
        Assert.Equal("เจ้าหน้าที่ คุ้มครองพยาน", first.DisplayName);
        Assert.Contains("witness_officer", first.Roles);
        Assert.True(first.HasPermission(WitnessPermissions.OfficerReview));
        Assert.False(first.IsGlobalAdministrator);
        Assert.Equal("กลุ่มงานคุ้มครองพยาน", first.OrganizationName);
        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task Rejected_admin_token_is_unauthorized_not_a_database_error()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var service = new WitnessUserContextService(dataSource,
            new HttpClient(handler) { BaseAddress = new Uri("https://admin.example/") },
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build());
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer expired-token";

        Assert.Null(await service.GetAsync(http, default));
    }

    [Fact]
    public async Task Admin_failure_is_reported_as_dependency_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var service = new WitnessUserContextService(dataSource,
            new HttpClient(handler) { BaseAddress = new Uri("https://admin.example/") },
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build());
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer access-token";

        await Assert.ThrowsAsync<WitnessDependencyException>(() => service.GetAsync(http, default));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_admin_api_call()
    {
        var requestCount = 0;
        var handler = new StubHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref requestCount);
            await Task.Delay(50, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    success = true,
                    data = new
                    {
                        userId = Guid.Parse("541f1763-943e-46de-baf7-ed2fc093ea78"),
                        username = "concurrent.officer",
                        roles = new[] { "witness_officer" },
                        permissions = Array.Empty<string>()
                    }
                })
            };
        });
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var service = new WitnessUserContextService(dataSource,
            new HttpClient(handler) { BaseAddress = new Uri("https://admin.example/") },
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build());

        var tasks = Enumerable.Range(0, 10).Select(_ =>
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = "Bearer shared-token";
            return service.GetAsync(http, default);
        });
        var users = await Task.WhenAll(tasks);

        Assert.All(users, Assert.NotNull);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public void View_pii_does_not_grant_global_case_scope_but_super_admin_does()
    {
        var piiReviewer = new WitnessUserContext(
            Guid.NewGuid(), "reviewer", "ผู้ตรวจ", "หัวหน้ากลุ่มงาน",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "group_head" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WitnessPermissions.ViewPii });
        var superAdmin = new WitnessUserContext(
            Guid.NewGuid(), "root", "ผู้ดูแลระบบ", "Super Admin",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "super_admin" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.True(piiReviewer.HasPermission(WitnessPermissions.ViewPii));
        Assert.False(piiReviewer.IsGlobalAdministrator);
        Assert.True(superAdmin.IsGlobalAdministrator);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request))) { }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
            => this.responseFactory = responseFactory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responseFactory(request, cancellationToken);
    }
}
