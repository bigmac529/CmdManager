using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace CmdManager.Client.Services;

/// <summary>Syntax highlighting per file type for the AvalonEdit editors (C# for .csx, Batch for .cmd/.bat, ...).</summary>
public static class SyntaxHighlighting
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        using var stream = typeof(SyntaxHighlighting).Assembly.GetManifestResourceStream("CmdManager.Client.Highlighting.Batch.xshd");
        if (stream is null)
            return;
        using var reader = XmlReader.Create(stream);
        var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting("Batch", [".cmd", ".bat"], def);
    }

    public static IHighlightingDefinition? ForFile(string fileName)
    {
        Register();
        var name = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csx" or ".cs" => "C#",
            ".cmd" or ".bat" => "Batch",
            ".ps1" or ".psm1" or ".psd1" => "PowerShell",
            ".json" => "Json",
            ".xml" or ".csproj" or ".config" or ".xaml" or ".props" or ".targets" or ".pubxml" => "XML",
            ".md" => "MarkDown",
            ".sql" => "TSQL",
            ".js" => "JavaScript",
            ".py" => "Python",
            ".html" or ".htm" => "HTML",
            ".vb" or ".vbs" => "VB",
            _ => null
        };
        return name is null ? null : HighlightingManager.Instance.GetDefinition(name);
    }
}
