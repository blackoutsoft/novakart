using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaKart.Infraestructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace NovaKart.WebApi.IntegrationTests;

public sealed class StartupTests
{
    [Fact]
    public async Task Startup_MigratesEmptyDatabase_AndRestartPreservesHistoryAndData()
    {
        await using var container = new PostgreSqlBuilder("postgres:15-alpine").Build();
        await container.StartAsync();
        string[] history;
        await using (var factory = new ProductsApiFixture.ProductsApiFactory(container.GetConnectionString(), true))
        {
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products?pageSize=1")).StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.NotEmpty(history);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE startup_probe (value integer); INSERT INTO startup_probe VALUES (42);");
        }

        await using var restarted = new ProductsApiFixture.ProductsApiFactory(container.GetConnectionString(), true);
        using var restartedClient = restarted.CreateClient();
        using var restartedScope = restarted.Services.CreateScope();
        var restartedDb = restartedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(history, (await restartedDb.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(42, await restartedDb.Database.SqlQueryRaw<int>("SELECT value AS \"Value\" FROM startup_probe").SingleAsync());

        await container.StopAsync();
        Assert.Equal(HttpStatusCode.OK, (await restartedClient.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await restartedClient.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task DisabledStartupMigrations_LeavesDatabaseUnmigrated()
    {
        await using var container = new PostgreSqlBuilder("postgres:15-alpine").Build();
        await container.StartAsync();
        await using var factory = new ProductsApiFixture.ProductsApiFactory(container.GetConnectionString());
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task StartupMigrationFailure_PreventsServingRequests()
    {
        await using var factory = new ProductsApiFixture.ProductsApiFactory(
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1", true);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
