using System.Net;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class JobTests(VaultFixture vault)
{
    private static string Unique(string extension) => $"GA-{Guid.NewGuid():N}{extension}";

    private static Task<StateChangeResult> ChangeState(OpenholdClient client, string toState, params Guid[] ids) =>
        client.ChangeStateAsync(new ChangeStateRequest(Guid.NewGuid(), ids, toState, "test"));

    /// <summary>Claims jobs until this file's comes up; other tests' jobs are set aside so they don't get in the way.</summary>
    private static async Task<JobInfo> ClaimJobForAsync(OpenholdClient worker, Guid fileId)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = await worker.ClaimJobAsync(["pdf"]) ?? throw new InvalidOperationException("No job was queued for the file.");
            if (job.FileId == fileId)
                return job;
            await worker.FailJobAsync(job, "set aside by another test", retry: false);
        }
        throw new InvalidOperationException("Too many other jobs.");
    }

    [Fact]
    public async Task Releasing_a_drawing_queues_a_pdf_that_a_worker_publishes()
    {
        var lena = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(lena, VaultFixture.UniqueFolder("publish"), Unique(".dwg")))[0];
        var released = await ChangeState(lena, DefaultConfiguration.Released, id);

        var notWorker = await Assert.ThrowsAsync<OpenholdApiException>(() => lena.ClaimJobAsync(["pdf"]));
        Assert.Equal(HttpStatusCode.Forbidden, notWorker.StatusCode);

        var worker = vault.Client(VaultFixture.Administrator, "JOBS1");
        var job = await ClaimJobForAsync(worker, id);
        Assert.Equal(released.Versions.Single().Version, job.Version);
        Assert.Equal(DefaultConfiguration.Released, job.State);
        Assert.EndsWith($"/verify/{id}/{job.Version}", job.VerifyUrl);

        var pdf = VaultFixture.Content(4096, 42);
        await worker.CompleteJobAsync(job, await worker.UploadContentAsync(new MemoryStream(pdf)));

        var published = await lena.GetPublishedPdfAsync(id);
        Assert.NotNull(published);
        Assert.Equal(job.Version, published.Version);
        var downloaded = new MemoryStream();
        await lena.DownloadAsync(published.Manifest, downloaded);
        Assert.Equal(pdf, downloaded.ToArray());
        Assert.Contains(await worker.ListJobsAsync("done"), j => j.Id == job.Id);
    }

    [Fact]
    public async Task A_failing_job_is_retried_then_left_failed_with_its_reason()
    {
        var lena = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(lena, VaultFixture.UniqueFolder("failing"), Unique(".dwg")))[0];
        await ChangeState(lena, DefaultConfiguration.Released, id);
        var worker = vault.Client(VaultFixture.Administrator, "JOBS1");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var job = await ClaimJobForAsync(worker, id);
            Assert.Equal(attempt, job.Attempts);
            await worker.FailJobAsync(job, "AutoCAD isn't installed on JOBS1");
        }

        var failed = Assert.Single(await worker.ListJobsAsync("failed"), j => j.FileId == id);
        Assert.Equal("AutoCAD isn't installed on JOBS1", failed.LastError);
        Assert.Null(await lena.GetPublishedPdfAsync(id));
    }

    [Fact]
    public async Task A_worker_whose_job_was_handed_on_cant_finish_it()
    {
        var lena = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(lena, VaultFixture.UniqueFolder("stale"), Unique(".dwg")))[0];
        await ChangeState(lena, DefaultConfiguration.Released, id);
        var worker = vault.Client(VaultFixture.Administrator, "JOBS1");
        var first = await ClaimJobForAsync(worker, id);
        await worker.FailJobAsync(first, "AutoCAD was busy"); // back in the queue
        var second = await ClaimJobForAsync(worker, id);

        var stale = await Assert.ThrowsAsync<OpenholdApiException>(() => worker.FailJobAsync(first, "late report", retry: false));
        Assert.Equal("job_not_yours", stale.Code);
        await worker.CompleteJobAsync(second, await worker.UploadContentAsync(new MemoryStream(VaultFixture.Content(100, 1))));
        Assert.NotNull(await lena.GetPublishedPdfAsync(id));
    }

    [Fact]
    public async Task The_qr_page_says_whether_a_printout_is_still_the_current_release()
    {
        var lena = vault.Client("lena");
        var path = VaultFixture.UniqueFolder("verify");
        var id = (await VaultFixture.AddFilesAsync(lena, path, Unique(".dwg")))[0];
        var printed = (await ChangeState(lena, DefaultConfiguration.Released, id)).Versions.Single().Version;
        var phone = vault.AnonymousHttpClient();

        async Task<string> Check(int version) => await phone.GetStringAsync($"/verify/{id}/{version}");

        Assert.Contains(">Current<", await Check(printed));

        await ChangeState(lena, DefaultConfiguration.WorkInProgress, id); // reopened: still the current release until re-released
        Assert.Contains("still the current release", await Check(printed));

        await lena.CheckOutAsync([id]);
        await lena.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "changed", [new CheckInFile(id, null, null,
            await lena.UploadContentAsync(new MemoryStream(VaultFixture.Content(5000, 7))), null, null)]));
        await ChangeState(lena, DefaultConfiguration.Released, id);
        Assert.Contains(">Superseded<", await Check(printed));

        Assert.Contains("Not recognised", await phone.GetStringAsync($"/verify/{Guid.NewGuid()}/1"));
    }
}
