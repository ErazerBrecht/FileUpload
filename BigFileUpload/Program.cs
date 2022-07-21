using BigFileUpload.SeedWork;
using BigFileUpload.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 5368709120);

builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options => options.OperationFilter<FileUploadOperation>());
builder.Services.AddMultiPartFileUploader();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();
app.Run();