using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace BigFileUpload.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMultiPartFileUploader(this IServiceCollection services) =>
        services.AddScoped<IMultiPartFileUploadService, MultiPartFileUploadLocalService>();
}

public interface IMultiPartFileUploadService
{
    bool IsMultipartContentType(string? contentType);
    string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit = 70);

    Task<bool> UploadFile(MultipartReader reader);
    Task<bool> UploadFiles(MultipartReader multipartReader);
}

internal class MultiPartFileUploadLocalService : IMultiPartFileUploadService
{
    public async Task<bool> UploadFile(MultipartReader reader)
    {
        var section = await reader.ReadNextSectionAsync();
        return await UploadFileCore(section);
    }

    public async Task<bool> UploadFiles(MultipartReader reader)
    {
        var section = await reader.ReadNextSectionAsync();

        while (section != null)
        {
            if (!await UploadFileCore(section))
                return false;

            section = await reader.ReadNextSectionAsync();
        }

        return true;
    }

    public bool IsMultipartContentType(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.IndexOf("multipart/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Content-Type: multipart/form-data; boundary="----WebKitFormBoundarymx2fSWqWSd0OxQqq"
    // The spec at https://tools.ietf.org/html/rfc2046#section-5.1 states that 70 characters is a reasonable limit.
    public string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit = 70)
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

    private static async Task<bool> UploadFileCore(MultipartSection? section)
    {
        if (section == null)
            return false;
        
        var hasContentDispositionHeader = ContentDispositionHeaderValue.TryParse(
            section.ContentDisposition, out var contentDisposition);

        if (hasContentDispositionHeader && HasFileContentDisposition(contentDisposition))
        {
            var filePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "UploadedFiles"));
            await using var fileStream = File.Create(Path.Combine(filePath, contentDisposition!.FileName.Value));
            await section.Body.CopyToAsync(fileStream);
            return true;
        }

        return false;
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