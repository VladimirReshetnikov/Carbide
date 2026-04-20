// Minimal GitHub REST v3 client. Runs inside Mono-WASM browser so HttpClient dispatches
// through `fetch` under the hood. GitHub's api.github.com has CORS enabled, so
// unauthenticated calls work from the browser — rate-limited to 60/hour/IP.
// Setting a token (via the `token` REPL command) lifts that to 5000/hour/user.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace CarbideGh;

internal sealed class GitHubClient
{
    private static readonly Uri BaseAddress = new("https://api.github.com/");
    private readonly HttpClient _http;

    public GitHubClient()
    {
        _http = new HttpClient { BaseAddress = BaseAddress };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        // Browsers manage their own User-Agent; setting it here is ignored. That's fine —
        // GitHub tolerates a missing UA from browser origins.
    }

    private string? _token;
    public string? Token
    {
        get => _token;
        set
        {
            _token = value;
            _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(value)
                ? null
                : new AuthenticationHeaderValue("Bearer", value);
        }
    }

    public async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _http.GetAsync(path).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string msg;
            try
            {
                using var doc = JsonDocument.Parse(body);
                msg = doc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString() ?? body
                    : body;
            }
            catch
            {
                msg = body;
            }
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} {response.StatusCode} for {path}: {msg}");
        }
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public Task<JsonElement> ListPullRequestsAsync(RepoRef repo, string state, int perPage = 25)
        => GetAsync($"repos/{repo.Owner}/{repo.Name}/pulls?state={Uri.EscapeDataString(state)}&per_page={perPage}");

    public Task<JsonElement> GetPullRequestAsync(RepoRef repo, int number)
        => GetAsync($"repos/{repo.Owner}/{repo.Name}/pulls/{number}");

    public Task<JsonElement> ListPullRequestFilesAsync(RepoRef repo, int number)
        => GetAsync($"repos/{repo.Owner}/{repo.Name}/pulls/{number}/files?per_page=100");

    public Task<JsonElement> ListIssuesAsync(RepoRef repo, string state, string? label, int perPage = 25)
    {
        var qs = new List<string> { $"state={Uri.EscapeDataString(state)}", $"per_page={perPage}" };
        if (!string.IsNullOrWhiteSpace(label)) qs.Add($"labels={Uri.EscapeDataString(label)}");
        return GetAsync($"repos/{repo.Owner}/{repo.Name}/issues?{string.Join('&', qs)}");
    }

    public Task<JsonElement> GetIssueAsync(RepoRef repo, int number)
        => GetAsync($"repos/{repo.Owner}/{repo.Name}/issues/{number}");

    public Task<JsonElement> ListContributorsAsync(RepoRef repo, int perPage = 25)
        => GetAsync($"repos/{repo.Owner}/{repo.Name}/contributors?per_page={perPage}");

    public Task<JsonElement> ListCommitsAsync(RepoRef repo, int perPage = 25)
        => GetAsync($"repos/{repo.Owner}/{repo.Name}/commits?per_page={perPage}");

    /// <summary>
    /// Fetches the repo's stargazer count timeline. GitHub's `/stargazers` endpoint with
    /// the <c>application/vnd.github.star+json</c> media type returns one entry per star
    /// with a <c>starred_at</c> timestamp. We paginate up to <paramref name="maxPages"/>
    /// pages (100 per page) to keep the demo's payload bounded.
    /// </summary>
    public async Task<List<DateTime>> GetStargazerTimestampsAsync(RepoRef repo, int maxPages = 4)
    {
        var timestamps = new List<DateTime>();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/stargazers?per_page=100&page=1");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.star+json"));

        for (int page = 1; page <= maxPages; page++)
        {
            using var pageReq = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/stargazers?per_page=100&page={page}");
            pageReq.Headers.Accept.Clear();
            pageReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.star+json"));
            if (_http.DefaultRequestHeaders.Authorization is not null)
            {
                pageReq.Headers.Authorization = _http.DefaultRequestHeaders.Authorization;
            }
            using var response = await _http.SendAsync(pageReq).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"GitHub stargazers returned {(int)response.StatusCode}: {body}");
            }
            var body2 = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body2);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) break;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("starred_at", out var sa) && sa.TryGetDateTime(out var dt))
                {
                    timestamps.Add(dt);
                }
            }
            if (arr.GetArrayLength() < 100) break;
        }
        return timestamps;
    }
}

internal readonly record struct RepoRef(string Owner, string Name)
{
    public override string ToString() => $"{Owner}/{Name}";

    public static bool TryParse(string s, out RepoRef repo)
    {
        var slash = s.IndexOf('/');
        if (slash > 0 && slash < s.Length - 1)
        {
            repo = new RepoRef(s[..slash].Trim(), s[(slash + 1)..].Trim());
            return repo.Owner.Length > 0 && repo.Name.Length > 0;
        }
        repo = default;
        return false;
    }
}

internal sealed class ReplState
{
    public RepoRef? CurrentRepo { get; set; }
    public GitHubClient Github { get; } = new();
    public bool Verbose { get; set; }
}
