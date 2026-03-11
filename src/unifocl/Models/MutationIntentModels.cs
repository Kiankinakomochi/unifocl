internal sealed record MutationIntentFlagsDto(
    bool DryRun = false,
    bool RequireRollback = true);

internal sealed record MutationIntentDto(
    string TransactionId,
    string Target,
    string Property,
    string? OldValue,
    string? NewValue,
    MutationIntentFlagsDto Flags);
