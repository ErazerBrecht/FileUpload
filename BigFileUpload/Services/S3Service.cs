using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace BigFileUpload.Services;

public interface IS3Service
{
    Task<string> InitiateUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken);
    Task<PartETag> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken);
    Task CompleteUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken);

    Task<MetadataCollection> GetMetaDataAsync(GetObjectMetadataRequest request,
        CancellationToken cancellationToken);

    Task<Stream> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken);
}

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddS3(this IServiceCollection services) =>
        services.AddSingleton<IS3Service, S3Service>();
}

internal class S3Service : IS3Service
{
    private readonly ILogger<S3Service> _logger;

    private AmazonS3Client _s3Client = new("AKIAXWRSOIIHBAJYW2DP",
        "j+4IEnGX+9ompOGdSNu0qNPSiq43jVxuLbDqVE52", RegionEndpoint.EUCentral1);

    public S3Service(ILogger<S3Service> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> InitiateUploadAsync(InitiateMultipartUploadRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _s3Client.InitiateMultipartUploadAsync(request, cancellationToken);
        return response.UploadId;
    }

    public async Task<PartETag> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken)
    {
        var response = await _s3Client.UploadPartAsync(request, cancellationToken);
        _logger.LogInformation(
            "Uploaded part {ResponsePartNumber}. (Part size = {RequestPartSize}, Upload Id: {RequestUploadId})",
            response.PartNumber, request.PartSize, request.UploadId);
        return new PartETag { PartNumber = response.PartNumber, ETag = response.ETag };
    }

    public async Task CompleteUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken)
    {
        await _s3Client.CompleteMultipartUploadAsync(request, cancellationToken);
    }

    public async Task<MetadataCollection> GetMetaDataAsync(GetObjectMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _s3Client.GetObjectMetadataAsync(request, cancellationToken);
        return response.Metadata;
    }

    public async Task<Stream> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
    {
        var response = await _s3Client.GetObjectAsync(request, cancellationToken);
        return response.ResponseStream;
    }
}