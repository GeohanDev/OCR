using System.Text.Json;
using Microsoft.Extensions.Logging;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.FieldMapping;
using OcrErpSystem.Application.Validation;
using OcrErpSystem.Domain.Enums;
using OcrErpSystem.Infrastructure.Persistence.Repositories;

namespace OcrErpSystem.Infrastructure.Validation;

public class ValidationService : IValidationService
{
    private readonly OcrResultRepository _ocrRepo;
    private readonly ValidationRepository _validationRepo;
    private readonly FieldMappingRepository _fieldMappingRepo;
    private readonly DocumentRepository _docRepo;
    private readonly VendorRepository _vendorRepo;
    private readonly IEnumerable<IFieldValidator> _validators;
    private readonly IValidationFieldContext _fieldContext;
    private readonly IVendorResolutionContext _vendorContext;
    private readonly ILogger<ValidationService> _logger;

    public ValidationService(
        OcrResultRepository ocrRepo,
        ValidationRepository validationRepo,
        FieldMappingRepository fieldMappingRepo,
        DocumentRepository docRepo,
        VendorRepository vendorRepo,
        IEnumerable<IFieldValidator> validators,
        IValidationFieldContext fieldContext,
        IVendorResolutionContext vendorContext,
        ILogger<ValidationService> logger)
    {
        _ocrRepo = ocrRepo;
        _validationRepo = validationRepo;
        _fieldMappingRepo = fieldMappingRepo;
        _docRepo = docRepo;
        _vendorRepo = vendorRepo;
        _validators = validators;
        _fieldContext = fieldContext;
        _vendorContext = vendorContext;
        _logger = logger;
    }

