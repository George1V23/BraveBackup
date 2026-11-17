using Xunit;
using System.Security.Cryptography;

namespace BraveBackup.Core.Tests;

public sealed class PublicSuffixTests
{
    [Fact]
    public void EmbeddedRulesMatchPinnedUpstreamDataHash()
    {
        using var source = typeof(BravePublicSuffix).Assembly.GetManifestResourceStream("BraveBackup.PublicSuffixRules");
        Assert.NotNull(source);
        Assert.Equal("d4d5c24befb4a21306ace7f28cb04d35121d90f300f8c56c442937f9d1b5e278", Convert.ToHexStringLower(SHA256.HashData(source)));
    }

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("a.blogspot.com", "a.blogspot.com")]
    [InlineData("b.a.blogspot.com", "a.blogspot.com")]
    [InlineData("a.github.io", "a.github.io")]
    [InlineData("a.synthetic.invalid", "synthetic.invalid")]
    [InlineData("localhost", "")]
    [InlineData("127.0.0.1", "")]
    [InlineData("com", "")]
    [InlineData("co.uk", "")]
    [InlineData("a.b.ck", "a.b.ck")]
    [InlineData("www.ck", "www.ck")]
    [InlineData("a.www.ck", "www.ck")]
    [InlineData("a.city.kawasaki.jp", "city.kawasaki.jp")]
    public void MatchesPinnedPublicPrivateWildcardExceptionAndUnknownCases(string host, string expected)
        => Assert.Equal(expected, BravePublicSuffix.ExtendedDomain(host, []));

    [Fact]
    public void CachedExtensionsExpandOneLabelAtATimeWithOrdinalMatching()
    {
        Assert.Equal("a.example.com", BravePublicSuffix.ExtendedDomain("x.a.example.com", ["example.com"]));
        Assert.Equal("x.a.example.com", BravePublicSuffix.ExtendedDomain("x.a.example.com", ["example.com", "a.example.com"]));
        Assert.Equal("example.com", BravePublicSuffix.ExtendedDomain("x.a.example.com", ["a.example.com"]));
        Assert.Equal("example.com", BravePublicSuffix.ExtendedDomain("x.a.example.com", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EXAMPLE.COM" }));
    }

    [Theory]
    [InlineData("https://synthetic.invalid/path?x=1#f", "https://synthetic.invalid")]
    [InlineData("https://synthetic.invalid:8443/path", "https://synthetic.invalid:8443")]
    [InlineData("http://synthetic.invalid/path", "http://synthetic.invalid/")]
    public void ConstructsSupportedFacetRepresentationFromRealm(string realm, string expected)
        => Assert.Equal(expected, BravePasswordCsvFacet.FromRealm(realm));

    [Fact]
    public void UnsupportedHostsAndCancellationFailExplicitly()
    {
        Assert.Throws<BackupOperationException>(() => BravePublicSuffix.ExtendedDomain("xn--bcher-kva.invalid", []));
        Assert.Throws<BackupOperationException>(() => BravePublicSuffix.ExtendedDomain("example.com:8443", []));
        Assert.Throws<OperationCanceledException>(() => BravePublicSuffix.ExtendedDomain("example.com", [], new CancellationToken(true)));
        Assert.Throws<OperationCanceledException>(() => BravePasswordCsvFacet.FromRealm("https://example.com/", new CancellationToken(true)));
    }
}
