using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace BigFileUpload.Services;

public interface IS3Service
{
    Task<string> InitiateUploadAsync(InitiateMultipartUploadRequest request);
    Task<PartETag> UploadPartAsync(UploadPartRequest request);
    Task CompleteUploadAsync(CompleteMultipartUploadRequest request);
}

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddS3(this IServiceCollection services) =>
        services.AddSingleton<IS3Service, S3Service>();
}

internal class S3Service : IS3Service
{
    private AmazonS3Client _s3Client = new("AKIAXWRSOIIHBAJYW2DP",
        "j+4IEnGX+9ompOGdSNu0qNPSiq43jVxuLbDqVE52", RegionEndpoint.EUCentral1);
    
    public async Task<string> InitiateUploadAsync(InitiateMultipartUploadRequest request)
    {
        var response = await _s3Client.InitiateMultipartUploadAsync(request);
        return response.UploadId;
    }
    
    public async Task<PartETag> UploadPartAsync(UploadPartRequest request)
    {
        var response = await  _s3Client.UploadPartAsync(request);
        return new PartETag { PartNumber = response.PartNumber, ETag = response.ETag };
    }

    public async Task CompleteUploadAsync(CompleteMultipartUploadRequest request)
    {
        var response = await _s3Client.CompleteMultipartUploadAsync(request);
    }
}