using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using System.Net;
using System.Security.Cryptography;

namespace Drive;

public sealed class Storage : IDisposable
{
    private readonly AmazonS3Client client;
    private readonly AmazonS3Client publicClient;
    private readonly Settings settings;

    public Storage(Settings settings)
    {
        this.settings = settings;
        client = Create(settings.S3Endpoint);
        publicClient = Create(settings.S3PublicEndpoint);
    }

    private AmazonS3Client Create(string endpoint) => new(
        new BasicAWSCredentials(settings.S3AccessKey, settings.S3SecretKey),
        new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    public async Task Initialize()
    {
        try
        {
            await client.GetBucketLocationAsync(settings.Bucket);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound || error.ErrorCode == "NoSuchBucket")
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = settings.Bucket });
        }
    }

    public async Task<string> Begin(string key, string contentType)
    {
        var response = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = settings.Bucket,
            Key = key,
            ContentType = contentType
        });
        return response.UploadId;
    }

    public async Task<string> Part(Row upload, int number, Stream stream, long size, CancellationToken ct)
    {
        var response = await client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = settings.Bucket,
            Key = upload.Text("object_key"),
            UploadId = upload.Text("multipart_id"),
            PartNumber = number,
            InputStream = stream,
            PartSize = size
        }, ct);
        return response.ETag;
    }

    public async Task<bool> Exists(string key)
    {
        try
        {
            await client.GetObjectMetadataAsync(settings.Bucket, key);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    public async Task Complete(Row upload, List<Row> parts)
    {
        // A previous completion may have reached S3 before the database transaction committed.
        if (await Exists(upload.Text("object_key")))
        {
            return;
        }

        if (upload.Number("size") == 0)
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = settings.Bucket,
                Key = upload.Text("object_key"),
                ContentBody = "",
                ContentType = upload.Text("content_type")
            });
            return;
        }
        await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = settings.Bucket,
            Key = upload.Text("object_key"),
            UploadId = upload.Text("multipart_id"),
            PartETags = parts.Select(p => new PartETag((int)p.Number("number"), p.Text("etag"))).ToList()
        });
    }

    public async Task<(long Size, string Hash)> Fingerprint(string key)
    {
        using var response = await client.GetObjectAsync(settings.Bucket, key);
        var digest = await SHA256.HashDataAsync(response.ResponseStream);
        return (response.ContentLength, Convert.ToHexStringLower(digest));
    }

    public Task<string> Download(string key, string name) => publicClient.GetPreSignedURLAsync(new GetPreSignedUrlRequest
    {
        BucketName = settings.Bucket,
        Key = key,
        Verb = HttpVerb.GET,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Protocol = settings.S3PublicEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        ResponseHeaderOverrides = new ResponseHeaderOverrides
        {
            ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(name),
            ContentType = "application/octet-stream"
        }
    });

    public async Task Delete(string key, string? multipart)
    {
        if (!string.IsNullOrEmpty(multipart))
        {
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = settings.Bucket,
                    Key = key,
                    UploadId = multipart
                });
            }
            catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload") { }
        }
        await client.DeleteObjectAsync(settings.Bucket, key);
    }

    public void Dispose()
    {
        client.Dispose();
        publicClient.Dispose();
    }
}
