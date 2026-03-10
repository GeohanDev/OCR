using OcrErpSystem.Application.Commands;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Common;

namespace OcrErpSystem.Application.FieldMapping;

public interface IFieldMappingService
{
    Task<IReadOnlyList<DocumentTypeDto>> GetDocumentTypesAsync(CancellationToken ct = default);
    Task<DocumentTypeDto> RegisterDocumentTypeAsync(RegisterDocumentTypeCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<FieldMappingConfigDto>> GetFieldMappingsAsync(Guid documentTypeId, CancellationToken ct = default);
    Task<FieldMappingConfigDto> CreateFieldMappingAsync(CreateFieldMappingCommand cmd, CancellationToken ct = default);
    Task<FieldMappingConfigDto> UpdateFieldMappingAsync(UpdateFieldMappingCommand cmd, CancellationToken ct = default);
    Task DeleteFieldMappingAsync(Guid fieldMappingId, CancellationToken ct = default);
    Task DeleteDocumentTypeAsync(Guid documentTypeId, CancellationToken ct = default);
    Task ReorderFieldMappingsAsync(Guid documentTypeId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);
    Task<IReadOnlyList<FieldMappingConfigDto>> GetActiveConfigAsync(Guid documentTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<TrashedFieldConfigDto>> GetTrashedFieldMappingsAsync(CancellationToken ct = default);
    Task RestoreFieldMappingAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TrashedDocTypeDto>> GetTrashedDocTypesAsync(CancellationToken ct = default);
    Task RestoreDocTypeAsync(Guid id, CancellationToken ct = default);
}
