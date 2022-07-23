using System.Buffers;
using Amazon.S3.Model;
using BigFileUpload.Services;

namespace BigFileUpload.SeedWork;

public class S3UploadStream : Stream, IDisposable
{
    private bool _canWrite;

    // TODO Configurable???
    private const int PartSize = 5 * 1024 * 1024;
    
    private byte[]? _inputBuffer;
    private MemoryStream? _inputBufferStream;

    // S3 specific parameters
    private readonly IS3Service _s3Service;
    private const string BucketName = "brecht-bigfileupload";
    private readonly string _fileName;
    private readonly Dictionary<string, string>? _fileMetadata;
    private string? _uploadId;
    private List<PartETag>? _partETags;

    public S3UploadStream(IS3Service s3Service, string fileName, Dictionary<string, string>? fileMetaData = null)
    {
        _canWrite = true;
        _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        _fileMetadata = fileMetaData;
    }

    public override void Flush()
    {
        FlushAsync(default).GetAwaiter().GetResult();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!CanWrite) 
            return;
        if (_inputBufferStream?.Position > 0)
            await WriteToS3(cancellationToken);

        await _s3Service.CompleteUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = BucketName,
            Key = _fileName,
            UploadId = _uploadId,
            PartETags = _partETags
        }, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (buffer.Length - offset < count)
            throw new ArgumentException("Invalid offset", nameof(offset));
        
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = new())
    {
        if (!CanWrite)
            throw new NotSupportedException();
        
        _inputBuffer ??= ArrayPool<byte>.Shared.Rent(PartSize + 100 * 1024);
        _inputBufferStream ??= new MemoryStream(_inputBuffer);
        
        await _inputBufferStream.WriteAsync(buffer, cancellationToken);

        // We have loaded a full part in memory
        // Send it to S3
        if (PartSize < _inputBufferStream.Position)
            await WriteToS3(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
            
            // Cleanup
            _uploadId = null;
            _partETags = null;
            
            _inputBufferStream?.Dispose();

            if (_inputBuffer is not null)
                ArrayPool<byte>.Shared.Return(_inputBuffer);
        }
        finally
        {
            _canWrite = false;
        }
    }

    public override bool CanWrite => _canWrite;
    
    // This stream is meant for Uploading to S3
    // Reading == Downloading
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private async Task WriteToS3(CancellationToken cancellationToken)
    {
        if (_inputBuffer is null || _inputBufferStream is null)
            throw new Exception("TODO");
        
        if (_uploadId == null)
        {
            var request = new InitiateMultipartUploadRequest
            {
                BucketName = BucketName,
                Key = _fileName,
            };

            if (_fileMetadata is not null)
                foreach (var x in _fileMetadata)
                    request.Metadata.Add(x.Key.ToLower(),  x.Value);

            _uploadId = await _s3Service.InitiateUploadAsync(request, cancellationToken);
        }

        _partETags ??= new List<PartETag>();
        var partSize = _inputBufferStream.Position;
        var partNumber = _partETags.Count + 1;
        _inputBufferStream.Position = 0;

        _partETags.Add(await _s3Service.UploadPartAsync(new UploadPartRequest
        {
            BucketName = BucketName,
            Key = _fileName,
            UploadId = _uploadId,
            InputStream = _inputBufferStream,
            PartSize = partSize,
            PartNumber = partNumber,
            IsLastPart = false
        }, cancellationToken));

        _inputBufferStream = new MemoryStream(_inputBuffer);
    }
}