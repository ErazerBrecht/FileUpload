namespace BigFileUpload.SeedWork;

public record Result
{
    private readonly int? _statusCode;
    
    public bool HasError { get; }
    public string? Error { get; }

    public int HttpStatusCode =>
        _statusCode ?? (HasError ? StatusCodes.Status500InternalServerError : StatusCodes.Status200OK);

    public Result(bool hasError, string? error = null, int? statusCode = null)
    {
        HasError = hasError;
        Error = error;
        _statusCode = statusCode;
    }
    
    public static Result Success() => new(false);
    public static Result BadRequest(string errorMsg) => new(true, errorMsg, StatusCodes.Status400BadRequest);
    public static Result Failed(string? errorMsg = null, int? statusCode = null) => new(true, errorMsg, statusCode);
    public static FileResult File(Stream stream, string fileName) => new(stream, fileName);
}

public record FileResult(Stream FileStream, string FileName) : Result(false)
{
    public Stream FileStream { get; } = FileStream ?? throw new ArgumentNullException(nameof(FileStream));
    public string FileName { get; } = FileName ?? throw new ArgumentNullException(nameof(FileName));
}