using Microsoft.EntityFrameworkCore;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;

namespace OcrSystem.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<FieldMappingConfig> FieldMappingConfigs => Set<FieldMappingConfig>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<OcrResult> OcrResults => Set<OcrResult>();
    public DbSet<ExtractedField> ExtractedFields => Set<ExtractedField>();
    public DbSet<Domain.Entities.ValidationResult> ValidationResults => Set<Domain.Entities.ValidationResult>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorAgingSnapshot> VendorAgingSnapshots => Set<VendorAgingSnapshot>();
    public DbSet<ValidationQueueItem> ValidationQueue => Set<ValidationQueueItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Vendor>(e =>
        {
            e.ToTable("vendors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.AcumaticaVendorId).HasColumnName("acumatica_vendor_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.VendorName).HasColumnName("vendor_name").HasMaxLength(500).IsRequired();
            e.Property(x => x.AddressLine1).HasColumnName("address_line1").HasMaxLength(500);
            e.Property(x => x.AddressLine2).HasColumnName("address_line2").HasMaxLength(500);
            e.Property(x => x.City).HasColumnName("city").HasMaxLength(250);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(100);
            e.Property(x => x.PostalCode).HasColumnName("postal_code").HasMaxLength(50);
            e.Property(x => x.Country).HasColumnName("country").HasMaxLength(100);
            e.Property(x => x.PaymentTerms).HasColumnName("payment_terms").HasMaxLength(100);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.LastSyncedAt).HasColumnName("last_synced_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.AcumaticaVendorId).IsUnique();
        });

        modelBuilder.Entity<Branch>(e =>
        {
            e.ToTable("branches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.AcumaticaBranchId).HasColumnName("acumatica_branch_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(50).IsRequired();
            e.Property(x => x.BranchName).HasColumnName("branch_name").HasMaxLength(250).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.SyncedAt).HasColumnName("synced_at");
            e.HasIndex(x => x.AcumaticaBranchId).IsUnique();
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.AcumaticaUserId).HasColumnName("acumatica_user_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.Username).HasColumnName("username").HasMaxLength(150).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(250).IsRequired();
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(250);
            e.Property(x => x.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(50);
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.LastSyncedAt).HasColumnName("last_synced_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.AcumaticaUserId).IsUnique();
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne(x => x.Branch).WithMany(b => b.Users).HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentType>(e =>
        {
            e.ToTable("document_types");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TypeKey).HasColumnName("type_key").HasMaxLength(100).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(250).IsRequired();
            e.Property(x => x.PluginClass).HasColumnName("plugin_class").HasMaxLength(500).IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasConversion<int>().HasDefaultValue(DocumentCategory.General);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.TypeKey).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<FieldMappingConfig>(e =>
        {
            e.ToTable("field_mapping_configs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DocumentTypeId).HasColumnName("document_type_id");
            e.Property(x => x.FieldName).HasColumnName("field_name").HasMaxLength(150).IsRequired();
            e.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(250);
            e.Property(x => x.RegexPattern).HasColumnName("regex_pattern").HasMaxLength(1000);
            e.Property(x => x.KeywordAnchor).HasColumnName("keyword_anchor").HasMaxLength(500);
            e.Property(x => x.PositionRule).HasColumnName("position_rule").HasColumnType("jsonb");
            e.Property(x => x.IsRequired).HasColumnName("is_required");
            e.Property(x => x.IsManualEntry).HasColumnName("is_manual_entry").HasDefaultValue(false);
            e.Property(x => x.IsCheckbox).HasColumnName("is_checkbox").HasDefaultValue(false);
            e.Property(x => x.AllowMultiple).HasColumnName("allow_multiple").HasDefaultValue(false);
            e.Property(x => x.ErpMappingKey).HasColumnName("erp_mapping_key").HasMaxLength(250);
            e.Property(x => x.ErpResponseField).HasColumnName("erp_response_field").HasMaxLength(250);
            e.Property(x => x.DependentFieldKey).HasColumnName("dependent_field_key").HasMaxLength(150);
            e.Property(x => x.ConfidenceThreshold).HasColumnName("confidence_threshold").HasPrecision(5, 2);
            e.Property(x => x.DisplayOrder).HasColumnName("display_order");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne(x => x.DocumentType).WithMany(dt => dt.FieldMappingConfigs).HasForeignKey(x => x.DocumentTypeId);
            e.HasIndex(x => new { x.DocumentTypeId, x.FieldName }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DocumentTypeId).HasColumnName("document_type_id");
            e.Property(x => x.OriginalFilename).HasColumnName("original_filename").HasMaxLength(500).IsRequired();
            e.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(1000).IsRequired();
            e.Property(x => x.FileHash).HasColumnName("file_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.MimeType).HasColumnName("mime_type").HasMaxLength(100);
            e.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(50);
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            e.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by");
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.PushedAt).HasColumnName("pushed_at");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CurrentVersion).HasColumnName("current_version");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.HasIndex(x => x.UploadedBy);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.FileHash);
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne(x => x.DocumentType).WithMany(dt => dt.Documents).HasForeignKey(x => x.DocumentTypeId);
            e.HasOne(x => x.UploadedByUser).WithMany(u => u.UploadedDocuments).HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Branch).WithMany(b => b.Documents).HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.VendorId).HasColumnName("vendor_id");
            e.Property(x => x.VendorName).HasColumnName("vendor_name").HasMaxLength(500);
            e.HasOne(x => x.Vendor).WithMany(v => v.Documents).HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.ReuploadRequired).HasColumnName("reupload_required").HasDefaultValue(false);
            e.Property(x => x.IsValidating).HasColumnName("is_validating").HasDefaultValue(false);
        });

        modelBuilder.Entity<DocumentVersion>(e =>
        {
            e.ToTable("document_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.VersionNumber).HasColumnName("version_number");
            e.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(1000).IsRequired();
            e.Property(x => x.FileHash).HasColumnName("file_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
            e.HasOne(x => x.Document).WithMany(d => d.Versions).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UploadedByUser).WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OcrResult>(e =>
        {
            e.ToTable("ocr_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.VersionNumber).HasColumnName("version_number");
            e.Property(x => x.RawText).HasColumnName("raw_text");
            e.Property(x => x.EngineVersion).HasColumnName("engine_version").HasMaxLength(50);
            e.Property(x => x.ProcessingMs).HasColumnName("processing_ms");
            e.Property(x => x.OverallConfidence).HasColumnName("overall_confidence").HasPrecision(5, 2);
            e.Property(x => x.PageCount).HasColumnName("page_count");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.Document).WithMany(d => d.OcrResults).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExtractedField>(e =>
        {
            e.ToTable("extracted_fields");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.OcrResultId).HasColumnName("ocr_result_id");
            e.Property(x => x.FieldMappingConfigId).HasColumnName("field_mapping_config_id");
            e.Property(x => x.FieldName).HasColumnName("field_name").HasMaxLength(150).IsRequired();
            e.Property(x => x.RawValue).HasColumnName("raw_value");
            e.Property(x => x.NormalizedValue).HasColumnName("normalized_value");
            e.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(5, 2);
            e.Property(x => x.BoundingBox).HasColumnName("bounding_box").HasColumnType("jsonb");
            e.Property(x => x.IsManuallyCorreected).HasColumnName("is_manually_corrected");
            e.Property(x => x.CorrectedValue).HasColumnName("corrected_value");
            e.Property(x => x.CorrectedBy).HasColumnName("corrected_by");
            e.Property(x => x.CorrectedAt).HasColumnName("corrected_at");
            e.HasOne(x => x.OcrResult).WithMany(r => r.ExtractedFields).HasForeignKey(x => x.OcrResultId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FieldMappingConfig).WithMany().HasForeignKey(x => x.FieldMappingConfigId);
            e.HasOne(x => x.CorrectedByUser).WithMany().HasForeignKey(x => x.CorrectedBy).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Domain.Entities.ValidationResult>(e =>
        {
            e.ToTable("validation_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.ExtractedFieldId).HasColumnName("extracted_field_id");
            e.Property(x => x.FieldName).HasColumnName("field_name").HasMaxLength(150).IsRequired();
            e.Property(x => x.ValidationType).HasColumnName("validation_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(50);
            e.Property(x => x.Message).HasColumnName("message");
            e.Property(x => x.ErpReference).HasColumnName("erp_reference").HasColumnType("jsonb");
            e.Property(x => x.ErpResponseField).HasColumnName("erp_response_field").HasMaxLength(250);
            e.Property(x => x.ValidatedAt).HasColumnName("validated_at");
            e.HasIndex(x => x.DocumentId);
            e.HasOne(x => x.Document).WithMany(d => d.ValidationResults).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ExtractedField).WithMany(f => f.ValidationResults).HasForeignKey(x => x.ExtractedFieldId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.TargetEntityType).HasColumnName("target_entity_type").HasMaxLength(100);
            e.Property(x => x.TargetEntityId).HasColumnName("target_entity_id").HasMaxLength(100);
            e.Property(x => x.BeforeValue).HasColumnName("before_value").HasColumnType("jsonb");
            e.Property(x => x.AfterValue).HasColumnName("after_value").HasColumnType("jsonb");
            e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(50);
            e.Property(x => x.UserAgent).HasColumnName("user_agent");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.OccurredAt);
            e.HasOne(x => x.ActorUser).WithMany(u => u.AuditLogs).HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Document).WithMany(d => d.AuditLogs).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<VendorAgingSnapshot>(e =>
        {
            e.ToTable("vendor_aging_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.VendorLocalId).HasColumnName("vendor_local_id").HasMaxLength(500).IsRequired();
            e.Property(x => x.AcumaticaVendorId).HasColumnName("acumatica_vendor_id").HasMaxLength(100);
            e.Property(x => x.VendorName).HasColumnName("vendor_name").HasMaxLength(500).IsRequired();
            e.Property(x => x.SnapshotDate).HasColumnName("snapshot_date");
            e.Property(x => x.SnapshotKind).HasColumnName("snapshot_kind").HasDefaultValue(0);
            e.Property(x => x.StatementDate).HasColumnName("statement_date");
            e.Property(x => x.SnapshotBranchId).HasColumnName("snapshot_branch_id").HasMaxLength(100);
            e.Property(x => x.Current).HasColumnName("current_amount").HasPrecision(18, 2);
            e.Property(x => x.Aging30).HasColumnName("aging_30").HasPrecision(18, 2);
            e.Property(x => x.Aging60).HasColumnName("aging_60").HasPrecision(18, 2);
            e.Property(x => x.Aging90).HasColumnName("aging_90").HasPrecision(18, 2);
            e.Property(x => x.Aging90Plus).HasColumnName("aging_90_plus").HasPrecision(18, 2);
            e.Property(x => x.TotalOutstanding).HasColumnName("total_outstanding").HasPrecision(18, 2);
            e.Property(x => x.CapturedAt).HasColumnName("captured_at");
            e.HasIndex(x => x.SnapshotDate);
            e.HasIndex(x => new { x.SnapshotDate, x.SnapshotKind });
        });

        modelBuilder.Entity<ValidationQueueItem>(e =>
        {
            e.ToTable("validation_queue");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.DocumentName).HasColumnName("document_name").HasMaxLength(500).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(50).HasDefaultValue(ValidationQueueStatus.Pending);
            e.Property(x => x.AcumaticaToken).HasColumnName("acumatica_token");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SystemConfig>(e =>
        {
            e.ToTable("system_config");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(250);
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.IsSensitive).HasColumnName("is_sensitive");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
