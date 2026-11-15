namespace MedStock.Domain;

/// <summary>Buckets a lot by how close it is to its expiration date.</summary>
public static class ExpiryClassifier
{
    public static int DaysToExpiry(DateOnly expiry, DateOnly today) => expiry.DayNumber - today.DayNumber;

    /// <summary>A lot is usable through its expiration date; it is Expired from the day after.</summary>
    public static ExpiryClass Classify(DateOnly expiry, DateOnly today)
    {
        var days = DaysToExpiry(expiry, today);
        return days switch
        {
            < 0 => ExpiryClass.Expired,
            <= 30 => ExpiryClass.Within30,
            <= 60 => ExpiryClass.Within60,
            <= 90 => ExpiryClass.Within90,
            _ => ExpiryClass.Ok
        };
    }

    public static bool IsExpiringSoon(ExpiryClass c) => c is ExpiryClass.Within30 or ExpiryClass.Within60 or ExpiryClass.Within90;
}
