using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation;

public class VendorResolutionContext : IVendorResolutionContext
{
    public string? ResolvedVendorId { get; set; }
    public bool VendorValidationFailed { get; set; }
}
