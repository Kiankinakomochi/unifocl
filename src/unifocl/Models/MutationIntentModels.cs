internal sealed record MutationIntentFlagsDto(
    bool DryRun = false,
    bool RequireRollback = true,
    string? VcsMode = null,
    List<MutationIntentVcsPathDto>? VcsOwnedPaths = null);

internal sealed record MutationIntentVcsPathDto(
    string Path,
    string Owner,
    bool RequiresCheckout = false);

internal sealed record MutationIntentDto(
    string TransactionId,
    string Target,
    string Property,
    string? OldValue,
    string? NewValue,
    MutationIntentFlagsDto Flags);
