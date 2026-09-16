using Ralven.App.Services;

namespace Ralven.App.ViewModels;

internal static class ProFeatureAvailability
{
    public const bool Enabled = true;
}

internal sealed class ProPageViewModel : BindableBase
{
    private bool consent;
    private bool busy;
    private bool signedIn;
    private bool demo;
    private BillingSnapshot? snapshot;
    private string? messageKey;
    private LocalizationService L => LocalizationService.Current;

    public bool Consent
    {
        get => consent;
        set { SetProperty(ref consent, value); Refresh(); }
    }
    public bool IsBusy => busy;
    public bool IsDemo => demo;
    public bool CanRefresh => ProFeatureAvailability.Enabled && !busy && signedIn && !demo;
    public bool CanSignIn => ProFeatureAvailability.Enabled && !busy && !signedIn && !demo;
    public bool CanCheckout => ProFeatureAvailability.Enabled && !busy && signedIn && !demo && consent && snapshot is { CheckoutAvailable: true, Offer: not null };
    public bool CanCancel => ProFeatureAvailability.Enabled && !busy && signedIn && !demo && snapshot?.Subscription?.CanCancel == true;
    public bool ShowCheckout => ProFeatureAvailability.Enabled && !demo && signedIn && snapshot is { CheckoutAvailable: true, Offer: not null };
    public bool ShowCancel => ProFeatureAvailability.Enabled && snapshot?.Subscription?.CanCancel == true;
    public string Price => ProFeatureAvailability.Enabled && snapshot?.Offer is { } offer
        ? L.FormatCurrency(offer.AmountCents / 100m, offer.Currency)
        : L.GetString("Pro.Development.Action");
    public string ConsentText => L.Format("Pro.Checkout.Consent", Price);
    public string StatusTitle => L.GetString(!ProFeatureAvailability.Enabled ? "Pro.Development.Title" : demo ? "Pro.Status.Demo" : !signedIn ? "Pro.Status.SignedOut"
        : snapshot is null ? "Pro.Status.Unavailable"
        : snapshot.Subscription is null ? "Pro.Status.Free"
        : snapshot.Subscription.State is "cancelled" or "canceled" ? "Pro.Status.Cancelled"
        : snapshot.Subscription.AccessUntil > DateTimeOffset.UtcNow ? "Pro.Status.Paid"
        : snapshot.Subscription.State == "paused" ? "Pro.Status.Paused" : "Pro.Status.Pending");
    public string StatusDetail => L.GetString(!ProFeatureAvailability.Enabled ? "Pro.Development.Detail" : demo ? "Pro.Demo.Description" : !signedIn ? "Pro.SignIn.Description"
        : snapshot is null ? "Pro.Unavailable.Description"
        : snapshot.Subscription?.State is "cancelled" or "canceled" ? "Pro.Cancelled.Description"
        : snapshot.Subscription is not null && snapshot.Subscription.AccessUntil <= DateTimeOffset.UtcNow ? "Pro.Pending.Description"
        : snapshot.Subscription is not null && snapshot.Subscription.AccessUntil is null ? "Pro.Pending.Description"
        : !snapshot.CheckoutAvailable && snapshot.Subscription is null ? "Pro.SalesUnavailable.Description" : "Pro.Status.Description");
    public string AccessDetail => ProFeatureAvailability.Enabled && snapshot?.Subscription?.AccessUntil is { } until
        ? L.Format("Pro.AccessUntil", until.ToLocalTime().ToString("g", L.CurrentCulture)) : string.Empty;
    public string RenewalDetail => ProFeatureAvailability.Enabled && snapshot?.Subscription is { RenewsAt: { } renewal, State: "authorized" or "active" }
        ? L.Format("Pro.RenewsAt", renewal.ToLocalTime().ToString("d", L.CurrentCulture)) : string.Empty;
    public string Message => messageKey is null ? string.Empty : L.GetString(messageKey);
    public bool HasMessage => messageKey is not null;
    public BillingOffer? Offer => ProFeatureAvailability.Enabled ? snapshot?.Offer : null;

    public void SetSession(bool isSignedIn, bool isDemo)
    {
        signedIn = isSignedIn;
        demo = isDemo;
        snapshot = null;
        consent = false;
        messageKey = null;
        Refresh();
    }

    public void SetBusy(bool value) { busy = value; Refresh(); }
    public void SetSnapshot(BillingSnapshot? value)
    {
        if (snapshot?.Offer != value?.Offer) consent = false;
        snapshot = value;
        Refresh();
    }
    public void ShowMessage(string? key) { messageKey = key; Refresh(); }
    public void Refresh() => OnPropertyChanged(string.Empty);

    internal static string ErrorKey(string? error) => error switch
    {
        "billing-disabled" or "billing-unavailable" or "billing-not-configured" => "Pro.SalesUnavailable.Description",
        "billing-rate-limited" or "rate-limited" => "Pro.Error.RateLimited",
        "email-verification-required" => "Account.Verification.PleaseConfirm",
        "subscription-exists" or "billing-subscription-exists" or "billing-cancellation-required" or "billing-access-active" => "Pro.Error.Existing",
        "checkout-pending" or "checkout-unresolved" or "checkout-reconciliation-required" or "cancellation-pending"
            or "billing-checkout-in-progress" or "billing-checkout-reconciliation-required" or "billing-cancellation-unconfirmed" => "Pro.Error.Pending",
        "offer-changed" or "invalid-offer" => "Pro.Error.OfferChanged",
        _ => "Pro.Error.Request",
    };
}
