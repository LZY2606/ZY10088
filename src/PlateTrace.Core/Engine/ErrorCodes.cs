namespace PlateTrace.Core.Engine;

public static class ErrorCodes
{
    public const string EmptyBatch = "BATCH_EMPTY";
    public const string TimeDescending = "TIME_DESCENDING";
    public const string UnknownPlate = "PLATE_UNKNOWN";
    public const string PlateDuplicate = "PLATE_DUPLICATE";
    public const string PlateLayoutInvalid = "PLATE_LAYOUT_INVALID";
    public const string WellOutOfRange = "WELL_OUT_OF_RANGE";
    public const string FrozenPlateMutation = "PLATE_FROZEN_MUTATION";
    public const string ReagentDuplicate = "REAGENT_DUPLICATE";
    public const string ReagentUnknown = "REAGENT_UNKNOWN";
    public const string ReagentAlreadyFrozen = "REAGENT_ALREADY_FROZEN";
    public const string VolumeInvalid = "VOLUME_INVALID";
    public const string AspirateExceedsVolume = "ASpirate_EXCEEDS_VOLUME";
    public const string TransferSourceMissing = "TRANSFER_SOURCE_MISSING";
    public const string TipUnknown = "TIP_UNKNOWN";
    public const string TipDiscardedReused = "TIP_DISCARDED_REUSED";
    public const string TipReused = "TIP_REUSED";
    public const string UnitUnknown = "UNIT_UNKNOWN";
    public const string UnitDimensionMismatch = "UNIT_DIMENSION_MISMATCH";
    public const string ReadingConflict = "READING_CONFLICT";
    public const string CorrectionTargetMissing = "CORRECTION_TARGET_MISSING";
    public const string CorrectionOfCorrection = "CORRECTION_OF_CORRECTION";
    public const string CorrectionWithoutReason = "CORRECTION_WITHOUT_REASON";
    public const string Validation = "VALIDATION";
}
