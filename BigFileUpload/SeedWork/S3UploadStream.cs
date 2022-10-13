using System.Buffers;
using Amazon.S3.Model;
using BigFileUpload.SeedWork.Exceptions;
using BigFileUpload.Services;

namespace BigFileUpload.SeedWork;

public sealed class S3UploadStream : Stream
{
    private bool _canWrite;

    // TODO Configurable???
    private const int PartSize = 5 * 1024 * 1024;
    private const long MaxSize = (5L * 1024 * 1024 * 1024) + 256;
    private static readonly long MaxAmountOfParts = (MaxSize + PartSize - 1) / PartSize;

    private byte[]? _inputBuffer;
    private MemoryStream? _inputBufferStream;

    // S3 specific parameters
    private readonly IS3Service _s3Service;
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
        if (_inputBufferStream?.Position > 0)
            await WriteToS3(true, cancellationToken);
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

        // Since images are completely buffered at once (they are converted by ImageSharp)
        // It can happen we have buffered more than our PartSize already
        // That's why we need the max value otherwise the underlying buffer would be to small
        var size = Math.Max(PartSize + 100 * 1024, buffer.Length);
        _inputBuffer ??= ArrayPool<byte>.Shared.Rent(size);
        _inputBufferStream ??= new MemoryStream(_inputBuffer);

        await _inputBufferStream.WriteAsync(buffer, cancellationToken);

        // We have loaded a full part in memory
        // Send it to S3
        if (_inputBufferStream.Position > PartSize)
            await WriteToS3(false, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                // Cancel the upload if there was still one 'active'
                if (_uploadId is not null)
                {
                    // If the aborting fails we want to continue with disposing
                    // Make sure you implement this in your S3:
                    // https://aws.amazon.com/blogs/aws-cloud-financial-management/discovering-and-deleting-incomplete-multipart-uploads-to-lower-amazon-s3-costs/#:~:text=If%20the%20complete%20multipart%20upload,are%20stored%20in%20Amazon%20S3
                    try
                    {
                        _s3Service.AbortUploadAsync(_fileName, _uploadId).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        _uploadId = null;
                        _partETags = null; 
                    }
                }

                // Cleanup
                _inputBufferStream?.Dispose();
                _inputBufferStream = null;

                if (_inputBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_inputBuffer);
                    _inputBuffer = null;
                }


            }

            base.Dispose(disposing);
        }
        finally
        {
            _canWrite = false;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            // Cancel the upload if there was still one 'active'
            if (_uploadId is not null)
            {
                // If the aborting fails we want to continue with disposing
                // Make sure you implement this in your S3:
                // https://aws.amazon.com/blogs/aws-cloud-financial-management/discovering-and-deleting-incomplete-multipart-uploads-to-lower-amazon-s3-costs/#:~:text=If%20the%20complete%20multipart%20upload,are%20stored%20in%20Amazon%20S3
                try
                {
                   await _s3Service.AbortUploadAsync(_fileName, _uploadId);
                }
                finally
                {
                    _uploadId = null;
                    _partETags = null; 
                }
            }

            // This will eventually call the synchronous Dispose method which will clean-up the buffer
            await base.DisposeAsync();
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
    
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_uploadId is not null)
        {
            await _s3Service.CompleteUploadAsync(_fileName, _uploadId, _partETags!, cancellationToken);
            _uploadId = null;
            _partETags = null;
            _canWrite = false;
        }
    }

    private async Task WriteToS3(bool isLastPart, CancellationToken cancellationToken = default)
    {
        if (_inputBuffer is null || _inputBufferStream is null)
            throw new Exception("TODO");

        _uploadId ??= await _s3Service.InitiateUploadAsync(_fileName, _fileMetadata, cancellationToken);

        _partETags ??= new List<PartETag>();
        var partSize = _inputBufferStream.Position;
        var partNumber = _partETags.Count + 1;
        _inputBufferStream.Position = 0;

        if (partNumber > MaxAmountOfParts)
            throw new InvalidRequestException("File exceeded max supported file size");

        _partETags.Add(await _s3Service.UploadPartAsync(
            new UploadPart(_fileName, _uploadId, partNumber, partSize, isLastPart),
            _inputBufferStream, cancellationToken));

        _inputBufferStream = new MemoryStream(_inputBuffer);
    }
}