using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.ERP;
using OcrSystem.Application.Validation;
using OcrSystem.Domain.Enums;

namespace OcrSystem.Infrastructure.Validation.Validators;

/// <summary>
/// Validates vendor statement fields (outstanding balance and aging buckets) against
/// open AP bills fetched from Acumatica for the resolved vendor.
/// Supported ERP mapping keys:
///   VendorStatement:OutstandingBalance
///   VendorStatement:AgingCurrent   (not yet due)
///   VendorStatement:Aging30        (1–30 days overdue)
///   VendorStatement:Aging60        (31–60 days overdue)
///   VendorStatement:Aging90Plus    (61+ days overdue)
/// </summary>
public class ErpVendorStatementValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IVendorResolutionContext _vendorContext;
    private readonly IValidationFieldContext _fieldContext;
    private readonly ILogger<ErpVendorStatementValidator> _logger;

    private static readonly IReadOnlyList<string> _keys =
    [
        "VendorStatement:OutstandingBalance",
        "VendorStatement:AgingCurrent",
        "VendorStatement:Aging30",
        "VendorStatement:Aging60",
        "VendorStatement:Aging90Plus",
    ];

    public IReadOnlyList<string> SupportedErpMappingKeys => _keys;
    public bool RunForAllFields => false;
    public DocumentCategory? RequiresCategory => DocumentCategory.VendorStatement;

    public bool CanHandle(string erpMappingKey) =>
        _keys.Any(k => string.Equals(k, erpMappingKey, StringComparison.OrdinalIgnoreCase));

    public ErpVendorStatementValidator(
        IErpIntegrationService erp,
        IVendorResolutionContext vendorContext,
        IValidationFieldContext fieldContext,
        ILogger<ErpVendorStatementValidator> logger)
    {
        _erp = erp;
        _vendorContext = vendorContext;
        _fieldContext = fieldContext;
        _logger = logger;
    }

    public async Task<FieldValidationResult> ValidateAsync(
        ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var key = config.ErpMappingKey ?? "";
        var value = field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue ?? "";

        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "VendorStatement");

        if (_vendorContext.VendorValidationFailed)
            return new FieldValidationResult("Skipped", "Skipped — vendor validation failed.", "VendorStatement");

        var vendorId = _vendorContext.ResolvedVendorId;
        if (string.IsNullOrWhiteSpace(vendorId))
            return new FieldValidationResult("Skipped", "Skipped — vendor not resolved yet.", "VendorStatement");

        var bills = await _erp.FetchOpenBillsForVendorAsync(vendorId, ct);
        if (bills.Count == 0)
            return new FieldValidationResult("Warning",
                $"No open bills found in Acumatica for vendor ID '{vendorId}'.", "VendorStatement");

        // Reference date: use statementDate field if available, otherwise today
        var refDate = GetStatementDate() ?? DateTimeOffset.UtcNow;

        var extractedAmount = ParseAmount(value);
        if (extractedAmount is null)
            return new FieldValidationResult("Warning",
                $"Could not parse numeric amount from '{value}'.", "VendorStatement");

        decimal computed = key.ToUpperInvariant() switch
        {
            var k when k == "VENDORSTATEMENT:OUTSTANDINGBALANCE" =>
                bills.Sum(b => b.Balance),
            var k when k == "VENDORSTATEMENT:AGINGCURRENT" =>
                bills.Where(b => b.DueDate.HasValue && b.DueDate.Value >= refDate)
                     .Sum(b => b.Balance),
            var k when k == "VENDORSTATEMENT:AGING30" =>
                bills.Where(b => b.DueDate.HasValue &&
                                 b.DueDate.Value >= refDate.AddDays(-30) &&
                                 b.DueDate.Value < refDate)
                     .Sum(b => b.Balance),
            var k when k == "VENDORSTATEMENT:AGING60" =>
                bills.Where(b => b.DueDate.HasValue &&
                                 b.DueDate.Value >= refDate.AddDays(-60) &&
                                 b.DueDate.Value < refDate.AddDays(-30))
                     .Sum(b => b.Balance),
            var k when k == "VENDORSTATEMENT:AGING90PLUS" =>
                bills.Where(b => !b.DueDate.HasValue ||
                                 b.DueDate.Value < refDate.AddDays(-60))
                     .Sum(b => b.Balance),
            _ => 0m
        };

        var diff = Math.Abs(extractedAmount.Value - computed);
        var tolerance = Math.Max(computed * 0.01m, 1m); // 1% or 1 unit

        _logger.LogInformation(
            "VendorStatement {Key}: extracted={Extracted} computed={Computed} diff={Diff}",
            key, extractedAmount.Value, computed, diff);

        var label = key.Split(':').LastOrDefault() ?? key;
        if (diff <= tolerance)
            return new FieldValidationResult("Passed",
                $"✓ {label}: {computed:N2} (matches Acumatica open bills)", "VendorStatement",
                new { computed, billCount = bills.Count });

        if (diff <= computed * 0.05m + 1m)
            return new FieldValidationResult("Warning",
                $"⚠ {label}: statement={extractedAmount.Value:N2}, ERP computed={computed:N2} — difference of {diff:N2}",
                "VendorStatement", new { computed, extracted = extractedAmount.Value, difference = diff });

        return new FieldValidationResult("Failed",
            $"✗ {label}: statement={extractedAmount.Value:N2}, ERP computed={computed:N2} — mismatch of {diff:N2}",
            "VendorStatement", new { computed, extracted = extractedAmount.Value, difference = diff });
    }

    private DateTimeOffset? GetStatementDate()
    {
        if (_fieldContext.FieldValues.TryGetValue("statementDate", out var raw) &&
            DateTimeOffset.TryParse(raw, out var dt))
            return dt;
        return null;
    }

    private static decimal? ParseAmount(string value)
    {
        // Strip currency symbols, spaces, commas (e.g. "MYR 12,450.00" → 12450.00)
        var cleaned = Regex.Replace(value, @"[^\d\.\-]", "");
        return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
