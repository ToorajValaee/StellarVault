using VaultEngine.Stellar.Services;

/// <summary>Credential-strategy boundaries for the one-route PWA workspace.
/// These are pure state tests; no vault key or signer is synthesized.</summary>
internal static class VaultWorkspaceModeSmoke
{
    public static void Run()
    {
        Check(VaultWorkspaceModeResolver.Resolve(false, false, false) ==
            VaultWorkspaceMode.Locked, "A locked session unexpectedly opened a workspace.");
        Check(VaultWorkspaceModeResolver.Resolve(true, false, false) ==
            VaultWorkspaceMode.MnemonicHd, "Offline HD storage lost its workspace.");
        Check(VaultWorkspaceModeResolver.Resolve(false, true, false) ==
            VaultWorkspaceMode.MnemonicHd, "Connected on-chain HD session was rejected.");
        Check(VaultWorkspaceModeResolver.Resolve(true, true, false) ==
            VaultWorkspaceMode.MnemonicHd, "Valid paired HD session was rejected.");
        Check(VaultWorkspaceModeResolver.Resolve(false, false, true) ==
            VaultWorkspaceMode.StellarSecretKey, "Imported S-key session was mistaken for HD.");
        Check(VaultWorkspaceModeResolver.Resolve(true, false, true) ==
            VaultWorkspaceMode.ConflictingSessions, "Simultaneous HD and S-key sessions were allowed.");
        Check(VaultWorkspaceModeResolver.Resolve(false, true, true) ==
            VaultWorkspaceMode.ConflictingSessions, "Simultaneous ledger strategies were allowed.");
        Check(VaultWorkspaceModeResolver.Resolve(true, true, true) ==
            VaultWorkspaceMode.ConflictingSessions, "Conflicting vault credentials were not rejected.");
        Console.WriteLine("PASS: single-route authenticated workspace strategy and conflict isolation");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
