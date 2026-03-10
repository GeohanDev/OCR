namespace OcrErpSystem.Domain.Entities;

public class Vendor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Acumatica VendorID (e.g. "V00001"). Unique key used for upsert.</summary>
    public string AcumaticaVendorId { get; set; } = string.Empty;

    public string VendorName { get; set; } = string.Empty;

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    /// <summary>Payment terms code (e.g. "30D", "NET30").</summary>
    public string? PaymentTerms { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Document> Documents { get; set; } = [];
}
