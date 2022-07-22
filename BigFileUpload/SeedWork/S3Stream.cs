using System.Buffers;
using Amazon.S3.Model;
using BigFileUpload.Services;

namespace BigFileUpload.SeedWork;

public class S3Stream : Stream
{
    // TODO Configurable
    private const string BucketName = "brecht-bigfileupload";
    
    // TODO Configurable???
    private const int PartSize = 5 * 1024 * 1024;
    private const int ReadBufferSize = 20000;

    private readonly IS3Service _s3Service;
    private readonly string _fileName;
    
    private byte[] _inputBuffer;
    private MemoryStream _inputBufferStream;

    private string? _uploadId;
    private List<PartETag> _partETags = new();

    public S3Stream(IS3Service s3Service, string fileName)
    {
        _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        _inputBuffer = new byte[PartSize + ReadBufferSize * 3];
        _inputBufferStream = new MemoryStream(_inputBuffer);
    }
    
    public override void Flush()
    {
        if (_inputBufferStream.Position > 0) 
            WriteToS3().GetAwaiter().GetResult();

        _s3Service.CompleteUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = BucketName,
            Key =_fileName,
            UploadId = _uploadId,
            PartETags = _partETags
        }).GetAwaiter().GetResult();
        
        // Cleanup
        _inputBuffer = new byte[PartSize + ReadBufferSize * 3];
        _inputBufferStream = new MemoryStream(_inputBuffer);
        _uploadId = null;
        _partETags = new List<PartETag>();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!CanWrite) 
            throw new NotSupportedException();
        if (offset < 0) 
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (buffer.Length - offset < count)
            throw new ArgumentException("Invalid offset", nameof(offset));

        _inputBufferStream.WriteAsync(buffer, offset, count);

        // We have loaded a full part in memory
        // Send it to S3
        if (PartSize < _inputBufferStream.Position)
            WriteToS3().GetAwaiter().GetResult();
        else
            Console.WriteLine("YOLO");
    }

    public override bool CanRead => false; // TODO FIX DOWNLOAD
    public override bool CanWrite => true; // TODO FIX DIFFERENCE BETWEEN DOWNLOAD & UPLOAD

    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private async Task WriteToS3()
    {
        _uploadId ??= await _s3Service.InitiateUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = BucketName,
            Key = _fileName
        });

        var partSize = _inputBufferStream.Position;
        _inputBufferStream.Position = 0;
        
        _partETags.Add(await _s3Service.UploadPartAsync(new UploadPartRequest
        {
            BucketName = BucketName,
            Key = _fileName,
            UploadId = _uploadId,
            InputStream = _inputBufferStream,
            PartSize = partSize,
            PartNumber = _partETags.Count + 1,
            IsLastPart = false
        }));
        
        _inputBufferStream = new MemoryStream(_inputBuffer);
    }
}