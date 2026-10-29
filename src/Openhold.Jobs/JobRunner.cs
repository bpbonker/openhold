using System.Globalization;
using Openhold.Client.Cad;
using Openhold.Client.Sdk;
using Openhold.Core.Contracts;

namespace Openhold.Jobs;

/// <summary>How this worker publishes PDFs.</summary>
public sealed record PublishOptions(string? DefaultPaper = null, StampCorner Corner = StampCorner.TopLeft);

/// <summary>
/// Does one job at a time: downloads the released version (with everything it references, at the versions it
/// was released with), makes the PDF, stamps it with the QR code and hands it back. A failure is reported with
/// a reason an administrator can act on, and the server decides whether to try again.
/// </summary>
public sealed class JobRunner(OpenholdClient client, PublishOptions options, TextWriter log)
{
    public static readonly IReadOnlyList<string> Kinds = ["pdf"];

    private readonly AutoCadPdfPublisher _autocad = new(defaultPaper: options.DefaultPaper);

    /// <summary>Claims and does one job. False when there was nothing to do.</summary>
    public async Task<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var job = await client.ClaimJobAsync(Kinds, cancellationToken);
        if (job is null)
            return false;

        log.WriteLine($"{DateTime.Now:HH:mm:ss}  Job {job.Id}: PDF of {job.FileName} v{job.Version} (attempt {job.Attempts})");
        var work = Path.Combine(Path.GetTempPath(), "openhold-job-" + job.Id + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var pdf = await PublishAsync(job, work, cancellationToken);
            await using (var content = File.OpenRead(pdf))
                await client.CompleteJobAsync(job, await client.UploadContentAsync(content, cancellationToken), cancellationToken);
            log.WriteLine($"{DateTime.Now:HH:mm:ss}  Job {job.Id}: published ({new FileInfo(pdf).Length / 1024} KB)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                       or OpenholdApiException or HttpRequestException)
        {
            // Unsupported files won't publish however often they're tried; anything else might (e.g. AutoCAD was busy).
            var retry = ex is not NotSupportedException;
            log.WriteLine($"{DateTime.Now:HH:mm:ss}  Job {job.Id}: failed{(retry ? "" : " (won't retry)")}: {ex.Message}");
            await client.FailJobAsync(job, ex.Message, retry, CancellationToken.None);
        }
        finally
        {
            TryDelete(work);
        }
        return true;
    }

    private async Task<string> PublishAsync(JobInfo job, string work, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(job.FileName).ToLowerInvariant();
        var source = await DownloadTreeAsync(job, work, cancellationToken);
        var plotted = Path.Combine(work, "plotted.pdf");
        switch (extension)
        {
            case ".dwg":
                if (!_autocad.Available)
                    throw new IOException("AutoCAD and the Openhold AutoCAD plugin must be installed on the job worker to publish drawings.");
                _autocad.Publish(source, plotted);
                break;
            case ".pdf":
                File.Copy(source, plotted);
                break;
            default:
                throw new NotSupportedException($"Openhold can't publish {extension} files as PDFs yet.");
        }

        var stamped = Path.Combine(work, Path.ChangeExtension(job.FileName, ".pdf"));
        PdfStamper.Stamp(plotted, stamped, job.VerifyUrl, StampText(job), options.Corner);
        return stamped;
    }

    /// <summary>"Released rev B · 28 Oct 2026 · v7" and "Scan to check it's current".</summary>
    private static string[] StampText(JobInfo job)
    {
        var revision = job.Revision is { Length: > 0 } r ? $" rev {r}" : "";
        var date = job.ChangedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return [$"{job.State ?? "Released"}{revision} · {date} · v{job.Version}", "Scan to check it's current"];
    }

    /// <summary>
    /// Downloads the version to publish and everything it references (xrefs, images, underlays), each at the
    /// version recorded with it, laid out as in the vault so relative references resolve. Returns the file's path.
    /// </summary>
    private async Task<string> DownloadTreeAsync(JobInfo job, string root, CancellationToken cancellationToken)
    {
        var seen = new HashSet<(Guid, int)>();
        var queue = new Queue<(Guid Id, int Version, string VaultPath)>();
        queue.Enqueue((job.FileId, job.Version, $"{job.FolderPath}/{job.FileName}"));
        string? main = null;
        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add((item.Id, item.Version)))
                continue;
            // A PDF plotted with xrefs missing would look complete; refuse instead.
            if (seen.Count > 2000)
                throw new InvalidOperationException($"{job.FileName} references more than 2,000 files, so it can't be published in one job.");
            var local = Path.Combine(root, item.VaultPath.TrimStart('$', '/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await using (var output = File.Create(local))
                await client.DownloadAsync(item.Id, item.Version, output, cancellationToken: cancellationToken);
            main ??= local;
            foreach (var child in await client.GetUsesAsync(item.Id, item.Version, cancellationToken))
                queue.Enqueue((child.FileId, child.Version, $"{child.Path}/{child.Name}"));
        }
        return main!;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // AutoCAD can hold a file a moment after exiting; a leftover temp folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
