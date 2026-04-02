namespace OcrSystem.Domain.Entities;

public class VendorAgingSnapshot
{
    public Guid Id { get; set; }
    public string VendorLocalId { get; set; } = string.Empty;
    public string? AcumaticaVendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public DateOnly SnapshotDate { get; set; }
    /// <summary>0 = AP aging as of the vendor's statement date; 1 = AP aging as of today (per branch).</summary>
    public int SnapshotKind { get; set; }
    /// <summary>The vendor's statement date. Set only for SnapshotKind = 0.</summary>
    public DateOnly? StatementDate { get; set; }
    /// <summary>Acumatica branch ID. Set only for SnapshotKind = 1.</summary>
    public string? SnapshotBranchId { get; set; }
    public decimal Current { get; set; }
    public decimal Aging30 { get; set; }
    public decimal Aging60 { get; set; }
    /// <summary>61–90 days overdue.</summary>
    public decimal Aging90 { get; set; }
    /// <summary>91+ days overdue.</summary>
    public decimal Aging90Plus { get; set; }
    public decimal TotalOutstanding { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}
