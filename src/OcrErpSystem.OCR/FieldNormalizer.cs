using System.Globalization;
using System.Text.RegularExpressions;

namespace OcrErpSystem.OCR;

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

        return erpMappingKey?.ToUpperInvariant() switch
        {
            "VENDORID" => trimmed.ToUpperInvariant().Replace(" ", ""),
            "CURRENCYID" => trimmed.ToUpperInvariant().Trim(),
            "BRANCHID" => trimmed.ToUpperInvariant().Trim(),
            "PONUMBER" or "PURCHASEORDER" => trimmed.Replace(" ", "").ToUpperInvariant(),
            "AMOUNT" or "TOTALAMOUNT" => NormalizeAmount(trimmed),
            _ => trimmed
        };
    }

    private static string NormalizeDate(string raw)
    {
        // dd/MM/yyyy is the standard format in Malaysia — list it first so it takes priority
        // over MM/dd/yyyy when both could match (e.g. "05/11/2025").
        var formats = new[]
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy",
            "dd-MM-yyyy", "d-M-yyyy", "dd-M-yyyy", "d-MM-yyyy",
            "dd.MM.yyyy", "d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd",
            "dd MMM yyyy", "d MMM yyyy",
            "d-M-yy", "dd/MM/yy", "dd.MM.yy",
        };
        if (DateOnly.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
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