    public async Task<ValidationSummary> ValidateDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null)
            return new ValidationSummary(documentId, 0, 0, 0, 0, false, []);

        await _validationRepo.DeleteByDocumentIdAsync(documentId, ct);

        // Populate sibling-field context so validators (e.g. ErpApInvoiceValidator) can read
        // other extracted values when their DependentFieldKey is configured.
        _fieldContext.SetFieldValues(BuildFieldValues(ocrResult.ExtractedFields));

        var results = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();

        _fieldContext.SetFieldErpKeys(BuildFieldErpKeys(ocrResult.ExtractedFields));

        // Build a lookup of table fields grouped by their column name, keeping extraction order intact.
        var tableFieldsByName = ocrResult.ExtractedFields
            .Where(f => f.FieldMappingConfig?.AllowMultiple == true)
            .OrderBy(f => f.SortOrder) // Ensure original extraction sequence applies
            .GroupBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Base field values (last value per name — used for header fields).
        var baseFieldValues = BuildFieldValues(ocrResult.ExtractedFields);

        // Sort by DisplayOrder so vendor-name fields (lower order) are validated before
        // invoice-number fields, ensuring IVendorResolutionContext is populated first.
        foreach (var field in ocrResult.ExtractedFields.OrderBy(f => f.FieldMappingConfig?.DisplayOrder ?? int.MaxValue))
        {
            var config = field.FieldMappingConfig;
            if (config is null) continue;

            // For table fields (AllowMultiple), override FieldValues with this row's sibling
            // values so DependentFieldKey cross-checks use the correct per-row value.
            if (config.AllowMultiple && tableFieldsByName.TryGetValue(field.FieldName, out var columnFields))
            {
                // Find the visual row index of THIS field within its column
                int rowIndex = columnFields.FindIndex(f => f.Id == field.Id);

                var rowValues = new Dictionary<string, string>(baseFieldValues, StringComparer.OrdinalIgnoreCase);
                if (rowIndex >= 0)
                {
                    // For each other table column, grab the sibling at the exact same row index
                    foreach (var kvp in tableFieldsByName)
                    {
                        var siblingCol = kvp.Value;
                        if (rowIndex < siblingCol.Count)
                        {
                            var sibling = siblingCol[rowIndex];
                            var v = sibling.CorrectedValue ?? sibling.NormalizedValue ?? sibling.RawValue;
                            if (v != null) rowValues[sibling.FieldName] = v;
                        }
                    }
                }
                _fieldContext.SetFieldValues(rowValues);
            }
            else
            {
                _fieldContext.SetFieldValues(baseFieldValues);
            }

            var fieldDto = MapFieldDto(field);
            var configDto = MapConfigDto(config);

            foreach (var validator in _validators)
            {
                bool shouldRun = validator.RunForAllFields ||
                    (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                     validator.CanHandle(config.ErpMappingKey));
                if (!shouldRun) continue;

                FieldValidationResult vResult;
                try
                {
                    vResult = await validator.ValidateAsync(fieldDto, configDto, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Validator {Validator} failed for field {Field} — recording as Warning",
                        validator.GetType().Name, field.FieldName);
                    vResult = new FieldValidationResult("Warning",
                        $"Validation check unavailable ({validator.GetType().Name}).",
                        validator.GetType().Name.Replace("Validator", ""));
                }

                var dbResult = new Domain.Entities.ValidationResult
                {
                    DocumentId       = documentId,
                    ExtractedFieldId = field.Id,
                    FieldName        = field.FieldName,
                    ValidationType   = vResult.ValidationType,
                    Status           = Enum.Parse<ValidationStatus>(vResult.Status),
                    Message          = vResult.Message,
                    ErpReference     = vResult.ErpReference is not null ? JsonSerializer.Serialize(vResult.ErpReference) : null,
                    ErpResponseField = configDto.ErpResponseField,
                    ValidatedAt      = DateTimeOffset.UtcNow
                };
                results.Add(dbResult);
                resultDtos.Add(new ValidationResultDto(
                    Guid.NewGuid(), documentId, field.Id, field.FieldName,
                    vResult.ValidationType, vResult.Status, vResult.Message,
                    vResult.ErpReference, configDto.ErpResponseField, dbResult.ValidatedAt));
            }
        }

        // Check for required fields that Claude didn't extract at all.
        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        if (doc?.DocumentTypeId is not null)
        {
            var allConfigs = await _fieldMappingRepo.GetFieldMappingsAsync(doc.DocumentTypeId.Value, true, ct);
            var extractedNames = ocrResult.ExtractedFields
                .Select(f => f.FieldName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var cfg in allConfigs.Where(c => c.IsRequired && !c.AllowMultiple && !extractedNames.Contains(c.FieldName)))
            {
                var label = cfg.DisplayLabel ?? cfg.FieldName;
                var dbResult = new Domain.Entities.ValidationResult
                {
                    DocumentId       = documentId,
                    ExtractedFieldId = null,
                    FieldName        = cfg.FieldName,
                    ValidationType   = "Required",
                    Status           = ValidationStatus.Failed,
                    Message          = $"Required field '{label}' was not found in the document.",
                    ErpResponseField = cfg.ErpResponseField,
                    ValidatedAt      = DateTimeOffset.UtcNow
                };
                results.Add(dbResult);
                resultDtos.Add(new ValidationResultDto(
                    Guid.NewGuid(), documentId, null, cfg.FieldName,
                    "Required", "Failed", dbResult.Message,
                    null, cfg.ErpResponseField, dbResult.ValidatedAt));
            }
        }

        await _validationRepo.AddRangeAsync(results, ct);

        // Auto-link document to local vendor when vendor name validation passes
        await TryLinkDocumentVendorAsync(documentId, ct);

        int passed = resultDtos.Count(r => r.Status == "Passed");
        int failed = resultDtos.Count(r => r.Status == "Failed");
        int warnings = resultDtos.Count(r => r.Status == "Warning");

        return new ValidationSummary(documentId, resultDtos.Count, passed, failed, warnings, failed == 0, resultDtos);
    }

    public async Task<IReadOnlyList<ValidationResultDto>> ValidateFieldAsync(
        Guid documentId, Guid extractedFieldId, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null) return [];

        var field = ocrResult.ExtractedFields.FirstOrDefault(f => f.Id == extractedFieldId);
        if (field is null) return [];

        var config = field.FieldMappingConfig;
        if (config is null) return [];

        // For table fields, use this row's sibling values so DependentFieldKey cross-checks
        // use the correct per-row value. For header fields, use the global last-value map.
        var allFieldValues = BuildFieldValues(ocrResult.ExtractedFields);
        if (config.AllowMultiple)
        {
            var tableFieldsByName = ocrResult.ExtractedFields
                .Where(f => f.FieldMappingConfig?.AllowMultiple == true)
                .OrderBy(f => f.SortOrder)
                .GroupBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var rowValues = new Dictionary<string, string>(allFieldValues, StringComparer.OrdinalIgnoreCase);
            
            if (tableFieldsByName.TryGetValue(field.FieldName, out var columnFields))
            {
                int rowIndex = columnFields.FindIndex(f => f.Id == field.Id);
                if (rowIndex >= 0)
                {
                    foreach (var kvp in tableFieldsByName)
                    {
                        var siblingCol = kvp.Value;
                        if (rowIndex < siblingCol.Count)
                        {
                            var sibling = siblingCol[rowIndex];
                            var v = sibling.CorrectedValue ?? sibling.NormalizedValue ?? sibling.RawValue;
                            if (v != null) rowValues[sibling.FieldName] = v;
                        }
                    }
                }
            }
            _fieldContext.SetFieldValues(rowValues);
        }
        else
        {
            _fieldContext.SetFieldValues(allFieldValues);
        }
        _fieldContext.SetFieldErpKeys(BuildFieldErpKeys(ocrResult.ExtractedFields));

        // Pre-populate vendor context from previously saved validation results.
        // This ensures invoice validators see vendor failures even in single-field validation,
        // where ErpVendorNameValidator doesn't run in the same request.
        await PrePopulateVendorContextAsync(documentId, ct);

        await _validationRepo.DeleteByExtractedFieldIdAsync(extractedFieldId, ct);

        var results = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();

        var fieldDto = MapFieldDto(field);
        var configDto = MapConfigDto(config);

        foreach (var validator in _validators)
        {
            bool shouldRun = validator.RunForAllFields ||
                (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                 validator.CanHandle(config.ErpMappingKey));
            if (!shouldRun) continue;

            FieldValidationResult vResult;
            try { vResult = await validator.ValidateAsync(fieldDto, configDto, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Validator {V} failed for field {F} — recording as Warning",
                    validator.GetType().Name, field.FieldName);
                vResult = new FieldValidationResult("Warning",
                    $"Validation check unavailable ({validator.GetType().Name}).",
                    validator.GetType().Name.Replace("Validator", ""));
            }

            var dbResult = new Domain.Entities.ValidationResult
            {
                DocumentId       = documentId,
                ExtractedFieldId = field.Id,
                FieldName        = field.FieldName,
                ValidationType   = vResult.ValidationType,
                Status           = Enum.Parse<ValidationStatus>(vResult.Status),
                Message          = vResult.Message,
                ErpReference     = vResult.ErpReference is not null ? JsonSerializer.Serialize(vResult.ErpReference) : null,
                ErpResponseField = configDto.ErpResponseField,
                ValidatedAt      = DateTimeOffset.UtcNow
            };
            results.Add(dbResult);
            resultDtos.Add(new ValidationResultDto(
                Guid.NewGuid(), documentId, field.Id, field.FieldName,
                vResult.ValidationType, vResult.Status, vResult.Message,
                vResult.ErpReference, configDto.ErpResponseField, dbResult.ValidatedAt));
        }

        await _validationRepo.AddRangeAsync(results, ct);
        return resultDtos;
    }

    public async Task<IReadOnlyList<ValidationResultDto>> GetValidationResultsAsync(Guid documentId, CancellationToken ct = default)
    {
        var results = await _validationRepo.GetByDocumentIdAsync(documentId, ct);
        return results.Select(r => new ValidationResultDto(
            r.Id, r.DocumentId, r.ExtractedFieldId, r.FieldName,
            r.ValidationType, r.Status.ToString(), r.Message,
            r.ErpReference is not null ? JsonSerializer.Deserialize<object>(r.ErpReference) : null,
            r.ErpResponseField, r.ValidatedAt)).ToList();
    }

    public async Task<ApprovalEligibility> CheckApprovalEligibilityAsync(Guid documentId, CancellationToken ct = default)
    {
        var hasBlocking = await _validationRepo.HasBlockingFailuresAsync(documentId, ct);
        if (!hasBlocking) return new ApprovalEligibility(true, []);

        var results = await _validationRepo.GetByDocumentIdAsync(documentId, ct);
        var blockingFields = results
            .Where(r => r.Status == ValidationStatus.Failed)
            .Select(r => r.FieldName)
            .Distinct()
            .ToList();

        return new ApprovalEligibility(false, blockingFields);
    }

    private static ExtractedFieldDto MapFieldDto(Domain.Entities.ExtractedField f) =>
        new(f.Id, f.FieldName, f.RawValue, f.NormalizedValue,
            f.Confidence.HasValue ? (double)f.Confidence.Value : null,
            null, f.IsManuallyCorreected, f.CorrectedValue);

    private static FieldMappingConfigDto MapConfigDto(Domain.Entities.FieldMappingConfig c) =>
        new(c.Id, c.DocumentTypeId, c.FieldName, c.DisplayLabel, c.RegexPattern,
            c.KeywordAnchor, c.PositionRule, c.IsRequired, c.AllowMultiple, c.ErpMappingKey,
            c.ErpResponseField, (double)c.ConfidenceThreshold, c.DisplayOrder, c.IsActive, c.CreatedAt, c.UpdatedAt,
            c.DependentFieldKey);

    private static IReadOnlyDictionary<string, string> BuildFieldValues(
        IEnumerable<Domain.Entities.ExtractedField> fields)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            var value = f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue;
            if (value is not null)
                dict[f.FieldName] = value;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, string> BuildFieldErpKeys(
        IEnumerable<Domain.Entities.ExtractedField> fields)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            if (!string.IsNullOrWhiteSpace(f.FieldMappingConfig?.ErpMappingKey))
                dict[f.FieldName] = f.FieldMappingConfig!.ErpMappingKey;
        }
        return dict;
    }

    /// <summary>
    /// After full-document validation, link the document to the local vendor record if available,
    /// or fall back to the extracted vendorName OCR field so group-by-vendor works regardless of sync status.
    /// </summary>
    private async Task TryLinkDocumentVendorAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        if (doc is null) return;

        var resolvedId = _vendorContext.ResolvedVendorId;
        if (!string.IsNullOrWhiteSpace(resolvedId))
        {
            var vendor = await _vendorRepo.GetByAcumaticaIdAsync(resolvedId, ct);
            if (vendor is not null)
            {
                doc.VendorId   = vendor.Id;
                doc.VendorName = vendor.VendorName;
                await _docRepo.UpdateAsync(doc, ct);
                _logger.LogInformation("Document {DocumentId} linked to vendor {VendorName} ({AcumaticaId})",
                    documentId, vendor.VendorName, vendor.AcumaticaVendorId);
                return;
            }
        }

        // Fallback: no local vendor record yet (sync pending) — use the extracted vendorName field
        // so documents can still be grouped by vendor in the document list.
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        var vendorField = ocrResult?.ExtractedFields
            .FirstOrDefault(f => f.FieldName.Equals("vendorName", StringComparison.OrdinalIgnoreCase));
        var extractedName = (vendorField?.CorrectedValue ?? vendorField?.NormalizedValue ?? vendorField?.RawValue)?.Trim();

        if (!string.IsNullOrWhiteSpace(extractedName) && doc.VendorName != extractedName)
        {
            doc.VendorName = extractedName;
            await _docRepo.UpdateAsync(doc, ct);
            _logger.LogInformation("Document {DocumentId} vendor name set from OCR field: '{VendorName}'",
                documentId, extractedName);
        }
    }

    /// <summary>
    /// Reads saved vendor-name validation results and pre-populates IVendorResolutionContext.
    /// Used in single-field validation so invoice validators can see whether vendor previously failed.
    /// </summary>
    private async Task PrePopulateVendorContextAsync(Guid documentId, CancellationToken ct)
    {
        var saved = await _validationRepo.GetByDocumentIdAsync(documentId, ct);
        var vendorResult = saved.FirstOrDefault(r => r.ValidationType == "ErpVendorName");
        if (vendorResult is null) return;

        if (vendorResult.Status == Domain.Enums.ValidationStatus.Failed)
        {
            _vendorContext.VendorValidationFailed = true;
        }
        else if (vendorResult.Status == Domain.Enums.ValidationStatus.Passed &&
                 vendorResult.ErpReference is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(vendorResult.ErpReference);
                if (doc.RootElement.TryGetProperty("VendorId", out var vid))
                    _vendorContext.ResolvedVendorId = vid.GetString();
            }
            catch { /* ignore malformed JSON */ }
        }
    }
}
