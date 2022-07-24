## FileUpload
The goal of this project was to create a POC for uploading big files (+-5Gb) to S3.
The memory usage should be limited, loading the whole file in memory is a no go!

Another goal was to client side encrypt the data before uploading it to a cloud, also without the need to load the whole file in memory.

This encryption system should be able to work with an external root key (Envelope Encryption). To mimick this behaviour I used the DataProtection library for Microsoft.

### Configuration
I have used the default configuration setup of ASP.NET Core & AWS SDK

E.g.
```
"environmentVariables": {
  "ASPNETCORE_ENVIRONMENT": "Development",
  "AWS_ACCESS_KEY_ID": "XXXXXXX",
  "AWS_SECRET_ACCESS_KEY": "XXXXXXX",
  "Aws:Region": "XXXXXXXXX",
  "Aws:BucketName": "XXXXXXXX"
}
```


### Warning
This is absolutely not production ready. I don't have that much experiences with multipart-formdata and with streaming in general. Also the DataProtection library isn't setup for a production setting. 