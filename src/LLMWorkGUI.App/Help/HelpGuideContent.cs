using System.IO;
using System.Text;

namespace LLMWorkGUI.App.Help;

public static class HelpGuideContent
{
    public const string ResourceName = "LLMWorkGUI.App.USER_ADMIN_GUIDE.md";

    public static string Load()
    {
        using var stream = typeof(HelpGuideContent).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Встроенное руководство не найдено.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
