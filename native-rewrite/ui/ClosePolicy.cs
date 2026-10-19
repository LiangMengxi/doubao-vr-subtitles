namespace DoubaoVR.Desktop;

internal readonly record struct ClosePolicy(bool Minimize, bool CloseDoubao)
{
    public static ClosePolicy Decide(Settings settings, bool exitButton, bool smokeTest) => new(
        settings.MinimizeOnClose && !exitButton && !smokeTest,
        settings.ExitCloseDoubao && exitButton && !smokeTest);
}
