namespace OcrErpSystem.Application.DTOs;

public record VendorDto(string VendorId, string VendorName, bool IsActive);
public record CurrencyDto(string CurrencyCode, string Description, bool IsActive);
public record BranchDto(string BranchId, string BranchCode, string BranchName, bool IsActive);
public record PurchaseOrderDto(string PoNumber, string VendorId, decimal Amount, string Status);
public record AcumaticaUserDto(string UserId, string Username, string FullName, string? Email, IReadOnlyList<string> Roles, string? BranchCode);
