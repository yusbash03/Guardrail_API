using Guardrail.Infrastructure.Persistence;
using Npgsql;
using Shouldly;

namespace Guardrail.Integration.Tests;

/// <summary>Render Postgres supplies postgres:// URLs; Npgsql needs key=value.</summary>
public sealed class PostgresConnectionStringTests
{
    [Fact]
    public void RenderInternalUrl_IsConverted()
    {
        var result = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize("postgresql://guardrail:p%40ss%3Aword@dpg-abc123-a/guardrail_xyz"));

        result.Host.ShouldBe("dpg-abc123-a");
        result.Port.ShouldBe(5432);
        result.Database.ShouldBe("guardrail_xyz");
        result.Username.ShouldBe("guardrail");
        result.Password.ShouldBe("p@ss:word");
    }

    [Fact]
    public void ExternalUrl_KeepsPortAndSslMode()
    {
        var result = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize("postgres://u:p@dpg-abc123-a.oregon-postgres.render.com:6543/db?sslmode=require"));

        result.Port.ShouldBe(6543);
        result.SslMode.ShouldBe(SslMode.Require);
    }

    [Fact]
    public void NeonPooledUrl_KeepsSslModeAndChannelBinding()
    {
        var result = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(
            "postgresql://owner:secret@ep-example-123-pooler.c-5.us-east-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require"));

        result.Host.ShouldBe("ep-example-123-pooler.c-5.us-east-1.aws.neon.tech");
        result.Database.ShouldBe("neondb");
        result.SslMode.ShouldBe(SslMode.Require);
        result.ChannelBinding.ShouldBe(ChannelBinding.Require);
    }

    [Fact]
    public void KeyValueString_IsUnchanged()
    {
        const string value = "Host=localhost;Port=5432;Database=guardrail;Username=guardrail;Password=guardrail";
        PostgresConnectionString.Normalize(value).ShouldBe(value);
    }
}
