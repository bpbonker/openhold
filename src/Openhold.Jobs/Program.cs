using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Openhold.Client.Sdk;
using Openhold.Jobs;

// Openhold job worker. Uses this PC's Openhold settings (server and vault, from Openhold Explorer's Settings) and runs
// as the signed-in Windows user, who must be an administrator or in the vault's "job-workers" group.
//
//   OpenholdJobs                     keep working: claim jobs as they arrive (Ctrl+C to stop)
//   OpenholdJobs --once              do every waiting job, then stop
//   OpenholdJobs --default-paper A3  paper for layouts with no printer set up (default: the size drawn)
//   OpenholdJobs --corner TopRight   where the QR stamp goes (TopLeft, TopRight, BottomLeft, BottomRight)
//   OpenholdJobs --server URL --vault NAME   use this server and vault instead of the PC's settings
//
// Installed as a Windows service (tools/install-job-worker.ps1) it runs as the service's account, starts with
// Windows, and writes a daily log file to --log-folder (default: the Logs folder beside OpenholdJobs.exe).
var once = args.Contains("--once");
var paper = Option("--default-paper");
var corner = Option("--corner") is { } c ? Enum.Parse<StampCorner>(c, ignoreCase: true) : StampCorner.TopLeft;

var settings = ClientSettings.Exists() ? ClientSettings.Load() : new ClientSettings();
settings.ServerUrl = Option("--server") ?? settings.ServerUrl;
settings.Vault = Option("--vault") ?? settings.Vault;
if (Option("--server") is not null)
    settings.SignIn = "Windows"; // a service has nobody to type a password; it signs in as its Windows account

if (WindowsServiceHelpers.IsWindowsService())
{
    var logFolder = Option("--log-folder") ?? Path.Combine(AppContext.BaseDirectory, "Logs");
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddWindowsService(o => o.ServiceName = "Openhold Job Worker");
    builder.Services.AddHostedService(_ => new WorkerService(settings, new PublishOptions(paper, corner), new DailyLog(logFolder)));
    await builder.Build().RunAsync();
    return 0;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
var client = settings.CreateClient();
Console.WriteLine($"Openhold job worker: {settings.ServerUrl.TrimEnd('/')} ({settings.Vault ?? "default vault"}) as {client.Username}. " +
                  (once ? "Doing waiting jobs, then stopping." : "Waiting for jobs; Ctrl+C to stop."));
return await WorkerLoop.RunAsync(new JobRunner(client, new PublishOptions(paper, corner), Console.Out), once, Console.Out, stop.Token);

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

/// <summary>Claims and does jobs until stopped; waits out a server that's down rather than giving up.</summary>
internal static class WorkerLoop
{
    public static async Task<int> RunAsync(JobRunner runner, bool once, TextWriter log, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await runner.RunOneAsync(stop))
                    continue;
                if (once)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(15), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                // The server is down or unreachable: wait and try again rather than stop (unless asked for one pass).
                if (once)
                {
                    log.WriteLine($"Can't reach the server ({ex.Message}).");
                    return 1;
                }
                log.WriteLine($"{DateTime.Now:HH:mm:ss}  Can't reach the server ({ex.Message}); trying again in a minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch (SignInRequiredException ex)
            {
                // The saved Openhold-account session has run out: someone signs in once in Openhold Explorer as the worker.
                if (once)
                {
                    log.WriteLine(ex.Message);
                    return 1;
                }
                log.WriteLine($"{DateTime.Now:HH:mm:ss}  {ex.Message} Waiting for a sign-in; trying again in five minutes.");
                await Task.Delay(TimeSpan.FromMinutes(5), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch (OpenholdApiException ex) when (ex.Code == "not_a_job_worker")
            {
                log.WriteLine(ex.Message + " Ask an Openhold administrator to add this account to the job-workers group.");
                return 2;
            }
        }
        return 0;
    }
}

/// <summary>The job worker running as a Windows service.</summary>
internal sealed class WorkerService(ClientSettings settings, PublishOptions options, DailyLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = settings.CreateClient();
        log.WriteLine($"{DateTime.Now:HH:mm:ss}  Openhold job worker service started: {settings.ServerUrl.TrimEnd('/')} ({settings.Vault ?? "default vault"}) as {client.Username}.");
        var runner = new JobRunner(client, options, log);
        while (await WorkerLoop.RunAsync(runner, once: false, log, stoppingToken) != 0)
        {
            // Not allowed to do jobs: retrying every 15 seconds would only fill the log. Wait for an administrator.
            log.WriteLine($"{DateTime.Now:HH:mm:ss}  Stopped claiming jobs; checking again in 10 minutes.");
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        log.WriteLine($"{DateTime.Now:HH:mm:ss}  Openhold job worker service stopped.");
    }
}

/// <summary>A log written to one file per day (jobs-yyyy-MM-dd.log), keeping 90 days.</summary>
internal sealed class DailyLog(string folder) : TextWriter
{
    private readonly object _gate = new();

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        lock (_gate)
        {
            Directory.CreateDirectory(folder);
            var today = Path.Combine(folder, $"jobs-{DateTime.Now:yyyy-MM-dd}.log");
            var isNew = !File.Exists(today);
            File.AppendAllText(today, value);
            if (isNew)
            {
                foreach (var old in Directory.GetFiles(folder, "jobs-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-90)))
                    File.Delete(old);
            }
        }
    }

    public override void WriteLine(string? value) => Write(value + Environment.NewLine);
}
