using System.Text.Json;
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
    private readonly IEnumerable<IFieldValidator> _validators;

    public ValidationService(
        OcrResultRepository ocrRepo,
        ValidationRepository validationRepo,
        IEnumerable<IFieldValidator> validators)
    {
        _ocrRepo = ocrRepo;
        _validationRepo = validationRepo;
        _validators = validators;
    }

    public async Task<ValidationSummary> ValidateDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is null)
            return new ValidationSummary(documentId, 0, 0, 0, 0, false, []);

        await _validationRepo.DeleteByDocumentIdAsync(documentId, ct);

        var results = new List<Domain.Entities.ValidationResult>();
        var resultDtos = new List<ValidationResultDto>();

        foreach (var field in ocrResult.ExtractedFields)
        {
            var config = field.FieldMappingConfig;
            if (config is null) continue;

            var fieldDto = MapFieldDto(field);
            var configDto = MapConfigDto(config);

            foreach (var validator in _validators)
            {
                bool shouldRun = validator.RunForAllFields ||
                    (!string.IsNullOrWhiteSpace(config.ErpMappingKey) &&
                     validator.SupportedErpMappingKeys.Contains(config.ErpMappingKey, StringComparer.OrdinalIgnoreCase));
                if (!shouldRun) continue;

                var vResult = await validator.ValidateAsync(fieldDto, configDto, ct);
                var dbResult = new Domain.Entities.ValidationResult
                {
                    DocumentId = documentId,
                    ExtractedFieldId = field.Id,
                    FieldName = field.FieldName,
                    ValidationType = vResult.ValidationType,
                    Status = Enum.Parse<ValidationStatus>(vResult.Status),
                    Message = vResult.Message,
                    ErpReference = vResult.ErpReference is not null ? JsonSerializer.Serialize(vResult.ErpReference) : null,
                    ValidatedAt = DateTimeOffset.UtcNow
                };
                results.Add(dbResult);
                resultDtos.Add(new ValidationResultDto(
                    Guid.NewGuid(), documentId, field.Id, field.FieldName,
                    vResult.ValidationType, vResult.Status, vResult.Message,
                    vResult.ErpReference, dbResult.ValidatedAt));
            }
        }

        await _validationRepo.AddRangeAsync(results, ct);

        int passed = resultDtos.Count(r => r.Status == "Passed");
        int failed = resultDtos.Count(r => r.Status == "Failed");
        int warnings = resultDtos.Count(r => r.Status == "Warning");

        return new ValidationSummary(documentId, resultDtos.Count, passed, failed, warnings, failed == 0, resultDtos);
    }

    public async Task<IReadOnlyList<ValidationResultDto>> GetValidationResultsAsync(Guid documentId, CancellationToken ct = default)
    {
        var results = await _validationRepo.GetByDocumentIdAsync(documentId, ct);
        return results.Select(r => new ValidationResultDto(
            r.Id, r.DocumentId, r.ExtractedFieldId, r.FieldName,
            r.ValidationType, r.Status.ToString(), r.Message,
            r.ErpReference is not null ? JsonSerializer.Deserialize<object>(r.ErpReference) : null,
            r.ValidatedAt)).ToList();
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
            c.KeywordAnchor, c.PositionRule, c.IsRequired, c.ErpMappingKey,
            (double)c.ConfidenceThreshold, c.DisplayOrder, c.IsActive, c.CreatedAt, c.UpdatedAt);
}
