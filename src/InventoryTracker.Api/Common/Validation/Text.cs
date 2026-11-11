namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Small helpers for text inputs such as names, reasons and notes.
/// </summary>
public static class Text
{
    /// <summary>
    /// Design 2.6: leading and trailing spaces are removed before a value is checked, and a value that is empty
    /// after trimming counts as missing. Returns the trimmed text, or null when nothing is left.
    /// </summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
