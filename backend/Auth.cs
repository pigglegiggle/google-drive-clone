using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Drive;

public sealed class Auth(Database database, IPasswordHasher<string> hasher)
{
    private static readonly string DummyHash = new PasswordHasher<string>().HashPassword("", "dummy-password-for-timing-only");
    public async Task<object> Register(HttpContext context, Credentials request)
    {
        var email = Input.Email(request.Email);
        if (request.Password is null || request.Password.Length is < 12 or > 128)
        {
            throw ApiError.Bad("Use a password of 12–128 characters.");
        }

        await using var db = await database.Open();
        var id = Guid.NewGuid();
        await db.Execute("INSERT INTO users(id,email,password_hash) VALUES($1,$2,$3)", id, email, hasher.HashPassword(email, request.Password));
        await SignIn(context, id, email);
        return new
        {
            id,
            email
        };
    }

    public async Task<object> Login(HttpContext context, Credentials request)
    {
        var email = Input.Email(request.Email);
        if (request.Password is null || request.Password.Length > 128)
        {
            throw new ApiError(401, "Email or password is incorrect.");
        }

        await using var db = await database.Open();
        var user = await db.One("SELECT * FROM users WHERE email=$1", email);
        var result = hasher.VerifyHashedPassword(email, user?.Text("password_hash") ?? DummyHash, request.Password);
        if (user is null || result == PasswordVerificationResult.Failed)
        {
            throw new ApiError(401, "Email or password is incorrect.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await db.Execute("UPDATE users SET password_hash=$2 WHERE id=$1", user.Id("id"), hasher.HashPassword(email, request.Password));
        }

        await SignIn(context, user.Id("id"), email);
        return new
        {
            id = user.Id("id"),
            email
        };
    }

    private static Task SignIn(HttpContext context, Guid id, string email) => context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Email, email)
        ], CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });

    public static Guid User(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public record Credentials(string Email, string Password);
