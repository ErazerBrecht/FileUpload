using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace BigFileUpload.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileService(this IServiceCollection services) =>
        services.AddScoped<IFileService, FileService>();
}

public interface IFileService
{
    bool IsMultipartContentType(string? contentType);

    Task<bool> UploadFile();
    Task DownloadFile(string fileName);
}

internal class FileService : IFileService
{
    private readonly HttpContext _httpContext;
    
    public FileService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContext = httpContextAccessor?.HttpContext ?? throw new ArgumentException(null, nameof(httpContextAccessor));
    }
    
    public async Task<bool> UploadFile()
    {
        var boundary = GetBoundary(MediaTypeHeaderValue.Parse(_httpContext.Request.ContentType));
        var reader = new MultipartReader(boundary, _httpContext.Request.Body);
        
        var section = await reader.ReadNextSectionAsync();
      
        var hasContentDispositionHeader = ContentDispositionHeaderValue.TryParse(
            section.ContentDisposition, out var contentDisposition);

        if (!hasContentDispositionHeader || !HasFileContentDisposition(contentDisposition)) 
            return false;
        
        var filePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "UploadedFiles"));
        await using var fileStream = File.Create(Path.Combine(filePath, contentDisposition!.FileName.Value));
        
        await section.Body.CopyToAsync(fileStream);
        return true;
    }

    public async Task DownloadFile(string fileName)
    {
        var folderPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "UploadedFiles"));
        var filePath = Path.Combine(folderPath, fileName);

        await using var stream = File.OpenRead(filePath);
        
        // Stream to client
        _httpContext.Response.ContentType = "application/octet-stream";
        await stream.CopyToAsync(_httpContext.Response.Body);
    }

    public bool IsMultipartContentType(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.IndexOf("multipart/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Content-Type: multipart/form-data; boundary="----WebKitFormBoundarymx2fSWqWSd0OxQqq"
    // The spec at https://tools.ietf.org/html/rfc2046#section-5.1 states that 70 characters is a reasonable limit.
    private static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit = 70)
    {
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidDataException("Missing content-type boundary.");
        }

        if (boundary.Length > lengthLimit)
        {
            throw new InvalidDataException(
                $"Multipart boundary length limit {lengthLimit} exceeded.");
        }

        return boundary;
    }

    private static bool HasFileContentDisposition(ContentDispositionHeaderValue? contentDisposition)
    {
        // Content-Disposition: form-data; name="myfile1"; filename="Misc 002.jpg"
        return contentDisposition != null
               && contentDisposition.DispositionType.Equals("form-data")
               && (!string.IsNullOrEmpty(contentDisposition.FileName.Value)
                   || !string.IsNullOrEmpty(contentDisposition.FileNameStar.Value));
    }
}