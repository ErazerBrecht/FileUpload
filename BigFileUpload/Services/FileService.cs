using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Aes = System.Security.Cryptography.Aes;

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
    private readonly IDataProtector _protector;
    private readonly HttpContext _httpContext;
    
    public FileService(IDataProtectionProvider  dataProtectionProvider, IHttpContextAccessor httpContextAccessor)
    {
        _protector = dataProtectionProvider.CreateProtector("ErazerBrecht.Files.V1");
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
            
        // AES Key generation
        using var aes = Aes.Create();
        var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            
        // Add AES key encrypted with 'DataProtector' in file
        var keys = new byte[aes.Key.Length + aes.IV.Length];
        Buffer.BlockCopy(aes.Key, 0, keys, 0, aes.Key.Length);
        Buffer.BlockCopy(aes.IV, 0, keys, aes.Key.Length, aes.IV.Length);
        var encryptedKeys = _protector.Protect(keys);                   // Results in 148 bytes
        await fileStream.WriteAsync(encryptedKeys);
            
        // Encrypt content
        await using var cryptoStream = new CryptoStream(fileStream, encryptor, CryptoStreamMode.Write);
        await section.Body.CopyToAsync(cryptoStream);
        
        return true;
    }

    public async Task DownloadFile(string fileName)
    {
        var folderPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "UploadedFiles"));
        var filePath = Path.Combine(folderPath, fileName);

        await using var stream = File.OpenRead(filePath);
        
        // Get encrypted AES key
        var encryptedKeys = new byte[148];
        var _ = await stream.ReadAsync(encryptedKeys.AsMemory(0, 148));
        
        // Decrypt AES key + Creating AES decryptor
        var decryptedKeys = _protector.Unprotect(encryptedKeys);
        using var aes = Aes.Create();
        // Key == 32 bytes (256bit)
        // IV == 16 bytes (128bit)
        aes.Key = decryptedKeys.AsMemory(0, 32).ToArray();
        aes.IV = decryptedKeys.AsMemory(32, 16).ToArray();
        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            
        // Decrypting content
        await using var cryptoStream = new CryptoStream(stream, decryptor, CryptoStreamMode.Read);
        
        // Stream to client
        _httpContext.Response.ContentType = "application/octet-stream";
        await cryptoStream.CopyToAsync(_httpContext.Response.Body);
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