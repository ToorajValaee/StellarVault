namespace StellarVaultPwa.Services;

/// <summary>Converts browser/ledger exceptions to safe, actionable messages for vault users.</summary>
public static class VaultErrorMessages
{
    public static string Friendly(Exception error)
    {
        var message = error.Message ?? "";
        if (message.Contains("Root treasury cannot afford registry entry", StringComparison.OrdinalIgnoreCase))
            return "This account does not have enough available XLM to finish vault setup. Add XLM to the account on this network, then try again. Setup has not changed your account.";
        if (message.Contains("Root treasury cannot cover sponsored child", StringComparison.OrdinalIgnoreCase))
            return "Your account needs more available XLM to create storage space. Add XLM and try again.";
        if (message.Contains("No index-0 account found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Mnemonic index-0 account", StringComparison.OrdinalIgnoreCase))
            return "We couldn't find an account for this recovery phrase on the selected network. Check the network, recovery phrase and optional passphrase. No account was created.";
        if (message.Contains("checksum mismatch", StringComparison.OrdinalIgnoreCase))
            return "The recovery phrase has a typing error. Check each word and its order.";
        if (message.Contains("Derived children already exist", StringComparison.OrdinalIgnoreCase))
            return "This vault already has storage accounts. Recover the existing vault instead of starting a new one.";
        if (message.Contains("Registry already exists", StringComparison.OrdinalIgnoreCase))
            return "This vault has already been set up. Lock it and open it again.";
        if (message.Contains("Registry changed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("refresh required", StringComparison.OrdinalIgnoreCase))
            return "Your vault changed on the network. Refresh and try again.";
        if (message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "The requested account or data was not found on this network. Check your network selection.";
        if (message.Contains("CORS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("NetworkError", StringComparison.OrdinalIgnoreCase))
            return "Cannot connect to the Stellar network. Check your internet connection and network settings, then try again.";
        if (message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("underfunded", StringComparison.OrdinalIgnoreCase))
            return "There isn't enough available XLM for this operation. Add XLM and try again.";
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "The network took too long to respond. Check the account before retrying.";
        // Browser JS exceptions may append a second 'Error:' and a local stack trace.
        // Do not display implementation paths, browser origins or stack frames to users.
        var firstLine = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        var duplicate = firstLine.IndexOf(" Error:", StringComparison.Ordinal);
        if (duplicate >= 0) firstLine = firstLine[..duplicate];
        if (firstLine.StartsWith("Error: ", StringComparison.Ordinal))
            firstLine = firstLine[7..];
        if (firstLine.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            firstLine.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            firstLine.Contains(".js:", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(firstLine))
            return "Something went wrong. Please try again. If the problem continues, refresh the vault.";
        return firstLine;
    }
}
