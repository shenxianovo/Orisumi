using System.Text.RegularExpressions;
using Orisumi.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;

namespace Orisumi.Infrastructure.Database;

public sealed partial class OrisumiDbContext(DbContextOptions<OrisumiDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetValueConverter>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        ConfigureManagement(modelBuilder);
        var storedObservation = modelBuilder.Entity<StoredObservation>();
        storedObservation.ToTable("observations");
        storedObservation.HasKey(row => row.ObservationId).HasName("pk_observations");
        storedObservation.Property(row => row.Content).HasColumnType("jsonb");
        storedObservation.Property(row => row.FactRange).HasColumnType("tstzrange");
        var storedThing = modelBuilder.Entity<StoredThing>();
        storedThing.ToTable("things");
        storedThing.HasKey(row => row.ThingId).HasName("pk_things");
        var deletedObservation = modelBuilder.Entity<DeletedObservation>();
        deletedObservation.ToTable("deleted_observations");
        deletedObservation.HasKey(row => row.ObservationId).HasName("pk_deleted_observations");
        var storedSchemaRevision = modelBuilder.Entity<StoredSchemaRevision>();
        storedSchemaRevision.ToTable("schema_revisions");
        storedSchemaRevision.HasKey(row => row.SchemaRevisionId).HasName("pk_schema_revisions");
        storedSchemaRevision.Property(row => row.Content).HasColumnType("jsonb");
        var storedThingType = modelBuilder.Entity<StoredThingType>();
        storedThingType.ToTable("thing_types");
        storedThingType.HasKey(row => row.SchemaId).HasName("pk_thing_types");
        var storedSchema = modelBuilder.Entity<StoredSchema>();
        storedSchema.ToTable("schemas");
        storedSchema.HasKey(row => row.SchemaId).HasName("pk_schemas");
        var storedInputSet = modelBuilder.Entity<StoredInputSet>();
        storedInputSet.ToTable("input_sets");
        storedInputSet.HasKey(row => row.InputSetId).HasName("pk_input_sets");
        var storedContent = modelBuilder.Entity<StoredContent>();
        storedContent.ToTable("contents");
        storedContent.HasKey(row => row.Digest).HasName("pk_contents");
        var storedObservationContent = modelBuilder.Entity<StoredObservationContent>();
        storedObservationContent.ToTable("observation_contents");
        storedObservationContent.HasKey(row => new { row.ObservationId, row.Digest }).HasName("pk_observation_contents");
        var storedObservationReference = modelBuilder.Entity<StoredObservationReference>();
        storedObservationReference.ToTable("observation_references");
        storedObservationReference.HasKey(row => new { row.TargetId, row.ObservationId }).HasName("pk_observation_references");
        var storedReplacement = modelBuilder.Entity<StoredReplacement>();
        storedReplacement.ToTable("replacements");
        storedReplacement.HasKey(row => new { row.ReplacedId, row.ReplacerId }).HasName("pk_replacements");
        var storedReview = modelBuilder.Entity<StoredReview>();
        storedReview.ToTable("reviews");
        storedReview.HasKey(row => row.ObservationId).HasName("pk_reviews");
        storedReview.Property(row => row.FactRange).HasColumnType("tstzrange");
        var storedSameAs = modelBuilder.Entity<StoredSameAs>();
        storedSameAs.ToTable("same_as_links");
        storedSameAs.HasKey(row => row.ObservationId).HasName("pk_same_as_links");
        storedSameAs.Property(row => row.FactRange).HasColumnType("tstzrange");
        var storedAdoption = modelBuilder.Entity<StoredAdoption>();
        storedAdoption.ToTable("adoptions");
        storedAdoption.HasKey(row => row.ObservationId).HasName("pk_adoptions");
        storedAdoption.Property(row => row.FactRange).HasColumnType("tstzrange");
        var storedRecognitionKey = modelBuilder.Entity<StoredRecognitionKey>();
        storedRecognitionKey.ToTable("recognition_keys");
        storedRecognitionKey.HasNoKey();
        var storedKeyTombstone = modelBuilder.Entity<StoredKeyTombstone>();
        storedKeyTombstone.ToTable("key_tombstones");
        storedKeyTombstone.HasKey(row => row.KeyDigest).HasName("pk_key_tombstones");
        var storedRun = modelBuilder.Entity<StoredRun>();
        storedRun.ToTable("runs");
        storedRun.HasKey(row => row.RunId).HasName("pk_runs");
        var storedRunRead = modelBuilder.Entity<StoredRunRead>();
        storedRunRead.ToTable("run_reads");
        storedRunRead.HasKey(row => new { row.RunId, row.ObservationId }).HasName("pk_run_reads");
        var storedWriteStream = modelBuilder.Entity<StoredWriteStream>();
        storedWriteStream.ToTable("write_streams");
        storedWriteStream.HasKey(row => row.ObserverId).HasName("pk_write_streams");
        storedWriteStream.Property(row => row.SchemaRevisionIds).HasColumnType("jsonb");
        storedObservation.HasOne<StoredSchemaRevision>().WithMany().HasForeignKey(row => row.SchemaRevisionId).OnDelete(DeleteBehavior.NoAction);
        storedObservation.HasIndex(row => new { row.SchemaId, row.FactRange }).HasMethod("gist");
        storedObservation.HasIndex(row => row.FactRange).HasMethod("gist");
        storedObservation.HasIndex(row => new { row.SchemaId, row.ObservationId });
        storedObservation.HasIndex(row => row.ObserverId);
        storedObservation.HasIndex(row => row.SpeakerId);
        storedObservation.HasIndex(row => row.Formation);
        storedObservation.HasIndex(row => row.ClaimKey).HasFilter("claim_key IS NOT NULL");
        storedObservation.HasIndex(row => row.StateKey).HasFilter("state_key IS NOT NULL");
        storedSchemaRevision.HasIndex(row => row.SchemaId);
        storedThing.HasIndex(row => new { row.SchemaId, row.ThingId });
        storedSchema.HasIndex(row => row.ResourceName).IsUnique().HasDatabaseName("ux_schemas_resource_name");
        storedThingType.HasIndex(row => row.ResourceName).IsUnique().HasDatabaseName("ux_thing_types_resource_name");
        storedInputSet.HasIndex(row => row.Members).HasMethod("gin");
        storedContent.ToTable("contents", table => table.HasCheckConstraint("ck_contents_size", "size = octet_length(data)"));
        storedContent.HasIndex(row => row.UploadedAt);
        storedObservationContent.HasIndex(row => row.Digest);
        storedObservationContent.HasOne<StoredObservation>().WithMany().HasForeignKey(row => row.ObservationId).OnDelete(DeleteBehavior.Cascade);
        storedObservationContent.HasOne<StoredContent>().WithMany().HasForeignKey(row => row.Digest).OnDelete(DeleteBehavior.NoAction);
        storedObservationReference.HasIndex(row => row.ObservationId);
        storedObservationReference.HasIndex(row => new { row.TargetId, row.Role });
        storedObservationReference.HasOne<StoredObservation>().WithMany().HasForeignKey(row => row.ObservationId).OnDelete(DeleteBehavior.Cascade);
        storedReplacement.HasIndex(row => row.ReplacerId);
        storedReview.HasIndex(row => row.TargetId);
        storedReview.HasIndex(row => row.TargetClaimKey);
        storedSameAs.HasIndex(row => row.LeftId);
        storedSameAs.HasIndex(row => row.RightId);
        storedAdoption.HasIndex(row => row.SchemaId);
        storedRecognitionKey.ToTable("recognition_keys", table => table.HasCheckConstraint("ck_recognition_keys_target", "(thing_id IS NULL) <> (latest_observation_id IS NULL)"));
        storedRecognitionKey.HasIndex(row => new { row.SchemaId, row.Scope, row.Scheme, row.Value }).IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_recognition_keys_schema_id_scope_scheme_value");
        storedRecognitionKey.HasIndex(row => row.ThingId).HasFilter("thing_id IS NOT NULL");
        storedRecognitionKey.HasIndex(row => row.LatestObservationId).HasFilter("latest_observation_id IS NOT NULL");
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties()) property.SetColumnName(WordBoundary().Replace(property.Name, "$1_$2").ToLowerInvariant());
            foreach (var index in entity.GetIndexes())
                if (index.GetDatabaseName()!.StartsWith("IX_", StringComparison.Ordinal)) index.SetDatabaseName("ix_" + entity.GetTableName() + "_" + string.Join('_', index.Properties.Select(property => property.GetColumnName())));
            foreach (var foreignKey in entity.GetForeignKeys()) foreignKey.SetConstraintName("fk_" + entity.GetTableName() + "_" + foreignKey.PrincipalEntityType.GetTableName() + "_" + string.Join('_', foreignKey.Properties.Select(property => property.GetColumnName())));
        }
    }

    private static void ConfigureManagement(ModelBuilder modelBuilder)
    {
        var hosts = modelBuilder.Entity<StoredHost>();
        hosts.ToTable("observer_hosts", table => table.HasCheckConstraint("ck_observer_hosts_kind", "kind IN ('device', 'server')"));
        hosts.HasKey(row => row.HostId).HasName("pk_observer_hosts");
        var instances = modelBuilder.Entity<StoredInstance>();
        instances.ToTable("observer_instances", table => table.HasCheckConstraint("ck_observer_instances_settings", "jsonb_typeof(settings) = 'object'"));
        instances.HasKey(row => row.ThingId).HasName("pk_observer_instances");
        instances.Property(row => row.Settings).HasColumnType("jsonb");
        instances.Property(row => row.Status).HasColumnType("jsonb");
        instances.HasOne<StoredHost>().WithMany().HasForeignKey(row => row.HostId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_observer_instances_observer_hosts_host_id");
        instances.HasIndex(row => new { row.HostId, row.PackageId }).IsUnique().HasFilter("removed = false")
            .HasDatabaseName("ux_observer_instances_host_id_package_id");
        instances.HasIndex(row => row.PackageId).HasFilter("removed = false").HasDatabaseName("ix_observer_instances_package_id");
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
