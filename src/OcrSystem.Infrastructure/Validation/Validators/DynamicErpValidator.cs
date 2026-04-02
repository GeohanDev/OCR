using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.ERP;
using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation.Validators;

/// <summary>
/// Handles any ERP mapping key in "Entity:Field" format (e.g. "Vendor:VendorName").
/// Dynamically queries the Acumatica OData endpoint and checks if a matching record exists.
/// </summary>
public class DynamicErpValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IVendorResolutionContext _vendorContext;
    private readonly IValidationFieldContext _fieldContext;
    private readonly IOwnCompanyService _ownCompany;
    private readonly IErpInvoiceContext _invoiceContext;
    private readonly ILogger<DynamicErpValidator> _logger;

    public DynamicErpValidator(IErpIntegrationService erp, IVendorResolutionContext vendorContext,
        IValidationFieldContext fieldContext, IOwnCompanyService ownCompany,
        IErpInvoiceContext invoiceContext, ILogger<DynamicErpValidator> logger)
    {
        _erp = erp;
        _vendorContext = vendorContext;
        _fieldContext = fieldContext;
        _ownCompany = ownCompany;
        _invoiceContext = invoiceContext;
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedErpMappingKeys => [];
    public bool RunForAllFields => false;

    // Only handles keys that contain ':' — "Entity:Field" format.
    public bool CanHandle(string erpMappingKey) => erpMappingKey.Contains(':');

    // Date formats OCR commonly produces (DD/MM/YYYY is standard in Malaysia/Asia).
    // Listed most-specific first so ParseExact prefers the right format.
    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy",
        "dd-MM-yyyy", "d-M-yyyy", "dd-M-yyyy", "d-MM-yyyy",
        "dd.MM.yyyy", "d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd",
        "dd MMM yyyy", "d MMM yyyy",
        "dd/MM/yy", "d/M/yy", "dd.MM.yy",
    ];

    /// <summary>
    /// Compares two field values:
    /// 1. Numeric: strips currency symbols and parses as decimal.
    /// 2. Date: parses both sides with common OCR and ISO 8601 formats, compares date-only.
    /// 3. String: case-insensitive fallback.
    /// </summary>
    private static bool CompareValues(string extracted, string erpValue)
    {
        // 1. Numeric comparison
        var cleanExtracted = CleanNumeric(extracted);
        var cleanErp       = CleanNumeric(erpValue);

        if (decimal.TryParse(cleanExtracted, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dExtracted) &&
            decimal.TryParse(cleanErp, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dErp))
            return dExtracted == dErp;

        // 2. Date comparison — normalise both sides to a DateOnly before comparing.
        //    Acumatica returns ISO 8601 (e.g. "2025-11-05T00:00:00"); OCR may return
        //    "05/11/2025" (DD/MM/YYYY) or other regional formats.
        if (TryParseDate(extracted.Trim(), out var dateExtracted) &&
            TryParseDate(erpValue.Trim(),  out var dateErp))
            return dateExtracted == dateErp;

        // 3. String fallback
        return string.Equals(extracted.Trim(), erpValue.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDate(string value, out DateOnly result)
    {
        result = default;

        // 1. Try explicit formats first — dd/MM/yyyy is first so regional OCR dates
        //    are never misread as MM/dd/yyyy by the runtime's culture-aware parser.
        if (DateOnly.TryParseExact(value, DateFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result))
            return true;

        // 2. Only fall back to DateTimeOffset.TryParse for ISO 8601 strings from Acumatica
        //    (e.g. "2025-11-05T00:00:00"). These start with a 4-digit year so they won't
        //    be ambiguous with dd/MM/yyyy OCR values.
        if (value.Length >= 10 && value[4] == '-' &&
            DateTimeOffset.TryParse(value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dto))
        {
            result = DateOnly.FromDateTime(dto.DateTime);
            return true;
        }

        return false;
    }

    /// <summary>
    /// If the value is a parseable date, returns it as dd/MM/yyyy for consistent display.
    /// Non-date values are returned as-is.
    /// </summary>
    private static string NormaliseForDisplay(string value)
    {
        if (TryParseDate(value.Trim(), out var date))
            return date.ToString("dd/MM/yyyy");
        return value;
    }

    /// <summary>Strips currency codes, symbols and whitespace, leaving only digits, '.', ',', and '-'.</summary>
    private static string CleanNumeric(string value)
    {
        // Remove known currency codes and symbols (RM, MYR, USD, $, etc.)
        var cleaned = System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"[A-Za-z$£€¥\s]", "");
        return cleaned;
    }

    public async Task<FieldValidationResult> ValidateAsync(
        ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var key = config.ErpMappingKey ?? string.Empty;
        var parts = key.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return new FieldValidationResult("Warning", $"Invalid ERP mapping key format '{key}'. Expected 'Entity:Field'.", "DynamicErp");

        var entity = parts[0].Trim();
        var matchField = parts[1].Trim();
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();

        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "DynamicErp");

        // Vendor:VendorName — use the dedicated case-insensitive name lookup (same as ErpVendorNameValidator)
        // instead of OData $filter, which is case-sensitive on VendorName in most Acumatica versions.
        if (string.Equals(entity, "Vendor", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(matchField, "VendorName", StringComparison.OrdinalIgnoreCase))
        {
            var vendorResult = await _erp.LookupVendorByNameAsync(value, ct);
            if (!vendorResult.Found)
            {
                // Non-null ErrorMessage means a connectivity/auth problem — the ERP was
                // unreachable, so we can't confirm or deny the vendor's existence.
                // Return Warning (not Failed) so it doesn't block document approval.
                if (!string.IsNullOrWhiteSpace(vendorResult.ErrorMessage))
                    return new FieldValidationResult("Warning",
                        $"Could not verify vendor '{value}' — {vendorResult.ErrorMessage}.", "DynamicErp");
                // Null ErrorMessage means the ERP call succeeded but no matching vendor was found.
                return new FieldValidationResult("Failed",
                    $"Vendor '{value}' not found in Acumatica.", "DynamicErp");
            }
            if (!vendorResult.Data!.IsActive)
                return new FieldValidationResult("Warning",
                    $"Vendor '{value}' found (ID: {vendorResult.Data.VendorId}) but is inactive.", "DynamicErp", vendorResult.Data);
            return new FieldValidationResult("Passed",
                $"Vendor verified — ID: {vendorResult.Data.VendorId}", "DynamicErp", vendorResult.Data);
        }

        bool isInvoiceEntity = entity.Contains("invoice", StringComparison.OrdinalIgnoreCase) ||
                               entity.Contains("bill", StringComparison.OrdinalIgnoreCase);

        // For invoice/bill entities with VendorRef field + a configured dependent vendor field:
        // use a single REST call filtered by both VendorRef and VendorName.
        if (isInvoiceEntity &&
            matchField.Equals("VendorRef", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(config.DependentFieldKey) &&
            _fieldContext.FieldValues.TryGetValue(config.DependentFieldKey, out var vendorName) &&
            !string.IsNullOrWhiteSpace(vendorName))
        {
            if (_ownCompany.IsOwnCompanyName(vendorName))
                return new FieldValidationResult("Warning",
                    $"Vendor name '{vendorName}' is your own company name — please verify this document is from an external vendor.",
                    "DynamicErp");

            var invResult = await _erp.LookupApInvoiceByVendorAsync(value, vendorName, ct);
            if (!invResult.Found)
                return new FieldValidationResult("Warning",
                    $"Invoice '{value}' not found for vendor '{vendorName}' in Acumatica.",
                    "DynamicErp");

            var inv = invResult.Data!;

            // Propagate resolved vendor ID so subsequent validators in the same run
            // (Date, Amount cross-field checks) can use the vendor-filtered bill lookup.
            if (!string.IsNullOrWhiteSpace(inv.VendorId))
                _vendorContext.ResolvedVendorId = inv.VendorId;

            // In row validation, cache the rich bill record so Date/Amount validators
            // can reuse it without a second ERP call.
            if (_invoiceContext.IsRowValidation && !string.IsNullOrWhiteSpace(inv.VendorId))
            {
                var rich = await _erp.LookupBillByVendorRefAndVendorIdAsync(value, inv.VendorId, ct);
                if (rich.Found && rich.Data is not null)
                    _invoiceContext.SetCachedInvoice(value, rich.Data);
            }

            return new FieldValidationResult("Passed",
                $"Invoice verified — {inv.DocType} {inv.RefNbr}, Vendor: {inv.VendorId}, Date: {inv.DocDate}, Status: {inv.Status}",
                "DynamicErp", inv);
        }

        // Cross-field verification: when DependentFieldKey is set, look up the record via the
        // dependent field then compare the target field value — instead of filtering by the field
        // itself (which fails for numeric/date fields or non-unique values like Amount).
        if (!string.IsNullOrWhiteSpace(config.DependentFieldKey) &&
            _fieldContext.FieldValues.TryGetValue(config.DependentFieldKey, out var depFieldValue) &&
            !string.IsNullOrWhiteSpace(depFieldValue))
        {
            // Determine which Acumatica field to use for the lookup.
            // Prefer the dependent field's own ERP key if it maps to the same entity (e.g. Bill:VendorRef).
            // Fall back to VendorRef for invoice entities when the dependent field has a different key
            // (e.g. ApInvoiceNbr, which is handled by ErpApInvoiceValidator but still holds the VendorRef value).
            string depField;
            if (_fieldContext.FieldErpKeys.TryGetValue(config.DependentFieldKey, out var depErpKey) &&
                depErpKey.StartsWith(entity + ":", StringComparison.OrdinalIgnoreCase))
                depField = depErpKey.Split(':', 2)[1].Trim();
            else if (isInvoiceEntity)
                depField = "VendorRef";
            else
                goto skipCrossField; // no way to determine lookup field for non-invoice entities

            _logger.LogInformation(
                "CrossField [{Field}={Value}] DependentFieldKey={DepKey} depValue={DepValue} depErpKey={DepErpKey} depField={DepField} resolvedVendorId={VendorId}",
                matchField, value, config.DependentFieldKey, depFieldValue,
                _fieldContext.FieldErpKeys.TryGetValue(config.DependentFieldKey, out var logDepErpKey) ? logDepErpKey : "(none)",
                depField, _vendorContext.ResolvedVendorId ?? "(none)");

            // When looking up a bill by VendorRef and we have a resolved vendor ID, use the
            // vendor-filtered lookup to ensure we get the correct bill — not just any bill
            // that happens to share the same VendorRef across different vendors.
            // When we have a resolved vendor ID, always use vendor-filtered lookup — applies to
            // Amount, Balance, DocDate, DueDate (and VendorRef) cross-field checks so the
            // validator never picks up a bill from the wrong vendor.
            ErpLookupResult<IReadOnlyDictionary<string, string>> billResult;
            // In row validation, ErpApInvoiceValidator may have already fetched this invoice — reuse it.
            if (_invoiceContext.IsRowValidation &&
                _invoiceContext.CachedInvoiceData is not null &&
                depField.Equals("VendorRef", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_invoiceContext.CachedVendorRef, depFieldValue, StringComparison.OrdinalIgnoreCase))
            {
                billResult = new ErpLookupResult<IReadOnlyDictionary<string, string>>(true, _invoiceContext.CachedInvoiceData, null);
            }
            else if (depField.Equals("VendorRef", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_vendorContext.ResolvedVendorId))
            {
                billResult = await _erp.LookupBillByVendorRefAndVendorIdAsync(depFieldValue, _vendorContext.ResolvedVendorId, ct);
                // Do NOT fall back to unfiltered — if vendor-filtered lookup finds nothing we report
                // it as not-found for this vendor, not as a hit on a different vendor's bill.
            }
            else if (depField.Equals("VendorRef", StringComparison.OrdinalIgnoreCase))
            {
                // Vendor ID could not be resolved — an unfiltered VendorRef lookup risks matching
                // a bill from a different vendor and producing a false mismatch on date/amount.
                // Return Warning so the user knows to validate the vendor/invoice fields first.
                return new FieldValidationResult("Warning",
                    $"Cannot verify {matchField} — vendor ID not resolved. Please re-validate the vendor name and invoice number fields first.",
                    "DynamicErp");
            }
            else
            {
                billResult = await _erp.LookupGenericAsync(entity, depField, depFieldValue, ct);
            }

            _logger.LogInformation(
                "CrossField lookup {Entity}.{DepField}='{DepValue}' → Found={Found} ErrorMessage={Error}",
                entity, depField, depFieldValue, billResult.Found, billResult.ErrorMessage);

            if (!billResult.Found)
                return new FieldValidationResult("Warning",
                    $"Could not find {entity} where {depField}='{depFieldValue}' — cannot verify {matchField}.",
                    "DynamicErp");

            var allKeys = billResult.Data != null ? string.Join(", ", billResult.Data.Keys) : "(null)";
            _logger.LogInformation("CrossField record keys returned: {Keys}", allKeys);

            if (billResult.Data == null || !billResult.Data.TryGetValue(matchField, out var erpFieldValue))
            {
                _logger.LogWarning("CrossField: matchField '{MatchField}' not in record. Available keys: {Keys}", matchField, allKeys);
                return new FieldValidationResult("Warning",
                    $"Field '{matchField}' was not returned in the {entity} record from Acumatica.",
                    "DynamicErp");
            }

            // Numeric comparison: strip currency symbols and non-numeric chars before parsing
            // so OCR values like "MYR 20,624.65" or "RM20,624.65" match Acumatica "20624.6500".
            var cleanExtracted = CleanNumeric(value);
            var cleanErp = CleanNumeric(erpFieldValue);
            bool matches = CompareValues(value, erpFieldValue);

            // For display in messages, normalise dates to dd/MM/yyyy so both sides look the same.
            string displayExtracted = NormaliseForDisplay(value);
            string displayErp       = NormaliseForDisplay(erpFieldValue);

            _logger.LogInformation(
                "CrossField compare: extracted='{Extracted}' cleanExtracted='{CleanExtracted}' erpValue='{ErpValue}' cleanErp='{CleanErp}' matches={Matches}",
                value, cleanExtracted, erpFieldValue, cleanErp, matches);

            return matches
                ? new FieldValidationResult("Passed",
                    $"Verified — {entity} {matchField}: {displayErp}",
                    "DynamicErp", billResult.Data)
                : new FieldValidationResult("Warning",
                    $"Mismatch — document has '{displayExtracted}' but Acumatica {entity} {matchField} is '{displayErp}'.",
                    "DynamicErp", billResult.Data);
        }

        skipCrossField:
        // For Bill/invoice VendorRef lookups without a DependentFieldKey, use vendor-filtered
        // lookup when ResolvedVendorId is available — prevents matching a same-ref invoice from
        // a different vendor.
        ErpLookupResult<IReadOnlyDictionary<string, string>> result;
        if (isInvoiceEntity &&
            matchField.Equals("VendorRef", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_vendorContext.ResolvedVendorId))
        {
            result = await _erp.LookupBillByVendorRefAndVendorIdAsync(value, _vendorContext.ResolvedVendorId, ct);
            if (!result.Found)
            {
                _logger.LogWarning(
                    "Vendor-filtered bill lookup returned not found for VendorRef='{Value}' VendorId='{VendorId}' — falling back to unfiltered",
                    value, _vendorContext.ResolvedVendorId);
                result = await _erp.LookupGenericAsync(entity, matchField, value, ct);
            }
        }
        else
        {
            result = await _erp.LookupGenericAsync(entity, matchField, value, ct);
        }

        if (!result.Found)
            return new FieldValidationResult("Failed",
                $"'{value}' not found in Acumatica {entity}.{matchField}. {result.ErrorMessage}",
                "DynamicErp");

        string? refNbr = null;
        result.Data?.TryGetValue("RefNbr", out refNbr);
        bool hasRefNbr = !string.IsNullOrWhiteSpace(refNbr);

        string successMsg = hasRefNbr
            ? $"Invoice verified — RefNbr: {refNbr}"
            : $"Verified in {entity} — {matchField}: {value}";

        // For invoice/bill results without a vendor filter, block if vendor validation already failed.
        // But only do this for the main identifier (e.g., VendorRef or ApInvoiceNbr), not for
        // dependent cross-fields (like Amount/Date) which shouldn't throw a vendor error if they match the retrieved record.
        if ((isInvoiceEntity || hasRefNbr) && _vendorContext.VendorValidationFailed && matchField.Equals("VendorRef", StringComparison.OrdinalIgnoreCase))
            return new FieldValidationResult("Warning",
                $"{successMsg}, but vendor validation failed — cannot confirm this record belongs to the correct vendor.",
                "DynamicErp", result.Data);

        return new FieldValidationResult("Passed", successMsg, "DynamicErp", result.Data);
    }
}
