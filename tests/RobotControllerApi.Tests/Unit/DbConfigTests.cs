using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RobotControllerApi.Infrastructure.DataAccess;

namespace RobotControllerApi.Tests.Unit;

// DbConfig is what makes one built image deployable to both staging and production,
// so its resolution order is worth pinning down: get it wrong and an environment
// silently talks to the wrong database.
public class DbConfigTests
{
    private static DbConfig BuildWith(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new DbConfig(configuration);
    }

    [Fact]
    public void GetConnectionString_BuildsAValidStringFromTheDiscreteEnvironmentVariables()
    {
        var config = BuildWith(new Dictionary<string, string?>
        {
            ["DB_HOST"] = "db",
            ["DB_PORT"] = "6543",
            ["DB_NAME"] = "robotcontroller_staging",
            ["DB_USER"] = "robot_staging",
            ["DB_PASSWORD"] = "s3cret-staging"
        });

        var connectionString = config.GetConnectionString();

        // Parsed rather than string-matched: the assertion is that Npgsql accepts it
        // and reads back the intended values, not that it happens to be formatted a
        // particular way.
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);

        parsed.Host.Should().Be("db");
        parsed.Port.Should().Be(6543);
        parsed.Database.Should().Be("robotcontroller_staging");
        parsed.Username.Should().Be("robot_staging");
        parsed.Password.Should().Be("s3cret-staging");
    }

    [Fact]
    public void GetConnectionString_FallsBackToDefaultsWhenNothingIsConfigured()
    {
        var config = BuildWith(new Dictionary<string, string?>());

        var parsed = new NpgsqlConnectionStringBuilder(config.GetConnectionString());

        parsed.Host.Should().Be("localhost");
        parsed.Port.Should().Be(5432);
        parsed.Database.Should().Be("robotcontroller");
        parsed.Username.Should().Be("postgres");

        // No default password: a fallback credential in source is exactly what the
        // environment-variable configuration exists to avoid.
        parsed.Password.Should().BeNullOrEmpty();
    }

    [Fact]
    public void GetConnectionString_PrefersAnExplicitDefaultConnectionOverTheDiscreteVariables()
    {
        var explicitConnectionString = "Host=explicit-host;Port=5555;Database=explicit_db;Username=explicit_user;Password=explicit_pw";

        var config = BuildWith(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = explicitConnectionString,

            // Present, and deliberately different: these must lose.
            ["DB_HOST"] = "should-be-ignored",
            ["DB_PORT"] = "1111",
            ["DB_NAME"] = "should_be_ignored",
            ["DB_USER"] = "ignored_user",
            ["DB_PASSWORD"] = "ignored_pw"
        });

        var parsed = new NpgsqlConnectionStringBuilder(config.GetConnectionString());

        parsed.Host.Should().Be("explicit-host");
        parsed.Port.Should().Be(5555);
        parsed.Database.Should().Be("explicit_db");
        parsed.Username.Should().Be("explicit_user");
    }

    // Phase 4 F12: prepared statements are on unless switched off, and a value in the connection string wins.
    [Fact]
    public void GetConnectionString_TurnsOnPreparedStatementsByDefault()
    {
        var parsed = new NpgsqlConnectionStringBuilder(BuildWith(new()).GetConnectionString());

        parsed.MaxAutoPrepare.Should().Be(64);
    }

    [Fact]
    public void GetConnectionString_PreparedStatements_CanBeSwitchedOff_OrSetInTheConnectionString()
    {
        var off = BuildWith(new() { ["Database:MaxAutoPrepare"] = "0" });
        var explicitValue = BuildWith(new() { ["ConnectionStrings:DefaultConnection"] = "Host=h;Database=d;Username=u;Max Auto Prepare=10" });

        new NpgsqlConnectionStringBuilder(off.GetConnectionString()).MaxAutoPrepare.Should().Be(0);
        new NpgsqlConnectionStringBuilder(explicitValue.GetConnectionString()).MaxAutoPrepare.Should().Be(10);
    }
}
