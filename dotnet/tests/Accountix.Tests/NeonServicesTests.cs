using System.Net;
using System.Text.Json;
using Accountix.Web.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Accountix.Tests;

/// <summary>
/// .env.local → environment → configuration: the path `neon deploy`'s pulled variables take
/// into the app (EnvFile), and the Neon Function client that /net/neon reports.
/// </summary>
public class EnvFileTests
{
    [Fact]
    public void Dotenv_lines_read_with_comments_quotes_and_export()
    {
        var parsed = EnvFile.Parse("""
            # comment, not a variable
            DATABASE_URL="postgresql://u:p@h/db?sslmode=require"
            export NEON_BRANCH='production'

            NEON_FUNCTION_HELLO_BASE_URL=https://example.com/
            not a variable line
            """).ToList();

        Assert.Equal(
            new[]
            {
                ("DATABASE_URL", "postgresql://u:p@h/db?sslmode=require"),
                ("NEON_BRANCH", "production"),
                ("NEON_FUNCTION_HELLO_BASE_URL", "https://example.com/"),
            },
            parsed);
    }

    [Fact]
    public void A_real_environment_variable_is_never_overridden_by_the_file()
    {
        string key = $"ACCOUNTIX_TEST_ENVFILE_{Guid.NewGuid():N}";
        string dir = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.local"), $"{key}=from-file\n");
            Environment.SetEnvironmentVariable(key, "from-environment");

            var set = EnvFile.LoadFrom(dir, ".env.local");

            Assert.DoesNotContain(key, set);
            Assert.Equal("from-environment", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Values_land_in_the_environment_and_missing_files_are_ignored()
    {
        string key = $"ACCOUNTIX_TEST_ENVFILE_{Guid.NewGuid():N}";
        string dir = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.local"), $"{key}=\"set-by-file\"\n");

            Assert.Empty(EnvFile.LoadFrom(dir, ".env"));                 // no such file: nothing to do
            Assert.Equal(new[] { key }, EnvFile.LoadFrom(dir, ".env.local"));
            Assert.Equal("set-by-file", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_loader_walks_up_from_a_subdirectory_to_the_repository_root()
    {
        string root = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "dotnet", "src")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".env.local"), "IGNORED=1\n");
            Assert.Equal(Path.Combine(root, ".env.local"), EnvFile.Find(nested, ".env.local"));
            Assert.Null(EnvFile.Find(nested, "no-such-file.env"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Only_the_app_s_own_process_loads_the_files()
    {
        // The test host runs Program.cs in-process: if the loader ignored whose process it is,
        // the repository's .env (a live DATABASE_URL) would land in this process's environment
        // and override the reference database every test host configures.
        string key = $"ACCOUNTIX_TEST_ENVFILE_{Guid.NewGuid():N}";
        string dir = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.local"), $"{key}=from-file\n");

            Assert.Empty(EnvFile.Load("some-other-host", ".env.local"));
            Assert.Null(Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>NeonServices (the neon.ts declaration, read from env) and the hello-function client.</summary>
public class NeonServicesTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void The_pulled_function_variable_is_read_and_the_url_loses_its_trailing_slash()
    {
        var services = NeonServices.From(Config(
            ("NEON_FUNCTION_HELLO_BASE_URL", "https://br-x-hello.compute.c-4.ap-southeast-1.aws.neon.tech/"),
            ("NEON_BRANCH", "production")));

        Assert.Equal("https://br-x-hello.compute.c-4.ap-southeast-1.aws.neon.tech", services.HelloFunctionUrl);
        Assert.Equal("production", services.Branch);
        Assert.True(services.HasFunctions);
    }

    [Fact]
    public void Without_the_variable_no_function_is_configured()
    {
        var services = NeonServices.From(Config(("NEON_BRANCH", "")));
        Assert.Null(services.HelloFunctionUrl);
        Assert.Null(services.Branch);
        Assert.False(services.HasFunctions);
    }

    [Fact]
    public async Task The_client_says_up_for_a_200_and_down_for_a_500()
    {
        var up = await Ping(HttpStatusCode.OK, "Hello from Neon Functions");
        Assert.True(up!.Value.Ok);
        Assert.Equal(200, up.Value.Status);
        Assert.Equal("Hello from Neon Functions", up.Value.Body);

        var down = await Ping(HttpStatusCode.InternalServerError, "boom");
        Assert.False(down!.Value.Ok);
        Assert.Equal(500, down.Value.Status);
    }

    [Fact]
    public async Task Without_a_configured_function_the_client_asks_nobody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "never called");
        var client = new NeonFunctionClient(new Factory(handler), new NeonServices());
        Assert.Null(await client.PingHelloAsync());
        Assert.Equal(0, handler.Calls);
    }

    private static async Task<NeonFunctionAnswer?> Ping(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var client = new NeonFunctionClient(
            new Factory(handler), new NeonServices { HelloFunctionUrl = "https://fn.example" });
        return await client.PingHelloAsync();
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
