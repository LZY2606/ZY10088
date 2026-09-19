using System.Text;

namespace PlateTrace.Core.Engine;

/// <summary>Microplate coordinates: letters for rows (A, B, ...), 1-based columns.</summary>
public static class PlateCoords
{
    public static bool TryParse(string well, int rows, int cols, out int row, out int col)
    {
        row = col = -1;
        if (string.IsNullOrWhiteSpace(well)) return false;
        var s = well.Trim().ToUpperInvariant();
        var i = 0;
        var r = 0;
        while (i < s.Length && char.IsLetter(s[i]))
        {
            r = r * 26 + (s[i] - 'A' + 1);
            i++;
        }
        if (i == 0 || i >= s.Length) return false;
        if (!int.TryParse(s[i..], out var c)) return false;
        row = r - 1;
        col = c - 1;
        return row >= 0 && row < rows && col >= 0 && col < cols;
    }

    public static string Format(int row, int col)
    {
        var sb = new StringBuilder();
        var r = row + 1;
        while (r > 0)
        {
            r--;
            sb.Insert(0, (char)('A' + r % 26));
            r /= 26;
        }
        sb.Append(col + 1);
        return sb.ToString();
    }
}
