using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OcrSystem.Application.Auth;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.ERP;

namespace OcrSystem.Infrastructure.ERP;

public class AcumaticaClient : IErpIntegrationService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<AcumaticaClient> _logger;
    private readonly IAcumaticaTokenContext _tokenContext;
    private static string? _serviceAccountToken;
    private static DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AcumaticaClient(HttpClient http, IConfiguration config, ILogger<AcumaticaClient> logger, IAcumaticaTokenContext tokenContext)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _tokenContext = tokenContext;
    }

    private string BaseUrl => _config["Acumatica:BaseUrl"] ?? throw new InvalidOperationException("Acumatica:BaseUrl not configured");
    private string ApiVersion => _config["Acumatica:ApiVersion"] ?? "24.200.001";
    private string ApiEndpoint => _config["Acumatica:ApiEndpoint"] ?? "Default";
    // OData company code — may differ from ApiEndpoint. Defaults to ApiEndpoint if not set.
    private string ODataCompany => _config["Acumatica:ODataCompany"] ?? ApiEndpoint;

    private async Task<string> GetServiceAccountTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_serviceAccountToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _serviceAccountToken;

            var clientId      = _config["Acumatica:ServiceAccount:ClientId"]
                                ?? throw new InvalidOperationException("Acumatica:ServiceAccount:ClientId not configured");
            var clientSecret  = _config["Acumatica:ServiceAccount:ClientSecret"];
            var tokenEndpoint = $"{BaseUrl}/identity/connect/token";

            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"]  = clientId,
                ["scope"]      = "api",
            };

            // Prefer client_secret_post when a secret is configured (matches user-login flow).
            // Fall back to private_key_jwt when no secret is present.
            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                fields["client_secret"] = clientSecret;
            }
            else
            {
                fields["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                fields["client_assertion"]      = BuildServiceAssertion(clientId, tokenEndpoint);
            }

            var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(fields),
            };

            _logger.LogInformation("Service-account token request: endpoint={Endpoint} grant=client_credentials client_id={ClientId} using={Method}",
                tokenEndpoint, clientId, string.IsNullOrWhiteSpace(clientSecret) ? "private_key_jwt" : "client_secret_post");

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Acumatica token endpoint returned {Status}: {Body}", (int)response.StatusCode, errBody);
                response.EnsureSuccessStatusCode();
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);
            _serviceAccountToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return _serviceAccountToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private string BuildServiceAssertion(string clientId, string tokenEndpoint)
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus  = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:N"]!),
            Exponent = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:E"]!),
            D        = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:D"]!),
            P        = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:P"]!),
            Q        = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:Q"]!),
            DP       = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:DP"]!),
            DQ       = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:DQ"]!),
            InverseQ = Base64UrlEncoder.DecodeBytes(_config["Acumatica:ClientAssertion:QI"]!),
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
            notBefore: now, expires: now.AddMinutes(5), signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(assertion);
    }

    private async Task SetAuthHeaderAsync(CancellationToken ct)
    {
        string token;
        if (!string.IsNullOrWhiteSpace(_tokenContext.ForwardedToken))
        {
            if (_tokenContext.SessionTimedOut)
                throw new AcumaticaAuthException(
                    "Acumatica session has expired due to inactivity. Please sign in again.");

            _logger.LogInformation("ERP call: using forwarded user Acumatica token");
            token = _tokenContext.ForwardedToken;
        }
        else
        {
            _logger.LogInformation("ERP call: no forwarded token — falling back to service-account");
            token = await GetServiceAccountTokenAsync(ct);
        }
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Throws AcumaticaAuthException when the response is 401 or 403,
    /// indicating the forwarded token is expired or invalid.
    /// Call this immediately after every HTTP response in API methods.
    /// </summary>
    private static void ThrowIfAuthFailure(HttpResponseMessage response, string context)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                 or System.Net.HttpStatusCode.Forbidden)
            throw new AcumaticaAuthException(
                $"Acumatica session expired during {context} (HTTP {(int)response.StatusCode})");
    }

    private void ThrowIfHtmlResponse(string body, string context)
    {
        if (body.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            _tokenContext.SessionTimedOut = true;
            throw new AcumaticaAuthException(
                $"Acumatica returned an HTML page instead of JSON during {context} — the session has expired. Please sign in again.");
        }
    }

    public async Task<(int Status, string Body)> RawGetAsync(string url, CancellationToken ct = default)
    {
        await SetAuthHeaderAsync(ct);
        var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var preview = body.Length > 1000 ? body[..1000] + "…" : body;
        return ((int)response.StatusCode, preview);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Vendor?$top=1";
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "Ping");
            return response.IsSuccessStatusCode;
        }
        catch (AcumaticaAuthException) { throw; }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogWarning(ex, "Acumatica ping failed");
            return false;
        }
    }

    public async Task<ErpLookupResult<VendorDto>> LookupVendorAsync(string vendorId, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Vendor/{Uri.EscapeDataString(vendorId)}?$select=VendorID,VendorName,Status";
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "LookupVendor");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new ErpLookupResult<VendorDto>(false, null, "Vendor not found");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var vendor = new VendorDto(
                doc.RootElement.GetProperty("VendorID").GetProperty("value").GetString()!,
                doc.RootElement.GetProperty("VendorName").GetProperty("value").GetString()!,
                doc.RootElement.GetProperty("Status").GetProperty("value").GetString() == "Active");
            return new ErpLookupResult<VendorDto>(true, vendor, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP vendor lookup failed for {VendorId}", vendorId);
            return new ErpLookupResult<VendorDto>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<VendorDto>> LookupVendorByNameAsync(string vendorName, CancellationToken ct = default)
    {
        try
        {
            var all = await GetAllVendorsAsync(ct: ct);
            // Normalize internal whitespace and common suffix variants so
            // "ABC  SDN BHD" / "ABC SDNBHD" / "ABC SDN.BHD" all match each other.
            static string Norm(string s)
            {
                var n = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                // "SDNBHD" (no separator) → "SDN BHD"
                n = System.Text.RegularExpressions.Regex.Replace(n, @"\bSDNBHD\b", "SDN BHD", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // "SDN.BHD" (dot separator) → "SDN BHD"
                n = System.Text.RegularExpressions.Regex.Replace(n, @"\bSDN\.BHD\b", "SDN BHD", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return n;
            }
            var normalizedInput = Norm(vendorName.Trim());
            var match = all.FirstOrDefault(v =>
                string.Equals(Norm(v.VendorName), normalizedInput, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // If the vendor list came back empty it most likely means the ERP call failed
                // internally (GetAllVendorsAsync swallows exceptions and returns []). Signal this
                // with a non-null ErrorMessage so callers can return Warning instead of Failed.
                if (all.Count == 0 && string.IsNullOrWhiteSpace(_tokenContext.ForwardedToken))
                    return new ErpLookupResult<VendorDto>(false, null, "ERP connection failed — no Acumatica session available");
                // Genuine not-found: ErrorMessage = null so the caller knows to report Failed.
                return new ErpLookupResult<VendorDto>(false, null, null);
            }
            return new ErpLookupResult<VendorDto>(true, match, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP vendor name lookup failed for {VendorName}", vendorName);
            return new ErpLookupResult<VendorDto>(false, null, ex.Message);
        }
    }


    public async Task<IReadOnlyList<VendorDto>> GetAllVendorsAsync(int? top = null, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var topClause = top.HasValue ? $"&$top={top.Value}" : string.Empty;
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Vendor?$select=VendorID,VendorName,Status{topClause}";
            var response = await _http.GetAsync(url, ct);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Acumatica auth failure fetching vendors: HTTP {Status}", (int)response.StatusCode);
                throw new AcumaticaAuthException($"Acumatica returned HTTP {(int)response.StatusCode} — session expired or token invalid");
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            var vendors = new List<VendorDto>();
            foreach (var v in arr.EnumerateArray())
                vendors.Add(new VendorDto(
                    v.GetProperty("VendorID").GetProperty("value").GetString()!,
                    v.GetProperty("VendorName").GetProperty("value").GetString()!,
                    v.GetProperty("Status").GetProperty("value").GetString() == "Active"));
            return vendors;
        }
        catch (AcumaticaAuthException)
        {
            throw; // let auth errors propagate so callers can return 424
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Failed to fetch all vendors from Acumatica");
            return [];
        }
    }

    public async Task<IReadOnlyList<VendorFullDto>> GetAllVendorsFullAsync(CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);

            // Use the same minimal URL that the working lookup endpoint uses.
            // $expand=MainAddress causes a 500 on some Acumatica versions — avoid it.
            // Address and terms are fetched per-vendor only when the batch succeeds.
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Vendor?$select=VendorID,VendorName,Status";
            var response = await _http.GetAsync(url, ct);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Acumatica auth failure fetching full vendors: HTTP {Status}", (int)response.StatusCode);
                throw new AcumaticaAuthException($"Acumatica returned HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Acumatica vendor fetch returned {Status}: {Body}", (int)response.StatusCode, errBody);
                response.EnsureSuccessStatusCode();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            var vendors = new List<VendorFullDto>();

            foreach (var v in arr.EnumerateArray())
            {
                static string Str(JsonElement el, string key) =>
                    el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var val)
                        ? val.GetString() ?? "" : "";

                vendors.Add(new VendorFullDto(
                    VendorId:     Str(v, "VendorID"),
                    VendorName:   Str(v, "VendorName"),
                    IsActive:     Str(v, "Status") == "Active",
                    AddressLine1: null,
                    AddressLine2: null,
                    City:         null,
                    State:        null,
                    PostalCode:   null,
                    Country:      null,
                    PaymentTerms: null));
            }
            _logger.LogInformation("Fetched {Count} vendors from Acumatica", vendors.Count);
            return vendors;
        }
        catch (AcumaticaAuthException) { throw; }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Failed to fetch full vendor list from Acumatica");
            return [];
        }
    }

    public async Task<IReadOnlyList<OpenBillDto>> FetchOpenBillsForVendorAsync(string vendorId, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            // Filter: Vendor eq vendorId and Status ne 'Closed' and Status ne 'Voided'
            // Acumatica Bill.Vendor field stores the VendorID string.
            var filter = Uri.EscapeDataString($"Vendor eq '{vendorId}' and Status ne 'Closed' and Status ne 'Voided'");
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filter}&$select=ReferenceNbr,VendorRef,Balance,Amount,DueDate,Status";
            _logger.LogInformation("FetchOpenBills for vendor {VendorId} → {Url}", vendorId, url);
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "FetchOpenBills");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            var bills = new List<OpenBillDto>();

            foreach (var b in arr.EnumerateArray())
            {
                static string Str(JsonElement el, string key) =>
                    el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                static decimal Dec(JsonElement el, string key) =>
                    el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) &&
                    v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
                static DateTimeOffset? Dt(JsonElement el, string key)
                {
                    if (!el.TryGetProperty(key, out var p) || !p.TryGetProperty("value", out var v)) return null;
                    var s = v.GetString();
                    return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
                }

                bills.Add(new OpenBillDto(
                    ReferenceNbr: Str(b, "ReferenceNbr"),
                    VendorRef:    Str(b, "VendorRef"),
                    Balance:      Dec(b, "Balance"),
                    Amount:       Dec(b, "Amount"),
                    DueDate:      Dt(b, "DueDate"),
                    Status:       Str(b, "Status")));
            }
            return bills;
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "FetchOpenBills failed for vendor {VendorId}", vendorId);
            return [];
        }
    }

    public async Task<IReadOnlyList<OpenBillDto>> FetchAllOpenBillsForAgingAsync(CancellationToken ct = default)
    {
        // Acumatica enforces a server-side page-size cap (typically 10 000).
        // Use $skip-based pagination so large datasets are fetched in safe chunks.
        const int PageSize = 5000;
        var filter  = Uri.EscapeDataString("Status ne 'Closed' and Status ne 'Voided' and Status ne 'Canceled'");
        var baseUrl = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filter}&$select=Vendor,Balance,DueDate&$top={PageSize}";

        var bills = new List<OpenBillDto>();
        int skip  = 0;

        while (true)
        {
            var url = skip == 0 ? baseUrl : $"{baseUrl}&$skip={skip}";
            _logger.LogInformation("FetchAllOpenBillsForAging → {Url}", url);
            try
            {
                await SetAuthHeaderAsync(ct);
                var response = await _http.GetAsync(url, ct);
                ThrowIfAuthFailure(response, "FetchAllOpenBillsForAging");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var arr  = JsonDocument.Parse(json).RootElement;

                int pageCount = 0;
                foreach (var b in arr.EnumerateArray())
                {
                    static string Str(JsonElement el, string key) =>
                        el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                    static decimal Dec(JsonElement el, string key) =>
                        el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) &&
                        v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
                    static DateTimeOffset? Dt(JsonElement el, string key)
                    {
                        if (!el.TryGetProperty(key, out var p) || !p.TryGetProperty("value", out var v)) return null;
                        var s = v.GetString();
                        return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
                    }

                    bills.Add(new OpenBillDto(
                        ReferenceNbr: "",
                        VendorRef:    "",
                        Balance:      Dec(b, "Balance"),
                        Amount:       0m,
                        DueDate:      Dt(b, "DueDate"),
                        Status:       "",
                        VendorId:     Str(b, "Vendor")));
                    pageCount++;
                }

                _logger.LogInformation("FetchAllOpenBillsForAging page skip={Skip} returned {Count} bills (total so far: {Total})", skip, pageCount, bills.Count);

                // Fewer records than page size → last page reached.
                if (pageCount < PageSize) break;

                skip += PageSize;
            }
            catch (AcumaticaAuthException) { throw; }
            catch (Exception ex) when (ex is not AcumaticaAuthException)
            {
                _logger.LogError(ex, "FetchAllOpenBillsForAging failed at skip={Skip} (collected {Count} bills so far)", skip, bills.Count);
                break; // Return whatever was collected before the failure
            }
        }

        _logger.LogInformation("FetchAllOpenBillsForAging completed: {Count} open bills total", bills.Count);
        return bills;
    }

    public async Task<ErpLookupResult<ApInvoiceDto>> LookupApInvoiceAsync(string invoiceNbr, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            // Acumatica 23.x+ exposes AP invoices as "Bill" (not "APInvoice").
            // Field names: ReferenceNbr (internal ref), VendorRef (vendor's ref), Vendor (VendorID).
            var filter = Uri.EscapeDataString($"ReferenceNbr eq '{invoiceNbr}'");
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filter}&$top=1";
            _logger.LogInformation("AP Invoice lookup → {Url}", url);
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "LookupApInvoice");
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("AP Invoice lookup {InvoiceNbr} → {Status}: {Err}", invoiceNbr, (int)response.StatusCode, errBody);
                return new ErpLookupResult<ApInvoiceDto>(false, null, $"ERP returned HTTP {(int)response.StatusCode}");
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<ApInvoiceDto>(false, null, "Invoice not found");
            var first = arr[0];

            static string Str(JsonElement el, string key) =>
                el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            static decimal Dec(JsonElement el, string key) =>
                el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

            var invoice = new ApInvoiceDto(
                Str(first, "ReferenceNbr"),
                Str(first, "Vendor"),
                Str(first, "Date"),
                Dec(first, "Amount"),
                Str(first, "Status"),
                Str(first, "Type"));
            return new ErpLookupResult<ApInvoiceDto>(true, invoice, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP AP invoice lookup failed for {InvoiceNbr}", invoiceNbr);
            return new ErpLookupResult<ApInvoiceDto>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<ApInvoiceDto>> LookupApInvoiceByVendorAsync(string vendorRef, string vendorName, CancellationToken ct = default)
    {
        try
        {
            static string Str(JsonElement el, string key) =>
                el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            static decimal Dec(JsonElement el, string key) =>
                el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

            // Step 1: resolve VendorID from vendor name.
            // Bill records only return VendorID — VendorName is not available in the Bill response.
            var vendorResult = await LookupVendorByNameAsync(vendorName, ct);
            if (!vendorResult.Found)
                return new ErpLookupResult<ApInvoiceDto>(false, null,
                    $"Vendor '{vendorName}' not found in Acumatica — cannot verify invoice ownership.");

            var resolvedVendorId = vendorResult.Data!.VendorId;

            // Step 2: query Bill filtered by VendorRef (tolower for case-insensitive, exact fallback).
            await SetAuthHeaderAsync(ct);
            var normalizedRef = vendorRef.Trim();
            var filterCI    = Uri.EscapeDataString($"tolower(VendorRef) eq '{normalizedRef.ToLowerInvariant()}'");
            var filterExact = Uri.EscapeDataString($"VendorRef eq '{normalizedRef}'");

            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filterCI}&$top=10";
            _logger.LogInformation("AP Invoice+Vendor lookup (VendorID={VendorId}) → {Url}", resolvedVendorId, url);
            var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filterExact}&$top=10";
                _logger.LogDebug("tolower() unsupported for VendorRef — retrying exact: {Url}", url);
                response = await _http.GetAsync(url, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("AP Invoice+Vendor lookup {VendorRef} → {Status}: {Err}",
                    vendorRef, (int)response.StatusCode, errBody);
                return new ErpLookupResult<ApInvoiceDto>(false, null, $"ERP returned HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<ApInvoiceDto>(false, null,
                    $"Invoice '{vendorRef}' not found in Acumatica.");

            // Step 3: filter results by Vendor (VendorID) field — case-insensitive.
            // Acumatica Bill endpoint returns "Vendor" (not "VendorID"), "ReferenceNbr" (not "RefNbr"),
            // "Date" (not "DocDate"), "Type" (not "DocType"), "Amount" (not "CuryOrigDocAmt").
            JsonElement? match = null;
            foreach (var item in arr.EnumerateArray())
            {
                var billVendorId = Str(item, "Vendor");
                if (string.Equals(billVendorId, resolvedVendorId, StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
            }

            if (match is null)
            {
                _logger.LogInformation(
                    "Invoice '{VendorRef}' found but Vendor field does not match '{ResolvedVendorId}'",
                    vendorRef, resolvedVendorId);
                return new ErpLookupResult<ApInvoiceDto>(false, null,
                    $"Invoice '{vendorRef}' found in Acumatica but does not belong to vendor '{vendorName}'.");
            }

            var invoice = new ApInvoiceDto(
                Str(match.Value, "ReferenceNbr"),
                Str(match.Value, "Vendor"),
                Str(match.Value, "Date"),
                Dec(match.Value, "Amount"),
                Str(match.Value, "Status"),
                Str(match.Value, "Type"));
            return new ErpLookupResult<ApInvoiceDto>(true, invoice, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP AP invoice+vendor lookup failed for {VendorRef}/{VendorName}", vendorRef, vendorName);
            return new ErpLookupResult<ApInvoiceDto>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> LookupBillByVendorRefAndVendorIdAsync(
        string vendorRef, string vendorId, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var normalizedRef = vendorRef.Trim();

            // Preferred: filter by both VendorRef AND Vendor in the OData query so only
            // the correct vendor's bill is returned (avoids in-memory matching over $top results).
            var filterCIWithVendor    = Uri.EscapeDataString($"tolower(VendorRef) eq '{normalizedRef.ToLowerInvariant()}' and Vendor eq '{vendorId}'");
            var filterExactWithVendor = Uri.EscapeDataString($"VendorRef eq '{normalizedRef}' and Vendor eq '{vendorId}'");
            var filterCI    = Uri.EscapeDataString($"tolower(VendorRef) eq '{normalizedRef.ToLowerInvariant()}'");
            var filterExact = Uri.EscapeDataString($"VendorRef eq '{normalizedRef}'");

            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filterCIWithVendor}&$top=1";
            _logger.LogInformation("Bill+VendorId lookup (VendorID={VendorId}) → {Url}", vendorId, url);
            var response = await _http.GetAsync(url, ct);

            // If the combined filter fails (Acumatica may not support nested Vendor field filter),
            // fall back to VendorRef-only filter with a larger $top and do in-memory vendor matching.
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filterExactWithVendor}&$top=1";
                _logger.LogDebug("tolower(VendorRef)+Vendor filter failed — retrying exact+vendor: {Url}", url);
                response = await _http.GetAsync(url, ct);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filterCI}&$top=50";
                _logger.LogDebug("Combined filter unsupported — falling back to VendorRef-only filter: {Url}", url);
                response = await _http.GetAsync(url, ct);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Bill?$filter={filterExact}&$top=50";
                _logger.LogDebug("tolower() unsupported — retrying exact VendorRef-only: {Url}", url);
                response = await _http.GetAsync(url, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Bill+VendorId lookup {VendorRef}/{VendorId} → {Status}: {Err}",
                    vendorRef, vendorId, (int)response.StatusCode, errBody);
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"ERP returned HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var arr  = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"No Bill found where VendorRef='{vendorRef}'");

            // Match by Vendor (VendorID) field — case-insensitive.
            JsonElement? match = null;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("Vendor", out var vProp) &&
                    vProp.TryGetProperty("value", out var vVal) &&
                    string.Equals(vVal.GetString(), vendorId, StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
            }

            if (match is null)
            {
                _logger.LogInformation(
                    "Bill VendorRef='{VendorRef}' found but no entry matches Vendor='{VendorId}'",
                    vendorRef, vendorId);
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"Bill '{vendorRef}' found but does not belong to vendor ID '{vendorId}'");
            }

            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in match.Value.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("value", out var val))
                    record[prop.Name] = val.ToString();
            }
            _logger.LogInformation("Bill+VendorId matched — Amount={Amount}", record.GetValueOrDefault("Amount"));
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, record, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Bill+VendorId lookup failed: VendorRef={VendorRef} VendorId={VendorId}", vendorRef, vendorId);
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<CurrencyDto>> LookupCurrencyAsync(string currencyCode, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/Currency/{Uri.EscapeDataString(currencyCode)}";
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "LookupCurrency");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new ErpLookupResult<CurrencyDto>(false, null, "Currency not found");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var currency = new CurrencyDto(
                doc.RootElement.GetProperty("CurrencyID").GetProperty("value").GetString()!,
                doc.RootElement.TryGetProperty("Description", out var desc)
                    ? desc.GetProperty("value").GetString() ?? "" : "",
                true);
            return new ErpLookupResult<CurrencyDto>(true, currency, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP currency lookup failed for {CurrencyCode}", currencyCode);
            return new ErpLookupResult<CurrencyDto>(false, null, ex.Message);
        }
    }

    // Acumatica entity name for branches — varies by version. Discovered via OData service doc.
    private static string? _resolvedBranchEntity;
    // All available entity names from the OData service document, cached per process.
    private static IReadOnlyList<string>? _cachedODataEntities;

    /// <summary>
    /// Queries /entity/{endpoint}/{version}/ — the OData service document — which returns
    /// a JSON array of all available entity names for this Acumatica instance and version.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableODataEntitiesAsync(CancellationToken ct = default)
    {
        // Don't use the cache here — let every explicit call hit Acumatica fresh so the test
        // page always reflects the current state. The branch resolution path still benefits from
        // the first-call cache in ResolveBranchEntityAsync.
        // (Cache is only populated for internal resolution calls, not for the diagnostic endpoint.)

        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/";
            _logger.LogInformation("Querying OData service document: {Url}", url);
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OData service document returned {Status}", (int)response.StatusCode);
                return [];
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("OData service document raw response (first 500 chars): {Json}",
                json.Length > 500 ? json[..500] : json);

            var doc  = JsonDocument.Parse(json);
            var names = new List<string>();

            // Format 1: Standard OData — { "value": [{ "name": "X", "url": "X" }, ...] }
            if (doc.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in values.EnumerateArray())
                {
                    // Try "name" first, then "Name", then "EntityName", then "entityName"
                    string? name = null;
                    foreach (var key in new[] { "name", "Name", "EntityName", "entityName", "entity", "Entity" })
                        if (item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                        { name = p.GetString(); break; }

                    // If item is a plain string
                    if (name is null && item.ValueKind == JsonValueKind.String)
                        name = item.GetString();

                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
                }
            }
            // Format 2: Root is an array of strings or objects
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        names.Add(item.GetString()!);
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var key in new[] { "name", "Name", "EntityName", "entityName" })
                            if (item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                            { names.Add(p.GetString()!); break; }
                    }
                }
            }
            // Format 3: Flat object whose property names are entity names (e.g. { "Vendor": {...}, "Bill": {...} })
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Walk all top-level properties — if they look like entity names (Pascal-case, no spaces), collect them
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var n = prop.Name;
                    if (!n.StartsWith('@') && !n.StartsWith('$') && n.Length > 1)
                        names.Add(n);
                }
            }

            _cachedODataEntities = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            _logger.LogInformation("OData service document parsed {Count} entity names: {Names}",
                _cachedODataEntities.Count,
                string.Join(", ", _cachedODataEntities.Take(20)));
            return _cachedODataEntities;
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Failed to query OData service document");
        }
        return [];
    }

    /// <summary>Returns the raw Acumatica OData service document response for debugging.</summary>
    public async Task<string> GetODataServiceDocumentRawAsync(CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/";
            var response = await _http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return $"HTTP {(int)response.StatusCode} {response.StatusCode}\nContent-Type: {response.Content.Headers.ContentType}\n\n{body}";
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ResolveBranchEntityAsync(CancellationToken ct)
    {
        if (_resolvedBranchEntity is not null) return _resolvedBranchEntity;

        // First: try to discover the entity name from the OData service document.
        var available = await GetAvailableODataEntitiesAsync(ct);
        if (available.Count > 0)
        {
            // Look for "Company" first, then any entity whose name contains "branch".
            var found = available.FirstOrDefault(n => n.Equals("Company", StringComparison.OrdinalIgnoreCase))
                     ?? available.FirstOrDefault(n => n.Contains("branch", StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                _resolvedBranchEntity = found;
                _logger.LogInformation("Resolved branch entity from service doc: {Entity}", found);
                return found;
            }
            _logger.LogWarning("No branch-related entity found in OData service document. Available: {Entities}",
                string.Join(", ", available));
        }

        // Fallback: probe common names directly.
        foreach (var candidate in new[] { "Company", "Branch", "GLBranch", "CompanyBranch", "Organization" })
        {
            var probe = await _http.GetAsync($"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{candidate}?$top=1", ct);
            if (probe.IsSuccessStatusCode)
            {
                _resolvedBranchEntity = candidate;
                _logger.LogInformation("Resolved branch entity via probe: {Entity}", candidate);
                return candidate;
            }
        }

        _resolvedBranchEntity = "Company"; // last resort — error will surface in the validator
        _logger.LogWarning("Could not resolve branch entity name — defaulting to 'Branch'");
        return _resolvedBranchEntity;
    }

    public async Task<ErpLookupResult<BranchDto>> LookupBranchAsync(string branchCode, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);

            var entity = await ResolveBranchEntityAsync(ct);

            // BranchID is always stored uppercase in Acumatica — normalise OCR input to uppercase
            // for a fast exact-match OData filter (avoids expensive tolower() table scans).
            var upperCode = branchCode.Trim().ToUpperInvariant();
            var escaped   = Uri.EscapeDataString(upperCode);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{entity}?$filter=BranchID eq '{escaped}'";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;

            if (arr.GetArrayLength() == 0)
            {
                // Fallback: try matching by BranchName
                var escapedName = Uri.EscapeDataString(branchCode.Trim());
                var fallbackUrl = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{entity}?$filter=BranchName eq '{escapedName}'";
                var fb = await _http.GetAsync(fallbackUrl, ct);
                fb.EnsureSuccessStatusCode();
                var fbJson = await fb.Content.ReadAsStringAsync(ct);
                var fbArr = JsonDocument.Parse(fbJson).RootElement;
                if (fbArr.GetArrayLength() == 0)
                    return new ErpLookupResult<BranchDto>(false, null, "Branch not found");
                arr = fbArr;
            }

            var first = arr[0];
            var branchId = first.GetProperty("BranchID").GetProperty("value").GetString()!;
            var isActive = !first.TryGetProperty("Active", out var active)
                || active.GetProperty("value").GetBoolean();
            var branch = new BranchDto(branchId, branchId,
                first.GetProperty("BranchName").GetProperty("value").GetString()!,
                isActive);
            return new ErpLookupResult<BranchDto>(true, branch, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP branch lookup failed for {BranchCode}", branchCode);
            return new ErpLookupResult<BranchDto>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<PurchaseOrderDto>> LookupPurchaseOrderAsync(string poNumber, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/PurchaseOrder/{Uri.EscapeDataString(poNumber)}";
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "LookupPurchaseOrder");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new ErpLookupResult<PurchaseOrderDto>(false, null, "PO not found");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var po = new PurchaseOrderDto(
                doc.RootElement.GetProperty("OrderNbr").GetProperty("value").GetString()!,
                doc.RootElement.GetProperty("VendorID").GetProperty("value").GetString()!,
                doc.RootElement.GetProperty("OrderTotal").GetProperty("value").GetDecimal(),
                doc.RootElement.GetProperty("Status").GetProperty("value").GetString()!);
            return new ErpLookupResult<PurchaseOrderDto>(true, po, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "ERP PO lookup failed for {PoNumber}", poNumber);
            return new ErpLookupResult<PurchaseOrderDto>(false, null, ex.Message);
        }
    }

    // ── Entity catalog ────────────────────────────────────────────────────────
    // Hardcoded list of known Acumatica entities + their filterable OData fields.
    // Add new entries here as the integration grows.
    private static readonly IReadOnlyList<ErpEntityDto> _entityCatalog =
    [
        new("Vendor",         "Vendor",          ["VendorID", "VendorName", "Status", "CurrencyID", "TaxRegistrationID"]),
        new("Customer",       "Customer",        ["CustomerID", "CustomerName", "Status", "CurrencyID"]),
        new("Bill",           "AP Invoice (Bill)", ["ReferenceNbr", "VendorRef", "Vendor", "Date", "DueDate", "Status", "Type", "Amount", "Balance", "TaxTotal", "CurrencyID", "BranchID", "LocationID", "Description", "Terms", "PostPeriod", "Project", "ApprovedForPayment", "Hold"]),
        new("SOInvoice",      "Sales Invoice",   ["RefNbr", "CustomerID", "CustomerName", "DocDate", "Status", "DocType"]),
        new("PurchaseOrder",  "Purchase Order",  ["OrderNbr", "VendorID", "VendorName", "Status", "CuryID", "CuryOrderTotal"]),
        new("Currency",       "Currency",        ["CurrencyID", "Description"]),
        new("Company",        "Company",          ["BranchID", "BranchName"]),
        new("InventoryItem",  "Inventory Item",  ["InventoryCD", "Descr", "ItemStatus", "ItemClass"]),
    ];

    public IReadOnlyList<ErpEntityDto> GetEntityCatalog() => _entityCatalog;

    // Entity name aliases — normalises legacy/renamed entity names stored in field configs.
    // APInvoice was renamed to Bill in Acumatica 23.x.
    private static readonly Dictionary<string, string> _entityAliases =
        new(StringComparer.OrdinalIgnoreCase) { ["APInvoice"] = "Bill" };

    public async Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> LookupGenericAsync(
        string entity, string field, string value, CancellationToken ct = default)
    {
        // Apply alias so configs with "APInvoice:VendorRef" still work after the rename.
        if (_entityAliases.TryGetValue(entity, out var aliasedEntity))
        {
            _logger.LogInformation("Entity alias: {Old} → {New}", entity, aliasedEntity);
            entity = aliasedEntity;
        }

        // Resolve the actual branch entity name (Branch vs GLBranch depends on Acumatica version).
        if (entity.Equals("Branch", StringComparison.OrdinalIgnoreCase) ||
            entity.Equals("GLBranch", StringComparison.OrdinalIgnoreCase))
        {
            entity = await ResolveBranchEntityAsync(ct);
        }

        try
        {
            await SetAuthHeaderAsync(ct);
            // Normalise the extracted value: trim whitespace, then try case-insensitive match.
            // Some Acumatica versions don't support tolower() on all field types, so fall back
            // to an exact eq filter when the server returns 400/500.
            // $select is intentionally omitted — restricting to a single field can prevent
            // Acumatica from returning filterable results for complex/linked fields like VendorRef.
            var normalizedValue = value.Trim();
            var filterCI    = Uri.EscapeDataString($"tolower({field}) eq '{normalizedValue.ToLowerInvariant()}'");
            var filterExact = Uri.EscapeDataString($"{field} eq '{normalizedValue}'");
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{entity}?$filter={filterCI}&$top=1";
            _logger.LogInformation("Generic ERP lookup → {Url}", url);
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, $"LookupGeneric({entity}.{field})");

            // Acumatica returns 400 or 500 when tolower() is unsupported — retry with exact match.
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                _logger.LogDebug("tolower() not supported for {Entity}.{Field} — retrying with exact match", entity, field);
                url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{entity}?$filter={filterExact}&$top=1";
                _logger.LogInformation("Generic ERP lookup (exact) → {Url}", url);
                response = await _http.GetAsync(url, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                // Extract human-readable message from Acumatica JSON error body if available.
                string acuMsg;
                try
                {
                    var errDoc = JsonDocument.Parse(errBody);
                    acuMsg = errDoc.RootElement.TryGetProperty("exceptionMessage", out var em) ? em.GetString()!
                           : errDoc.RootElement.TryGetProperty("message", out var m) ? m.GetString()!
                           : $"HTTP {(int)response.StatusCode}";
                }
                catch { acuMsg = $"HTTP {(int)response.StatusCode}"; }

                _logger.LogWarning("Generic ERP lookup {Entity}.{Field}='{Value}' → {Status}: {Err}",
                    entity, field, normalizedValue, (int)response.StatusCode, errBody);
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"Acumatica error: {acuMsg}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    "Acumatica returned an empty response (session may have expired)");
            ThrowIfHtmlResponse(json, $"LookupGeneric({entity}.{field})");
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"No {entity} record found where {field} = \"{normalizedValue}\"");

            // Flatten the first record: each Acumatica field is { "value": "..." }
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in arr[0].EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("value", out var val))
                    record[prop.Name] = val.ToString();
            }
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, record, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Generic ERP lookup failed: {Entity}.{Field}='{Value}'", entity, field, value.Trim());
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> ProbeEntityAsync(
        string entity, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            // Resolve actual entity name for branch (version-dependent).
            if (entity.Equals("Branch", StringComparison.OrdinalIgnoreCase) ||
                entity.Equals("GLBranch", StringComparison.OrdinalIgnoreCase))
                entity = await ResolveBranchEntityAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{entity}?$top=1";
            _logger.LogInformation("Probe entity → {Url}", url);
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Probe {Entity} → {Status}: {Err}", entity, (int)response.StatusCode, errBody);
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"HTTP {(int)response.StatusCode}: {errBody}");
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    "Acumatica returned an empty response (session may have expired)");
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    "Entity exists but returned 0 records");

            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in arr[0].EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("value", out var val))
                    record[prop.Name] = val.ToString();
            }
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, record, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Probe entity failed: {Entity}", entity);
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> LookupVendorBalanceAsync(
        string vendorId, string period, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            // APHistory stores AP-side period balances. FinPeriodID format: YYYYMM (e.g. "202501").
            var filter = Uri.EscapeDataString($"VendorID eq '{vendorId}' and FinPeriodID eq '{period}'");
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/APHistory?$filter={filter}";
            _logger.LogInformation("LookupVendorBalance VendorID={VendorId} Period={Period} → {Url}", vendorId, period, url);
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "LookupVendorBalance");

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("LookupVendorBalance {Status}: {Err}", (int)response.StatusCode, errBody);
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"HTTP {(int)response.StatusCode}: {errBody}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"No APHistory record found for VendorID='{vendorId}' and FinPeriodID='{period}'.");

            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in arr[0].EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("value", out var val))
                    record[prop.Name] = val.ToString();
            }
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, record, null);
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "LookupVendorBalance failed for VendorID={VendorId} Period={Period}", vendorId, period);
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null, ex.Message);
        }
    }

    public async Task<ErpPushResult> PushDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        _logger.LogInformation("Pushing document {DocumentId} to Acumatica", documentId);
        // Feature-flagged stub: implement AP Bill creation or equivalent endpoint
        await Task.Delay(50, ct);
        return new ErpPushResult(true, $"ERP-{documentId:N}", null);
    }

    public async Task<IReadOnlyList<AcumaticaUserDto>> FetchAllUsersAsync(CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            // Users live under the UserManagement endpoint (not Default entity).
            // Acumatica 25.1 exposes: GET /entity/UserManagement/25.100.001/Users
            var url = $"{BaseUrl}/entity/UserManagement/{ApiVersion}/Users?$select=Username,FullName,Email,Roles,DefaultBranchID";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            var users = new List<AcumaticaUserDto>();
            foreach (var u in arr.EnumerateArray())
            {
                var roles = new List<string>();
                if (u.TryGetProperty("Roles", out var rolesEl))
                    foreach (var r in rolesEl.EnumerateArray())
                        if (r.TryGetProperty("Rolename", out var rn))
                            roles.Add(rn.GetProperty("value").GetString() ?? "");
                users.Add(new AcumaticaUserDto(
                    u.GetProperty("Username").GetProperty("value").GetString()!,
                    u.GetProperty("Username").GetProperty("value").GetString()!,
                    u.TryGetProperty("FullName", out var fn) ? fn.GetProperty("value").GetString() ?? "" : "",
                    u.TryGetProperty("Email", out var em) ? em.GetProperty("value").GetString() : null,
                    roles,
                    u.TryGetProperty("DefaultBranchID", out var br) ? br.GetProperty("value").GetString() : null));
            }
            return users;
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Failed to fetch users from Acumatica");
            return [];
        }
    }

    public async Task<IReadOnlyList<BranchDto>> FetchAllBranchesAsync(CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);

            // Use the same entity resolution that LookupBranchAsync uses (works in ERP Test).
            // Avoid $select — the resolved entity (often "Company") returns 500 when $select is used.
            var entity = await ResolveBranchEntityAsync(ct);
            var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/{entity}?$top=500";
            _logger.LogInformation("FetchAllBranches using entity: {Entity}", entity);
            var response = await _http.GetAsync(url, ct);
            ThrowIfAuthFailure(response, "FetchAllBranches");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            var branches = new List<BranchDto>();
            foreach (var b in arr.EnumerateArray())
            {
                static string Str(JsonElement el, string key) =>
                    el.TryGetProperty(key, out var p) && p.TryGetProperty("value", out var v)
                        ? v.GetString() ?? "" : "";
                static bool Active(JsonElement el) =>
                    !el.TryGetProperty("Active", out var p) ||
                    !p.TryGetProperty("value", out var v) ||
                    v.ValueKind != JsonValueKind.False;

                var bid = Str(b, "BranchID");
                if (string.IsNullOrWhiteSpace(bid)) continue;
                branches.Add(new BranchDto(bid, bid, Str(b, "BranchName"), Active(b)));
            }
            _logger.LogInformation("FetchAllBranches returned {Count} branches", branches.Count);
            return branches;
        }
        catch (Exception ex) when (ex is not AcumaticaAuthException)
        {
            _logger.LogError(ex, "Failed to fetch branches from Acumatica");
            return [];
        }
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> FetchApAgingGiAsync(
        string? branchId, string? vendorId, DateTimeOffset ageDate, CancellationToken ct = default)
    {
        await SetAuthHeaderAsync(ct);
        var ageDateStr = ageDate.ToString("yyyy-MM-dd");
        var url        = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/APAging";

        // The APAging GI is an inquiry-form endpoint: PUT with the parameters and the
        // response body already contains the result row — no separate GET is needed.
        var putPayload = new Dictionary<string, object>
        {
            ["AgeasofDate"] = new { value = ageDateStr },
        };
        if (!string.IsNullOrWhiteSpace(branchId))
            putPayload["Branch"] = new { value = branchId };
        if (!string.IsNullOrWhiteSpace(vendorId))
            putPayload["Vendor"] = new { value = vendorId };

        var putJson    = JsonSerializer.Serialize(putPayload);
        var putContent = new StringContent(putJson, System.Text.Encoding.UTF8, "application/json");
        _logger.LogInformation("FetchApAgingGi PUT {Url}", url);
        var putResp = await _http.PutAsync(url, putContent, ct);
        ThrowIfAuthFailure(putResp, "FetchApAgingGi");
        var body = await putResp.Content.ReadAsStringAsync(ct);

        if (!putResp.IsSuccessStatusCode)
        {
            _logger.LogError("FetchApAgingGi PUT HTTP {Status}: {Body}", (int)putResp.StatusCode, body);
            throw new InvalidOperationException($"APAging GI PUT returned HTTP {(int)putResp.StatusCode}: {body}");
        }

        // Parse the single result row from the PUT response body.
        var rows = ParseGiRows(body);

        // If ParseGiRows found no array, the PUT response is a single object — wrap it.
        if (rows.Count == 0 && body.TrimStart().StartsWith('{'))
        {
            try
            {
                var root = JsonDocument.Parse(body).RootElement;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name is "id" or "rowNumber" or "note" or "custom") continue;
                    string val;
                    if (p.Value.ValueKind == JsonValueKind.Object &&
                        p.Value.TryGetProperty("value", out var inner))
                        val = inner.ToString();
                    else
                        val = p.Value.ToString();
                    dict[p.Name] = val;
                }
                if (dict.Count > 0) rows.Add(dict);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FetchApAgingGi: failed to parse PUT response as single row");
            }
        }

        if (rows.Count > 0)
            _logger.LogInformation("FetchApAgingGi returned row: {Keys}={Values}",
                string.Join(",", rows[0].Keys), string.Join(",", rows[0].Values));
        else
            _logger.LogWarning("FetchApAgingGi: PUT returned 0 parseable rows; body: {Body}",
                body.Length > 500 ? body[..500] : body);

        return rows;
    }

    /// <summary>
    /// Calls the AllAPAging Generic Inquiry — a no-vendor-filter GI that returns
    /// all vendor aging rows for a given AgeDate.
    /// Like APAging, the PUT response body itself contains the result rows
    /// (BQL delegates prevent a separate GET from working).
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> FetchAllApAgingFromAllGiAsync(
        string branchId, DateTimeOffset ageDate, CancellationToken ct = default)
    {
        await SetAuthHeaderAsync(ct);
        var ageDateStr = ageDate.ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/AllAPAging";

        // Step 1: PUT to set Branch + AgeasofDate on the inquiry form.
        var putPayload = JsonSerializer.Serialize(new
        {
            Branch      = new { value = branchId },
            AgeasofDate = new { value = ageDateStr },
        });
        var putContent = new StringContent(putPayload, System.Text.Encoding.UTF8, "application/json");
        _logger.LogInformation("FetchAllApAgingFromAllGi PUT {Url} (Branch={Branch}, AgeDate={Date})", url, branchId, ageDateStr);
        var putResp = await _http.PutAsync(url, putContent, ct);
        ThrowIfAuthFailure(putResp, "FetchAllApAgingFromAllGi PUT");
        var putBody = await putResp.Content.ReadAsStringAsync(ct);

        if (!putResp.IsSuccessStatusCode)
        {
            _logger.LogError("FetchAllApAgingFromAllGi PUT returned {Status}: {Body}", (int)putResp.StatusCode,
                putBody.Length > 500 ? putBody[..500] : putBody);
            throw new InvalidOperationException($"AllAPAging PUT returned HTTP {(int)putResp.StatusCode}: {putBody}");
        }

        // Step 2: GET with $top=2000 to retrieve per-vendor rows.
        // Without $top, Acumatica triggers optimized-export which fails on BQL-delegate GIs.
        var getUrl = $"{url}?$top=2000";
        _logger.LogInformation("FetchAllApAgingFromAllGi GET {Url} (Branch={Branch})", getUrl, branchId);
        var getResp = await _http.GetAsync(getUrl, ct);
        ThrowIfAuthFailure(getResp, "FetchAllApAgingFromAllGi GET");
        var getBody = await getResp.Content.ReadAsStringAsync(ct);

        if (!getResp.IsSuccessStatusCode)
        {
            _logger.LogError("FetchAllApAgingFromAllGi GET returned {Status}: {Body}", (int)getResp.StatusCode,
                getBody.Length > 500 ? getBody[..500] : getBody);
            throw new InvalidOperationException($"AllAPAging GET returned HTTP {(int)getResp.StatusCode}: {getBody}");
        }

        var rows = ParseGiRows(getBody);
        _logger.LogInformation("FetchAllApAgingFromAllGi GET returned {Count} rows for branch {Branch}; body preview: {Preview}",
            rows.Count, branchId, getBody.Length > 300 ? getBody[..300] : getBody);
        return rows;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> FetchAllApAgingRowsAsync(
        DateTimeOffset ageDate, CancellationToken ct = default)
    {
        await SetAuthHeaderAsync(ct);
        var ageDateStr = ageDate.ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/entity/{ApiEndpoint}/{ApiVersion}/APAging";

        // Step 1: PUT to set the AgeDate parameter on the inquiry form.
        var putPayload = JsonSerializer.Serialize(new { AgeasofDate = new { value = ageDateStr } });
        var putContent = new StringContent(putPayload, System.Text.Encoding.UTF8, "application/json");
        _logger.LogInformation("FetchAllApAgingRows PUT {Url} (AgeDate={Date})", url, ageDateStr);
        var putResp = await _http.PutAsync(url, putContent, ct);
        ThrowIfAuthFailure(putResp, "FetchAllApAgingRows PUT");
        var putBody = await putResp.Content.ReadAsStringAsync(ct);
        if (!putResp.IsSuccessStatusCode)
        {
            _logger.LogError("FetchAllApAgingRows PUT returned {Status}: {Body}", (int)putResp.StatusCode, putBody);
            throw new InvalidOperationException($"APAging PUT returned HTTP {(int)putResp.StatusCode}: {putBody}");
        }

        // Step 2: GET to retrieve all vendor rows from the inquiry result.
        _logger.LogInformation("FetchAllApAgingRows GET {Url}", url);
        var getResp = await _http.GetAsync(url, ct);
        ThrowIfAuthFailure(getResp, "FetchAllApAgingRows GET");
        var getBody = await getResp.Content.ReadAsStringAsync(ct);

        if (!getResp.IsSuccessStatusCode)
        {
            _logger.LogError("FetchAllApAgingRows GET returned {Status}: {Body}", (int)getResp.StatusCode,
                getBody.Length > 500 ? getBody[..500] : getBody);
            throw new InvalidOperationException($"APAging GET returned HTTP {(int)getResp.StatusCode}");
        }

        var rows = ParseGiRows(getBody);
        _logger.LogInformation("FetchAllApAgingRows GET returned {Count} rows; body preview: {Preview}",
            rows.Count, getBody.Length > 300 ? getBody[..300] : getBody);
        return rows;
    }

    /// <summary>
    /// Parses GI rows from an Acumatica JSON response.
    /// Handles: root array, OData {value:[]} wrapper, and nested arrays under any property.
    /// Each row flattens Acumatica {value:...} wrappers to plain strings.
    /// </summary>
    private List<IReadOnlyDictionary<string, string>> ParseGiRows(string body)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        try
        {
            var root = JsonDocument.Parse(body).RootElement;

            // Find the array element
            JsonElement arr = default;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // OData wrapper
                if (root.TryGetProperty("value", out var vp) && vp.ValueKind == JsonValueKind.Array)
                    arr = vp;
                else
                {
                    // Search for first array-type property (GI result grid)
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            arr = prop.Value;
                            _logger.LogInformation("ParseGiRows: found array under property '{Name}'", prop.Name);
                            break;
                        }
                    }
                }
            }

            if (arr.ValueKind != JsonValueKind.Array) return rows;

            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in row.EnumerateObject())
                {
                    string val;
                    if (p.Value.ValueKind == JsonValueKind.Object &&
                        p.Value.TryGetProperty("value", out var inner))
                        val = inner.ToString();
                    else
                        val = p.Value.ToString();
                    dict[p.Name] = val;
                }
                rows.Add(dict);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ParseGiRows: failed to parse body");
        }
        return rows;
    }

}
