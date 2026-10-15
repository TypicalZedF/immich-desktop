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
    public void Crop_comes_first_measured_on_the_original_then_turn_and_flip()
    {
        // On screen: turned a quarter clockwise (3000 wide, 4000 tall), flipped left-right, cropped.
        var edits = ImageEditPlan.Build(4000, 3000, 90, true, false, 0.25, 0.1, 0.5, 0.5);

        // Immich crops the original before turning: un-flip (x 0.25..0.75 stays), un-turn (screen y is original x).
        Assert.Equal(new CropEdit(400, 750, 2000, 1500), edits[0]);
        Assert.Equal(new RotateEdit(90), edits[1]);
        Assert.Equal(new MirrorEdit("horizontal"), edits[2]);
    }

    [Fact]
    public void A_crop_on_a_photo_turned_left_maps_to_its_right_side()
    {
        // Turned 270 clockwise, the original's top-right corner is at the top-left of the screen.
        var edits = ImageEditPlan.Build(4000, 3000, 270, false, false, 0, 0, 0.5, 0.25);

        Assert.Equal(new CropEdit(3000, 0, 1000, 1500), edits[0]);
        Assert.Equal(new RotateEdit(270), edits[1]);
    }

    [Fact]
    public async Task Sideways_photos_report_their_upright_size()
    {
        var server = new FakeImmichServer { ExifOrientation = "6" };
        using var client = Client(server);

        var size = await client.GetAssetSizeAsync("a1", TestContext.Current.CancellationToken);

        Assert.Equal((3000, 4000), (size.Width, size.Height));
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
        await client.ApplyEditsAsync("a1", [new CropEdit(10, 20, 300, 200), new RotateEdit(270), new MirrorEdit("vertical")], ct);

        Assert.Equal((4000, 3000, false), (size.Width, size.Height, size.IsEdited));
        Assert.Equal("""{"edits":[{"action":"crop","parameters":{"x":10,"y":20,"width":300,"height":200}},{"action":"rotate","parameters":{"angle":270}},{"action":"mirror","parameters":{"axis":"vertical"}}]}""",
            Assert.Single(server.AssetEdits));
    }

    [Fact]
    public async Task Pictures_are_asked_for_as_edited_unless_the_original_is_wanted()
    {
        // Immich serves the unedited picture unless asked for the edited one ("edited" defaults to false).
        var server = new FakeImmichServer();
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        await client.GetAssetImageAsync("a1", AssetImageSize.Thumbnail, ct);
        await client.GetAssetImageAsync("a1", AssetImageSize.Preview, ct, edited: false);
        await client.DownloadOriginalAsync("a1", Stream.Null, ct);

        Assert.Equal(["/api/assets/a1/thumbnail?size=thumbnail&edited=true", "/api/assets/a1/thumbnail?size=preview&edited=false", "/api/assets/a1/original?edited=true"],
            server.MediaRequests.ToArray());
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
