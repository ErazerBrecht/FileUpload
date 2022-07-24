using BigFileUpload.SeedWork;
using BigFileUpload.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 5368709120);

builder.Services.AddDataProtection();
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options => options.OperationFilter<FileUploadOperation>());
builder.Services.AddFileService();
builder.Services.AddS3();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.MapControllers();
app.Run();