using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation;

public class VendorResolutionContext : IVendorResolutionContext
{
    public string? ResolvedVendorId { get; set; }
    public bool VendorValidationFailed { get; set; }
}
