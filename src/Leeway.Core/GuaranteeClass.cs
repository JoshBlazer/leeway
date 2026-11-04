namespace Leeway.Core;

/// <summary>
/// The honest answer to "what happens to this rule when the device has no signal?".
/// Every rule Leeway enforces belongs to exactly one class.
/// </summary>
public enum GuaranteeClass
{
    /// <summary>The rule cannot be violated by offline activity, even if the device never reconnects.</summary>
    GuaranteedOffline,

    /// <summary>A violation can happen offline. It is detected and reported when the device reconnects.</summary>
    DetectedOnReconnect,

    /// <summary>The rule can only be evaluated with a server connection, so offline attempts are refused or deferred.</summary>
    RequiresConnection,
}
