using System.Text.Json;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.FieldMapping;
using OcrSystem.Application.Validation;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;
using DocumentCategoryEnum = OcrSystem.Domain.Enums.DocumentCategory;

namespace OcrSystem.Infrastructure.Validation;

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
    private readonly IErpInvoiceContext _invoiceContext;
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
        IErpInvoiceContext invoiceContext,
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
        _invoiceContext = invoiceContext;
        _logger = logger;
    }

    public async Task<ValidationSummary> ValidateDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null)
            return new ValidationSummary(documentId, 0, 0, 0, 0, false, []);

        // Resolve document category to gate category-specific validators.
        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        var docCategory = DocumentCategoryEnum.General;
        if (doc?.DocumentTypeId is not null)
        {
            var docType = await _fieldMappingRepo.GetDocumentTypeByIdAsync(doc.DocumentTypeId.Value, ct);
            if (docType is not null) docCategory = docType.Category;
        }

        await _validationRepo.DeleteByDocumentIdAsync(documentId, ct);

        // Populate sibling-field context so validators (e.g. ErpApInvoiceValidator) can read
        // other extracted values when their DependentFieldKey is configured.
        _fieldContext.SetFieldValues(BuildFieldValues(ocrResult.ExtractedFields));

        var results = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();
        var fieldResults = new List<Domain.Entities.ValidationResult>();

        _fieldContext.SetFieldErpKeys(BuildFieldErpKeys(ocrResult.ExtractedFields));

        // Build a lookup of table fields grouped by their column name, keeping extraction order intact.
        var tableFieldsByName = ocrResult.ExtractedFields
            .Where(f => f.FieldMappingConfig?.AllowMultiple == true)
            .OrderBy(f => f.SortOrder)
            .GroupBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Base field values (last value per name — used for header fields).
        var baseFieldValues = BuildFieldValues(ocrResult.ExtractedFields);

        // ── Phase 1: Header (non-AllowMultiple) fields ───────────────────────────
        // Validate in DisplayOrder so vendor-name resolves before invoice fields.
        _fieldContext.SetFieldValues(baseFieldValues);

        foreach (var field in ocrResult.ExtractedFields
            .Where(f => f.FieldMappingConfig?.AllowMultiple != true)
            .OrderBy(f => f.FieldMappingConfig?.DisplayOrder ?? int.MaxValue))
        {
            var config = field.FieldMappingConfig;
            if (config is null) continue;

            fieldResults.Clear();
            var fieldDto  = MapFieldDto(field);
            var configDto = MapConfigDto(config);

            foreach (var validator in _validators)
            {
                bool shouldRun = validator.RunForAllFields ||
                    (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                     validator.CanHandle(config.ErpMappingKey));
                if (!shouldRun) continue;
                if (validator.RequiresCategory.HasValue && validator.RequiresCategory.Value != docCategory)
                    continue;

                FieldValidationResult vResult;
                try { vResult = await validator.ValidateAsync(fieldDto, configDto, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Validator {V} failed for field {F} — recording as Warning",
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
                fieldResults.Add(dbResult);
                resultDtos.Add(new ValidationResultDto(
                    Guid.NewGuid(), documentId, field.Id, field.FieldName,
                    vResult.ValidationType, vResult.Status, vResult.Message,
                    vResult.ErpReference, configDto.ErpResponseField, dbResult.ValidatedAt));
            }

            if (fieldResults.Count > 0)
            {
                await _validationRepo.AddRangeAsync(fieldResults, ct);
                results.AddRange(fieldResults);
            }
        }

        // ── Phase 2: Table rows — one complete row at a time ─────────────────────
        // Enable invoice caching so ErpApInvoiceValidator fetches once per row and
        // DynamicErpValidator reuses the same record for date/amount validation.
        _invoiceContext.IsRowValidation = true;

        int rowCount = tableFieldsByName.Values.Count > 0
            ? tableFieldsByName.Values.Max(col => col.Count)
            : 0;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            // Reset per-row invoice cache so row N doesn't bleed into row N+1.
            // Vendor ID stays resolved (header vendor applies to all rows).
            _invoiceContext.ClearCache();

            // Build field-value context for this row: start from header values,
            // then overlay each column's value at rowIndex.
            var rowValues = new Dictionary<string, string>(baseFieldValues, StringComparer.OrdinalIgnoreCase);
            foreach (var col in tableFieldsByName.Values)
            {
                if (rowIndex < col.Count)
                {
                    var f = col[rowIndex];
                    var v = f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue;
                    if (v != null) rowValues[f.FieldName] = v;
                }
            }
            _fieldContext.SetFieldValues(rowValues);

            // Within the row, process the invoice-number field first so its ERP fetch
            // populates the cache before date/amount validators run.
            var rowFields = tableFieldsByName.Values
                .Where(col => rowIndex < col.Count)
                .Select(col => col[rowIndex])
                .OrderBy(f => f.FieldMappingConfig?.ErpMappingKey == "ApInvoiceNbr" ||
                               f.FieldMappingConfig?.ErpMappingKey?.EndsWith(":VendorRef", StringComparison.OrdinalIgnoreCase) == true
                               ? 0 : 1)
                .ThenBy(f => f.FieldMappingConfig?.DisplayOrder ?? int.MaxValue);

            foreach (var field in rowFields)
            {
                var config = field.FieldMappingConfig;
                if (config is null) continue;

                fieldResults.Clear();
                var fieldDto  = MapFieldDto(field);
                var configDto = MapConfigDto(config);

                foreach (var validator in _validators)
                {
                    bool shouldRun = validator.RunForAllFields ||
                        (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                         validator.CanHandle(config.ErpMappingKey));
                    if (!shouldRun) continue;
                    if (validator.RequiresCategory.HasValue && validator.RequiresCategory.Value != docCategory)
                        continue;

                    FieldValidationResult vResult;
                    try { vResult = await validator.ValidateAsync(fieldDto, configDto, ct); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Validator {V} failed for field {F} — recording as Warning",
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
                    fieldResults.Add(dbResult);
                    resultDtos.Add(new ValidationResultDto(
                        Guid.NewGuid(), documentId, field.Id, field.FieldName,
                        vResult.ValidationType, vResult.Status, vResult.Message,
                        vResult.ErpReference, configDto.ErpResponseField, dbResult.ValidatedAt));
                }

                if (fieldResults.Count > 0)
                {
                    await _validationRepo.AddRangeAsync(fieldResults, ct);
                    results.AddRange(fieldResults);
                }
            }
        }

        // Check for required fields that Claude didn't extract at all.
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
                await _validationRepo.AddRangeAsync([dbResult], ct);
                results.Add(dbResult);
                resultDtos.Add(new ValidationResultDto(
                    Guid.NewGuid(), documentId, null, cfg.FieldName,
                    "Required", "Failed", dbResult.Message,
                    null, cfg.ErpResponseField, dbResult.ValidatedAt));
            }
        }

        // Auto-link document to local vendor when vendor name validation passes
        await TryLinkDocumentVendorAsync(documentId, ct);

        int passed = resultDtos.Count(r => r.Status == "Passed");
        int failed = resultDtos.Count(r => r.Status == "Failed");
        int warnings = resultDtos.Count(r => r.Status == "Warning");

        return new ValidationSummary(documentId, resultDtos.Count, passed, failed, warnings, failed == 0, resultDtos);
    }

    /// <summary>
    /// Re-validates only table (AllowMultiple) rows, preserving existing header field results.
    /// Used by the "Validate Table" queue job so header results don't need to be re-run.
    /// Vendor context is pre-populated from saved header validation results.
    /// </summary>
    public async Task<ValidationSummary> ValidateTableRowsAsync(Guid documentId, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null) return new ValidationSummary(documentId, 0, 0, 0, 0, false, []);

        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        var docCategory = DocumentCategoryEnum.General;
        if (doc?.DocumentTypeId is not null)
        {
            var docType = await _fieldMappingRepo.GetDocumentTypeByIdAsync(doc.DocumentTypeId.Value, ct);
            if (docType is not null) docCategory = docType.Category;
        }

        var baseFieldValues = BuildFieldValues(ocrResult.ExtractedFields);
        _fieldContext.SetFieldValues(baseFieldValues);
        _fieldContext.SetFieldErpKeys(BuildFieldErpKeys(ocrResult.ExtractedFields));

        // Restore vendor context from previously saved header validation results so
        // invoice and cross-field validators use the correct vendor-filtered lookups.
        await PrePopulateVendorContextAsync(documentId, ct);

        var tableFieldsByName = ocrResult.ExtractedFields
            .Where(f => f.FieldMappingConfig?.AllowMultiple == true)
            .OrderBy(f => f.SortOrder)
            .GroupBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Delete existing results for table fields only — header results are kept intact.
        foreach (var col in tableFieldsByName.Values)
            foreach (var field in col)
                await _validationRepo.DeleteByExtractedFieldIdAsync(field.Id, ct);

        _invoiceContext.IsRowValidation = true;

        int rowCount = tableFieldsByName.Values.Count > 0
            ? tableFieldsByName.Values.Max(col => col.Count)
            : 0;

        var results    = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();
        var fieldResults = new List<Domain.Entities.ValidationResult>();

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            _invoiceContext.ClearCache();

            var rowValues = new Dictionary<string, string>(baseFieldValues, StringComparer.OrdinalIgnoreCase);
            foreach (var col in tableFieldsByName.Values)
            {
                if (rowIndex < col.Count)
                {
                    var f = col[rowIndex];
                    var v = f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue;
                    if (v != null) rowValues[f.FieldName] = v;
                }
            }
            _fieldContext.SetFieldValues(rowValues);

            var rowFields = tableFieldsByName.Values
                .Where(col => rowIndex < col.Count)
                .Select(col => col[rowIndex])
                .OrderBy(f => f.FieldMappingConfig?.ErpMappingKey == "ApInvoiceNbr" ||
                               f.FieldMappingConfig?.ErpMappingKey?.EndsWith(":VendorRef", StringComparison.OrdinalIgnoreCase) == true
                               ? 0 : 1)
                .ThenBy(f => f.FieldMappingConfig?.DisplayOrder ?? int.MaxValue);

            foreach (var field in rowFields)
            {
                var config = field.FieldMappingConfig;
                if (config is null) continue;

                fieldResults.Clear();
                var fieldDto  = MapFieldDto(field);
                var configDto = MapConfigDto(config);

                foreach (var validator in _validators)
                {
                    bool shouldRun = validator.RunForAllFields ||
                        (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                         validator.CanHandle(config.ErpMappingKey));
                    if (!shouldRun) continue;
                    if (validator.RequiresCategory.HasValue && validator.RequiresCategory.Value != docCategory)
                        continue;

                    FieldValidationResult vResult;
                    try { vResult = await validator.ValidateAsync(fieldDto, configDto, ct); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Validator {V} failed for field {F} — recording as Warning",
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
                    fieldResults.Add(dbResult);
                    resultDtos.Add(new ValidationResultDto(
                        Guid.NewGuid(), documentId, field.Id, field.FieldName,
                        vResult.ValidationType, vResult.Status, vResult.Message,
                        vResult.ErpReference, configDto.ErpResponseField, dbResult.ValidatedAt));
                }

                if (fieldResults.Count > 0)
                {
                    await _validationRepo.AddRangeAsync(fieldResults, ct);
                    results.AddRange(fieldResults);
                }
            }
        }

        int passed   = resultDtos.Count(r => r.Status == "Passed");
        int failed   = resultDtos.Count(r => r.Status == "Failed");
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

        // Resolve document category for category-specific validator gating.
        var docForField = await _docRepo.GetByIdAsync(documentId, ct);
        var docCategoryForField = DocumentCategoryEnum.General;
        if (docForField?.DocumentTypeId is not null)
        {
            var docType = await _fieldMappingRepo.GetDocumentTypeByIdAsync(docForField.DocumentTypeId.Value, ct);
            if (docType is not null) docCategoryForField = docType.Category;
        }

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

            if (validator.RequiresCategory.HasValue && validator.RequiresCategory.Value != docCategoryForField)
                continue;

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

    public async Task<IReadOnlyList<ValidationResultDto>> ValidateRowAsync(
        Guid documentId, IReadOnlyList<Guid> fieldIds, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null) return [];

        var rowFields = ocrResult.ExtractedFields
            .Where(f => fieldIds.Contains(f.Id))
            // Process ApInvoiceNbr first so ErpApInvoiceValidator populates the invoice cache
            // and ResolvedVendorId before DynamicErpValidator runs cross-field checks.
            // Without this, Date/Amount fields validated first fall to LookupGenericAsync (2 calls).
            .OrderBy(f => f.FieldMappingConfig?.ErpMappingKey == "ApInvoiceNbr" ? 0 : 1)
            .ThenBy(f => f.FieldMappingConfig?.DisplayOrder ?? int.MaxValue)
            .ToList();

        if (rowFields.Count == 0) return [];

        // Resolve document category for category-specific validator gating.
        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        var docCategory = DocumentCategoryEnum.General;
        if (doc?.DocumentTypeId is not null)
        {
            var docType = await _fieldMappingRepo.GetDocumentTypeByIdAsync(doc.DocumentTypeId.Value, ct);
            if (docType is not null) docCategory = docType.Category;
        }

        // Activate invoice caching so ErpApInvoiceValidator populates it and
        // DynamicErpValidator reuses it without a second ERP call.
        _invoiceContext.IsRowValidation = true;

        // Build field value context: start from all extracted fields, then override with this row's values.
        var allFieldValues = BuildFieldValues(ocrResult.ExtractedFields);
        var rowValues = new Dictionary<string, string>(allFieldValues, StringComparer.OrdinalIgnoreCase);
        foreach (var f in rowFields)
        {
            var v = f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue;
            if (v is not null) rowValues[f.FieldName] = v;
        }
        _fieldContext.SetFieldValues(rowValues);
        _fieldContext.SetFieldErpKeys(BuildFieldErpKeys(ocrResult.ExtractedFields));

        // Pre-populate vendor context from previously saved validation results.
        await PrePopulateVendorContextAsync(documentId, ct);

        // Delete existing results for these fields.
        foreach (var fieldId in fieldIds)
            await _validationRepo.DeleteByExtractedFieldIdAsync(fieldId, ct);

        var results = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();

        foreach (var field in rowFields)
        {
            var config = field.FieldMappingConfig;
            if (config is null) continue;

            var fieldDto = MapFieldDto(field);
            var configDto = MapConfigDto(config);

            foreach (var validator in _validators)
            {
                bool shouldRun = validator.RunForAllFields ||
                    (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                     validator.CanHandle(config.ErpMappingKey));
                if (!shouldRun) continue;

                if (validator.RequiresCategory.HasValue && validator.RequiresCategory.Value != docCategory)
                    continue;

                FieldValidationResult vResult;
                try { vResult = await validator.ValidateAsync(fieldDto, configDto, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Validator {V} failed for field {F} — recording as Warning",
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

        await _validationRepo.AddRangeAsync(results, ct);
        return resultDtos;
    }

    /// <summary>
    /// Reads saved vendor-name validation results and pre-populates IVendorResolutionContext.
    /// Used in single-field validation so invoice validators can see whether vendor previously failed.
    /// </summary>
    private async Task PrePopulateVendorContextAsync(Guid documentId, CancellationToken ct)
    {
        var saved = await _validationRepo.GetByDocumentIdAsync(documentId, ct);

        // ── Step 1: ErpVendorName (from ErpVendorNameValidator, ErpMappingKey = "VendorName") ──
        var vendorResult = saved.FirstOrDefault(r => r.ValidationType == "ErpVendorName");
        if (vendorResult?.Status == Domain.Enums.ValidationStatus.Failed)
        {
            _vendorContext.VendorValidationFailed = true;
            return;
        }
        // Accept Passed and Warning (inactive vendor still has a valid VendorId).
        if (vendorResult?.Status is Domain.Enums.ValidationStatus.Passed or Domain.Enums.ValidationStatus.Warning &&
            vendorResult.ErpReference is not null &&
            TryParseVendorId(vendorResult.ErpReference, out var vid1))
        {
            _vendorContext.ResolvedVendorId = vid1;
            return;
        }

        // ── Step 2: DynamicErp results that contain a VendorId property ──
        // Covers Vendor:VendorName mapping (stores VendorDto) and Bill:VendorRef with
        // DependentFieldKey (stores ApInvoiceDto). Both have "VendorId" in JSON.
        // Cross-field bill dicts use "Vendor" not "VendorId", so they are naturally excluded.
        foreach (var r in saved
            .Where(r => r.ValidationType == "DynamicErp" &&
                        r.Status == Domain.Enums.ValidationStatus.Passed &&
                        r.ErpReference is not null)
            .OrderByDescending(r => r.ValidatedAt))
        {
            if (TryParseVendorId(r.ErpReference!, out var vid2))
            {
                _vendorContext.ResolvedVendorId = vid2;
                return;
            }
        }

        // ── Step 3: ErpApInvoice results (from ErpApInvoiceValidator) ──
        // Priority 2 stores ApInvoiceDto ("VendorId" key).
        // Priority 1 stores raw Acumatica dict ("Vendor" key) — try both.
        var invoiceResult = saved
            .Where(r => r.ValidationType == "ErpApInvoice" &&
                        r.Status == Domain.Enums.ValidationStatus.Passed &&
                        r.ErpReference is not null)
            .OrderByDescending(r => r.ValidatedAt)
            .FirstOrDefault();

        if (invoiceResult is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(invoiceResult.ErpReference!);
                string? resolvedId = null;
                if (doc.RootElement.TryGetProperty("VendorId", out var v) && !string.IsNullOrWhiteSpace(v.GetString()))
                    resolvedId = v.GetString();
                else if (doc.RootElement.TryGetProperty("Vendor", out var v2) && !string.IsNullOrWhiteSpace(v2.GetString()))
                    resolvedId = v2.GetString();
                if (!string.IsNullOrWhiteSpace(resolvedId))
                    _vendorContext.ResolvedVendorId = resolvedId;
            }
            catch { /* ignore malformed JSON */ }
        }
    }

    /// <summary>Parses a JSON ERP reference and extracts the "VendorId" string if present and non-empty.</summary>
    private static bool TryParseVendorId(string erpReference, out string vendorId)
    {
        vendorId = string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(erpReference);
            if (doc.RootElement.TryGetProperty("VendorId", out var prop) &&
                !string.IsNullOrWhiteSpace(prop.GetString()))
            {
                vendorId = prop.GetString()!;
                return true;
            }
        }
        catch { /* ignore malformed JSON */ }
        return false;
    }
}
