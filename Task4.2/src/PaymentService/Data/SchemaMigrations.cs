using DbUp;

namespace PaymentService.Data;

/// <summary>Миграции схемы из Data/Migrations (DbUp, журнал — schemaversions), применяются при старте сервиса.</summary>
public static class SchemaMigrations
{
    public static bool Apply(string connectionString, ILoggerFactory logging) => DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(typeof(SchemaMigrations).Assembly, name => name.Contains(".Data.Migrations."))
        .WithTransactionPerScript()
        .LogTo(logging)
        .Build()
        .PerformUpgrade()
        .Successful;
}
