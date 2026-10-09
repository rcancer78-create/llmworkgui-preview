namespace LLMWorkGUI.Application.Providers;

public enum UrlClassification
{
    ValidLoopbackHttp,
    ValidRemoteHttps,
    InsecureRemoteHttp,
    InvalidFormat
}
