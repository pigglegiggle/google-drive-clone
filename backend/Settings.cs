namespace Drive;

public sealed class Settings(IConfiguration configuration)
{
    public string Database => configuration["DATABASE_URL"]
        ?? "Host=127.0.0.1;Port=55432;Database=drive;Username=drive";
    public string S3Endpoint => configuration["S3_ENDPOINT"] ?? "http://127.0.0.1:9000";
    public string S3PublicEndpoint => configuration["S3_PUBLIC_ENDPOINT"] ?? S3Endpoint;
    public string S3AccessKey => Required("S3_ACCESS_KEY");
    public string S3SecretKey => Required("S3_SECRET_KEY");
    public string Bucket => configuration["S3_BUCKET"] ?? "drive";
    public bool SecureCookies => configuration.GetValue("SECURE_COOKIES", false);
    public string KeyDirectory => configuration["KEY_DIRECTORY"] ?? ".local/keys";
    public string TempDirectory => configuration["TEMP_DIRECTORY"] ?? Path.GetTempPath();
    public const int PartSize = 8 * 1024 * 1024;
    public const long MaxFileSize = 5L * 1024 * 1024 * 1024;
    public const long Quota = 10L * 1024 * 1024 * 1024;
    private string Required(string key) => configuration[key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {key} before starting the API.");
}

public sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public static ApiError Bad(string message) => new(400, message);
    public static ApiError Missing() => new(404, "File or resource not found.");
}

public static class Input
{
    public static string Name(string value)
    {
        if (value is null)
        {
            throw ApiError.Bad("A name is required.");
        }

        var name = value.Trim().Normalize();
        if (name.Length is < 1 or > 180 || name is "." or ".." ||
            name.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            throw ApiError.Bad("Use a name of 1–180 characters without slashes or control characters.");
        }

        return name;
    }
    public static string Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw ApiError.Bad("A SHA-256 checksum must contain 64 hexadecimal characters.");
        }

        return value.ToLowerInvariant();
    }
    public static string Email(string value)
    {
        if (value is null)
        {
            throw ApiError.Bad("An email is required.");
        }

        var email = value.Trim().ToLowerInvariant();
        if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            throw ApiError.Bad("Enter a valid email address.");
        }

        return email;
    }
}
