using OcrSystem.Application.Commands;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.FieldMapping;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Services;

public class FieldMappingService : IFieldMappingService
{
    private readonly FieldMappingRepository _repo;

    public FieldMappingService(FieldMappingRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<DocumentTypeDto>> GetDocumentTypesAsync(CancellationToken ct = default)
    {
        var types = await _repo.GetDocumentTypesAsync(ct);
        return types.Select(MapTypeDto).ToList();
    }

    public async Task<DocumentTypeDto> RegisterDocumentTypeAsync(RegisterDocumentTypeCommand cmd, CancellationToken ct = default)
    {
        var dt = new DocumentType
        {
            TypeKey = cmd.TypeKey,
            DisplayName = cmd.DisplayName,
            PluginClass = cmd.PluginClass,
            Category = cmd.Category,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _repo.AddDocumentTypeAsync(dt, ct);
        return MapTypeDto(dt);
    }

    public async Task<DocumentTypeDto> UpdateDocumentTypeAsync(UpdateDocumentTypeCommand cmd, CancellationToken ct = default)
    {
        var dt = await _repo.GetDocumentTypeByIdAsync(cmd.DocumentTypeId, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.DocumentTypeId} not found");
        dt.DisplayName = cmd.DisplayName;
        dt.Category = Enum.TryParse<DocumentCategory>(cmd.Category, ignoreCase: true, out var cat)
            ? cat : DocumentCategory.General;
        await _repo.UpdateDocumentTypeAsync(dt, ct);
        return MapTypeDto(dt);
    }

    public async Task<IReadOnlyList<FieldMappingConfigDto>> GetFieldMappingsAsync(Guid documentTypeId, CancellationToken ct = default)
    {
        var configs = await _repo.GetFieldMappingsAsync(documentTypeId, true, ct);
        return configs.Select(MapConfigDto).ToList();
    }

    public async Task<FieldMappingConfigDto> CreateFieldMappingAsync(CreateFieldMappingCommand cmd, CancellationToken ct = default)
    {
        var config = new FieldMappingConfig
        {
            DocumentTypeId = cmd.DocumentTypeId,
            FieldName = cmd.FieldName,
            DisplayLabel = cmd.DisplayLabel,
            RegexPattern = cmd.RegexPattern,
            KeywordAnchor = cmd.KeywordAnchor,
            PositionRule = cmd.PositionRule,
            IsRequired = cmd.IsRequired,
            AllowMultiple = cmd.AllowMultiple,
            ErpMappingKey = cmd.ErpMappingKey,
            ErpResponseField = cmd.ErpResponseField,
            DependentFieldKey = cmd.DependentFieldKey,
            ConfidenceThreshold = (decimal)cmd.ConfidenceThreshold,
            DisplayOrder = cmd.DisplayOrder,
            IsManualEntry = cmd.IsManualEntry,
            IsCheckbox = cmd.IsCheckbox,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo.AddFieldMappingAsync(config, ct);
        return MapConfigDto(config);
    }

    public async Task<FieldMappingConfigDto> UpdateFieldMappingAsync(UpdateFieldMappingCommand cmd, CancellationToken ct = default)
    {
        var config = await _repo.GetFieldMappingByIdAsync(cmd.FieldMappingId, ct)
            ?? throw new KeyNotFoundException($"FieldMappingConfig {cmd.FieldMappingId} not found");
        config.DisplayLabel = cmd.DisplayLabel;
        config.RegexPattern = cmd.RegexPattern;
        config.KeywordAnchor = cmd.KeywordAnchor;
        config.PositionRule = cmd.PositionRule;
        config.IsRequired = cmd.IsRequired;
        config.AllowMultiple = cmd.AllowMultiple;
        config.ErpMappingKey = cmd.ErpMappingKey;
        config.ErpResponseField = cmd.ErpResponseField;
        config.DependentFieldKey = cmd.DependentFieldKey;
        config.ConfidenceThreshold = (decimal)cmd.ConfidenceThreshold;
        config.DisplayOrder = cmd.DisplayOrder;
        config.IsManualEntry = cmd.IsManualEntry;
        config.IsCheckbox = cmd.IsCheckbox;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateFieldMappingAsync(config, ct);
        return MapConfigDto(config);
    }

    public Task DeleteFieldMappingAsync(Guid fieldMappingId, CancellationToken ct = default) =>
        _repo.DeleteFieldMappingAsync(fieldMappingId, ct);

    public Task DeleteDocumentTypeAsync(Guid documentTypeId, CancellationToken ct = default) =>
        _repo.DeleteDocumentTypeAsync(documentTypeId, ct);

    public async Task ReorderFieldMappingsAsync(Guid documentTypeId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        for (int i = 0; i < orderedIds.Count; i++)
        {
            var config = await _repo.GetFieldMappingByIdAsync(orderedIds[i], ct);
            if (config is not null) { config.DisplayOrder = i; await _repo.UpdateFieldMappingAsync(config, ct); }
        }
    }

    public async Task<IReadOnlyList<FieldMappingConfigDto>> GetActiveConfigAsync(Guid documentTypeId, CancellationToken ct = default)
    {
        var configs = await _repo.GetFieldMappingsAsync(documentTypeId, true, ct);
        return configs.Select(MapConfigDto).ToList();
    }

    public async Task<IReadOnlyList<TrashedFieldConfigDto>> GetTrashedFieldMappingsAsync(CancellationToken ct = default)
    {
        var configs = await _repo.GetTrashedFieldMappingsAsync(ct);
        return configs.Select(c => new TrashedFieldConfigDto(
            c.Id, c.DocumentTypeId, c.DocumentType?.DisplayName ?? c.DocumentTypeId.ToString(),
            c.FieldName, c.DisplayLabel, c.DeletedAt ?? c.UpdatedAt)).ToList();
    }

    public Task RestoreFieldMappingAsync(Guid id, CancellationToken ct = default) =>
        _repo.RestoreFieldMappingAsync(id, ct);

    public async Task<IReadOnlyList<TrashedDocTypeDto>> GetTrashedDocTypesAsync(CancellationToken ct = default)
    {
        var types = await _repo.GetTrashedDocTypesAsync(ct);
        return types.Select(t => new TrashedDocTypeDto(
            t.Id, t.TypeKey, t.DisplayName, t.DeletedAt ?? t.CreatedAt, 0)).ToList();
    }

    public Task RestoreDocTypeAsync(Guid id, CancellationToken ct = default) =>
        _repo.RestoreDocTypeAsync(id, ct);

    private static DocumentTypeDto MapTypeDto(DocumentType dt) =>
        new(dt.Id, dt.TypeKey, dt.DisplayName, dt.PluginClass, dt.Category.ToString(), dt.IsActive, dt.CreatedAt);

    private static FieldMappingConfigDto MapConfigDto(FieldMappingConfig c) =>
        new(c.Id, c.DocumentTypeId, c.FieldName, c.DisplayLabel, c.RegexPattern,
            c.KeywordAnchor, c.PositionRule, c.IsRequired, c.AllowMultiple, c.ErpMappingKey,
            c.ErpResponseField, (double)c.ConfidenceThreshold, c.DisplayOrder, c.IsActive, c.CreatedAt, c.UpdatedAt,
            c.DependentFieldKey, c.IsManualEntry, c.IsCheckbox);
}
