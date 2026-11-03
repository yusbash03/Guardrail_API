using Npgsql;

namespace Guardrail.Infrastructure.Persistence;

/// <summary>
/// Accepts both Npgsql key=value strings and postgres:// URLs (what Render Postgres hands out); Npgsql only parses the former.
/// </summary>
public static class PostgresConnectionString
{
    public static string Normalize(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
        };
        if (userInfo.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        // libpq-style query options that Neon/Render put in their URLs (e.g. ?sslmode=require&channel_binding=require).
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var optionValue = Uri.UnescapeDataString(parts[1]);
            if (parts[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<SslMode>(optionValue, ignoreCase: true, out var sslMode))
            {
                builder.SslMode = sslMode;
            }
            else if (parts[0].Equals("channel_binding", StringComparison.OrdinalIgnoreCase)
                     && Enum.TryParse<ChannelBinding>(optionValue, ignoreCase: true, out var channelBinding))
            {
                builder.ChannelBinding = channelBinding;
            }
        }

        return builder.ConnectionString;
    }
}
