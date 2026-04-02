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
        "VendorStatement:TotalInvoiceAmount",
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

        // OutstandingBalance: arithmetic cross-field check — no ERP call needed.
        // Formula: opening_balance + total_debit - total_credit = outstanding_balance
        if (key.Equals("VendorStatement:OutstandingBalance", StringComparison.OrdinalIgnoreCase))
            return ValidateOutstandingBalance(value);

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
            var k when k == "VENDORSTATEMENT:TOTALINVOICEAMOUNT" =>
                bills.Sum(b => b.Amount),
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

    /// <summary>
    /// Validates outstanding balance using extracted sibling fields only — no ERP call.
    /// Formula: opening_balance + total_debit - total_credit = outstanding_balance
    /// "-", empty, or unparseable component values are treated as 0.
    /// Requires at least two of the three component fields to be present to avoid
    /// a false pass when only one unrelated field happens to match.
    /// </summary>
    private FieldValidationResult ValidateOutstandingBalance(string value)
    {
        var extracted = ParseAmount(value);
        if (extracted is null)
            return new FieldValidationResult("Warning",
                $"Could not parse numeric amount from '{value}'.", "VendorStatement");

        // Strict name variants only — do NOT include totalInvoiceAmount here because
        // that field is a separate summary field and would be misread as the debit total.
        var opening = GetFieldAmount("openingBalance", "opening_balance", "openBalance", "bfBalance", "balanceBroughtForward");
        var debit   = GetFieldAmount("totalDebit", "total_debit", "totalCharges");
        var credit  = GetFieldAmount("totalCredit", "total_credit", "totalPayments", "totalCreditNote", "totalCN");

        int found = (opening is not null ? 1 : 0) + (debit is not null ? 1 : 0) + (credit is not null ? 1 : 0);

        if (found < 2)
            return new FieldValidationResult("Warning",
                $"Cannot verify outstanding balance — need at least 2 of: opening balance ({(opening.HasValue ? "found" : "missing")}), total debit ({(debit.HasValue ? "found" : "missing")}), total credit ({(credit.HasValue ? "found" : "missing")}).",
                "VendorStatement");

        var computed = (opening ?? 0m) + (debit ?? 0m) - (credit ?? 0m);
        var diff = Math.Abs(extracted.Value - computed);
        var tolerance = Math.Max(Math.Abs(computed) * 0.01m, 0.10m); // 1% or 10 cents

        _logger.LogInformation(
            "VendorStatement OutstandingBalance: opening={Opening} debit={Debit} credit={Credit} computed={Computed} extracted={Extracted} diff={Diff} tolerance={Tolerance}",
            opening ?? 0m, debit ?? 0m, credit ?? 0m, computed, extracted.Value, diff, tolerance);

        if (diff <= tolerance)
            return new FieldValidationResult("Passed",
                $"✓ Outstanding balance verified: {opening ?? 0m:N2} + {debit ?? 0m:N2} − {credit ?? 0m:N2} = {computed:N2}",
                "VendorStatement", new { opening = opening ?? 0m, totalDebit = debit ?? 0m, totalCredit = credit ?? 0m, computed });

        return new FieldValidationResult("Failed",
            $"✗ Outstanding balance mismatch: {opening ?? 0m:N2} + {debit ?? 0m:N2} − {credit ?? 0m:N2} = {computed:N2}, but statement shows {extracted.Value:N2} (diff {diff:N2})",
            "VendorStatement", new { opening = opening ?? 0m, totalDebit = debit ?? 0m, totalCredit = credit ?? 0m, computed, extracted = extracted.Value });
    }

    /// <summary>
    /// Looks up the first matching field name variant and parses it.
    /// Returns null only when no matching field exists; returns 0 when the field
    /// exists but contains "-", empty, or a non-numeric value (e.g. "0", "nil", "-").
    /// </summary>
    private decimal? GetFieldAmount(params string[] fieldNameVariants)
    {
        foreach (var name in fieldNameVariants)
        {
            if (_fieldContext.FieldValues.TryGetValue(name, out var raw))
                return ParseAmountOrZero(raw);
        }
        return null; // field not present at all
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
        var cleaned = Regex.Replace(value.Trim(), @"[^\d\.\-]", "");
        if (string.IsNullOrEmpty(cleaned)) return null;
        return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    /// <summary>
    /// Like ParseAmount but treats missing/dash/non-numeric as 0 instead of null.
    /// Used for component fields (debit, credit, opening) where absence means zero.
    /// </summary>
    private static decimal ParseAmountOrZero(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        var trimmed = value.Trim();
        // Pure dash or dash-like placeholders mean "no amount" → 0
        if (trimmed == "-" || trimmed == "–" || trimmed == "—" || trimmed == "nil" || trimmed == "n/a")
            return 0m;
        return ParseAmount(trimmed) ?? 0m;
    }
}
