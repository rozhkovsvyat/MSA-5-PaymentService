using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PaymentService.Data;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace PaymentService.Tests;

/// <summary>PostgreSQL в контейнере, общий для класса тестов. У каждого теста своя база со схемой сервиса.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-bookworm").Build();

    public ValueTask InitializeAsync() => new(container.StartAsync());

    public ValueTask DisposeAsync() => container.DisposeAsync();

    public async Task<NpgsqlDataSource> NewDatabaseAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(container.GetConnectionString()))
        await using (var create = admin.CreateCommand($"create database {name}"))
            await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
        SchemaMigrations.Apply(connectionString, NullLoggerFactory.Instance).ShouldBeTrue();
        return NpgsqlDataSource.Create(connectionString);
    }
}
