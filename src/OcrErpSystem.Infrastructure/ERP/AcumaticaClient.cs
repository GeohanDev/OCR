using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;

namespace OcrErpSystem.Infrastructure.ERP;

public class AcumaticaClient : IErpIntegrationService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<AcumaticaClient> _logger;
    private static string? _serviceAccountToken;
    private static DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AcumaticaClient(HttpClient http, IConfiguration config, ILogger<AcumaticaClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    private string BaseUrl => _config["Acumatica:BaseUrl"] ?? throw new InvalidOperationException("Acumatica:BaseUrl not configured");
    private string ApiVersion => _config["Acumatica:ApiVersion"] ?? "23.200.001";

    private async Task<string> GetServiceAccountTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_serviceAccountToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _serviceAccountToken;

            var clientId = _config["Acumatica:ServiceAccount:ClientId"]
                ?? throw new InvalidOperationException("Acumatica:ServiceAccount:ClientId not configured");
            var clientSecret = _config["Acumatica:ServiceAccount:ClientSecret"]
                ?? throw new InvalidOperationException("Acumatica:ServiceAccount:ClientSecret not configured");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/identity/connect/token");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });

            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
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

    private async Task SetAuthHeaderAsync(CancellationToken ct)
    {
        var token = await GetServiceAccountTokenAsync(ct);
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
            var url = $"{BaseUrl}/entity/Default/{ApiVersion}/Users?$select=Username,FullName,Email,Roles,BranchID";
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
                    u.GetProperty("FullName").GetProperty("value").GetString() ?? "",
                    u.TryGetProperty("Email", out var em) ? em.GetProperty("value").GetString() : null,
                    roles,
                    u.TryGetProperty("BranchID", out var br) ? br.GetProperty("value").GetString() : null));
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
