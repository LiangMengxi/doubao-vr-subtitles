namespace DoubaoVR.Desktop;

internal readonly record struct TitleBarPalette(Rgb Background, Rgb Foreground, Rgb Inactive, Rgb Hover, Rgb Pressed)
{
    public static TitleBarPalette ForTheme(bool dark) => dark
        ? new(new(32, 32, 32), new(255, 255, 255), new(170, 170, 170), new(55, 55, 55), new(70, 70, 70))
        : new(new(243, 243, 243), new(0, 0, 0), new(96, 96, 96), new(225, 225, 225), new(210, 210, 210));
}
