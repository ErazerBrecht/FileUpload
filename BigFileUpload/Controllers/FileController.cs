using BigFileUpload.SeedWork;
using BigFileUpload.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace BigFileUpload.Controllers;

[ApiController]
[Route("[controller]")]
public class FileController : ControllerBase
{
    private readonly ILogger<FileController> _logger;
    private readonly IMultiPartFileUploadService _multiPartFileUploadService;

    public FileController(ILogger<FileController> logger, IMultiPartFileUploadService multiPartFileUploadService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _multiPartFileUploadService = multiPartFileUploadService ?? throw new ArgumentNullException(nameof(multiPartFileUploadService));
    }

    [HttpPost]
    [FileUploadOperation.FileContentType]
    public async Task<IActionResult> Post()
    {
        if (!_multiPartFileUploadService.IsMultipartContentType(Request.ContentType))
        {
            ModelState.AddModelError("File", $"The request couldn't be processed");
            return BadRequest(ModelState);
        }
        
        try
        {
            var boundary = _multiPartFileUploadService.GetBoundary(MediaTypeHeaderValue.Parse(Request.ContentType));
            var reader = new MultipartReader(boundary, HttpContext.Request.Body);
            
            return await _multiPartFileUploadService.UploadFile(reader) ? Ok() : BadRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Something failed uploading the file");
            throw;
        }
    }
    
    [HttpGet("{name}")]
    public async Task<FileStreamResult> Download(string name)
    {
        var folderPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "UploadedFiles"));
        var filePath = Path.Combine(folderPath, name);
        var stream = System.IO.File.OpenRead(filePath);
        return new FileStreamResult(stream, "application/octet-stream");
    }
}