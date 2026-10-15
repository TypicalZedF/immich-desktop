using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

public class ImageEditTests
{
    static ImmichClient Client(FakeImmichServer server) =>
        new(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), server);

    [Fact]
    public void Nothing_changed_means_no_edits()
    {
        Assert.Empty(ImageEditPlan.Build(4000, 3000, 0, false, false, 0, 0, 1, 1));
    }

    [Fact]
    public void Turn_flip_then_crop_in_that_order()
    {
        var edits = ImageEditPlan.Build(4000, 3000, 90, true, false, 0.25, 0.1, 0.5, 0.5);

        Assert.Equal(new RotateEdit(90), edits[0]);
        Assert.Equal(new MirrorEdit("horizontal"), edits[1]);
        // After a quarter turn the photo is 3000 wide and 4000 tall.
        Assert.Equal(new CropEdit(750, 400, 1500, 2000), edits[2]);
    }

    [Fact]
    public void A_crop_never_leaves_the_photo()
    {
        var crop = Assert.IsType<CropEdit>(Assert.Single(ImageEditPlan.Build(1000, 500, 0, false, false, 0.9, 0.9, 0.5, 0.5)));

        Assert.True(crop.X + crop.Width <= 1000 && crop.Y + crop.Height <= 500 && crop.Width >= 1 && crop.Height >= 1);
    }

    [Fact]
    public async Task Edits_are_sent_the_way_Immich_expects()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        var size = await client.GetAssetSizeAsync("a1", ct);
        await client.ApplyEditsAsync("a1", [new RotateEdit(270), new MirrorEdit("vertical"), new CropEdit(10, 20, 300, 200)], ct);

        Assert.Equal((4000, 3000, false), (size.Width, size.Height, size.IsEdited));
        Assert.Equal("""{"edits":[{"action":"rotate","parameters":{"angle":270}},{"action":"mirror","parameters":{"axis":"vertical"}},{"action":"crop","parameters":{"x":10,"y":20,"width":300,"height":200}}]}""",
            Assert.Single(server.AssetEdits));
    }

    [Fact]
    public async Task Edits_can_be_taken_off_again()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.RemoveEditsAsync("a1", TestContext.Current.CancellationToken);

        Assert.Equal("removed", Assert.Single(server.AssetEdits));
    }
}
