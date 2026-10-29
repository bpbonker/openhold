using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Openhold.Core.Content;
using Openhold.Core.Contracts;

namespace Openhold.Client.Sdk;

public sealed class OpenholdClientOptions
{
    /// <summary>Concurrent transfer requests. Parallel streams are what fill a long-latency WAN link.</summary>
    public int TransferParallelism { get; set; } = 8;

    /// <summary>Target bytes of chunk data per upload or fetch request.</summary>
    public int TransferBatchBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Bytes of content chunked before asking the server which chunks it lacks; bounds client memory.</summary>
    public int UploadWindowBytes { get; set; } = 32 * 1024 * 1024;
}

/// <summary>
/// Typed client for an Openhold site server. Always talk to your own site: reads and chunk transfers are
/// served locally, and the site forwards lock and commit operations to the authority.
/// </summary>
public sealed class OpenholdClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly OpenholdClientOptions _options;

    public OpenholdClient(HttpClient http, string username, string machine, OpenholdClientOptions? options = null)
    {
        _http = http;
        _options = options ?? new OpenholdClientOptions();
        Username = username;
        Machine = machine;
        _http.DefaultRequestHeaders.Remove("X-Openhold-User");
        _http.DefaultRequestHeaders.Remove("X-Openhold-Machine");
        _http.DefaultRequestHeaders.Add("X-Openhold-User", username);
        _http.DefaultRequestHeaders.Add("X-Openhold-Machine", machine);
    }

    /// <summary>The signed-in user as the server knows them (lower-case; no domain for the organisation's own people).</summary>
    public string Username { get; private set; }

    /// <summary>
    /// Sends an Openhold or organisation access token with every request, in place of Windows sign-in; null goes back to
    /// Windows sign-in. <paramref name="username"/> is who the token is for.
    /// </summary>
    public void UseAccessToken(string? token, string? username = null)
    {
        _http.DefaultRequestHeaders.Authorization = token is null ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (username is not null)
        {
            Username = username;
            _http.DefaultRequestHeaders.Remove("X-Openhold-User");
            _http.DefaultRequestHeaders.Add("X-Openhold-User", username);
        }
    }

    // ---- Sign-in and accounts ----

    public Task<SignInOptions> GetSignInOptionsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SignInOptions>("api/auth/options", cancellationToken);

    public Task<SignInResult> SignInAsync(string username, string password, string? code = null, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResult>("api/auth/sign-in", new SignInRequest(username, password, code, Machine), cancellationToken);

    public Task<SignInResult> RefreshSessionAsync(string sessionToken, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResult>("api/auth/refresh", new RefreshRequest(sessionToken, Machine), cancellationToken);

    public async Task SignOutAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/sign-out", new SignOutRequest(sessionToken), Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/password", new ChangePasswordRequest(currentPassword, newPassword), Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<TwoFactorSetup> BeginTwoFactorAsync(CancellationToken cancellationToken = default) =>
        PostAsync<TwoFactorSetup>("api/auth/two-factor/setup", new { }, cancellationToken);

    public async Task ConfirmTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/two-factor/confirm", new TwoFactorConfirmRequest(code), Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<WhoAmI> WhoAmIAsync(CancellationToken cancellationToken = default) => GetAsync<WhoAmI>("api/auth/me", cancellationToken);

    /// <summary>The audit log, newest first (View Audit privilege).</summary>
    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(DateTime? from = null, DateTime? to = null, string? username = null, string? action = null, int limit = 1000, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (from is { } f)
            query.Add($"from={Uri.EscapeDataString(f.ToUniversalTime().ToString("o"))}");
        if (to is { } t)
            query.Add($"to={Uri.EscapeDataString(t.ToUniversalTime().ToString("o"))}");
        if (username is not null)
            query.Add($"username={Uri.EscapeDataString(username)}");
        if (action is not null)
            query.Add($"action={Uri.EscapeDataString(action)}");
        return GetAsync<IReadOnlyList<AuditEntry>>("api/admin/audit?" + string.Join('&', query), cancellationToken);
    }

    /// <summary>Everyone with access and what they may do (View Audit or Manage Users privilege).</summary>
    public Task<IReadOnlyList<AccessReviewEntry>> GetAccessReviewAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<AccessReviewEntry>>("api/admin/access-review", cancellationToken);

    public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<AccountInfo>>("api/admin/accounts", cancellationToken);

    public Task<CreatedAccount> CreateAccountAsync(CreateAccountRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CreatedAccount>("api/admin/accounts", request, cancellationToken);

    public async Task<AccountInfo> UpdateAccountAsync(string username, UpdateAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PatchAsJsonAsync($"api/admin/accounts/{Uri.EscapeDataString(username)}", request, Json, cancellationToken);
        return await ReadAsync<AccountInfo>(response, cancellationToken);
    }

    public Task<ResetPasswordResult> ResetPasswordAsync(string username, CancellationToken cancellationToken = default) =>
        PostAsync<ResetPasswordResult>($"api/admin/accounts/{Uri.EscapeDataString(username)}/reset-password", new { }, cancellationToken);

    public Task<AccountInfo> ResetTwoFactorAsync(string username, CancellationToken cancellationToken = default) =>
        PostAsync<AccountInfo>($"api/admin/accounts/{Uri.EscapeDataString(username)}/reset-two-factor", new { }, cancellationToken);

    public Task<AccountInfo> UnlockAccountAsync(string username, CancellationToken cancellationToken = default) =>
        PostAsync<AccountInfo>($"api/admin/accounts/{Uri.EscapeDataString(username)}/unlock", new { }, cancellationToken);

    public Task<AccountInfo> EndSessionsAsync(string username, CancellationToken cancellationToken = default) =>
        PostAsync<AccountInfo>($"api/admin/accounts/{Uri.EscapeDataString(username)}/end-sessions", new { }, cancellationToken);

    /// <summary>This workstation; a check-out belongs to the PC that holds the checked-out copy.</summary>
    public string Machine { get; }

    /// <summary>Checks the server is reachable and which site it serves; needs no sign-in.</summary>
    public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ServerHealth>("health", cancellationToken);

    /// <summary>Whether the server can serve: its databases answer (and, at a site, whether the authority does).</summary>
    public Task<ServerHealth> GetReadinessAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ServerHealth>("health/ready", cancellationToken);

    /// <summary>The vaults the server hosts, e.g. Production and Sandbox.</summary>
    public Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<string>>("api/vaults", cancellationToken);

    public Task<FolderListing> ListFolderAsync(string path, CancellationToken cancellationToken = default) =>
        GetAsync<FolderListing>($"api/folders?path={Uri.EscapeDataString(path)}", cancellationToken);

    public Task<FolderInfo> EnsureFolderAsync(string path, CancellationToken cancellationToken = default) =>
        PostAsync<FolderInfo>("api/folders", new CreateFolderRequest(path), cancellationToken);

    /// <summary>Removes an empty folder (refused if it holds anything, even in the trash).</summary>
    public async Task<FolderInfo> DeleteFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/folders?path={Uri.EscapeDataString(path)}", cancellationToken);
        return await ReadAsync<FolderInfo>(response, cancellationToken);
    }

    public Task<VaultFileInfo> GetFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        GetAsync<VaultFileInfo>($"api/files/{fileId}", cancellationToken);

    public Task<SearchResults> SearchAsync(string text, string? folder = null, int limit = 200, CancellationToken cancellationToken = default) =>
        PostAsync<SearchResults>("api/search", new SearchRequest(text, folder, limit), cancellationToken);

    /// <summary>Resolves vault paths to files in one round trip.</summary>
    public Task<IReadOnlyList<ResolvedPath>> ResolvePathsAsync(IEnumerable<string> vaultPaths, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<ResolvedPath>>("api/files/resolve", new ResolvePathsRequest(vaultPaths.ToList()), cancellationToken);

    public Task<IReadOnlyList<VersionInfo>> GetHistoryAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<VersionInfo>>($"api/files/{fileId}/history", cancellationToken);

    public Task<IReadOnlyList<ReferenceInfo>> GetUsesAsync(Guid fileId, int? version = null, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ReferenceInfo>>($"api/files/{fileId}/uses{(version is null ? "" : $"?version={version}")}", cancellationToken);

    /// <summary>The PNG preview stored with a version (latest by default), or null if it has none.</summary>
    public async Task<byte[]?> GetThumbnailAsync(Guid fileId, int? version = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/files/{fileId}/thumbnail{(version is null ? "" : $"?version={version}")}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Gives a version the preview it was checked in without. Never replaces an existing preview.</summary>
    public async Task AddMissingThumbnailAsync(Guid fileId, int version, byte[] png, CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await _http.PutAsync($"api/files/{fileId}/thumbnail?version={version}", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Every file a file uses, at any depth, following each child's latest version.</summary>
    public Task<IReadOnlyList<ReferenceInfo>> GetAllUsesAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ReferenceInfo>>($"api/files/{fileId}/uses/all", cancellationToken);

    public Task<IReadOnlyList<ReferenceInfo>> GetWhereUsedAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ReferenceInfo>>($"api/files/{fileId}/where-used", cancellationToken);

    /// <summary>Checks out all files or none. A conflict is returned as an unsuccessful result listing the holders, not thrown.</summary>
    public async Task<CheckOutResult> CheckOutAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/locks/checkout", new FileIdsRequest(fileIds.ToList()), Json, cancellationToken);
        // Someone else holding a file is an answer (who has it); anything else, e.g. a released file, is an error.
        if (response.StatusCode == HttpStatusCode.Conflict && response.Content.Headers.ContentType?.MediaType != "application/problem+json")
            return (await response.Content.ReadFromJsonAsync<CheckOutResult>(Json, cancellationToken))!;
        return await ReadAsync<CheckOutResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> UndoCheckOutAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<Guid>>("api/locks/undo", new FileIdsRequest(fileIds.ToList()), cancellationToken);

    public Task<IReadOnlyList<LockHolder>> GetLocksAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<LockHolder>>("api/locks/query", new FileIdsRequest(fileIds.ToList()), cancellationToken);

    public Task<CheckInResult> RestoreVersionAsync(Guid fileId, RestoreRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CheckInResult>($"api/files/{fileId}/restore", request, cancellationToken);

    /// <summary>Moves files to the trash bin. All-or-nothing; refused if a file is checked out by someone else or still used.</summary>
    public Task<IReadOnlyList<Guid>> DeleteFilesAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<Guid>>("api/files/delete", new FileIdsRequest(fileIds.ToList()), cancellationToken);

    public Task<IReadOnlyList<TrashItem>> ListTrashAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<TrashItem>>("api/trash", cancellationToken);

    public Task<IReadOnlyList<Guid>> RestoreFromTrashAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<Guid>>("api/trash/restore", new FileIdsRequest(fileIds.ToList()), cancellationToken);

    public Task<CheckInResult> CheckInAsync(CheckInRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CheckInResult>("api/checkin", request, cancellationToken);

    public Task<IReadOnlyList<ReservedNumber>> ReserveNumbersAsync(string schemeId, int count = 1, IReadOnlyDictionary<string, string>? fields = null, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<ReservedNumber>>($"api/numbering/schemes/{Uri.EscapeDataString(schemeId)}/reserve", new ReserveNumbersRequest(count, fields), cancellationToken);

    public Task<IReadOnlyList<string>> ReleaseNumbersAsync(IEnumerable<string> numbers, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<string>>("api/numbering/release", new ReleaseNumbersRequest(numbers.ToList()), cancellationToken);

    public Task<VersionedConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        GetAsync<VersionedConfiguration>("api/config", cancellationToken);

    public Task<IReadOnlyList<ConfigurationVersion>> GetConfigurationHistoryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ConfigurationVersion>>("api/config/history", cancellationToken);

    public Task<VersionedConfiguration> GetConfigurationVersionAsync(int version, CancellationToken cancellationToken = default) =>
        GetAsync<VersionedConfiguration>($"api/config/versions/{version}", cancellationToken);

    /// <summary>Every site's server and how many file contents it still lacks.</summary>
    public Task<IReadOnlyList<SiteStatus>> ListSitesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<SiteStatus>>("api/sites", cancellationToken);

    /// <summary>Makes this site's server fetch everything it lacks now (administrators). Returns how many contents arrived.</summary>
    public async Task<int> CatchUpSiteAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("api/sites/catch-up", null, cancellationToken);
        return (await ReadAsync<JsonElement>(response, cancellationToken)).GetProperty("arrived").GetInt32();
    }

    // Item master (catalogue).

    public Task<ItemInfo> CreateItemAsync(SaveItemRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ItemInfo>("api/items", request, cancellationToken);

    public async Task<ItemInfo> UpdateItemAsync(Guid id, SaveItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/items/{id}", request, Json, cancellationToken);
        return await ReadAsync<ItemInfo>(response, cancellationToken);
    }

    public Task<ItemInfo> ChangeItemStateAsync(Guid id, ItemStateRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ItemInfo>($"api/items/{id}/state", request, cancellationToken);

    public Task<ItemInfo> GetItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<ItemInfo>($"api/items/{id}", cancellationToken);

    /// <summary>An item by its part number or any cross-reference number.</summary>
    public Task<ItemInfo> GetItemByNumberAsync(string number, CancellationToken cancellationToken = default) =>
        GetAsync<ItemInfo>($"api/items/by-number/{Uri.EscapeDataString(number)}", cancellationToken);

    public Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(ItemSearchRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<ItemSummary>>("api/items/search", request, cancellationToken);

    public Task<IReadOnlyList<DuplicateCandidate>> FindDuplicateItemsAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<IReadOnlyList<DuplicateCandidate>>("api/items/duplicates", request, cancellationToken);

    public Task<IReadOnlyList<ItemSummary>> GetItemsForFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ItemSummary>>($"api/files/{fileId}/items", cancellationToken);

    public Task<IReadOnlyList<ItemErpStatus>> GetItemErpStatusAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ItemErpStatus>>($"api/items/{itemId}/erp", cancellationToken);

    public Task<IReadOnlyList<ErpConnectionInfo>> ListErpConnectionsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ErpConnectionInfo>>("api/erp/connections", cancellationToken);

    public async Task TestErpConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/erp/connections/{Uri.EscapeDataString(connectionId)}/test", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>The kinds of ERP connection the server can make, and the fields each needs (administrators).</summary>
    public Task<IReadOnlyList<ErpKindInfo>> ListErpKindsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ErpKindInfo>>("api/erp/kinds", cancellationToken);

    /// <summary>Every ERP connection's set-up, without its secrets (administrators).</summary>
    public Task<IReadOnlyList<ErpConnectionDetails>> ListErpSetupAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ErpConnectionDetails>>("api/erp/setup", cancellationToken);

    public async Task<ErpConnectionDetails> SaveErpConnectionAsync(string id, SaveErpConnectionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/erp/setup/{Uri.EscapeDataString(id)}", request, Json, cancellationToken);
        return await ReadAsync<ErpConnectionDetails>(response, cancellationToken);
    }

    public async Task DeleteErpConnectionAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/erp/setup/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Queues every released and obsolete item for one ERP connection (e.g. after adding it). Returns how many.</summary>
    public async Task<int> SendAllToErpAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/erp/connections/{Uri.EscapeDataString(connectionId)}/send-all", null, cancellationToken);
        return (await ReadAsync<JsonElement>(response, cancellationToken)).GetProperty("queued").GetInt32();
    }

    /// <summary>Sends waiting items to the ERPs now, retrying failed ones. Returns how many were sent.</summary>
    public async Task<int> SendToErpNowAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("api/erp/send-now", null, cancellationToken);
        return (await ReadAsync<JsonElement>(response, cancellationToken)).GetProperty("sent").GetInt32();
    }

    /// <summary>What a person (you, by default) may do in a folder, and why.</summary>
    public Task<FolderAccessInfo> ExplainAccessAsync(string folderPath, string? username = null, CancellationToken cancellationToken = default) =>
        GetAsync<FolderAccessInfo>($"api/permissions/explain?path={Uri.EscapeDataString(folderPath)}{(username is null ? "" : $"&username={Uri.EscapeDataString(username)}")}", cancellationToken);

    public Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<UserInfo>>("api/users", cancellationToken);

    public async Task<VersionedConfiguration> SaveConfigurationAsync(SaveConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/config", request, Json, cancellationToken);
        return await ReadAsync<VersionedConfiguration>(response, cancellationToken);
    }

    /// <summary>Moves files to another lifecycle state together, or asks for approval when the transition needs it.</summary>
    public Task<StateChangeResult> ChangeStateAsync(ChangeStateRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<StateChangeResult>("api/lifecycle/change-state", request, cancellationToken);

    public Task<StateChangeResult> ChangeCategoryAsync(ChangeCategoryRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<StateChangeResult>("api/lifecycle/change-category", request, cancellationToken);

    /// <summary>Change requests waiting for the caller's approval, and the caller's own pending requests.</summary>
    public Task<IReadOnlyList<ChangeRequestInfo>> ListChangeRequestsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ChangeRequestInfo>>("api/change-requests", cancellationToken);

    public Task<ChangeRequestInfo> ApproveAsync(Guid requestId, string? comment = null, CancellationToken cancellationToken = default) =>
        PostAsync<ChangeRequestInfo>($"api/change-requests/{requestId}/approve", new ApprovalRequest(comment), cancellationToken);

    public Task<ChangeRequestInfo> RejectAsync(Guid requestId, string comment, CancellationToken cancellationToken = default) =>
        PostAsync<ChangeRequestInfo>($"api/change-requests/{requestId}/reject", new ApprovalRequest(comment), cancellationToken);

    public Task<ChangeRequestInfo> CancelChangeRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        PostAsync<ChangeRequestInfo>($"api/change-requests/{requestId}/cancel", new { }, cancellationToken);

    /// <summary>Takes the oldest waiting job of these kinds, or null when there's nothing to do. For job workers.</summary>
    public async Task<JobInfo?> ClaimJobAsync(IEnumerable<string> kinds, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/jobs/claim", new ClaimJobRequest(kinds.ToList()), Json, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        return await ReadAsync<JobInfo>(response, cancellationToken);
    }

    public async Task CompleteJobAsync(JobInfo job, byte[] manifest, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/jobs/{job.Id}/complete", new CompleteJobRequest(manifest, job.Attempts), Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task FailJobAsync(JobInfo job, string error, bool retry = true, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/jobs/{job.Id}/fail", new FailJobRequest(error, job.Attempts, retry), Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<JobStatus>> ListJobsAsync(string? status = null, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<JobStatus>>($"api/jobs{(status is null ? "" : $"?status={Uri.EscapeDataString(status)}")}", cancellationToken);

    /// <summary>The newest published PDF of a file (or of one version), or null if none has been published.</summary>
    public async Task<PublishedPdf?> GetPublishedPdfAsync(Guid fileId, int? version = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/files/{fileId}/pdf{(version is null ? "" : $"?version={version}")}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType != "application/problem+json")
            return null;
        return await ReadAsync<PublishedPdf>(response, cancellationToken);
    }

    public Task<IReadOnlyList<NumberingScheme>> ListNumberingSchemesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<NumberingScheme>>("api/numbering/schemes", cancellationToken);

    public async Task<NumberingScheme> SaveNumberingSchemeAsync(NumberingScheme scheme, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/numbering/schemes/{Uri.EscapeDataString(scheme.Id)}", scheme, Json, cancellationToken);
        return await ReadAsync<NumberingScheme>(response, cancellationToken);
    }

    /// <summary>Deletes a scheme that has never issued a number and that no category, item class or the catalogue uses.</summary>
    public async Task DeleteNumberingSchemeAsync(string schemeId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/numbering/schemes/{Uri.EscapeDataString(schemeId)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>The number a scheme would issue next for these field values, without reserving it.</summary>
    public Task<NumberPreview> PreviewNumberAsync(string schemeId, IReadOnlyDictionary<string, string>? fields = null, CancellationToken cancellationToken = default) =>
        PostAsync<NumberPreview>($"api/numbering/schemes/{Uri.EscapeDataString(schemeId)}/preview", new ReserveNumbersRequest(1, fields), cancellationToken);

    /// <summary>
    /// Chunks <paramref name="content"/> and uploads only the chunks the site does not already have.
    /// Returns the encoded manifest to put in a <see cref="CheckInFile"/>.
    /// </summary>
    public async Task<byte[]> UploadContentAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var chunker = new FastCdc();
        var chunks = new List<ChunkRef>();
        var pending = new List<(ContentHash Hash, byte[] Data)>();
        long pendingBytes = 0;
        var fileHasher = Blake3.Hasher.New();
        long length = 0;

        try
        {
            await chunker.SplitAsync(content, async chunk =>
            {
                fileHasher.Update(chunk.Span);
                length += chunk.Length;
                var hash = ContentHash.Compute(chunk.Span);
                chunks.Add(new ChunkRef(hash, chunk.Length));
                pending.Add((hash, chunk.ToArray()));
                pendingBytes += chunk.Length;
                if (pendingBytes >= _options.UploadWindowBytes)
                    await FlushAsync();
            }, cancellationToken);
            await FlushAsync();

            return ManifestCodec.Encode(new ChunkManifest(ContentHash.FromBytes(fileHasher.Finalize().AsSpan()), length, chunks));
        }
        finally
        {
            fileHasher.Dispose();
        }

        async Task FlushAsync()
        {
            if (pending.Count == 0)
                return;
            var missing = (await PostAsync<IReadOnlyList<string>>("api/chunks/missing",
                pending.Select(p => p.Hash.ToString()).Distinct().ToList(), cancellationToken)).ToHashSet();
            var toSend = pending.Where(p => missing.Remove(p.Hash.ToString())).ToList();
            pending.Clear();
            pendingBytes = 0;

            await Parallel.ForEachAsync(Batch(toSend, c => c.Data.Length),
                new ParallelOptions { MaxDegreeOfParallelism = _options.TransferParallelism, CancellationToken = cancellationToken },
                async (batch, ct) =>
                {
                    using var frames = new MemoryStream();
                    foreach (var (hash, data) in batch)
                        await ChunkFrames.WriteAsync(frames, hash, data, ct);
                    frames.Position = 0;
                    using var body = new StreamContent(frames);
                    body.Headers.ContentType = new("application/octet-stream");
                    using var response = await _http.PostAsync("api/chunks/upload", body, ct);
                    await EnsureSuccessAsync(response, ct);
                });
        }
    }

    /// <summary>
    /// Downloads a file version (latest if <paramref name="version"/> is null) into <paramref name="output"/>,
    /// fetching chunks in parallel and verifying the whole-file hash. Chunks already in
    /// <paramref name="localCache"/> are not downloaded again; fetched chunks are added to it.
    /// </summary>
    public async Task<ChunkManifest> DownloadAsync(Guid fileId, int? version, Stream output, IChunkStore? localCache = null, CancellationToken cancellationToken = default)
    {
        using var manifestResponse = await _http.GetAsync($"api/files/{fileId}/manifest{(version is null ? "" : $"?version={version}")}", cancellationToken);
        await EnsureSuccessAsync(manifestResponse, cancellationToken);
        return await DownloadAsync(await manifestResponse.Content.ReadAsByteArrayAsync(cancellationToken), output, localCache, cancellationToken);
    }

    /// <summary>Downloads the content a manifest describes (e.g. a published PDF), verifying every chunk and the whole.</summary>
    public async Task<ChunkManifest> DownloadAsync(byte[] encodedManifest, Stream output, IChunkStore? localCache = null, CancellationToken cancellationToken = default)
    {
        var manifest = ManifestCodec.Decode(encodedManifest);

        // Fetch a window of batches in parallel, then write the window in file order.
        var windowBytes = (long)_options.TransferBatchBytes * _options.TransferParallelism;
        var fileHasher = Blake3.Hasher.New();
        try
        {
            foreach (var window in Batch(manifest.Chunks, c => c.Length, windowBytes))
            {
                var data = await FetchWindowAsync(window, localCache, cancellationToken);
                foreach (var chunk in window)
                {
                    var bytes = data[chunk.Hash];
                    fileHasher.Update(bytes);
                    await output.WriteAsync(bytes, cancellationToken);
                }
            }

            var actual = ContentHash.FromBytes(fileHasher.Finalize().AsSpan());
            if (actual != manifest.FileHash)
                throw new ChunkIntegrityException(manifest.FileHash, $"downloaded file hashes to {actual}.");
            return manifest;
        }
        finally
        {
            fileHasher.Dispose();
        }
    }

    private async Task<Dictionary<ContentHash, byte[]>> FetchWindowAsync(IReadOnlyList<ChunkRef> window, IChunkStore? localCache, CancellationToken cancellationToken)
    {
        var data = new Dictionary<ContentHash, byte[]>();
        var needed = new List<ChunkRef>();
        foreach (var chunk in window.DistinctBy(c => c.Hash))
        {
            if (localCache is not null && await localCache.ContainsAsync(chunk.Hash, cancellationToken))
                data[chunk.Hash] = await localCache.GetAsync(chunk.Hash, cancellationToken);
            else
                needed.Add(chunk);
        }

        var fetched = new ConcurrentDictionary<ContentHash, byte[]>();
        await Parallel.ForEachAsync(Batch(needed, c => c.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _options.TransferParallelism, CancellationToken = cancellationToken },
            async (batch, ct) =>
            {
                using var response = await _http.PostAsJsonAsync("api/chunks/fetch", batch.Select(c => c.Hash.ToString()).ToList(), Json, ct);
                await EnsureSuccessAsync(response, ct);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await foreach (var (hash, bytes) in ChunkFrames.ReadAsync(body, FastCdc.DefaultMaxSize, ct))
                {
                    fetched[hash] = bytes;
                    if (localCache is not null)
                        await localCache.PutAsync(hash, bytes, ct);
                }
            });

        foreach (var chunk in needed)
        {
            if (!fetched.TryGetValue(chunk.Hash, out var bytes) || bytes.Length != chunk.Length)
                throw new ChunkIntegrityException(chunk.Hash, "server did not return this chunk.");
            data[chunk.Hash] = bytes;
        }
        return data;
    }

    /// <summary>Groups items into consecutive batches of roughly <paramref name="maxBytes"/> (default: one transfer request).</summary>
    private List<List<T>> Batch<T>(IReadOnlyList<T> items, Func<T, int> size, long? maxBytes = null)
    {
        var limit = maxBytes ?? _options.TransferBatchBytes;
        var batches = new List<List<T>>();
        var current = new List<T>();
        long currentBytes = 0;
        foreach (var item in items)
        {
            if (current.Count > 0 && currentBytes + size(item) > limit)
            {
                batches.Add(current);
                current = [];
                currentBytes = 0;
            }
            current.Add(item);
            currentBytes += size(item);
        }
        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    private async Task<T> GetAsync<T>(string uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string uri, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(uri, body, Json, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var code = "http_" + (int)response.StatusCode;
        var message = response.ReasonPhrase ?? "Request failed.";
        JsonElement? details = null;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = problem.RootElement;
            if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                code = c.GetString()!;
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                message = t.GetString()!;
            if (root.TryGetProperty("details", out var d))
                details = d.Clone();
        }
        catch (JsonException)
        {
            // Not a problem-details body; keep the HTTP status as the code.
        }
        throw new OpenholdApiException(response.StatusCode, code, message, details);
    }
}
