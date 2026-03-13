namespace OcrSystem.Application.Validation;

/// <summary>
/// Scoped service that holds the effective values of all extracted fields for the document
/// currently being validated. Populated by ValidationService before each validation run so
/// individual validators can access sibling field values (e.g. AP invoice validator reading
/// the vendor name field to cross-check ownership).
/// </summary>
public interface IValidationFieldContext
{
    /// <summary>Field name → effective value (CorrectedValue ?? NormalizedValue ?? RawValue).</summary>
    IReadOnlyDictionary<string, string> FieldValues { get; }

    /// <summary>Field name → ErpMappingKey (e.g. "Bill:VendorRef"), so validators can
    /// look up a sibling field's Acumatica entity and field name for cross-field verification.</summary>
    IReadOnlyDictionary<string, string> FieldErpKeys { get; }

    void SetFieldValues(IReadOnlyDictionary<string, string> values);
    void SetFieldErpKeys(IReadOnlyDictionary<string, string> erpKeys);
}
