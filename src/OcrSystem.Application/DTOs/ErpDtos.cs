namespace OcrSystem.Application.DTOs;

/// <summary>
/// Describes a known Acumatica entity and the fields that can be used in ERP mapping.
/// </summary>
public record ErpEntityDto(string EntityName, string DisplayName, IReadOnlyList<string> Fields);

public record VendorDto(string VendorId, string VendorName, bool IsActive);

/// <summary>Extended vendor record including address and payment terms, used for vendor sync.</summary>
public record VendorFullDto(
    string VendorId,
    string VendorName,
    bool IsActive,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? PaymentTerms);

/// <summary>Open AP bill record used for outstanding balance and aging calculations.</summary>
public record OpenBillDto(
    string ReferenceNbr,
    string VendorRef,
    decimal Balance,
    decimal Amount,
    DateTimeOffset? DueDate,
    string Status,
    string VendorId = "");
public record CurrencyDto(string CurrencyCode, string Description, bool IsActive);
public record BranchDto(string BranchId, string BranchCode, string BranchName, bool IsActive);
public record PurchaseOrderDto(string PoNumber, string VendorId, decimal Amount, string Status);
public record AcumaticaUserDto(string UserId, string Username, string FullName, string? Email, IReadOnlyList<string> Roles, string? BranchCode);
public record ApInvoiceDto(string RefNbr, string VendorId, string DocDate, decimal Amount, string Status, string DocType);
