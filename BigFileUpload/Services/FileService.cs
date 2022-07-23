using System.Security.Cryptography;
using Amazon.S3.Model;
using BigFileUpload.SeedWork;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Aes = System.Security.Cryptography.Aes;

namespace BigFileUpload.Services;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileService(this IServiceCollection services) =>
        services.AddScoped<IFileService, FileService>();
}

public interface IFileService
{
    bool IsMultipartContentType(string? contentType);

    Task<bool> UploadFile(CancellationToken cancellationToken = default);
    Task DownloadFile(string fileName, CancellationToken cancellationToken = default);
}

internal class FileService : IFileService
{
    private const string EncryptionKeyHeader = "EncryptionKey";
    
    private readonly IS3Service _s3Service;
    private readonly IDataProtector _protector;
    private readonly HttpContext _httpContext;

    public FileService(IDataProtectionProvider dataProtectionProvider, IS3Service s3Service,
        IHttpContextAccessor httpContextAccessor)
    {
        _protector = dataProtectionProvider.CreateProtector("ErazerBrecht.Files.V1");
        _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        _httpContext = httpContextAccessor?.HttpContext ??
                       throw new ArgumentException(null, nameof(httpContextAccessor));
    }

    public async Task<bool> UploadFile(CancellationToken cancellationToken = default)
    {
        var boundary = GetBoundary(MediaTypeHeaderValue.Parse(_httpContext.Request.ContentType));
        var reader = new MultipartReader(boundary, _httpContext.Request.Body);

        var section = await reader.ReadNextSectionAsync(cancellationToken);

        var hasContentDispositionHeader = ContentDispositionHeaderValue.TryParse(
            section?.ContentDisposition, out var contentDisposition);

        if (!hasContentDispositionHeader || !HasFileContentDisposition(contentDisposition))
            return false;

        // AES Key generation
        using var aes = Aes.Create();
        var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

        // Add AES key encrypted with 'DataProtector' in file
        var keys = new byte[aes.Key.Length + aes.IV.Length];
        Buffer.BlockCopy(aes.Key, 0, keys, 0, aes.Key.Length);
        Buffer.BlockCopy(aes.IV, 0, keys, aes.Key.Length, aes.IV.Length);
        var encryptedKeys = _protector.Protect(keys); // Results in 148 bytes

        await using var s3Stream = new S3UploadStream(_s3Service,
            $"{Guid.NewGuid().ToString()}-{contentDisposition!.FileName}", new Dictionary<string, string>
            {
                { EncryptionKeyHeader, Convert.ToBase64String(encryptedKeys) }
            });
        
        // Encrypt content
        await using var cryptoStream = new CryptoStream(s3Stream, encryptor, CryptoStreamMode.Write);
        await section!.Body.CopyToAsync(cryptoStream, cancellationToken);

        return true;
    }

    public async Task DownloadFile(string fileName, CancellationToken cancellationToken = default)
    {
        var metadataCollection = await _s3Service.GetMetaDataAsync(new GetObjectMetadataRequest
        {
            BucketName = "brecht-bigfileupload",
            Key = fileName
        }, cancellationToken);

        // Get encrypted AES key
        // TODO Check if NULL => CRASH!
        var encryptedKeys = Convert.FromBase64String(metadataCollection[EncryptionKeyHeader.ToLower()]);

        // Decrypt AES key + Creating AES decryptor
        var decryptedKeys = _protector.Unprotect(encryptedKeys);
        using var aes = Aes.Create();
        // Key == 32 bytes (256bit)
        // IV == 16 bytes (128bit)
        aes.Key = decryptedKeys.AsMemory(0, 32).ToArray();
        aes.IV = decryptedKeys.AsMemory(32, 16).ToArray();
        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

        // Start download
        await using var downloadStream = await _s3Service.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "brecht-bigfileupload",
            Key = fileName
        }, cancellationToken);

        
        // Decrypting content
        await using var cryptoStream = new CryptoStream(downloadStream, decryptor, CryptoStreamMode.Read);

        // Stream to client
        _httpContext.Response.ContentType = "application/octet-stream";
        await cryptoStream.CopyToAsync(_httpContext.Response.Body, cancellationToken);
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