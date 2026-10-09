namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public enum MirasimTurnErrorClass
{
    None,
    UpstreamUnavailable503,
    PlatformBusy422,
    DoneWithError,
    IncompletePayload,
    Timeout,
    ConnectionDrop,
    RefusedByWriterLock,
    RouteMismatch,
    UnsupportedChannel,
    Unknown
}
