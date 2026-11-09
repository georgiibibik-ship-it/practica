using System.Globalization;

namespace MedTest.Core;

public static class Numbers
{
    /// <summary>
    /// Reads a typed number. A comma and a point both mean the decimal point whatever the Windows settings,
    /// so «6,4» is never read as 64; spaces inside the number are ignored; thousands separators are not accepted.
    /// </summary>
    public static bool TryParse(string? text, out decimal value)
    {
        var t = new string((text ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray()).Replace(',', '.').Replace('−', '-');
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
