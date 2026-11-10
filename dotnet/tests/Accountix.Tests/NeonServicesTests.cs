using System.Net;
using System.Reflection;
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
    public void Values_keep_inner_equals_and_carriage_returns()
    {
        var parsed = EnvFile.Parse("A=b=c\r\nB=\"x=y\"\r\nKEY=\r\n").ToList();
        Assert.Equal(new[] { ("A", "b=c"), ("B", "x=y"), ("KEY", "") }, parsed);
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
    public void The_last_occurrence_of_a_key_wins_and_a_blank_value_sets_nothing()
    {
        string first = $"ACCOUNTIX_TEST_ENVFILE_{Guid.NewGuid():N}";
        string blank = $"ACCOUNTIX_TEST_ENVFILE_{Guid.NewGuid():N}";
        string dir = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.local"), $"{first}=one\n{first}=two\n{blank}=\n");

            var set = EnvFile.LoadFrom(dir, ".env.local");

            Assert.Equal(new[] { first }, set);                          // the blank sets nothing, and is not reported
            Assert.Equal("two", Environment.GetEnvironmentVariable(first));
            Assert.Null(Environment.GetEnvironmentVariable(blank));      // .NET deletes on blank: never claim it was set
        }
        finally
        {
            Environment.SetEnvironmentVariable(first, null);
            Environment.SetEnvironmentVariable(blank, null);
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
            Directory.CreateDirectory(Path.Combine(root, ".git"));      // the repository root is the walk's ceiling
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
    public void The_walk_never_reads_a_file_outside_the_repository()
    {
        // A stray ~/.env above the checkout must never be loaded: the walk stops at the
        // folder that holds .git, without reading anything further up.
        string outer = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        string root = Directory.CreateDirectory(Path.Combine(outer, "repo")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "dotnet", "src")).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(outer, ".env.local"), "OUTSIDE=1\n");

            Assert.Null(EnvFile.Find(nested, ".env.local"));
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
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

            Assert.Empty(EnvFile.Load("some-other-host", [dir], ".env.local"));
            Assert.Null(Environment.GetEnvironmentVariable(key));

            // The host that names its own entry assembly gets the file.
            string entry = Assembly.GetEntryAssembly()?.GetName().Name!;
            Assert.Equal(new[] { key }, EnvFile.Load(entry, [dir], ".env.local"));
            Assert.Equal("from-file", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_opt_out_variable_makes_every_load_a_no_op()
    {
        string key = $"ACCOUNTIX_TEST_ENVFILE_{Guid.NewGuid():N}";
        string dir = Directory.CreateTempSubdirectory("accountix-envfile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.local"), $"{key}=from-file\n");
            Environment.SetEnvironmentVariable(EnvFile.OptOutVariable, "0");

            Assert.Empty(EnvFile.Load(Assembly.GetEntryAssembly()?.GetName().Name!, [dir], ".env.local"));
            Assert.Null(Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvFile.OptOutVariable, null);
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
    public void A_blank_variable_falls_back_to_the_app_settings_url()
    {
        // A blank is "not set": an empty App Setting must not hide the configured URL.
        var services = NeonServices.From(Config(
            ("NEON_FUNCTION_HELLO_BASE_URL", ""),
            ("Accountix:Neon:HelloFunctionUrl", "https://fn.example/")));
        Assert.Equal("https://fn.example", services.HelloFunctionUrl);

        // And when both are set, the variable wins.
        services = NeonServices.From(Config(
            ("NEON_FUNCTION_HELLO_BASE_URL", "https://var.example"),
            ("Accountix:Neon:HelloFunctionUrl", "https://fn.example")));
        Assert.Equal("https://var.example", services.HelloFunctionUrl);
    }

    [Fact]
    public void A_malformed_or_non_http_address_is_not_configured()
    {
        foreach (string bad in new[] { "fn.example.com", "ftp://fn.example", "not a url", "https://" })
        {
            var services = NeonServices.From(Config(("NEON_FUNCTION_HELLO_BASE_URL", bad)));
            Assert.Null(services.HelloFunctionUrl);   // reported as "not configured", never asked, never a 500
        }
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
    public async Task A_broken_address_is_an_answer_not_an_exception()
    {
        // Even when a value slips past the URL check, the page must report "down", not 500.
        var handler = new StubHandler(HttpStatusCode.OK, "never called");
        var client = new NeonFunctionClient(new Factory(handler), new NeonServices { HelloFunctionUrl = "fn.example" });
        var answer = await client.PingHelloAsync();
        Assert.False(answer!.Value.Ok);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task The_answer_is_reused_for_a_minute()
    {
        // The anonymous page must not let anyone flood the function: one probe per CacheFor.
        var handler = new StubHandler(HttpStatusCode.OK, "Hello from Neon Functions");
        var client = new NeonFunctionClient(new Factory(handler), new NeonServices { HelloFunctionUrl = "https://fn.example" });

        var first = await client.PingHelloAsync();
        var second = await client.PingHelloAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_slow_function_is_reported_as_down()
    {
        var handler = new SlowHandler();
        var client = new NeonFunctionClient(
            new Factory(handler, timeout: TimeSpan.FromMilliseconds(100)), new NeonServices { HelloFunctionUrl = "https://fn.example" });

        var answer = await client.PingHelloAsync();

        Assert.False(answer!.Value.Ok);
        Assert.Null(answer.Value.Status);
    }

    [Fact]
    public async Task A_caller_cancel_is_rethrown()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "never reached");
        var client = new NeonFunctionClient(new Factory(handler), new NeonServices { HelloFunctionUrl = "https://fn.example" });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingHelloAsync(cancelled.Token));
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

    private sealed class Factory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
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

    private sealed class SlowHandler() : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("too late") };
        }
    }
}
