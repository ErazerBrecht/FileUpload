using System.Net;
using System.Net.Mime;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using BigFileUpload.SeedWork;
using BigFileUpload.SeedWork.Exceptions;
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
    private const string OriginalContentSize = "OriginalContentSize";

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

        var metaData = new Dictionary<string, string>
        {
            { EncryptionKeyHeader, Convert.ToBase64String(encryptedKeys) },
        };

        if (_httpContext.Request.ContentLength.HasValue)
            metaData.Add(OriginalContentSize,
                CalculateFileSize(_httpContext.Request.ContentLength.Value, boundary, section!).ToString());

        await using var s3Stream = new S3UploadStream(_s3Service,
            $"{Guid.NewGuid().ToString()}-{contentDisposition!.FileName}", metaData);

        // Encrypt content
        await using var cryptoStream = new CryptoStream(s3Stream, encryptor, CryptoStreamMode.Write);
        await section!.Body.CopyToAsync(cryptoStream, cancellationToken);
        return true;
    }

    public async Task DownloadFile(string fileName, CancellationToken cancellationToken = default)
    {
        try
        {
            var metaData = await _s3Service.GetMetaDataAsync(new GetObjectMetadataRequest
            {
                BucketName = "brecht-bigfileupload",
                Key = fileName
            }, cancellationToken);

            // Get encrypted AES key
            var encryptedKeysBase64 = metaData[EncryptionKeyHeader.ToLower()];
            if (encryptedKeysBase64 is null) throw new InvalidS3FileException(InvalidS3FileReason.NoEncryptionKeys);

            // Decode + Decrypt AES key + Creating AES decryptor
            var encryptedKeys = Convert.FromBase64String(encryptedKeysBase64);
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
            if (metaData[OriginalContentSize.ToLower()] is not null)
                _httpContext.Response.ContentLength = Convert.ToInt64(metaData[OriginalContentSize.ToLower()]);

            await cryptoStream.CopyToAsync(_httpContext.Response.Body, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            switch (ex)
            {
                case { StatusCode: HttpStatusCode.NotFound }:
                    throw new InvalidS3FileException(InvalidS3FileReason.NotFound, innerException: ex);
                default:
                    throw;
            }
        }
    }

    public bool IsMultipartContentType(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
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
    
    private static long CalculateFileSize(long contentLength, string boundary, MultipartSection multipartSection)
    {
        if (multipartSection.Headers == null)
            throw new ArgumentException(null, nameof(multipartSection));

        // Example:
        // ------WebKitFormBoundaryx0PMpT1jrefklcyS
        // Content-Disposition: form-data; name="file"; filename="Cat.png"
        // Content-Type: image/png
        //
        //
        // ------WebKitFormBoundaryx0PMpT1jrefklcyS--
        // Boundary length (42 * 2 => TOP & BOTTOM)
        // +2 because the /r/n at the end
        // +2 because for reasons unknown the BE receives two dashes less as the client sends, MAGIC :D
        // Binary length (6)
        // Binary data seems to be 3 NEWLINES (/r/n)
        var sizeWithoutBoundary = contentLength - 2 * (boundary.Length + 4) - 3 * 2;
        // Header length (90)  
        // In this case Content-Disposition & Content-Type
        // +2 because the ': ' 
        // +2 because the endline (/r/n)
        // TOTAL: 84 + 6 + 90 = 180
        return multipartSection.Headers.Aggregate(sizeWithoutBoundary,
            (current, header) => current - (header.Key.Length + header.Value.ToString().Length + 2 + 2));
    }
}