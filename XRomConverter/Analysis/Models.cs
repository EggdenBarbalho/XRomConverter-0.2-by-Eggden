using System.Text.Json.Serialization;

namespace XRomConverter.Analysis;

internal sealed record AssetProvenance(
    string SourceId,
    string RootPath,
    string PackageId,
    int ContainerIndex,
    string LogicalPath,
    string StorageKind,
    string? ParentLogicalPath = null);

internal sealed record XModuleRecord(short ModuleId, short CosId, string Character,
    short SortIndex, string Name, AssetProvenance Provenance);

internal sealed record XTextureReference(uint SourceId, string? Name,
    AssetProvenance Provenance);

internal sealed record XTextureChange(uint OriginalId, string? OriginalName,
    uint ReplacementId, string? ReplacementName);

internal sealed record XItemObjectReference(uint SourceId, string? SourceName,
    byte Rpk);

internal sealed record XSkinParameter(string Name, IReadOnlyList<string> BocTargets,
    string? CollisionTarget, int NodeCount, SkinParameterPhysicalData? Physical = null);

// Relative field names retain the original value lexemes. An absent key stays
// absent instead of inheriting defaults from the library's typed representation.
internal sealed record SkinParameterPhysicalData(string OriginKind, string SourceSha256,
    IReadOnlyDictionary<string, string> Fields);

internal sealed record SkinTextLine(string? ParameterName, string? FieldName,
    string Prefix, string? Literal, string Ending);

internal sealed record SkinTextLayout(bool Utf8Bom, IReadOnlyList<SkinTextLine> Lines);

internal sealed record SkinReferenceSelection(string Label, string ReferencePath,
    string ReferenceSha256, string TargetArchivePath, string TargetArchiveSha256,
    SkinTextLayout Layout);

internal sealed record XSkinBlock(string Kind, string? Name, string? ExternalName,
    string? ParentName, IReadOnlyList<string> NodeNames);

internal sealed record XSkinParameterAsset(string ExpectedFileName, bool Found,
    AssetProvenance? Provenance, IReadOnlyList<XSkinParameter> Parameters);

internal sealed record XObjectRecord(uint SourceId, string SourceName,
    IReadOnlyList<XSkinBlock> SkinBlocks, XSkinParameterAsset SkinParameter,
    AssetProvenance Provenance);

internal sealed record XObjectSetRecord(uint SourceId, string SourceName,
    IReadOnlyList<XObjectRecord> Objects, IReadOnlyList<XTextureReference> Textures,
    AssetProvenance Provenance);

internal sealed record XComponentRecord(int SlotIndex, string Slot, short ItemNumber,
    string SubId, short Attribute, short SortIndex, byte Type, byte DestinationId,
    IReadOnlyList<XItemObjectReference> Objects,
    IReadOnlyList<XObjectSetRecord> ObjectSets,
    IReadOnlyList<XTextureChange> TextureChanges, AssetProvenance Provenance);

internal sealed record XPresentationRecord(IReadOnlyList<string> SpriteArchives,
    IReadOnlyDictionary<string, string> LocalizedNames);

internal sealed record XModuleAggregate(XModuleRecord Module,
    IReadOnlyList<XComponentRecord> Components, XPresentationRecord Presentation,
    IReadOnlyList<Diagnostic> Diagnostics);

internal enum SkinExpectation
{
    NotRequired,
    RequiredAndFound,
    RequiredButMissing,
    DerivedOnly,
    PartiallyDerived,
    Indeterminate
}

internal sealed record NormalizedSkinParameter(string Name,
    IReadOnlyList<string> BocTargets, string? CollisionTarget, int NodeCount,
    AssetProvenance Provenance, bool Derived, SkinParameterPhysicalData? Physical = null);

internal sealed record NormalizedSkinBlock(string Kind, string Name,
    string? ExternalName, string? ParentName, IReadOnlyList<string> NodeNames, bool Derived);

internal sealed record SkinValidationIssue(string Code, string Severity,
    string Message, string? Reference = null);

internal sealed record SkinValidationResult(bool IsValid,
    IReadOnlyList<SkinValidationIssue> Issues);

internal sealed record NormalizedObject(uint SourceId, string SourceName,
    IReadOnlyList<NormalizedSkinBlock> OriginalBlocks,
    IReadOnlyList<NormalizedSkinParameter> OriginalParameters,
    SkinExpectation Expectation, AssetProvenance Provenance,
    AssetProvenance? SkinParameterProvenance, SkinValidationResult SourceValidation);

