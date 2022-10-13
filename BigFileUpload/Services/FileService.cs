using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using BigFileUpload.SeedWork;
using BigFileUpload.SeedWork.Exceptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using Aes = System.Security.Cryptography.Aes;

namespace BigFileUpload.Services;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileService(this IServiceCollection services) =>
        services.AddScoped<IFileService, FileService>();
}

public interface IFileService
{
    Task<Result> UploadFile(CancellationToken cancellationToken = default);
    Task<Result> UploadImage(CancellationToken cancellationToken = default);
    Task<Result> DownloadFile(string fileName, CancellationToken cancellationToken = default);
}

internal class FileService : IFileService
{
    private const string EncryptionKeyHeader = "EncryptionKey";
    private const string OriginalContentSizeHeader = "OriginalContentSize";

    private readonly IS3Service _s3Service;
    private readonly IDataProtector _protector;
    private readonly HttpContext _httpContext;
    private readonly ILogger<FileService> _logger;

    public FileService(IDataProtectionProvider dataProtectionProvider, IS3Service s3Service,
        IHttpContextAccessor httpContextAccessor, ILogger<FileService> logger)
    {
        _protector = dataProtectionProvider.CreateProtector("ErazerBrecht.Files.V1");
        _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpContext = httpContextAccessor?.HttpContext ??
                       throw new ArgumentException(null, nameof(httpContextAccessor));
    }

