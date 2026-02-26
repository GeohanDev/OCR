using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Domain.Entities;
using OcrErpSystem.Domain.Enums;
using OcrErpSystem.Infrastructure.Persistence.Repositories;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me([FromServices] ICurrentUserContext ctx)
    {
        if (ctx.UserId == Guid.Empty)
            return Unauthorized();

        return Ok(new
        {
            id = ctx.UserId,
            username = ctx.Username,
            role = ctx.Role,
            branchId = ctx.BranchId,
        });
    }

    [HttpPost("callback")]
    public async Task<IActionResult> Callback(
        [FromBody] AuthCallbackRequest request,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IConfiguration config,
        [FromServices] UserRepository userRepo,
        CancellationToken ct)
    {
        var baseUrl  = config["Acumatica:BaseUrl"]
                       ?? throw new InvalidOperationException("Acumatica:BaseUrl not configured");
        var clientId = config["Acumatica:ServiceAccount:ClientId"]
                       ?? throw new InvalidOperationException("Acumatica:ServiceAccount:ClientId not configured");

        // Exchange authorization code for tokens using private_key_jwt client authentication.
        var tokenEndpoint = $"{baseUrl}/identity/connect/token";
        var http          = httpClientFactory.CreateClient();
        var tokenRequest  = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]            = "authorization_code",
            ["code"]                  = request.Code,
            ["redirect_uri"]          = request.RedirectUri,
            ["client_id"]             = clientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"]      = BuildClientAssertion(config, clientId, tokenEndpoint),
        });
        tokenRequest.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        var tokenResponse = await http.PostAsync($"{baseUrl}/identity/connect/token", tokenRequest, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var err = await tokenResponse.Content.ReadAsStringAsync(ct);
            return Unauthorized(new { error = "token_exchange_failed", detail = err });
        }

        var tokenData = await tokenResponse.Content.ReadFromJsonAsync<AcumaticaTokenResponse>(cancellationToken: ct);
        if (tokenData?.AccessToken is null)
            return Unauthorized(new { error = "empty_token_response" });

        // Acumatica may issue an opaque (reference) access_token depending on the Connected
        // Application configuration.  The id_token is always a signed JWT in OIDC — use it
        // as the fallback for both claim extraction and as the bearer our backend validates.
        var handler = new JwtSecurityTokenHandler();
        var jwtRaw  = handler.CanReadToken(tokenData.AccessToken)
                          ? tokenData.AccessToken          // JWT access token (preferred)
                          : tokenData.IdToken;             // opaque access token → fall back to id_token

        if (jwtRaw is null || !handler.CanReadToken(jwtRaw))
            return Unauthorized(new { error = "no_jwt_in_token_response" });

        var acuJwt   = handler.ReadJwtToken(jwtRaw);
        var sub      = acuJwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? string.Empty;
        var username = acuJwt.Claims.FirstOrDefault(c =>
                           c.Type == "preferred_username" || c.Type == "unique_name" || c.Type == ClaimTypes.Name)?.Value
                       ?? sub;
        var display  = acuJwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value ?? username;
        var email    = acuJwt.Claims.FirstOrDefault(c => c.Type == "email" || c.Type == ClaimTypes.Email)?.Value;

        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new { error = "no_username_in_token" });

        await userRepo.UpsertAsync(new User
        {
            AcumaticaUserId = sub,
            Username        = username,
            DisplayName     = display,
            Email           = email,
            IsActive        = true,
            LastSyncedAt    = DateTimeOffset.UtcNow,
        }, ct);

        // Send the JWT (access or id_token) as the bearer — our middleware can validate it.
        // Also return the opaque access_token separately in case it is needed for Acumatica API calls.
        return Ok(new
        {
            accessToken       = jwtRaw,
            acumaticaToken    = tokenData.AccessToken,
            refreshToken      = tokenData.RefreshToken,
            expiresIn         = tokenData.ExpiresIn,
        });
    }

    [HttpPost("demo-login")]
    public async Task<IActionResult> DemoLogin(
        [FromServices] IConfiguration config,
        [FromServices] UserRepository userRepo,
        CancellationToken ct)
    {
        if (!config.GetValue<bool>("Demo:Enabled"))
            return NotFound();

        var signingKey = config["Demo:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            return StatusCode(500, "Demo signing key not configured");

        // Ensure demo admin user exists and is active (sync jobs may deactivate it)
        var demoUser = await userRepo.GetByUsernameAsync("demo-admin", ct);
        if (demoUser is null || !demoUser.IsActive)
        {
            var upsert = new User
            {
                AcumaticaUserId = "demo-admin",
                Username        = "demo-admin",
                DisplayName     = "Demo Admin",
                Email           = "demo@example.com",
                Role            = UserRole.Admin,
                IsActive        = true,
            };
            await userRepo.UpsertAsync(upsert, ct);
            demoUser = await userRepo.GetByUsernameAsync("demo-admin", ct);
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim("sub", demoUser!.Id.ToString()),
            new Claim("preferred_username", demoUser.Username),
            new Claim("name", demoUser.DisplayName),
            new Claim("email", demoUser.Email ?? string.Empty),
            new Claim("role", demoUser.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: creds);

        return Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token) });
    }

    // ── private_key_jwt client assertion ────────────────────────────────────────────
    // Builds a short-lived JWT signed with the RSA private key stored in config.
    // Acumatica verifies it against the public key registered in the Connected Application.
    private static string BuildClientAssertion(IConfiguration config, string clientId, string tokenEndpoint)
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus  = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:N"]!),
            Exponent = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:E"]!),
            D        = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:D"]!),
            P        = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:P"]!),
            Q        = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:Q"]!),
            DP       = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:DP"]!),
            DQ       = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:DQ"]!),
            InverseQ = Base64UrlEncoder.DecodeBytes(config["Acumatica:ClientAssertion:QI"]!),
        });

        var now   = DateTime.UtcNow;
        var creds = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);

        var assertion = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("iss", clientId),
                new Claim("sub", clientId),
                new Claim("aud", tokenEndpoint),
                new Claim("jti", Guid.NewGuid().ToString()),
                new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                          ClaimValueTypes.Integer64),
            },
            notBefore:         now,
            expires:           now.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(assertion);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshRequest request,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        var baseUrl  = config["Acumatica:BaseUrl"]
                       ?? throw new InvalidOperationException("Acumatica:BaseUrl not configured");
        var clientId = config["Acumatica:ServiceAccount:ClientId"]
                       ?? throw new InvalidOperationException("Acumatica:ServiceAccount:ClientId not configured");

        var tokenEndpoint = $"{baseUrl}/identity/connect/token";
        var http          = httpClientFactory.CreateClient();
        var form          = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]            = "refresh_token",
            ["refresh_token"]         = request.RefreshToken,
            ["client_id"]             = clientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"]      = BuildClientAssertion(config, clientId, tokenEndpoint),
        });

        var response = await http.PostAsync($"{baseUrl}/identity/connect/token", form, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            return Unauthorized(new { error = "refresh_failed", detail = err });
        }

        var token = await response.Content.ReadFromJsonAsync<AcumaticaTokenResponse>(cancellationToken: ct);
        if (token?.AccessToken is null)
            return Unauthorized(new { error = "empty_refresh_response" });

        return Ok(new
        {
            accessToken  = token.AccessToken,
            refreshToken = token.RefreshToken,
            expiresIn    = token.ExpiresIn,
        });
    }
}

public record AuthCallbackRequest(string Code, string RedirectUri);
public record RefreshRequest(string RefreshToken);

internal sealed class AcumaticaTokenResponse
{
    [JsonPropertyName("access_token")]  public string? AccessToken  { get; set; }
    [JsonPropertyName("id_token")]      public string? IdToken      { get; set; }
    [JsonPropertyName("token_type")]    public string? TokenType    { get; set; }
    [JsonPropertyName("expires_in")]    public int     ExpiresIn    { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
}
