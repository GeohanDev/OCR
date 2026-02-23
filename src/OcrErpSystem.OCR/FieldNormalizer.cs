using System.Globalization;
using System.Text.RegularExpressions;

namespace OcrErpSystem.OCR;

public interface IFieldNormalizer
{
    string? Normalize(string? rawValue, string? erpMappingKey);
}

public class FieldNormalizer : IFieldNormalizer
{
    public string? Normalize(string? rawValue, string? erpMappingKey)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;
        var trimmed = rawValue.Trim();

        return erpMappingKey?.ToUpperInvariant() switch
        {
            "VENDORID" => trimmed.ToUpperInvariant().Replace(" ", ""),
            "CURRENCYID" => trimmed.ToUpperInvariant().Trim(),
            "BRANCHID" => trimmed.ToUpperInvariant().Trim(),
            "PONUMBER" or "PURCHASEORDER" => trimmed.Replace(" ", "").ToUpperInvariant(),
            "DATE" or "STATEMENTDATE" => NormalizeDate(trimmed),
            "AMOUNT" or "TOTALAMOUNT" => NormalizeAmount(trimmed),
            _ => trimmed
        };
    }

    private static string NormalizeDate(string raw)
    {
        var formats = new[] { "d/M/yyyy", "M/d/yyyy", "dd-MM-yyyy", "MM-dd-yyyy",
                               "yyyy-MM-dd", "d-M-yy", "M/d/yy", "dd/MM/yyyy" };
        if (DateTimeOffset.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd");
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt2))
            return dt2.ToString("yyyy-MM-dd");
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
