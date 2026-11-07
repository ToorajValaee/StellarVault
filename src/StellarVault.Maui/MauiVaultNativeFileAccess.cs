using CommunityToolkit.Maui.Storage;
using StellarVault.Core.Services;

namespace StellarVault.Maui;

/// <summary>MAUI-specific file picker and explicit, user-directed export.</summary>
public sealed class MauiVaultNativeFileAccess(IFileSaver fileSaver) : IVaultNativeFileAccess
{
    public async Task<VaultNativePickedFile?> PickAsync()
    {
        var file = await FilePicker.Default.PickAsync();
        if (file is null) return null;
        return new VaultNativePickedFile(file.FileName, await file.OpenReadAsync(),
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
    }

    public async Task<bool> SaveAsync(string fileName, Stream content)
    {
        var result = await fileSaver.SaveAsync(fileName, content, CancellationToken.None);
        return result.IsSuccessful;
    }
}
