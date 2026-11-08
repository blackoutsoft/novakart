using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaKart.Infraestructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace NovaKart.WebApi.IntegrationTests;

public sealed class ProductsApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:15-alpine")
        .WithDatabase("novakart_tests")
        .Build();

    private ProductsApiFactory _factory = null!;

    public HttpClient CreateClient() => _factory.CreateClient();

    public async Task WithDbContextAsync(Func<ApplicationDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db);
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = new ProductsApiFactory(_container.GetConnectionString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(ReadSeedScript());
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static string ReadSeedScript()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "seed.sql"))
            .Where(line => !line.Trim().Equals("BEGIN;", StringComparison.OrdinalIgnoreCase)
                        && !line.Trim().Equals("COMMIT;", StringComparison.OrdinalIgnoreCase));

        return string.Join(Environment.NewLine, lines);
    }

    internal sealed class ProductsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly bool _applyMigrations;

        public ProductsApiFactory(string connectionString, bool applyMigrations = false)
        {
            _connectionString = connectionString;
            _applyMigrations = applyMigrations;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:ApplyMigrationsOnStartup", _applyMigrations.ToString());

            builder.ConfigureTestServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>)
                             || d.ServiceType == typeof(DbContextOptions)
                             || d.ServiceType == typeof(ApplicationDbContext)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration")
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(ApplicationDbContext)))
                    .ToList();

                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_connectionString));
            });
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ProductsApiCollection : ICollectionFixture<ProductsApiFixture>
{
    public const string Name = "ProductsApi";
}
