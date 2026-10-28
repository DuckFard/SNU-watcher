namespace SnuWatcher.Windows;

/// <summary>Fits a first-run window to its available work area in device-independent units.</summary>
public static class WindowSizePolicy
{
    /// <summary>Returns usable initial dimensions without imposing permanent resize limits.</summary>
    /// <param name="availableWidth">Positive finite work-area width.</param>
    /// <param name="availableHeight">Positive finite work-area height.</param>
    /// <param name="preferredWidth">Positive finite preferred window width.</param>
    /// <param name="preferredHeight">Positive finite preferred window height.</param>
    /// <param name="minimumWidth">Positive finite preferred minimum width.</param>
    /// <param name="minimumHeight">Positive finite preferred minimum height.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is nonpositive or nonfinite.</exception>
    public static InitialWindowSize FitToWorkArea(double availableWidth, double availableHeight,
        double preferredWidth, double preferredHeight, double minimumWidth, double minimumHeight)
    {
        EnsurePositiveFinite(availableWidth, nameof(availableWidth));
        EnsurePositiveFinite(availableHeight, nameof(availableHeight));
        EnsurePositiveFinite(preferredWidth, nameof(preferredWidth));
        EnsurePositiveFinite(preferredHeight, nameof(preferredHeight));
        EnsurePositiveFinite(minimumWidth, nameof(minimumWidth));
        EnsurePositiveFinite(minimumHeight, nameof(minimumHeight));
        var fittedMinimumWidth = Math.Min(minimumWidth, availableWidth);
        var fittedMinimumHeight = Math.Min(minimumHeight, availableHeight);
        return new InitialWindowSize(
            Math.Clamp(preferredWidth, fittedMinimumWidth, availableWidth),
            Math.Clamp(preferredHeight, fittedMinimumHeight, availableHeight),
            fittedMinimumWidth, fittedMinimumHeight);
    }

    private static void EnsurePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Window dimensions must be positive and finite.");
    }
}

/// <summary>Immutable initial window dimensions, all in device-independent units.</summary>
/// <param name="Width">Initial outer window width.</param>
/// <param name="Height">Initial outer window height.</param>
/// <param name="MinimumWidth">Minimum width constrained to the available work area.</param>
/// <param name="MinimumHeight">Minimum height constrained to the available work area.</param>
public readonly record struct InitialWindowSize(double Width, double Height, double MinimumWidth, double MinimumHeight);