internal sealed record NormalizedObjectSet(uint SourceId, string SourceName,
    IReadOnlyList<NormalizedObject> Objects, IReadOnlyList<XTextureReference> Textures,
    AssetProvenance Provenance);

internal sealed record NormalizedComponent(int SlotIndex, string Role,
    short SourceItemNumber, string SourceSubId, short Attribute, short SortIndex,
    byte Type, byte DestinationId, IReadOnlyList<XItemObjectReference> Objects,
    IReadOnlyList<NormalizedObjectSet> ObjectSets,
    IReadOnlyList<XTextureChange> TextureChanges, AssetProvenance Provenance);

internal sealed record CanonicalIdentity(string CanonicalId, string DisplayName,
    string Confidence, IReadOnlyList<string> Evidence);

internal sealed record NormalizedModule(short SourceModuleId, short SourceCosId,
    string Character, string SourceName, IReadOnlyList<NormalizedComponent> Components,
    XPresentationRecord Presentation, AssetProvenance Provenance,
    CanonicalIdentity? Identity, IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record TargetIdBinding(int? TargetId, string Status, string Source,
    string? BindingKind = null, string? Authority = null,
    int? ObservedTargetId = null, string? Action = null);

internal sealed record PlannedSkinParameter(string SourceObject,
    string TargetObject, string OutputFile, SkinExpectation Expectation,
    IReadOnlyList<NormalizedSkinBlock> FinalBlocks,
    IReadOnlyList<NormalizedSkinParameter> FinalParameters,
    SkinValidationResult Validation, AssetProvenance ObjectProvenance,
    AssetProvenance? OspProvenance, SkinReferenceSelection? ReferenceSelection = null);

internal sealed record PlannedObjectSet(string SourceName, string TargetName,
    string OutputArchive, AssetProvenance Provenance,
    IReadOnlyList<PlannedSkinParameter> SkinParameters,
    PhysicalObjectSetSelection? PhysicalSelection = null);

internal sealed record PhysicalObjectBinding(string SourceName, string TargetName,
    uint SourceId, uint TargetId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AnalyticalSourceName = null);
internal sealed record PhysicalTextureBinding(int Slot, string SourceName, string TargetName,
    uint SourceId, uint TargetId);
internal sealed record PhysicalObjectSetSelection(string OriginKind, string ArchivePath,
    string ArchiveSha256, string TextureDatabasePath, string TextureDatabaseSha256,
    string OccupancySha256, string RegistryProfile, uint TargetSetId,
    IReadOnlyList<PhysicalObjectBinding> Objects, IReadOnlyList<PhysicalTextureBinding> Textures);

internal sealed record PlannedOutput(string Path, string Kind, string Status,
    IReadOnlyList<string> Inputs);

internal sealed record MegaMixBuildPlan(TargetIdBinding ModuleBinding,
    IReadOnlyList<PlannedObjectSet> ObjectSets,
    IReadOnlyList<PlannedOutput> RequiredOutputs,
    string Status, IReadOnlyList<Diagnostic> Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TargetTableSelection? TableSelection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModulePresentationSelection? PresentationSelection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OsageSettingSelection? OsageSettingSelection = null);

internal sealed record TargetItemIdBinding(int SourceItemNumber, int TargetItemNumber);
internal sealed record TargetTableSelection(string Character, int TargetCostumeId, IReadOnlyList<TargetItemIdBinding> Items,
    string RegistryProfile, string OccupancyPath, string OccupancySha256, string ReservationsPath, string ReservationsSha256,
    string ParentAnalysisSha256);
internal sealed record ModulePresentationSelection(string ReferenceArchive, string ReferenceArchiveSha256,
    string ReferenceDatabase, string ReferenceDatabaseSha256, string ReferenceEntry, string PayloadSha256,
    string OutputArchive, string OutputEntry, string SetName, uint SetId,
    string SpriteName, uint SpriteId, string TextureName, uint TextureId);
internal sealed record OsageSettingSelection(string ReferencePath, string ReferenceSha256,
    string SourceObject, string TargetObject, string Category, IReadOnlyList<string> Roots,
    string OutputPath, string OutputSha256);

internal sealed record Diagnostic(string Code, string Severity, string Message,
    string? LogicalPath = null);

internal sealed record ModuleAnalysisResult(XModuleAggregate Aggregate,
    NormalizedModule NormalizedModule, MegaMixBuildPlan BuildPlan);
