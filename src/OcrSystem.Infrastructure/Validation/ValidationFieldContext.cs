using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation;

public class ValidationFieldContext : IValidationFieldContext
{
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _erpKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> FieldValues => _values;
    public IReadOnlyDictionary<string, string> FieldErpKeys => _erpKeys;

    public void SetFieldValues(IReadOnlyDictionary<string, string> values) => _values = values;
    public void SetFieldErpKeys(IReadOnlyDictionary<string, string> erpKeys) => _erpKeys = erpKeys;
}
