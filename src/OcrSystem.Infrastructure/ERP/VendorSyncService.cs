using Microsoft.Extensions.Logging;
using OcrSystem.Application.ERP;
using OcrSystem.Domain.Entities;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.ERP;

public class VendorSyncService : IVendorSyncService
{
    private readonly IErpIntegrationService _erp;
    private readonly VendorRepository _repo;
    private readonly ILogger<VendorSyncService> _logger;

    public VendorSyncService(IErpIntegrationService erp, VendorRepository repo, ILogger<VendorSyncService> logger)
    {
        _erp = erp;
        _repo = repo;
        _logger = logger;
    }

    public async Task<int> SyncVendorsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting vendor sync from Acumatica");
        var vendors = await _erp.GetAllVendorsFullAsync(ct);
        _logger.LogInformation("Fetched {Count} vendors from Acumatica", vendors.Count);

        int count = 0;
        foreach (var v in vendors)
        {
            if (string.IsNullOrWhiteSpace(v.VendorId)) continue;

            var entity = new Vendor
            {
                AcumaticaVendorId = v.VendorId,
                VendorName        = v.VendorName,
                AddressLine1      = v.AddressLine1,
                AddressLine2      = v.AddressLine2,
                City              = v.City,
                State             = v.State,
                PostalCode        = v.PostalCode,
                Country           = v.Country,
                PaymentTerms      = v.PaymentTerms,
                IsActive          = v.IsActive,
                LastSyncedAt      = DateTimeOffset.UtcNow,
            };

            await _repo.UpsertAsync(entity, ct);
            count++;
        }

        _logger.LogInformation("Vendor sync complete — {Count} vendors upserted", count);
        return count;
    }
}
