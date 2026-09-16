using System.Net;
using System.Text;
using System.Text.Json;
using Ralven.App.Services;
using Ralven.App.ViewModels;
using Xunit;

namespace Ralven.Tests.App;

public sealed class CloudflareBillingServiceTests
{
    private const string OfferJson = """{"offer":{"key":"ralven_pro_monthly_1990","amountCents":1990,"currency":"BRL","intervalMonths":1},"checkoutAvailable":true,"subscription":null}""";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BillingRoutesUseBearerAndCheckoutSendsOnlyOfferedKey()
    {
        var requests = new List<(string Path, string Method, string? Body)>();
        var service = new CloudflareBillingService(new HttpClient(new BillingHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("fixture-token", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            requests.Add((request.RequestUri.AbsolutePath, request.Method.Method,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(Cancellation)));
            return Json(request.RequestUri.AbsolutePath.EndsWith("checkout", StringComparison.Ordinal)
                ? """{"checkoutUrl":"https://asaas.com/checkoutSession/show?id=fixture"}""" : OfferJson);
        })), new Uri("https://example.com/account/profile"));

        Assert.Equal(1990, (await service.FetchAsync("fixture-token", Cancellation)).Value!.Offer!.AmountCents);
        Assert.NotNull((await service.CheckoutAsync("fixture-token", "ralven_pro_monthly_1990", Cancellation)).Value);
        Assert.NotNull((await service.CancelAsync("fixture-token", Cancellation)).Value);
        Assert.Equal(("/account/billing", "GET", (string?)null), requests[0]);
        Assert.Equal("/account/billing/checkout", requests[1].Path);
        Assert.Equal("POST", requests[1].Method);
        using var checkout = JsonDocument.Parse(requests[1].Body!);
        Assert.Single(checkout.RootElement.EnumerateObject());
        Assert.Equal("ralven_pro_monthly_1990", checkout.RootElement.GetProperty("offerKey").GetString());
        Assert.Equal(("/account/billing/cancel", "POST", "{}"), requests[2]);
    }

    [Theory]
    [InlineData("http://asaas.com/checkoutSession/show?id=fixture")]
    [InlineData("https://asaas.com.attacker.test/checkoutSession/show?id=fixture")]
    [InlineData("https://asaas.com@attacker.test/checkoutSession/show?id=fixture")]
    [InlineData("https://user@asaas.com/checkoutSession/show?id=fixture")]
    [InlineData("https://asaas.com:8443/checkoutSession/show?id=fixture")]
    [InlineData("https://asaas.com/redirect?id=fixture")]
    [InlineData("https://asaas.com/checkoutSession/show?id=fixture#token")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("https://asaas.com/checkoutSession/show")]
    [InlineData("https://asaas.com/checkoutSession/show?id=a&id=b")]
    [InlineData("https://asaas.com/checkoutSession/show?id=a&%69d=b")]
    [InlineData("https://asaas.com/checkoutSession/show?id=a&next=evil")]
    public async Task CheckoutRejectsNonHostedProviderTargets(string target)
    {
        var service = Create(_ => Json(JsonSerializer.Serialize(new { checkoutUrl = target })));
        Assert.Null((await service.CheckoutAsync("fixture-token", "ralven_pro_monthly_1990", Cancellation)).Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"offer\":null,\"checkoutAvailable\":true}")]
    [InlineData("{\"offer\":{\"key\":\"pro\",\"amountCents\":-1,\"currency\":\"BRL\",\"intervalMonths\":1},\"checkoutAvailable\":true}")]
    [InlineData("{\"offer\":{\"key\":\"pro\",\"amountCents\":1990,\"currency\":\"USD\",\"intervalMonths\":1},\"checkoutAvailable\":true}")]
    [InlineData("{\"subscription\":{\"state\":\"unknown\",\"canCancel\":true}}")]
    public async Task InvalidOfferCannotEnableCheckout(string body)
    {
        var result = await Create(_ => Json(body)).FetchAsync("fixture-token", Cancellation);
        var vm = new ProPageViewModel();
        vm.SetSession(true, false);
        vm.SetSnapshot(result.Value);
        vm.Consent = true;
        Assert.False(vm.CanCheckout);
    }

    [Fact]
    public async Task OversizedAndRemoteFreeTextErrorsNeverReachPresentation()
    {
        var large = await Create(_ => Json(new string('x', 17000))).FetchAsync("fixture-token", Cancellation);
        Assert.Null(large.Value);
        var remote = await Create(_ => Json("""{"error":"private provider details"}""", HttpStatusCode.BadGateway)).FetchAsync("fixture-token", Cancellation);
        Assert.Equal("request-failed", remote.Error);
        var conflict = await Create(_ => Json("""{"error":"billing-checkout-reconciliation-required"}""", HttpStatusCode.Conflict)).FetchAsync("fixture-token", Cancellation);
        Assert.Equal("Pro.Error.Pending", ProPageViewModel.ErrorKey(conflict.Error));
        var verification = await Create(_ => Json("""{"error":"email-verification-required"}""", HttpStatusCode.Forbidden)).FetchAsync("fixture-token", Cancellation);
        Assert.Equal("Account.Verification.PleaseConfirm", ProPageViewModel.ErrorKey(verification.Error));
    }

    [Fact]
    public async Task CancellationIsPropagatedAndTransportFailureIsRecoverable()
    {
        var failure = await Create(_ => throw new HttpRequestException("fixture failure")).FetchAsync("fixture-token", Cancellation);
        Assert.Null(failure.Value);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(_ => Json(OfferJson)).FetchAsync("fixture-token", cancelled.Token));
        Assert.Throws<ArgumentException>(() => new CloudflareBillingService(new Uri("http://example.com/account/profile")));
    }

    [Fact]
    public async Task DeadlineIncludesResponseBodyAndDoesNotLeaveBillingBusyForever()
    {
        var service = new CloudflareBillingService(new HttpClient(new BillingHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }))),
            new Uri("https://example.com/account/profile"), TimeSpan.FromMilliseconds(30));
        var result = await service.FetchAsync("fixture-token", Cancellation).WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        Assert.Null(result.Value);
        Assert.Equal("request-failed", result.Error);
    }

    [Fact]
    public void CheckoutRequiresConsentAndLocksWhileBusySignedOutOrInDemo()
    {
        var vm = new ProPageViewModel();
        var offer = new BillingOffer("ralven_pro_monthly_1990", 1990, "BRL", 1);
        vm.SetSession(true, false);
        vm.SetSnapshot(new(offer, true, null));
        Assert.False(vm.CanCheckout); // consent not given yet
        vm.Consent = true;
        Assert.True(vm.CanCheckout);
        Assert.True(vm.CanRefresh);
        vm.SetBusy(true);
        Assert.False(vm.CanCheckout);
        vm.SetBusy(false);
        vm.SetSnapshot(new(offer with { AmountCents = 2990, Key = "ralven_pro_monthly_2990" }, true, null));
        Assert.False(vm.Consent); // a changed offer requires re-consent
        Assert.False(vm.CanCheckout);
        vm.SetSession(false, false);
        Assert.Null(vm.Offer);
        Assert.False(vm.CanCancel);
        vm.SetSession(true, true); // demo mode never offers real checkout
        vm.Consent = true;
        Assert.False(vm.CanCheckout);
        Assert.False(vm.CanRefresh);
        Assert.False(vm.CanCancel);
    }

    private static CloudflareBillingService Create(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new HttpClient(new BillingHandler(request => Task.FromResult(send(request)))), new Uri("https://example.com/account/profile"));
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class BillingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
