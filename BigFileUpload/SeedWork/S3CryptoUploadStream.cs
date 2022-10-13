using System.Security.Cryptography;

namespace BigFileUpload.SeedWork;

public class S3CryptoUploadStream : CryptoStream
{
    private readonly S3UploadStream _stream;

    public S3CryptoUploadStream(S3UploadStream stream, ICryptoTransform transform) : base(stream, transform, CryptoStreamMode.Write, true)
    {
        _stream = stream;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (!HasFlushedFinalBlock)
            await FlushFinalBlockAsync(cancellationToken);
        await _stream.CompleteAsync(cancellationToken);
    }
}