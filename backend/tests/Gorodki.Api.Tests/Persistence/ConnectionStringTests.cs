using Gorodki.Api.Infrastructure.Persistence;
using Npgsql;

namespace Gorodki.Api.Tests.Persistence;

/// <summary>Строка подключения к базе — в том виде, в каком её даёт Supabase и вводит Никита на Render.</summary>
public sealed class ConnectionStringTests
{
    [Fact]
    public void Supabase_uri_is_translated_and_gets_search_path_pool_and_no_gss()
    {
        var uri = "postgresql://postgres.abcdefgh:p%40ss%3Aw0rd@aws-0-eu-central-1.pooler.supabase.com:5432/postgres?sslmode=require&pgbouncer=true";

        var builder = new NpgsqlConnectionStringBuilder(AppDbContext.WithSearchPath(uri));

        Assert.Equal("aws-0-eu-central-1.pooler.supabase.com", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("postgres", builder.Database);
        Assert.Equal("postgres.abcdefgh", builder.Username);
        Assert.Equal("p@ss:w0rd", builder.Password); // пароль с «@» и «:» закодирован в URI
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.Equal("app,extensions,public", builder.SearchPath);
        Assert.Equal(8, builder.MaxPoolSize);
        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
    }

    [Fact]
    public void Key_value_string_keeps_its_own_settings()
    {
        var text = "Host=db;Database=gorodki;Username=u;Password=p;Search Path=custom;Maximum Pool Size=3;GSS Encryption Mode=Prefer";

        var builder = new NpgsqlConnectionStringBuilder(AppDbContext.WithSearchPath(text));

        Assert.Equal("custom", builder.SearchPath);
        Assert.Equal(3, builder.MaxPoolSize);
        Assert.Equal(GssEncryptionMode.Prefer, builder.GssEncryptionMode);
    }

    [Fact]
    public void Uri_without_port_and_database_uses_postgres_defaults()
    {
        var builder = new NpgsqlConnectionStringBuilder(AppDbContext.FromUri("postgres://user:secret@db.example"));

        Assert.Equal(5432, builder.Port);
        Assert.Equal("postgres", builder.Database);
    }
}
