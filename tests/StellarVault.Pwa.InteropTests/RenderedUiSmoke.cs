using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StellarVault.UI.Components;

internal static class RenderedUiSmoke
{
    public static async Task RunAsync()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<VaultRecordFields>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Title"] = "Synthetic title", ["IsFolder"] = false }))).ToHtmlString());
        var controlIds = Regex.Matches(html, "<(?:input|select|textarea)\\b[^>]*\\bid=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var labels = Regex.Matches(html, "<label\\b[^>]*\\bfor=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        if (labels.Length < 7 || labels.Any(id => !controlIds.Contains(id)))
            throw new Exception("Rendered record label points to a missing control ID");
        Console.WriteLine("PASS: actual shared record form renders all labels bound to existing controls.");
    }
}