    public async Task<Result> UploadFile(CancellationToken cancellationToken = default)
    {
        try
        {
            var multipart = await GetMultipart(cancellationToken);
            if (multipart.Error is not null) return multipart.Error;

            // AES Key generation
            using var aes = Aes.Create();
            var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

            // Add AES key encrypted with 'DataProtector' in file
            var encryptedKeys = EncryptKeys(aes);
            var metaData = new Dictionary<string, string>
            {
                {EncryptionKeyHeader, Convert.ToBase64String(encryptedKeys)},
            };

            // if (_httpContext.Request.ContentLength.HasValue)
            //     metaData.Add(OriginalContentSizeHeader,
            //         CalculateFileSize(_httpContext.Request.ContentLength.Value, boundary, section!).ToString());

            var fileName = $"{Guid.NewGuid().ToString()}-{multipart.Header.FileName}";
            await using (var s3Stream = new S3UploadStream(_s3Service, fileName, metaData))
            {
                await using var cryptoStream = new S3CryptoUploadStream(s3Stream, encryptor);
                await multipart.Body.CopyToAsync(cryptoStream, cancellationToken);
                await cryptoStream.CompleteAsync(cancellationToken);
            }

            return Result.Success();
        }
        catch (InvalidRequestException ex)
        {
            return Result.BadRequest(ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            return Result.BadRequest(ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Operation was cancelled");
            return Result.BadRequest("Operation cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Something went wrong uploading a file");
            return Result.Failed();
        }
    }

    public async Task<Result> UploadImage(CancellationToken cancellationToken = default)
    {
        try
        {
            var multipart = await GetMultipart(cancellationToken);
            if (multipart.Error is not null) return multipart.Error!;

            // AES Key generation
            using var aes = Aes.Create();
            var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

            // Add AES key encrypted with 'DataProtector' in file
            var encryptedKeys = EncryptKeys(aes);
            var metaData = new Dictionary<string, string>
            {
                {EncryptionKeyHeader, Convert.ToBase64String(encryptedKeys)},
            };

            // if (_httpContext.Request.ContentLength.HasValue)
            //     metaData.Add(OriginalContentSizeHeader,
            //         CalculateFileSize(_httpContext.Request.ContentLength.Value, boundary, section!).ToString());

            var fileName = $"{Guid.NewGuid().ToString()}-{multipart.Header.FileName}";

            #region Image processing
            
            using var ms = new MemoryStream();
            await multipart.Body.CopyToAsync(ms, cancellationToken);
            var (imageInfo, fileType) = await Image.LoadWithFormatAsync(ms, cancellationToken);
            if (imageInfo is null || fileType is null)
                return Result.BadRequest("Invalid file");
            
            var rawAmountOfBits = imageInfo.PixelType.BitsPerPixel * imageInfo.Height * (long) imageInfo.Width;
            // Max limit on 40MP with 32bit per pixel (160Mb RAW DATA)
            // This check is to prevent lotta pixel attack
            if (rawAmountOfBits > 40L * 1000 * 1000 * 32)
                return Result.BadRequest("Invalid file, too many pixels");

            var image = await Image.LoadAsync(ms, cancellationToken);
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            IImageEncoder encoder;
            if (fileType == JpegFormat.Instance)
                encoder = new JpegEncoder {Quality = 70};
            else if (fileType == PngFormat.Instance)
                encoder = new PngEncoder {CompressionLevel = PngCompressionLevel.Level7, IgnoreMetadata = true};
            else if (fileType == GifFormat.Instance)
                encoder = new GifEncoder();
            else
                return Result.Failed("Invalid file type");

            #endregion
            
            await using (var s3Stream = new S3UploadStream(_s3Service, fileName, metaData))
            {
                await using var cryptoStream = new S3CryptoUploadStream(s3Stream, encryptor);
                await image.SaveAsync(cryptoStream, encoder, cancellationToken);
                await cryptoStream.CompleteAsync(cancellationToken);
            }

            return Result.Success();
        }
        catch (InvalidRequestException ex)
        {
            return Result.BadRequest(ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            return Result.BadRequest(ex.Message);
        }
        catch (ImageFormatException)
        {
            return Result.BadRequest("Invalid file");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Operation was cancelled");
            return Result.BadRequest("Operation cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Something went wrong uploading a file");
            return Result.Failed();
        }
    }

    public async Task<Result> DownloadFile(string fileName, CancellationToken cancellationToken = default)
    {
        try
        {
            var metaData = await _s3Service.GetMetaDataAsync(fileName, cancellationToken);

            // Get encrypted AES key
            var encryptedKeysBase64 = metaData[EncryptionKeyHeader.ToLower()];
            if (encryptedKeysBase64 is null)
            {
                _logger.LogError("File {FileName} doesn't have encryption keys, cannot decrypt content", fileName);
                return Result.Failed();
            }

            // Decode + Decrypt AES key + Creating AES decryptor
            var encryptedKeys = Convert.FromBase64String(encryptedKeysBase64);
            var decryptedKeys = _protector.Unprotect(encryptedKeys);
            using var aes = Aes.Create();
            // Key == 32 bytes (256bit)
            // IV == 16 bytes (128bit)
            aes.Key = decryptedKeys.AsMemory(0, 32).ToArray();
            aes.IV = decryptedKeys.AsMemory(32, 16).ToArray();
            var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

            // Start download
            var downloadStream = await _s3Service.GetObjectAsync(fileName, cancellationToken);

            // Decrypting content
            var cryptoStream = new CryptoStream(downloadStream, decryptor, CryptoStreamMode.Read);

            // if (metaData[OriginalContentSizeHeader.ToLower()] is not null)
            //     _httpContext.Response.ContentLength = Convert.ToInt64(metaData[OriginalContentSizeHeader.ToLower()]);

            return Result.File(cryptoStream, fileName);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Operation was cancelled");
            return Result.BadRequest("Operation cancelled");
        }
        catch (AmazonS3Exception ex)
        {
            if (ex is {StatusCode: HttpStatusCode.NotFound})
                return Result.BadRequest("File not found");

            _logger.LogError(ex, "Something went wrong uploading a file");
            return Result.Failed();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Something went wrong uploading a file");
            throw;
        }
    }
    
    private byte[] EncryptKeys(SymmetricAlgorithm symmetricAlgorithm)
    {
        var keys = new byte[symmetricAlgorithm.Key.Length + symmetricAlgorithm.IV.Length];
        Buffer.BlockCopy(symmetricAlgorithm.Key, 0, keys, 0, symmetricAlgorithm.Key.Length);
        Buffer.BlockCopy(symmetricAlgorithm.IV, 0, keys, symmetricAlgorithm.Key.Length, symmetricAlgorithm.IV.Length);
        var encryptedKeys = _protector.Protect(keys); // Results in 148 bytes
        return encryptedKeys;
    }
    
    private async Task<MultipartValue> GetMultipart(CancellationToken cancellationToken)
    {
        if (!IsMultipartContentType(_httpContext.Request.ContentType))
           return new MultipartValue {Error = Result.BadRequest("Invalid Content-Type")};

        var boundary = GetBoundary(MediaTypeHeaderValue.Parse(_httpContext.Request.ContentType));
        var reader = new MultipartReader(boundary, _httpContext.Request.Body);

        var section = await reader.ReadNextSectionAsync(cancellationToken);

        var hasContentDispositionHeader = ContentDispositionHeaderValue.TryParse(
            section?.ContentDisposition, out var contentDisposition);

        if (!hasContentDispositionHeader || contentDisposition == null || !HasFileContentDisposition(contentDisposition))
            return new MultipartValue {Error = Result.BadRequest("Invalid Content-Type")};
        
        return new MultipartValue { Header = contentDisposition, Body = section!.Body};
    }

    private static bool IsMultipartContentType(string? contentType)
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
            throw new InvalidRequestException("Missing content-type boundary.");
        }

        if (boundary.Length > lengthLimit)
        {
            throw new InvalidRequestException(
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

    [Obsolete("This depends too hard on user-input which is never trustable!")]
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
        // +2 because the newline (/r/n)
        // TOTAL: 84 + 6 + 90 = 180
        return multipartSection.Headers.Aggregate(sizeWithoutBoundary,
            (current, header) => current - (header.Key.Length + header.Value.ToString().Length + 2 + 2));
    }
    
    private record MultipartValue
    {
        public Result? Error { get; init; }
        public ContentDispositionHeaderValue Header { get; init; }
        public Stream Body { get; init; }
    }
}