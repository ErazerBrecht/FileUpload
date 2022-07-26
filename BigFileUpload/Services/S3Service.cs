﻿using Amazon.S3;
using Amazon.S3.Model;

namespace BigFileUpload.Services;

public interface IS3Service
{
    Task<string> InitiateUploadAsync(string fileName, Dictionary<string, string>? metaData = null,
        CancellationToken ct = default);

    Task<PartETag> UploadPartAsync(UploadPart part, Stream stream, CancellationToken ct = default);
    Task CompleteUploadAsync(string fileName, string id, List<PartETag> eTags, CancellationToken ct = default);
    Task AbortUploadAsync(string fileName, string id, CancellationToken ct = default);

    Task<MetadataCollection> GetMetaDataAsync(string fileName, CancellationToken ct = default);
    Task<Stream> GetObjectAsync(string fileName, CancellationToken ct = default);
}

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddS3(this IServiceCollection services)
    {
        return services
            .AddAWSService<IAmazonS3>()
            .AddSingleton<IS3Service, S3Service>();
    }
}

internal class S3Service : IS3Service
{
    private readonly IAmazonS3 _s3Client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<S3Service> _logger;

    private string BucketName => _configuration.GetValue<string>("Aws:BucketName");

    public S3Service(IAmazonS3 s3Client, IConfiguration configuration, ILogger<S3Service> logger)
    {
        (s3Client.Config as AmazonS3Config).ForcePathStyle = true;
        _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (BucketName is null)
            throw new ArgumentException("BucketName not found in configuration", nameof(configuration));
    }

    public async Task<string> InitiateUploadAsync(string fileName, Dictionary<string, string>? metaData = null,
        CancellationToken ct = default)
    {
        var request = new InitiateMultipartUploadRequest
        {
            BucketName = BucketName,
            Key = fileName,
        };

        if (metaData is not null)
            foreach (var x in metaData)
                request.Metadata.Add(x.Key.ToLower(), x.Value);

        var response = await _s3Client.InitiateMultipartUploadAsync(request, ct);
        return response.UploadId;
    }

    public async Task<PartETag> UploadPartAsync(UploadPart part, Stream stream, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Starting to upload part {ResponsePartNumber}. (Part size = {RequestPartSize}, Upload Id: {RequestUploadId})",
            part.Number, part.Size, part.UploadId);
        
        var request = new UploadPartRequest
        {
            BucketName = BucketName,
            Key = part.FileName,
            UploadId = part.UploadId,
            PartSize = part.Size,
            PartNumber = part.Number,
            IsLastPart = part.IsLast,
            InputStream = stream,
        };

        var response = await _s3Client.UploadPartAsync(request, ct);
        _logger.LogInformation(
            "Uploaded part {ResponsePartNumber}. (Part size = {RequestPartSize}, Upload Id: {RequestUploadId})",
            response.PartNumber, request.PartSize, request.UploadId);

        return new PartETag { PartNumber = response.PartNumber, ETag = response.ETag };
    }

    public async Task CompleteUploadAsync(string fileName, string id, List<PartETag> eTags,
        CancellationToken ct)
    {
        _logger.LogDebug("Completing upload for {FileName} with Upload Id: {RequestUploadId})", fileName, id);
        
        var request = new CompleteMultipartUploadRequest
        {
            BucketName = BucketName,
            Key = fileName,
            UploadId = id,
            PartETags = eTags
        };
        await _s3Client.CompleteMultipartUploadAsync(request, ct);
    }

    public async Task AbortUploadAsync(string fileName, string id, CancellationToken ct = default)
    {
        _logger.LogDebug("Cancelling upload for {FileName} with Upload Id: {RequestUploadId})", fileName, id);
        
        var request = new AbortMultipartUploadRequest
        {
            BucketName = BucketName,
            Key = fileName,
            UploadId = id,
        };
        await _s3Client.AbortMultipartUploadAsync(request, ct);
    }

    public async Task<MetadataCollection> GetMetaDataAsync(string fileName, CancellationToken ct = default)
    {
        var request = new GetObjectMetadataRequest
        {
            BucketName = BucketName,
            Key = fileName
        };
        var response = await _s3Client.GetObjectMetadataAsync(request, ct);
        return response.Metadata;
    }

    public async Task<Stream> GetObjectAsync(string fileName, CancellationToken ct = default)
    {
        var request = new GetObjectRequest
        {
            BucketName = BucketName,
            Key = fileName
        };
        var response = await _s3Client.GetObjectAsync(request, ct);
        return response.ResponseStream;
    }
}

public record UploadPart(string FileName, string UploadId, int Number, long Size, bool IsLast);