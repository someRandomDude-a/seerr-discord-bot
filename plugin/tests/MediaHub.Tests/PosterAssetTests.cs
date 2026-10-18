using System.Buffers.Binary;
using System.Net;
using Jellyfin.Plugin.MediaHub;
using Xunit;

namespace MediaHub.Tests;

public sealed class PosterAssetTests
{
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https://image.tmdb.org/t/p/w185/poster.jpg", request.RequestUri!.ToString());
            Assert.Empty(request.Headers); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[24]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height); return bytes;
    }
    [Fact]
    public async Task PosterFetchUsesOnlyFixedPublicOriginWithoutCredentials()
    {
        using var handler = new Handler(Png(185, 278)); using var assets = new PosterAssets(handler);
        var image = await assets.Download("/poster.jpg", default); Assert.Equal("image/png", image.Mime); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("https://attacker.example/x.jpg")] [InlineData("/../poster.jpg")] [InlineData("/poster.jpg?secret=x")] [InlineData("/poster.svg")] [InlineData("/poster.jpg\n")]
    public async Task UntrustedPathsCannotSelectAnOriginOrActiveFormat(string path)
    {
        using var handler = new Handler([]); using var assets = new PosterAssets(handler);
        await Assert.ThrowsAsync<HubError>(() => assets.Download(path, default)); Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData(0, 278)] [InlineData(185, 100000)] [InlineData(2048, 2048)]
    public void UnboundedDecodedImageDimensionsAreRejected(int width, int height) => Assert.Throws<HubError>(() => PosterAssets.Mime(Png(width, height)));
    [Fact]
    public void HtmlSvgAndMalformedJpegAreRejected()
    {
        Assert.Throws<HubError>(() => PosterAssets.Mime("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()));
        Assert.Throws<HubError>(() => PosterAssets.Mime([255, 216, 255, 192, 0, 1]));
    }
}
