﻿using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BigFileUpload.SeedWork;

/// <summary>
/// Add extra parameters for uploading files in swagger.
/// </summary>
/// Copied from: https://stackoverflow.com/questions/50172268/how-can-i-add-an-upload-button-to-swagger-ui-in-net-core-web-api
public class FileUploadOperation : IOperationFilter
{
    /// <summary>
    /// Applies the specified operation.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="context">The context.</param>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var isFileUploadOperation =
            context.MethodInfo.CustomAttributes.Any(a => a.AttributeType == typeof(FileContentType));

        if (!isFileUploadOperation) return;

        operation.Parameters.Clear();

        var uploadFileMediaType = new OpenApiMediaType()
        {
            Schema = new OpenApiSchema()
            {
                Type = "object",
                Properties =
                {
                    ["file"] = new OpenApiSchema()
                    {
                        Description = "Upload File",
                        Type = "file",
                        Format = "formData"
                    }
                },
                Required = new HashSet<string> {"file"}
            }
        };

        operation.RequestBody = new OpenApiRequestBody
        {
            Content = {["multipart/form-data"] = uploadFileMediaType}
        };
    }

    /// <summary>
    /// Indicates swashbuckle should consider the parameter as a file upload
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class FileContentType : Attribute
    {
    }
}