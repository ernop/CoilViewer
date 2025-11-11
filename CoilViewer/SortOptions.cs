using System;

namespace CoilViewer;

internal enum SortField
{
    FileName,
    CreationTime,
    LastWriteTime,
    FileSize
}

internal enum SortDirection
{
    Ascending,
    Descending
}

internal static class SortOptions
{
    public static SortField ParseField(string? value)
    {
        if (Enum.TryParse(value, true, out SortField field))
        {
            return field;
        }

        return SortField.FileName;
    }

    public static SortDirection ParseDirection(string? value)
    {
        if (Enum.TryParse(value, true, out SortDirection direction))
        {
            return direction;
        }

        return SortDirection.Ascending;
    }

    public static string ToConfigValue(this SortField field) => field.ToString();

    public static string ToConfigValue(this SortDirection direction) => direction.ToString();
}
