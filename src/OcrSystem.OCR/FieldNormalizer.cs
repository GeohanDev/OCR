using System.Globalization;
using System.Text.RegularExpressions;

namespace OcrSystem.OCR;

public interface IFieldNormalizer
{
    string? Normalize(string? rawValue, string? erpMappingKey, string? fieldName = null);
}

public class FieldNormalizer : IFieldNormalizer
{
    public string? Normalize(string? rawValue, string? erpMappingKey, string? fieldName = null)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;
        var trimmed = rawValue.Trim();

        // Detect date fields by ERP key or by field name containing "date".
        bool isDateField =
            erpMappingKey?.ToUpperInvariant() is "DATE" or "STATEMENTDATE" ||
            (fieldName?.IndexOf("date", StringComparison.OrdinalIgnoreCase) >= 0);

        if (isDateField) return NormalizeDate(trimmed);

        var normalized = erpMappingKey?.ToUpperInvariant() switch
        {
            "VENDORID" => trimmed.ToUpperInvariant().Replace(" ", ""),
            "CURRENCYID" => trimmed.ToUpperInvariant().Trim(),
            "BRANCHID" => trimmed.ToUpperInvariant().Trim(),
            "PONUMBER" or "PURCHASEORDER" => trimmed.Replace(" ", "").ToUpperInvariant(),
            "AMOUNT" or "TOTALAMOUNT" => NormalizeAmount(trimmed),
            _ => trimmed
        };

        // Apply company-name normalization to any vendor/company name field.
        // Removes dots from Malaysian company suffixes: SDN. BHD. → SDN BHD,
        // PLT. → PLT, BHD. → BHD, etc.
        bool isVendorNameField =
            erpMappingKey?.IndexOf("vendor", StringComparison.OrdinalIgnoreCase) >= 0 ||
            erpMappingKey?.IndexOf("company", StringComparison.OrdinalIgnoreCase) >= 0 ||
            fieldName?.IndexOf("vendor", StringComparison.OrdinalIgnoreCase) >= 0 ||
            fieldName?.IndexOf("company", StringComparison.OrdinalIgnoreCase) >= 0 ||
            fieldName?.IndexOf("supplier", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isVendorNameField)
            normalized = NormalizeCompanyName(normalized);

        return normalized;
    }

    // Normalises Malaysian/Singapore company name suffixes:
    //   "SDN. BHD."  → "SDN BHD"
    //   "SDN.BHD."   → "SDN BHD"
    //   "SDN BHD."   → "SDN BHD"
    //   "BHD."       → "BHD"
    //   "PLT."       → "PLT"
    //   "(M) SDN BHD" is kept as-is (parentheses preserved).
    // Also collapses any double-spaces produced by dot removal.
    private static string NormalizeCompanyName(string raw)
    {
        // Remove trailing dot from each company-suffix token
        var result = Regex.Replace(
            raw,
            @"\b(SDN|BHD|PLT|PTE|LTD|INC|LLC|CORP|SDN\.?BHD)\.",
            m => m.Value.TrimEnd('.'),
            RegexOptions.IgnoreCase);

        // Collapse "SDN BHD" variants — handles "SDN.BHD" leftover after first pass
        result = Regex.Replace(result, @"\bSDN\.BHD\b", "SDN BHD", RegexOptions.IgnoreCase);

        // Collapse multiple spaces
        result = Regex.Replace(result, @"  +", " ").Trim();

        return result;
    }

    private static string NormalizeDate(string raw)
    {
        // Normalise to Title-Case so InvariantCulture can parse abbreviated month names
        // regardless of what case OCR produces (e.g. "NOV" → "Nov", "nov" → "Nov").
        var normalised = System.Text.RegularExpressions.Regex.Replace(
            raw,
            @"[A-Za-z]+",
            m => char.ToUpperInvariant(m.Value[0]) + m.Value.Substring(1).ToLowerInvariant());

        // dd/MM/yyyy is the standard format in Malaysia — list it first so it takes priority
        // over MM/dd/yyyy when both could match (e.g. "05/11/2025").
        var formats = new[]
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy",
            "dd-MM-yyyy", "d-M-yyyy", "dd-M-yyyy", "d-MM-yyyy",
            "dd.MM.yyyy", "d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd",
            // Abbreviated month name with space separator (4-digit year)
            "dd MMM yyyy", "d MMM yyyy",
            // Abbreviated month name with dash separator (2-digit and 4-digit year)
            // Handles OCR output like "07-NOV-24" or "7-Nov-2024"
            "dd-MMM-yy", "d-MMM-yy", "dd-MMM-yyyy", "d-MMM-yyyy",
            // Abbreviated month name with dot separator
            "dd.MMM.yy", "d.MMM.yy", "dd.MMM.yyyy", "d.MMM.yyyy",
            "dd MMMM yyyy", "d MMMM yyyy",
            "MMMM d, yyyy", "MMMM dd, yyyy",
            "MMM d, yyyy", "MMM dd, yyyy",
            "d MMM, yyyy", "dd MMM, yyyy",
            "d MMMM, yyyy", "dd MMMM, yyyy",
            "d-M-yy", "dd/MM/yy", "dd.MM.yy",
        };
        if (DateOnly.TryParseExact(normalised, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("dd/MM/yyyy");
        // Fallback only for ISO 8601 strings (4-digit year, dash separator) — avoids
        // misreading dd/MM/yyyy OCR values as MM/dd/yyyy via culture-aware parsing.
        if (raw.Length >= 10 && raw[4] == '-' &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt.DateTime).ToString("dd/MM/yyyy");
        return raw;
    }

    private static string NormalizeAmount(string raw)
    {
        var cleaned = Regex.Replace(raw, @"[^\d.,\-]", "").Replace(",", "");
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            return amount.ToString("F2", CultureInfo.InvariantCulture);
        return raw;
    }
}
