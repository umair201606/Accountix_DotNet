using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Accountix.TestSupport;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Accountix.E2E.Harness;

/// <summary>
/// The real .NET app on Kestrel, on a real port, with Core + Web.Shared + the
/// named module assemblies, on the given database. It owns its port: a port
/// already taken fails the start (the Python suite once ran for hours against
/// a stale server that held its port), and every response carries this
/// instance's id so the browser can tell it is talking to this server.
/// Unhandled exceptions and 5xx answers are recorded with their stack.
/// </summary>
public sealed class AppServer : IAsyncDisposable
{
    public const string InstanceHeader = "X-E2E-Instance";
    private const string PingPath = "/__e2e/ping";

    private readonly WebApplication _app;

    private AppServer(WebApplication app, int port, string connectionString, string instance, IReadOnlyList<string> modules,
                      ConcurrentQueue<string> errors)
    {
        _app = app;
        Port = port;
        ConnectionString = connectionString;
        Instance = instance;
        Modules = modules;
        ServerErrors = errors;
        BaseUrl = $"http://127.0.0.1:{port}";
    }

    public int Port { get; }
    public string BaseUrl { get; }
    public string ConnectionString { get; }
    public string Instance { get; }
    public IReadOnlyList<string> Modules { get; }
    public IServiceProvider Services => _app.Services;

    /// <summary>Server-side failures (exception text with stack, or "500 GET /x"), oldest first.</summary>
    public ConcurrentQueue<string> ServerErrors { get; }

    public static async Task<AppServer> StartAsync(string connectionString, int port, params string[] modules)
    {
        if (modules.Length == 0)
            throw new ArgumentException("Name at least one module.", nameof(modules));
        if (ModuleStatus.WhyNot(modules) is { } why && modules.Any(m => !ModuleStatus.AssemblyPresent(m)))
            throw new InvalidOperationException(why);
        AssertPortFree(port);

        var assemblies = modules.Select(ModuleStatus.Load).ToList();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = assemblies[0].GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration["SECRET_KEY"] = Reference.SecretKey;
        builder.Configuration["ConnectionStrings:Accountix"] = connectionString;
        AccountixApp.ConfigureServices(builder, assemblies);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        string instance = Guid.NewGuid().ToString("N");
        var errors = new ConcurrentQueue<string>();
        // Outermost (after the development exception page): see every request first.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == PingPath)
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(instance);
                return;
            }
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[InstanceHeader] = instance;
                return Task.CompletedTask;
            });
            try
            {
                await next(ctx);
            }
            catch (Exception e)
            {
                errors.Enqueue($"{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} threw: {e}");
                throw;
            }
            if (ctx.Response.StatusCode >= 500)
                errors.Enqueue($"{ctx.Response.StatusCode} {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString}");
        });
        AccountixApp.ConfigurePipeline(app);
        await app.StartAsync();

        var server = new AppServer(app, port, connectionString, instance, modules, errors);
        try
        {
            await server.AssertAliveAsync();
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }

    /// <summary>
    /// The server still runs and still owns the port: /__e2e/ping answers with this
    /// instance's id. Called before each test, and by the start.
    /// </summary>
    public async Task AssertAliveAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string answer;
        try
        {
            answer = await http.GetStringAsync(BaseUrl + PingPath);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"The E2E server on {BaseUrl} is not answering (died?): {e.Message}", e);
        }
        if (answer != Instance)
            throw new InvalidOperationException(
                $"Another server answers on {BaseUrl} (instance '{answer[..Math.Min(answer.Length, 60)]}', expected {Instance}). " +
                "Stop whatever holds the port and re-run.");
    }

    /// <summary>Fails fast, naming the port, when anything listens there already.</summary>
    public static void AssertPortFree(int port)
    {
        // Something accepting connections (any process, any address family on loopback).
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var connect = probe.ConnectAsync(new IPEndPoint(address, port));
                if (connect.Wait(TimeSpan.FromMilliseconds(500)) && probe.Connected)
                    throw new InvalidOperationException(
                        $"Port {port} is already in use ({address}). The E2E server must own it, or the browser " +
                        $"tests run against the wrong app or database. Stop the process holding it (lsof -i :{port}) and re-run.");
            }
            catch (AggregateException) { /* refused: free */ }
            catch (SocketException) { /* refused or no IPv6: free */ }
        }
        // And it can be bound.
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException($"Port {port} cannot be bound ({e.SocketErrorCode}). Stop the process holding it (lsof -i :{port}).", e);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Takes the recorded server errors (they are reported once).</summary>
    public List<string> DrainErrors()
    {
        var list = new List<string>();
        while (ServerErrors.TryDequeue(out var e))
            list.Add(e);
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(cts.Token);
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }
}
