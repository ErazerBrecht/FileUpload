using BigFileUpload.SeedWork;
using BigFileUpload.Services;
using Microsoft.AspNetCore.Mvc;

namespace BigFileUpload.Controllers;

[Route("[controller]")]
public class FileController : BaseController
{
    private readonly IFileService _fileService;

    public FileController(IFileService fileService) => 
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));

    [HttpPost]
    [FileUploadOperation.FileContentType]
    [RequestSizeLimit(5L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile() => 
        Result(await _fileService.UploadFile(HttpContext.RequestAborted));
    
    [HttpPost("image")]
    [FileUploadOperation.FileContentType]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage() => 
        Result(await _fileService.UploadImage(HttpContext.RequestAborted));
    
    [HttpGet("{name}")]
    public async Task<IActionResult> Download(string? name) => 
        Result(await _fileService.DownloadFile(name, HttpContext.RequestAborted));
}