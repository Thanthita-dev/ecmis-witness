using System.Globalization;

namespace EcmisWitness.Api.Domain;

public static class WitnessIsoDate
{
    public static bool TryParse(string? value, out DateOnly result)
        => DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);
}
