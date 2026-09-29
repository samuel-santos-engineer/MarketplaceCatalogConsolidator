using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class StarterCatalogTests
{
    [Fact]
    public void StarterDatabaseHasExpectedBaselineAndOpensReadOnly()
    {
        var databasePath = Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Product), (SELECT COUNT(*) FROM SellerProduct);";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(975, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }
}
