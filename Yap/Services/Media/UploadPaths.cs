namespace Yap.Services.Media;

/// <summary>
/// Where uploaded files live on disk. The URLs stay <c>/uploads/*</c>; the folder comes from
/// <c>ChatSettings:UploadsPath</c>, relative to the content root. The default keeps the Docker volume
/// (<c>/app/wwwroot/uploads</c>) valid.
/// </summary>
public sealed class UploadPaths
{
    public string Root { get; }
    public string Profiles => Path.Combine(Root, "profiles");
    public string Gifs => Path.Combine(Root, "gifs");
    public const string TusTempFolder = "tus-temp";   // also the URL segment under /uploads that Program.cs refuses to serve
    public string TusTemp => Path.Combine(Root, TusTempFolder);

    public UploadPaths(IConfiguration configuration, IWebHostEnvironment env)
    {
        var configured = configuration["ChatSettings:UploadsPath"];
        Root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.WebRootPath, "uploads")
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));
    }
}
