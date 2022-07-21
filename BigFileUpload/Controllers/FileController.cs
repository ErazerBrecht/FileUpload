using BigFileUpload.SeedWork;
using BigFileUpload.Services;
using Microsoft.AspNetCore.Mvc;

namespace BigFileUpload.Controllers;

[ApiController]
[Route("[controller]")]
public class FileController : ControllerBase
{
    private readonly ILogger<FileController> _logger;
    private readonly IFileService _fileService;

    public FileController(ILogger<FileController> logger, IFileService fileService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
    }

    [HttpPost]
    [FileUploadOperation.FileContentType]
    public async Task<IActionResult> Post()
    {
        if (!_fileService.IsMultipartContentType(Request.ContentType))
        {
            ModelState.AddModelError("File", $"The request couldn't be processed");
            return BadRequest(ModelState);
        }
        
        try
        {
            return await _fileService.UploadFile() ? Ok() : BadRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Something failed uploading the file");
            throw;
        }
    }
    
    [HttpGet("{name}")]
    public async Task Download(string name)
    {
        await _fileService.DownloadFile(name);
    }
}