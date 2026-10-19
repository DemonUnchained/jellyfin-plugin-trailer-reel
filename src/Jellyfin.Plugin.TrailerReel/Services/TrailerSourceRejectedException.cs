namespace Jellyfin.Plugin.TrailerReel.Services;

public enum TrailerSourceRejection
{
    FormatUnavailable,
    AgeRestricted,
}

/// <summary>An individual upload cannot satisfy the current download policy.</summary>
public sealed class TrailerSourceRejectedException : Exception
{
    public TrailerSourceRejectedException(TrailerSourceRejection reason, bool extractionWarning = false)
        : base(reason == TrailerSourceRejection.AgeRestricted
            ? "YouTube requires an age-verified signed-in session for this upload."
            : extractionWarning
                ? "The requested format is unavailable; YouTube extraction/runtime warnings may explain missing formats."
                : "No format matching the configured quality policy is available for this upload.")
    {
        Reason = reason;
        ExtractionWarning = extractionWarning;
    }

    public TrailerSourceRejection Reason { get; }

    public bool ExtractionWarning { get; }

    internal static TrailerSourceRejectedException? FromYtDlpError(string stderr)
    {
        if (stderr.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase))
        {
            return new(TrailerSourceRejection.AgeRestricted);
        }

        if (!stderr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var extractionWarning = new[]
        {
            "challenge solving failed", "signature extraction failed", "nsig extraction failed",
            "No supported JavaScript runtime", "Only images are available", "SABR",
        }.Any(value => stderr.Contains(value, StringComparison.OrdinalIgnoreCase));
        return new(TrailerSourceRejection.FormatUnavailable, extractionWarning);
    }
}
