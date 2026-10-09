namespace LLMWorkGUI.Application.Providers;

public sealed record UrlValidationResult(
    bool IsValid,
    string? NormalizedUrl,
    string? ErrorMessage,
    UrlClassification Classification)
{
    public static UrlValidationResult Success(string normalizedUrl, UrlClassification classification) =>
        new(true, normalizedUrl, null, classification);

    public static UrlValidationResult Failure(string errorMessage, UrlClassification classification) =>
        new(false, null, errorMessage, classification);
}
