using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Yap.LoadTest.Shared;

public sealed record SignInResult(HttpStatusCode Status, string? Token, string? Detail);

/// <summary>
/// The HTTP side of a Yap client. One instance per process; the cookie travels per call, because the auth cookie
/// is marked Secure and a CookieContainer would refuse to send it over plain http to a test instance.
/// </summary>
public sealed class YapApi
{
    public Uri BaseUri { get; }
    private readonly HttpClient _http;

    public YapApi(Uri baseUri)
    {
        BaseUri = baseUri;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = int.MaxValue,   // hundreds of virtual users share this client
        }) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Add("Cookie", $"yap_auth={token}");
        return request;
    }

    public async Task<SignInResult> SignInAsync(string username, string? code = null, CancellationToken ct = default)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Post, "/api/auth/signin", null, JsonContent.Create(new SignInRequest(username, code), options: Json.Options)), ct);
        var token = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).Where(c => c.StartsWith("yap_auth=")).Select(c => c["yap_auth=".Length..]).FirstOrDefault()
            : null;
        var detail = response.IsSuccessStatusCode ? null : await ProblemDetail(response, ct);
        return new SignInResult(response.StatusCode, token, detail);
    }

    public async Task<T?> GetAsync<T>(string path, string token, CancellationToken ct = default)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Get, path, token), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json.Options, ct);
    }

    /// <summary>A POST/PUT/PATCH with a JSON body. The caller reads the status; nothing throws on a 4xx.</summary>
    public Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object body, string token, CancellationToken ct = default)
        => _http.SendAsync(Request(method, path, token, JsonContent.Create(body, body.GetType(), options: Json.Options)), ct);

    public static async Task<string?> ProblemDetail(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Json.Options, ct);
            return problem?.GetValueOrDefault("detail")?.ToString() ?? problem?.GetValueOrDefault("title")?.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// One image through tus, the way the browser does it: create, one PATCH with the whole body, then poll the
    /// info route until the thumbnails exist. Returns the /uploads/... URL to put in a message.
    /// </summary>
    public async Task<string> UploadImageAsync(string token, byte[] jpeg, string filename, string uploadBase, CancellationToken ct)
    {
        var endpoint = (string.IsNullOrEmpty(uploadBase) ? BaseUri.ToString().TrimEnd('/') : uploadBase.TrimEnd('/')) + "/api/tus";
        var create = Request(HttpMethod.Post, endpoint, token);
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", jpeg.Length.ToString());
        create.Headers.Add("Upload-Metadata", $"filename {B64(filename)},filetype {B64("image/jpeg")}");
        using var created = await _http.SendAsync(create, ct);
        if (created.StatusCode != HttpStatusCode.Created) throw new HttpRequestException($"tus create {(int)created.StatusCode}");
        var location = created.Headers.Location ?? throw new HttpRequestException("tus create: no Location");
        var uploadUrl = location.IsAbsoluteUri ? location : new Uri(new Uri(endpoint), location);

        var patch = Request(HttpMethod.Patch, uploadUrl.ToString(), token, new ByteArrayContent(jpeg));
        patch.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", "0");
        using var patched = await _http.SendAsync(patch, ct);
        if (patched.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException($"tus patch {(int)patched.StatusCode}");

        var id = uploadUrl.Segments[^1].Trim('/');
        var infoUrl = uploadUrl.ToString()[..uploadUrl.ToString().LastIndexOf('/')] + "/info/" + id;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var info = await _http.SendAsync(Request(HttpMethod.Get, infoUrl, token), ct);
            if (info.IsSuccessStatusCode)
            {
                var result = await info.Content.ReadFromJsonAsync<TusInfo>(Json.Options, ct);
                if (result?.Url is { } url) return url;
                throw new HttpRequestException("tus info: no url");
            }
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException("tus info: the server did not finish the image in 30 s");
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
