namespace BigFileUpload.SeedWork.Exceptions;

public class InvalidS3FileException : Exception
{
    public readonly InvalidS3FileReason Reason;

    public InvalidS3FileException(InvalidS3FileReason reason, string? message = null, Exception? innerException = null) :
        base(message, innerException)
    {
        Reason = reason;
    }
}

public enum InvalidS3FileReason
{
    NotFound = 1,
    NoEncryptionKeys = 2,
    InvalidEncryptionKeys = 3
}