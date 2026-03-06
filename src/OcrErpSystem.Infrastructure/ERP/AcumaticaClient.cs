using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;

namespace OcrErpSystem.Infrastructure.ERP;

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

    public async Task<ErpLookupResult<VendorDto>> LookupVendorAsync(string vendorId, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Vendor/{Uri.EscapeDataString(vendorId)}?$select=VendorID,VendorName,Status";
            var response = await _http.GetAsync(url, ct);
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
        catch (Exception ex)
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
            // Normalize internal whitespace so "ABC  SDN BHD" matches "ABC SDN BHD".
            static string Norm(string s) =>
                string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var normalizedInput = Norm(vendorName.Trim());
            var match = all.FirstOrDefault(v =>
                string.Equals(Norm(v.VendorName), normalizedInput, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return new ErpLookupResult<VendorDto>(false, null, "Vendor not found");
            return new ErpLookupResult<VendorDto>(true, match, null);
        }
        catch (Exception ex)
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
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Vendor?$select=VendorID,VendorName,Status{topClause}";
            var response = await _http.GetAsync(url, ct);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch all vendors from Acumatica");
            return [];
        }
    }

    public async Task<ErpLookupResult<ApInvoiceDto>> LookupApInvoiceAsync(string invoiceNbr, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            // Acumatica 23.x+ exposes AP invoices as "Bill" (not "APInvoice").
            // Omit $select so unknown/renamed fields don't cause 400/parse errors.
            var filter = Uri.EscapeDataString($"RefNbr eq '{invoiceNbr}'");
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Bill?$filter={filter}&$top=1";
            _logger.LogInformation("AP Invoice lookup → {Url}", url);
            var response = await _http.GetAsync(url, ct);
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
                Str(first, "RefNbr"),
                Str(first, "VendorID"),
                Str(first, "DocDate"),
                Dec(first, "CuryOrigDocAmt"),
                Str(first, "Status"),
                Str(first, "DocType"));
            return new ErpLookupResult<ApInvoiceDto>(true, invoice, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ERP AP invoice lookup failed for {InvoiceNbr}", invoiceNbr);
            return new ErpLookupResult<ApInvoiceDto>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<CurrencyDto>> LookupCurrencyAsync(string currencyCode, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Currency/{Uri.EscapeDataString(currencyCode)}";
            var response = await _http.GetAsync(url, ct);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "ERP currency lookup failed for {CurrencyCode}", currencyCode);
            return new ErpLookupResult<CurrencyDto>(false, null, ex.Message);
        }
    }

    public async Task<ErpLookupResult<BranchDto>> LookupBranchAsync(string branchCode, CancellationToken ct = default)
    {
        try
        {
            await SetAuthHeaderAsync(ct);
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Branch?$filter=BranchID eq '{Uri.EscapeDataString(branchCode)}'";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<BranchDto>(false, null, "Branch not found");
            var first = arr[0];
            var branch = new BranchDto(
                first.GetProperty("BranchID").GetProperty("value").GetString()!,
                first.GetProperty("BranchID").GetProperty("value").GetString()!,
                first.GetProperty("BranchName").GetProperty("value").GetString()!,
                first.TryGetProperty("Active", out var active) && active.GetProperty("value").GetBoolean());
            return new ErpLookupResult<BranchDto>(true, branch, null);
        }
        catch (Exception ex)
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
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/PurchaseOrder/{Uri.EscapeDataString(poNumber)}";
            var response = await _http.GetAsync(url, ct);
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
        catch (Exception ex)
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
        new("Bill",           "AP Invoice (Bill)", ["RefNbr", "VendorRef", "VendorID", "VendorName", "DocDate", "Status", "DocType", "CuryID", "CuryOrigDocAmt"]),
        new("SOInvoice",      "Sales Invoice",   ["RefNbr", "CustomerID", "CustomerName", "DocDate", "Status", "DocType"]),
        new("PurchaseOrder",  "Purchase Order",  ["OrderNbr", "VendorID", "VendorName", "Status", "CuryID", "CuryOrderTotal"]),
        new("Currency",       "Currency",        ["CurrencyID", "Description"]),
        new("Branch",         "Branch",          ["BranchID", "BranchName", "Active", "LedgerID"]),
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
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/{entity}?$filter={filterCI}&$top=1";
            _logger.LogInformation("Generic ERP lookup → {Url}", url);
            var response = await _http.GetAsync(url, ct);

            // Acumatica returns 400 or 500 when tolower() is unsupported — retry with exact match.
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                _logger.LogDebug("tolower() not supported for {Entity}.{Field} — retrying with exact match", entity, field);
                url = $"{BaseUrl}/entity/Default/{ApiVersion}/{entity}?$filter={filterExact}&$top=1";
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
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    $"No {entity} record found where {field} = \"{normalizedValue}\"");

            // Flatten the first record: each Acumatica field is { "value": "..." }
            var record = new Dictionary<string, string>();
            foreach (var prop in arr[0].EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("value", out var val))
                    record[prop.Name] = val.ToString();
            }
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, record, null);
        }
        catch (Exception ex)
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
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/{entity}?$top=1";
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
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.GetArrayLength() == 0)
                return new ErpLookupResult<IReadOnlyDictionary<string, string>>(false, null,
                    "Entity exists but returned 0 records");

            var record = new Dictionary<string, string>();
            foreach (var prop in arr[0].EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("value", out var val))
                    record[prop.Name] = val.ToString();
            }
            return new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, record, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Probe entity failed: {Entity}", entity);
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
        catch (Exception ex)
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
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Branch?$select=BranchID,BranchName,Active";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            var branches = new List<BranchDto>();
            foreach (var b in arr.EnumerateArray())
                branches.Add(new BranchDto(
                    b.GetProperty("BranchID").GetProperty("value").GetString()!,
                    b.GetProperty("BranchID").GetProperty("value").GetString()!,
                    b.GetProperty("BranchName").GetProperty("value").GetString()!,
                    b.TryGetProperty("Active", out var active) && active.GetProperty("value").GetBoolean()));
            return branches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch branches from Acumatica");
            return [];
        }
    }
}
