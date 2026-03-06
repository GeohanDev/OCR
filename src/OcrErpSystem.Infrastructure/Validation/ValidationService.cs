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
    private readonly IEnumerable<IFieldValidator> _validators;
    private readonly ILogger<ValidationService> _logger;

    public ValidationService(
        OcrResultRepository ocrRepo,
        ValidationRepository validationRepo,
        FieldMappingRepository fieldMappingRepo,
        DocumentRepository docRepo,
        IEnumerable<IFieldValidator> validators,
        ILogger<ValidationService> logger)
    {
        _ocrRepo = ocrRepo;
        _validationRepo = validationRepo;
        _fieldMappingRepo = fieldMappingRepo;
        _docRepo = docRepo;
        _validators = validators;
        _logger = logger;
    }

    public async Task<ValidationSummary> ValidateDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null)
            return new ValidationSummary(documentId, 0, 0, 0, 0, false, []);

        await _validationRepo.DeleteByDocumentIdAsync(documentId, ct);

        var results = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();

        // Sort by DisplayOrder so vendor-name fields (lower order) are validated before
        // invoice-number fields, ensuring IVendorResolutionContext is populated first.
        foreach (var field in ocrResult.ExtractedFields.OrderBy(f => f.FieldMappingConfig?.DisplayOrder ?? int.MaxValue))
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
            c.ErpResponseField, (double)c.ConfidenceThreshold, c.DisplayOrder, c.IsActive, c.CreatedAt, c.UpdatedAt);
}
