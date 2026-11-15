namespace StellarVault.Core.Services;

/// <summary>
/// Optional transport capability for *durably journaled* linked mutations.
/// The callback must commit the exact signed transaction hash to encrypted
/// local storage before the transport broadcasts anything to Horizon.
/// Throwing from the callback MUST abort broadcast. No retry is authorized.
/// Direct on-chain (non-journaled) workflows retain their separate interface.
/// </summary>
public interface IStellarSecretKeySignedSubmission
{
    Task<string> SubmitWithSignedHashAsync(
        string quoteId, Func<string, Task> durableCheckpoint,
        CancellationToken ct = default);
}
