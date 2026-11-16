namespace VaultEngine.Stellar.Services;

/// <summary>
/// Shared type selection for existing Stellar folder/record drafts. Both HD
/// and independent S-key writers use this exact validation boundary; transport,
/// account allocation, signing and network fees remain strategy-specific.
/// </summary>
public sealed class VaultStellarDraftClassification(IVaultRecordTypeFactory recordTypes)
{
    public VaultRecordDefinition Classify(LedgerRecordDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return recordTypes.Create(draft.IsFolder
            ? VaultRecordTypeFactory.Folder
            : VaultRecordTypeFactory.Credential, draft.Title);
    }
}
