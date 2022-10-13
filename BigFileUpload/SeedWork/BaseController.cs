using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;

namespace BigFileUpload.SeedWork;

public class BaseController : ControllerBase
{
    [NonAction]
    public IActionResult Result(Result result)
    {
            return result.HasError
                ? Problem(title: result.Error, statusCode: result.HttpStatusCode)
                : result is FileResult fileResult ? 
                    File(fileResult.FileStream, MediaTypeNames.Application.Octet, fileResult.FileName)    
                : new StatusCodeResult(result.HttpStatusCode);

    }
}